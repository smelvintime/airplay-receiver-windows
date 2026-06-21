using FFmpeg.AutoGen;
using Microsoft.UI.Xaml.Controls;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
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
    private VertexShader                _vs          = null!;
    private PixelShader                 _ps          = null!;
    private SamplerState                _sampler     = null!;
    private ShaderResourceView?         _lumaView;
    private ShaderResourceView?         _chromaView;
    private Texture2D?                  _yTex;       // NV12 luma  (R8)
    private Texture2D?                  _uvTex;      // NV12 chroma (R8G8, half res)
    private SharpDX.Direct3D11.Buffer?  _rotBuffer;  // PS constant buffer: UV remap for display rotation

    // Display rotation applied in the pixel shader (sampling-coordinate remap).
    // Identity by default; only the AirPlay Video path sets a non-zero rotation
    // for portrait phone clips whose pixels are stored landscape + a rotation flag.
    private RotationConstants _rotConstants = new()
    {
        Map0 = new RawVector4(1, 0, 0, 0),   // source_u = u
        Map1 = new RawVector4(0, 1, 0, 0),   // source_v = v
    };
    private bool _rotDirty = true;           // upload the constants on the next frame

    // Serialises immediate-context use between the decode thread (PresentFrame)
    // and the UI thread (Resize).
    private readonly object _renderLock = new();

    // ── State ─────────────────────────────────────────────────────────────────

    private int  _swapChainWidth;
    private int  _swapChainHeight;
    private int  _frameWidth;
    private int  _frameHeight;
    private long _presentCount;    // frames presented, for periodic progress logging
    private bool _active = true;   // when false, drop frames and keep the surface black

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

        // The panel hasn't been laid out yet at app launch, so ActualWidth/Height
        // are 0. DXGI rejects a 0-sized swap chain, so clamp to at least 1px; the
        // panel's SizeChanged handler resizes the buffers once layout completes.
        int initialWidth  = Math.Max(1, (int)panel.ActualWidth);
        int initialHeight = Math.Max(1, (int)panel.ActualHeight);

        // Create the D3D11 device first (no swap chain). A SwapChainPanel needs a
        // *composition* swap chain (IDXGIFactory2::CreateSwapChainForComposition),
        // not the HWND-bound swap chain that Device.CreateWithSwapChain produces.
        var device = new SharpDX.Direct3D11.Device(
            DriverType.Hardware,
            flags,
            new[]
            {
                SharpDX.Direct3D.FeatureLevel.Level_11_1,
                SharpDX.Direct3D.FeatureLevel.Level_11_0,
                SharpDX.Direct3D.FeatureLevel.Level_10_1,
            });

        _device  = device;
        _context = device.ImmediateContext;

        // ── Composition swap chain bound to the DXGI factory ──────────────────
        using var dxgiDevice = device.QueryInterface<SharpDX.DXGI.Device2>();
        using var adapter    = dxgiDevice.Adapter;
        using var factory    = adapter.GetParent<SharpDX.DXGI.Factory2>();

        var swapChainDesc = BuildSwapChainDesc(initialWidth, initialHeight);
        _swapChain = new SwapChain1(factory, device, ref swapChainDesc);

        // ── Bind swap chain to SwapChainPanel ─────────────────────────────────
        // SwapChainPanel implements ISwapChainPanelNative (via WinRT interop).
        var panelNative = panel.As<ISwapChainPanelNative>();
        panelNative.SetSwapChain(_swapChain.NativePointer);

        _swapChainWidth  = initialWidth;
        _swapChainHeight = initialHeight;

        // ── Shaders ───────────────────────────────────────────────────────────
        CompileShaders();

        // ── Rotation constant buffer (PS slot b0) ─────────────────────────────
        // Two float4 rows = 32 bytes. Default identity remap (no rotation).
        _rotBuffer = new SharpDX.Direct3D11.Buffer(_device, new BufferDescription
        {
            SizeInBytes    = 32,
            Usage          = ResourceUsage.Default,
            BindFlags      = BindFlags.ConstantBuffer,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags    = ResourceOptionFlags.None,
        });

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

        System.Diagnostics.Debug.WriteLine($"[Renderer] Initialized {_swapChainWidth}×{_swapChainHeight}");
    }

    // ── Frame presentation ────────────────────────────────────────────────────

    /// <summary>
    /// Called by VideoDecoder on the decode thread for each decoded frame.
    /// Presents the NV12 texture to the swap chain.
    /// </summary>
    public void PresentFrame(IntPtr framePtr)
    {
        AVFrame* frame = (AVFrame*)framePtr;
        int w = frame->width;
        int h = frame->height;
        if (w <= 0 || h <= 0) return;

        // CPU NV12: data[0] = Y plane (linesize[0]), data[1] = interleaved UV (linesize[1]).
        IntPtr yPtr  = (IntPtr)frame->data[0];
        int    yPitch  = frame->linesize[0];
        IntPtr uvPtr = (IntPtr)frame->data[1];
        int    uvPitch = frame->linesize[1];
        if (yPtr == IntPtr.Zero || uvPtr == IntPtr.Zero) return;

        lock (_renderLock)
        {
            if (!_active) return; // session ended — ignore late/queued frames

            EnsureNv12Textures(w, h);

            // Push a changed display-rotation remap to the GPU before drawing.
            if (_rotDirty)
            {
                _context.UpdateSubresource(ref _rotConstants, _rotBuffer!);
                _rotDirty = false;
            }

            // Upload the planes (source row pitch = FFmpeg linesize, which may be padded).
            _context.UpdateSubresource(new DataBox(yPtr,  yPitch,  yPitch  * h),       _yTex!,  0);
            _context.UpdateSubresource(new DataBox(uvPtr, uvPitch, uvPitch * (h / 2)), _uvTex!, 0);

            // Flip-model swap chains rotate the back buffer on every Present, so the
            // render target must be re-acquired each frame — a single cached RTV would
            // only ever update the first presented frame.
            using var backBuffer = _swapChain.GetBackBuffer<Texture2D>(0);
            using var rtv        = new RenderTargetView(_device, backBuffer);
            _context.OutputMerger.SetRenderTargets(rtv);
            _context.ClearRenderTargetView(rtv, new RawColor4(0, 0, 0, 1));

            _context.VertexShader.Set(_vs);
            _context.PixelShader.Set(_ps);
            _context.PixelShader.SetSampler(0, _sampler);
            _context.PixelShader.SetConstantBuffer(0, _rotBuffer!);
            _context.PixelShader.SetShaderResource(0, _lumaView);
            _context.PixelShader.SetShaderResource(1, _chromaView);

            // Full-screen triangle (VS generates positions from SV_VertexID).
            _context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            _context.Draw(3, 0);

            _swapChain.Present(1, PresentFlags.None);

            if (++_presentCount % 60 == 0)
                System.Diagnostics.Debug.WriteLine($"[Renderer] presented {_presentCount} frames ({w}×{h})");
        }
    }

    /// <summary>
    /// Enables or disables presentation. When disabled (e.g. a mirroring session
    /// ended) the surface is cleared to black and subsequent frames are dropped,
    /// so the last frame doesn't linger under the idle overlay.
    /// </summary>
    public void SetActive(bool active)
    {
        lock (_renderLock)
        {
            _active = active;
            if (!active)
            {
                using var backBuffer = _swapChain.GetBackBuffer<Texture2D>(0);
                using var rtv        = new RenderTargetView(_device, backBuffer);
                _context.OutputMerger.SetRenderTargets(rtv);
                _context.ClearRenderTargetView(rtv, new RawColor4(0, 0, 0, 1));
                _swapChain.Present(1, PresentFlags.None);
            }
        }
    }

    /// <summary>
    /// Sets the display rotation (degrees, clockwise — must be a multiple of 90)
    /// applied when sampling the video. AirPlay Video (URL playback) uses this for
    /// portrait phone clips, which are encoded with landscape pixel dimensions plus
    /// a rotation flag in the container; without it they render sideways/stretched.
    /// The mirror path leaves this at 0.
    /// </summary>
    public void SetRotation(int degrees)
    {
        int rot = ((degrees % 360) + 360) % 360;
        rot = (rot + 45) / 90 * 90 % 360;   // snap to the nearest quarter turn

        // Sampling-coordinate remap (dest uv → source uv) for a clockwise image
        // rotation. texY/texUV are sampled at source = (dot(Map0.xy, uv) + Map0.z,
        // dot(Map1.xy, uv) + Map1.z), so each row is (a, b, offset, _).
        RawVector4 m0, m1;
        switch (rot)
        {
            case 90:  m0 = new RawVector4( 0, 1, 0, 0); m1 = new RawVector4(-1,  0, 1, 0); break; // src = ( v, 1-u)
            case 180: m0 = new RawVector4(-1, 0, 1, 0); m1 = new RawVector4( 0, -1, 1, 0); break; // src = (1-u,1-v)
            case 270: m0 = new RawVector4( 0,-1, 1, 0); m1 = new RawVector4( 1,  0, 0, 0); break; // src = (1-v,  u)
            default:  m0 = new RawVector4( 1, 0, 0, 0); m1 = new RawVector4( 0,  1, 0, 0); break; // src = ( u,  v)
        }

        lock (_renderLock)
        {
            _rotConstants.Map0 = m0;
            _rotConstants.Map1 = m1;
            _rotDirty = true;
        }

        System.Diagnostics.Debug.WriteLine($"[Renderer] display rotation = {rot}°");
    }

    // ── Resize ────────────────────────────────────────────────────────────────

    private void Resize(int width, int height)
    {
        if (width == _swapChainWidth && height == _swapChainHeight) return;
        if (width <= 0 || height <= 0) return;

        lock (_renderLock)
        {
            _swapChain.ResizeBuffers(
                bufferCount: 2,
                width, height,
                Format.B8G8R8A8_UNorm,
                SwapChainFlags.None);

            _swapChainWidth  = width;
            _swapChainHeight = height;

            // The back-buffer RTV is acquired per frame in PresentFrame, so there's
            // nothing to recreate here after resizing the buffers.
            _context.Rasterizer.SetViewport(0, 0, width, height);
        }

        System.Diagnostics.Debug.WriteLine($"[Renderer] Resized to {width}×{height}");
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

    /// <summary>
    /// (Re)creates the NV12 upload textures and their SRVs when the frame size
    /// changes: an R8 luma texture (full res) and an R8G8 chroma texture (half res).
    /// </summary>
    private void EnsureNv12Textures(int width, int height)
    {
        if (_yTex is not null && _frameWidth == width && _frameHeight == height)
            return;

        _lumaView?.Dispose();
        _chromaView?.Dispose();
        _yTex?.Dispose();
        _uvTex?.Dispose();

        _yTex = new Texture2D(_device, new Texture2DDescription
        {
            Width = width, Height = height, MipLevels = 1, ArraySize = 1,
            Format = Format.R8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None, OptionFlags = ResourceOptionFlags.None,
        });
        _uvTex = new Texture2D(_device, new Texture2DDescription
        {
            Width = (width + 1) / 2, Height = (height + 1) / 2, MipLevels = 1, ArraySize = 1,
            Format = Format.R8G8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None, OptionFlags = ResourceOptionFlags.None,
        });

        _lumaView   = new ShaderResourceView(_device, _yTex);
        _chromaView = new ShaderResourceView(_device, _uvTex);
        _frameWidth  = width;
        _frameHeight = height;

        System.Diagnostics.Debug.WriteLine($"[Renderer] NV12 textures {width}×{height}");
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

            // Display-rotation UV remap: source = (dot(uMap.xy, uv) + uMap.z,
            //                                      dot(vMap.xy, uv) + vMap.z)
            cbuffer Rotation : register(b0)
            {
                float4 uMap;
                float4 vMap;
            };

            struct PS_IN { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            float4 main(PS_IN i) : SV_TARGET
            {
                float2 src = float2(dot(uMap.xy, i.uv) + uMap.z,
                                    dot(vMap.xy, i.uv) + vMap.z);
                float  y  = texY.Sample(samp, src).r;
                float2 uv = texUV.Sample(samp, src).rg;

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
        _yTex?.Dispose();
        _uvTex?.Dispose();
        _rotBuffer?.Dispose();
        _sampler.Dispose();
        _ps.Dispose();
        _vs.Dispose();
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

    // PS constant buffer layout (b0): two float4 rows that remap the sampling
    // coordinates to apply the stream's display rotation.
    [StructLayout(LayoutKind.Sequential)]
    private struct RotationConstants
    {
        public RawVector4 Map0;   // source_u = dot(Map0.xy, uv) + Map0.z
        public RawVector4 Map1;   // source_v = dot(Map1.xy, uv) + Map1.z
    }
}
