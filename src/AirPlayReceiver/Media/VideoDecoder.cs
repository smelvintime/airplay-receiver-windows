using FFmpeg.AutoGen;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AirPlayReceiver.Media;

/// <summary>
/// Hardware-accelerated H.264 / HEVC decoder using FFmpeg with D3D11VA.
///
/// Pipeline:
///   RtpReceiver.SubmitPacket()
///       → SubmitPacket() enqueues Annex-B NAL data
///           → decode thread: avcodec_send_packet → avcodec_receive_frame
///               → frame is a D3D11 texture (AVHWFrameContext / AV_PIX_FMT_D3D11)
///                   → VideoPresenter renders via HLSL NV12→RGB shader
///
/// D3D11VA zero-copy path:
///   - Frame pixel format: AV_PIX_FMT_D3D11  (hardware surface)
///   - Frame data[0]: (ID3D11Texture2D*)
///   - Frame data[1]: (intptr_t) texture array index
///   - NO CPU readback required for rendering.
///
/// References:
///   FFmpeg hw_decode example: doc/examples/hw_decode.c
///   UxPlay renderers/video_renderer_ffmpeg.c
/// </summary>
// Note: 'unsafe' is applied per-member rather than to the whole class, because
// an async method (StopAsync) can't 'await' inside an unsafe context.
public sealed class VideoDecoder : IDisposable
{
    // ── Dependencies ──────────────────────────────────────────────────────────

    // The decoded frame is handed across as an IntPtr (a boxed AVFrame*), because
    // C# forbids pointer types as generic arguments to Action<>. The presenter,
    // an unsafe class, casts it back to AVFrame*.
    private readonly Action<IntPtr>        _onFrame;       // called on decode thread
    private readonly Action<int, int>      _onDimensions;  // (width, height) first frame
    private readonly Action<string>        _onHudUpdate;

    // ── FFmpeg context ────────────────────────────────────────────────────────

    private unsafe AVCodecContext* _codecCtx;
    private unsafe AVBufferRef*    _hwDeviceCtx;
    private unsafe AVPacket*       _packet;
    private unsafe AVFrame*        _frame;
    private unsafe AVFrame*        _swFrame;   // fallback software frame

    // Kept alive for the codec context's lifetime so the GC doesn't collect the
    // managed delegate that FFmpeg invokes to negotiate the hardware pixel format.
    private unsafe AVCodecContext_get_format _getFormatCallback;

    // ── Threading ─────────────────────────────────────────────────────────────

    private readonly BlockingCollection<byte[]> _packetQueue = new(boundedCapacity: 120);
    private CancellationTokenSource? _cts;
    private Task? _decodeTask;
    private bool _dimensionsReported;

    // ── Construction ──────────────────────────────────────────────────────────

    /// <param name="onFrame">
    ///   Called on the decode thread with each decoded frame as an IntPtr that
    ///   wraps an AVFrame*. The pointer is valid only until this callback returns,
    ///   so the VideoPresenter must copy the texture reference, not hold the pointer.
    /// </param>
    public VideoDecoder(
        Action<IntPtr>   onFrame,
        Action<int, int> onDimensions,
        Action<string>   onHudUpdate)
    {
        _onFrame      = onFrame;
        _onDimensions = onDimensions;
        _onHudUpdate  = onHudUpdate;
    }

    // ── Initialisation ────────────────────────────────────────────────────────

    /// <param name="codecId">AV_CODEC_ID_H264 or AV_CODEC_ID_HEVC</param>
    public unsafe void Initialize(AVCodecID codecId = AVCodecID.AV_CODEC_ID_H264)
    {
        // ── Hardware device context ───────────────────────────────────────────
        AVBufferRef* hwCtx = null;
        int ret = ffmpeg.av_hwdevice_ctx_create(
            &hwCtx,
            AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA,
            null,   // device (default adapter)
            null,   // options
            0);     // flags

        if (ret < 0)
        {
            Console.WriteLine($"[Decoder] D3D11VA unavailable (err={ret}), falling back to software.");
            hwCtx = null;
        }
        else
        {
            Console.WriteLine("[Decoder] D3D11VA hardware context created.");
        }

        _hwDeviceCtx = hwCtx;

        // ── Codec ─────────────────────────────────────────────────────────────
        AVCodec* codec = ffmpeg.avcodec_find_decoder(codecId);
        if (codec == null)
            throw new InvalidOperationException($"No decoder found for {codecId}");

        _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
        if (_codecCtx == null)
            throw new OutOfMemoryException("avcodec_alloc_context3 returned null");

        // Enable hardware decoding
        if (_hwDeviceCtx != null)
        {
            _codecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);
            // A method group can't convert directly to FFmpeg.AutoGen's get_format
            // function-pointer struct; go through a delegate-typed field (which also
            // keeps the callback alive against GC).
            _getFormatCallback    = GetHwFormat;
            _codecCtx->get_format = _getFormatCallback;
        }

        // Low-latency flags: don't wait for B-frames, decode immediately.
        _codecCtx->flags  |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _codecCtx->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;

        // Thread count: 1 for minimum latency (parallel decoding adds a frame delay).
        _codecCtx->thread_count = 1;
        _codecCtx->thread_type  = 0;

        ret = ffmpeg.avcodec_open2(_codecCtx, codec, null);
        if (ret < 0)
            throw new InvalidOperationException($"avcodec_open2 failed: {ret}");

        // ── Allocate reusable structs ─────────────────────────────────────────
        _packet  = ffmpeg.av_packet_alloc();
        _frame   = ffmpeg.av_frame_alloc();
        _swFrame = ffmpeg.av_frame_alloc();

        Console.WriteLine($"[Decoder] Opened codec: {Marshal.PtrToStringAnsi((IntPtr)codec->name)}");
    }

    // ── Packet submission ─────────────────────────────────────────────────────

    /// <summary>
    /// Called by RtpReceiver on the network thread.
    /// Enqueues a copy of the NAL data for the decode thread.
    /// Non-blocking: if the queue is full (decoder can't keep up), the oldest
    /// packet is dropped to maintain low latency.
    /// </summary>
    public void SubmitPacket(byte[] annexBData)
    {
        if (!_packetQueue.TryAdd(annexBData))
        {
            // Queue full — drop oldest to avoid growing latency
            if (_packetQueue.TryTake(out _))
                _packetQueue.TryAdd(annexBData);
        }
    }

    // ── Decode thread ─────────────────────────────────────────────────────────

    public void Start()
    {
        _cts        = new CancellationTokenSource();
        _decodeTask = Task.Factory.StartNew(
            DecodeLoop,
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public async Task StopAsync()
    {
        _packetQueue.CompleteAdding();
        _cts?.Cancel();
        if (_decodeTask is not null)
            await _decodeTask.ConfigureAwait(false);
    }

    private void DecodeLoop()
    {
        Console.WriteLine("[Decoder] Decode thread started.");

        try
        {
            foreach (byte[] data in _packetQueue.GetConsumingEnumerable(_cts!.Token))
            {
                DecodePacket(data);
            }
        }
        catch (OperationCanceledException) { }

        Console.WriteLine("[Decoder] Decode thread stopped.");
    }

    private unsafe void DecodePacket(byte[] data)
    {
        fixed (byte* pData = data)
        {
            _packet->data = pData;
            _packet->size = data.Length;

            int ret = ffmpeg.avcodec_send_packet(_codecCtx, _packet);
            if (ret < 0)
            {
                Console.WriteLine($"[Decoder] avcodec_send_packet error: {ret}");
                return;
            }
        }

        while (true)
        {
            int ret = ffmpeg.avcodec_receive_frame(_codecCtx, _frame);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                break;
            if (ret < 0)
            {
                Console.WriteLine($"[Decoder] avcodec_receive_frame error: {ret}");
                break;
            }

            // Report stream dimensions on the first frame
            if (!_dimensionsReported)
            {
                _dimensionsReported = true;
                _onDimensions(_frame->width, _frame->height);
                Console.WriteLine($"[Decoder] Stream: {_frame->width}×{_frame->height}  " +
                                  $"fmt={_frame->format}  hw={(AVPixelFormat)_frame->format == AVPixelFormat.AV_PIX_FMT_D3D11}");
            }

            // Invoke the render callback (VideoPresenter). The pointer is passed
            // as an IntPtr because Action<> can't take a pointer type argument.
            _onFrame((IntPtr)_frame);

            // Unref immediately so FFmpeg can recycle the HW texture
            ffmpeg.av_frame_unref(_frame);
        }
    }

    // ── Hardware format callback ──────────────────────────────────────────────

    /// <summary>
    /// Tells FFmpeg to use the D3D11 hardware pixel format when the codec
    /// queries which output format to use.
    /// This is called once during decoder initialisation / first keyframe.
    /// </summary>
    private static unsafe AVPixelFormat GetHwFormat(AVCodecContext* ctx, AVPixelFormat* fmts)
    {
        for (AVPixelFormat* p = fmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
        {
            if (*p == AVPixelFormat.AV_PIX_FMT_D3D11)
                return AVPixelFormat.AV_PIX_FMT_D3D11;
        }

        // D3D11 not available for this frame — fall back to first offered format.
        Console.WriteLine("[Decoder] D3D11 format not offered; using software fallback.");
        return *fmts;
    }

    // ── Disposal ─────────────────────────────────────────────────────────────

    public unsafe void Dispose()
    {
        if (_codecCtx != null)
        {
            fixed (AVCodecContext** p = &_codecCtx)
                ffmpeg.avcodec_free_context(p);
        }
        if (_hwDeviceCtx != null)
        {
            fixed (AVBufferRef** p = &_hwDeviceCtx)
                ffmpeg.av_buffer_unref(p);
        }
        if (_packet != null)
        {
            fixed (AVPacket** p = &_packet)
                ffmpeg.av_packet_free(p);
        }
        if (_frame != null)
        {
            fixed (AVFrame** p = &_frame)
                ffmpeg.av_frame_free(p);
        }
        if (_swFrame != null)
        {
            fixed (AVFrame** p = &_swFrame)
                ffmpeg.av_frame_free(p);
        }

        _packetQueue.Dispose();
    }
}
