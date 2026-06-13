using FFmpeg.AutoGen;
using Microsoft.UI.Xaml.Controls;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Runtime.InteropServices;
using WinRT;

namespace AirPlayReceiver.Rendering;

/// <summary>
/// Binds a D3D11 swap chain to a WinUI 3 <see cref="SwapChainPanel"/> and
/// presents decoded video frames with a NV12 → RGB HLSL pixel shader.
///
/// Zero-copy GPU path:
///   VideoDecoder produces AVFrame with data[0] = ID3D11Texture2D* (NV12)
///   VideoPresenter copies the texture reference into the shader's input SRV
///   HLSL converts NV12 to RGBA and writes to the swap chain render target
///   IDXGISwapChain1::Present(1, 0) — vsync locked (1 interval)
///
/// To reduce latency at the cost of tearing, change Present to (0, 0).
///
/// References:
///   Microsoft docs: DXGI_SWAP_CHAIN_DESC1, IDXGISwapChain1
///   UxPlay renderers/video_renderer_d3d.c (conceptual equivalent)
/// </summary>
public sealed unsafe class VideoPresenter : IDisposable
{
    // ── D3D11 objects ─────────────────────────────────────────────────────────

    private SharpDX.Direct3D11.Device  _device      = null!;
    private DeviceContext               _context     = null!;
    private SwapChain1                  _swapChain   = null!;
    private RenderTargetView            _rtv         = null!;
    private VertexShader                _vs          = null!;
    private PixelShader                 _ps          = null!;
    private SamplerState                _sampler     = null!;
    private ShaderResourceView?         _lumaView;
    private ShaderResourceView?         _chromaView;
    private Texture2D?                  _stagingTex; // for software-decoded frames

    // ── State ─────────────────────────────────────────────────────────────────

    private int _swapChainWidth;
    private int _swapChainHeight;

    // ── Initialisation ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the D3D11 device and binds a new DXGI swap chain to the panel.
    /// Must be called on the UI thread.
    /// </summary>
    public void Initialize(SwapChainPanel panel)
    {
        // ── Create D3D11 device ───────────────────────────────────────────────
        var flags = DeviceCreationFlags.BgraSupport;
#if DEBUG
        flags |= DeviceCreationFlags.Debug;
#endif

        SharpDX.Direct3D11.Device.CreateWithSwapChain(
            DriverType.Hardware,
            flags,
            new[]
            {
                SharpDX.Direct3D.FeatureLevel.Level_11_1,
                SharpDX.Direct3D.FeatureLevel.Level_11_0,
                SharpDX.Direct3D.FeatureLevel.Level_10_1,
            },
            BuildSwapChainDesc((int)panel.ActualWidth, (int)panel.ActualHeight),
            out SharpDX.Direct3D11.Device device,
            out SwapChain swapChain);

        _device  = device;
        _context = device.ImmediateContext;

        // ── Upgrade to IDXGISwapChain1 ────────────────────────────────────────
        _swapChain = swapChain.QueryInterface<SwapChain1>();
        swapChain.Dispose();

        // ── Bind swap chain to SwapChainPanel ─────────────────────────────────
        // SwapChainPanel implements ISwapChainPanelNative (via WinRT interop).
        var panelNative = panel.As<ISwapChainPanelNative>();
        panelNative.SetSwapChain(_swapChain.NativePointer);

        _swapChainWidth  = (int)panel.ActualWidth;
        _swapChainHeight = (int)panel.ActualHeight;

        // ── Render target view ────────────────────────────────────────────────
        CreateRenderTargetView();

        // ── Shaders ───────────────────────────────────────────────────────────
        CompileShaders();

        // ── Sampler ───────────────────────────────────────────────────────────
        _sampler = new SamplerState(_device, new SamplerStateDescription
        {
            Filter             = Filter.MinMagMipLinear,
            AddressU           = TextureAddressMode.Clamp,
            AddressV           = TextureAddressMode.Clamp,
            AddressW           = TextureAddressMode.Clamp,
            MaximumLod         = float.MaxValue,
            ComparisonFunction = Comparison.Always,
        });

        // ── Viewport ──────────────────────────────────────────────────────────
        _context.Rasterizer.SetViewport(0, 0, _swapChainWidth, _swapChainHeight);

        // ── Panel size-change ─────────────────────────────────────────────────
        panel.SizeChanged += (_, e) =>
            Resize((int)e.NewSize.Width, (int)e.NewSize.Height);

        Console.WriteLine($"[Renderer] Initialized {_swapChainWidth}×{_swapChainHeight}");
    }

    // ── Frame presentation ────────────────────────────────────────────────────

    /// <summary>
    /// Called by VideoDecoder on the decode thread for each decoded frame.
    /// Presents the NV12 texture to the swap chain.
    /// </summary>
    public void PresentFrame(AVFrame* frame)
    {
        // The D3D11VA frame has:
        //   frame->data[0] = (uint8_t*)ID3D11Texture2D*
        //   frame->data[1] = (uint8_t*)(uintptr_t)arrayIndex
        var tex2D = (ID3D11Texture2D*)(void*)frame->data[0];
        int slice  = (int)(nint)(void*)frame->data[1];

        // Create (or recreate) SRVs for the Y and UV planes of the NV12 texture.
        UpdateNv12Views(tex2D, slice, frame->width, frame->height);

        // ── Render ────────────────────────────────────────────────────────────
        _context.OutputMerger.SetRenderTargets(_rtv);
        _context.ClearRenderTargetView(_rtv, new Color4(0, 0, 0, 1));

        _context.VertexShader.Set(_vs);
        _context.PixelShader.Set(_ps);
        _context.PixelShader.SetSampler(0, _sampler);

        if (_lumaView is not null)
            _context.PixelShader.SetShaderResource(0, _lumaView);
        if (_chromaView is not null)
            _context.PixelShader.SetShaderResource(1, _chromaView);

        // Full-screen triangle (no vertex buffer needed — VS generates from SV_VertexID)
        _context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        _context.Draw(3, 0);

        // Present vsync-locked (1 interval = wait for next vblank)
        _swapChain.Present(1, PresentFlags.None);
    }

    // ── Resize ────────────────────────────────────────────────────────────────

    private void Resize(int width, int height)
    {
        if (width == _swapChainWidth && height == _swapChainHeight) return;
        if (width <= 0 || height <= 0) return;

        _rtv.Dispose();

        _swapChain.ResizeBuffers(
            bufferCount: 2,
            width, height,
            Format.B8G8R8A8_UNorm,
            SwapChainFlags.None);

        _swapChainWidth  = width;
        _swapChainHeight = height;

        CreateRenderTargetView();
        _context.Rasterizer.SetViewport(0, 0, width, height);

        Console.WriteLine($"[Renderer] Resized to {width}×{height}");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private SwapChainDescription1 BuildSwapChainDesc(int width, int height)
        => new()
        {
            Width             = width,
            Height            = height,
            Format            = Format.B8G8R8A8_UNorm,
            Stereo            = false,
            SampleDescription = new SampleDescription(1, 0),
            Usage             = Usage.RenderTargetOutput,
            BufferCount       = 2,
            Scaling           = Scaling.Stretch,
            SwapEffect        = SwapEffect.FlipSequential,
            AlphaMode         = AlphaMode.Premultiplied,
            Flags             = SwapChainFlags.None,
        };

    private void CreateRenderTargetView()
    {
        using var backBuffer = _swapChain.GetBackBuffer<Texture2D>(0);
        _rtv = new RenderTargetView(_device, backBuffer);
    }

    private void UpdateNv12Views(ID3D11Texture2D* tex2D, int slice, int width, int height)
    {
        // TODO: Create SRVs for luma (R8) and chroma (RG8) planes.
        // This requires marshalling the native ID3D11Texture2D* into SharpDX.
        // Full implementation:
        //   var sharpTex = new Texture2D((IntPtr)tex2D);
        //   _lumaView   = new ShaderResourceView(_device, sharpTex, lumaDesc);
        //   _chromaView = new ShaderResourceView(_device, sharpTex, chromaDesc);
        // where lumaDesc.Format = R8_UNorm, chromaDesc.Format = R8G8_UNorm
        // and both include the array slice index.
    }

    private void CompileShaders()
    {
        // Full-screen triangle vertex shader (no input layout needed).
        // Outputs UV coordinates for the pixel shader.
        const string vsHlsl = @"
            struct VS_OUT { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            VS_OUT main(uint id : SV_VertexID)
            {
                // Triangle that covers the full NDC clip space:
                //   vertex 0: (-1, -1)  uv (0, 1)
                //   vertex 1: (-1,  3)  uv (0,-1)
                //   vertex 2: ( 3, -1)  uv (2, 1)
                VS_OUT o;
                o.uv  = float2((id == 2) ? 2.0 : 0.0, (id == 1) ? -1.0 : 1.0);
                o.pos = float4(o.uv * float2(2,-2) + float2(-1,1), 0, 1);
                return o;
            }";

        // NV12 → RGB conversion pixel shader.
        // Slot 0: Y plane  (R8_UNorm)
        // Slot 1: UV plane (R8G8_UNorm, half resolution)
        // BT.709 limited-range coefficients.
        const string psHlsl = @"
            Texture2D<float>  texY  : register(t0);
            Texture2D<float2> texUV : register(t1);
            SamplerState      samp  : register(s0);

            struct PS_IN { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            float4 main(PS_IN i) : SV_TARGET
            {
                float  y  = texY.Sample(samp, i.uv).r;
                float2 uv = texUV.Sample(samp, i.uv).rg;

                // BT.709 limited range: Y in [16/255, 235/255], UV in [16/255, 240/255]
                y  = (y  - 16.0/255.0) / (219.0/255.0);
                uv = (uv - 128.0/255.0) / (224.0/255.0);

                float r = y + 1.5748 * uv.y;
                float g = y - 0.1873 * uv.x - 0.4681 * uv.y;
                float b = y + 1.8556 * uv.x;

                return float4(saturate(r), saturate(g), saturate(b), 1.0);
            }";

        using var vsBlob = SharpDX.D3DCompiler.ShaderBytecode.Compile(
            vsHlsl, "main", "vs_5_0", SharpDX.D3DCompiler.ShaderFlags.None);
        using var psBlob = SharpDX.D3DCompiler.ShaderBytecode.Compile(
            psHlsl, "main", "ps_5_0", SharpDX.D3DCompiler.ShaderFlags.None);

        _vs = new VertexShader(_device, vsBlob);
        _ps = new PixelShader(_device, psBlob);
    }

    // ── Disposal ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _lumaView?.Dispose();
        _chromaView?.Dispose();
        _stagingTex?.Dispose();
        _sampler.Dispose();
        _ps.Dispose();
        _vs.Dispose();
        _rtv.Dispose();
        _swapChain.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    // ── Native interop ────────────────────────────────────────────────────────

    [ComImport]
    [Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISwapChainPanelNative
    {
        void SetSwapChain(IntPtr swapChain);
    }

    // Unmanaged COM pointer type (opaque — only used for SRV creation)
    [StructLayout(LayoutKind.Sequential)]
    private struct ID3D11Texture2D { }
}
