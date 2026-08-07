using AirPlayReceiver.Services;
using FFmpeg.AutoGen;
using System;
using System.Threading;

namespace AirPlayReceiver.Media;

/// <summary>
/// AirPlay <b>Photos</b> display path: turns the raw JPEG body of a
/// <c>PUT /photo</c> into an NV12 frame and hands it to the very same render
/// sinks <see cref="AirPlayVideoPlayer"/> uses.
///
/// Photos is a protocol of its own — no /play, no FCUP, no HLS, no RTP. A JPEG
/// simply arrives on an HTTP request and has to appear on screen. Rather than
/// bolt a second display path onto the window (a XAML Image over the
/// SwapChainPanel), this decodes to NV12 and pushes through
/// <see cref="Rendering.VideoPresenter"/>, so photos inherit the letterboxing,
/// rotation reset, fullscreen (F11) and idle-overlay behaviour the video path
/// already has, and there is exactly one thing that can be "on screen".
///
/// Colour: JPEG stores luma full-range [0,255], but the presenter's shader
/// expands from limited-range [16,235] — the assumption that suits H.264 mirror
/// frames. Handing it raw JPEG luma crushes blacks and blows highlights, so the
/// NV12 scale also performs the range conversion. Getting libswscale to do that
/// reliably is subtler than it looks; see <see cref="AsFullRangeFormat"/> for
/// the measurements behind how it is requested.
///
/// Threading: photos arrive on RTSP session threads (iOS opens several short-
/// lived connections per session), so every FFmpeg call here is serialised on
/// one lock. Decode of a 12-megapixel JPEG is a few tens of milliseconds — fine
/// on the request thread, and it keeps the 200 OK honest: we only answer after
/// the photo is actually on screen.
/// </summary>
public sealed unsafe class PhotoDecoder : IDisposable
{
    /// <summary>
    /// Upper bound on the displayed pixel dimensions. A modern iPhone photo is
    /// 4032×3024 (12 MP); decoding is unavoidable but uploading that to the GPU
    /// for a 1080p/4K window is pure waste, and the NV12 staging buffer alone
    /// would be ~18 MB. libswscale downscales for free as part of the NV12
    /// conversion we already perform. Aspect ratio is always preserved.
    /// </summary>
    private const int MaxDisplayWidth  = 3840;
    private const int MaxDisplayHeight = 2160;

    // ── Render sinks (wired by AirPlayService to the shared presenter) ─────────

    private readonly Action<IntPtr>   _onFrame;       // (AVFrame* as IntPtr) NV12 frame
    private readonly Action<int, int> _onDimensions;  // (width, height) for letterboxing
    private readonly Action<bool>     _onActive;      // true on every photo, false when the session ends
    private readonly Action<int>      _onRotation;    // presenter rotation, always reset to 0 for photos

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly object _sync = new();
    private bool _active;       // a photo is currently on screen
    private bool _disposed;
    private long _displayed;    // photos successfully presented this app run

    // Reused NV12 destination + scaler, rebuilt when the target size changes.
    private AVFrame*    _nv12;
    private SwsContext* _sws;
    private int _swsSrcW, _swsSrcH, _swsDstW, _swsDstH;
    private AVPixelFormat _swsSrcFmt = AVPixelFormat.AV_PIX_FMT_NONE;

    /// <summary>True while a photo is on screen (drives the idle overlay).</summary>
    public bool IsActive { get { lock (_sync) return _active; } }

    /// <summary>Photos presented since app start — asserted by the loopback harness.</summary>
    public long DisplayedCount => Interlocked.Read(ref _displayed);

    /// <summary>Pixel dimensions of the last photo actually presented (0×0 if none).</summary>
    public (int Width, int Height) LastDisplaySize { get; private set; }

    public PhotoDecoder(
        Action<IntPtr>   onFrame,
        Action<int, int> onDimensions,
        Action<bool>     onActive,
        Action<int>      onRotation)
    {
        _onFrame      = onFrame;
        _onDimensions = onDimensions;
        _onActive     = onActive;
        _onRotation   = onRotation;
    }

    // ── Display ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Decodes <paramref name="jpeg"/> and presents it. Returns false (having
    /// logged the reason) when the bytes aren't a decodable image — the caller
    /// still answers 200, because a per-photo failure must not abort the phone's
    /// whole Photos session.
    /// </summary>
    /// <param name="assetKey">X-Apple-AssetKey, for logs only.</param>
    /// <param name="transition">X-Apple-Transition, for logs only (see remarks).</param>
    /// <remarks>
    /// Transitions: the swap is already graceful — the presenter clears and draws
    /// the whole surface inside one lock and Presents once, so a photo replaces
    /// its predecessor atomically, never half-torn. The named effects (Dissolve,
    /// SlideLeft, …) would need a second NV12 buffer and a blend pass in the
    /// shader; the requested effect is logged so a field trace shows what iOS
    /// actually asks for before that work is justified.
    /// </remarks>
    public bool Display(byte[] jpeg, string assetKey, string transition)
    {
        if (jpeg is null || jpeg.Length == 0)
        {
            Log.Warn("Photo", $"display key={assetKey}: empty body, nothing to show");
            return false;
        }

        // JPEG SOI marker. Not fatal (we still hand it to FFmpeg, which probes),
        // but a body that isn't a JPEG usually means a framing bug upstream —
        // e.g. a Content-Length mismatch truncating or shifting the body.
        if (jpeg.Length < 2 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            Log.Warn("Photo", $"display key={assetKey}: body does not start with the JPEG SOI marker " +
                              $"(first bytes {jpeg[0]:X2} {(jpeg.Length > 1 ? jpeg[1].ToString("X2") : "--")}, " +
                              $"{jpeg.Length}B) — attempting decode anyway");
        }

        lock (_sync)
        {
            if (_disposed) return false;

            AVFrame* decoded = null;
            try
            {
                decoded = DecodeJpeg(jpeg, assetKey);
                if (decoded is null) return false;

                AVFrame* nv12 = Convert(decoded, assetKey);
                if (nv12 is null) return false;

                // Photos arrive upright. A previous AirPlay Video clip may have
                // left a 90/270° rotation on the presenter; without this reset the
                // first photo of a session renders sideways.
                _onRotation(0);
                _onDimensions(nv12->width, nv12->height);

                // Re-assert on EVERY photo, not just the first. The presenter's
                // active flag is shared with the other two frame producers (the
                // video player and the mirror session), and any of them can
                // clear it: an album containing a video runs POST /play then
                // POST /stop, and the stop calls SetActive(false). Latching on
                // our own _active would leave every later photo silently dropped
                // by PresentFrame while this method still returned true and
                // logged "displayed" — a black screen with a log claiming
                // success, healed only by a TEARDOWN that POST /stop does not
                // imply. SetActive(true) merely sets a flag (the clear-and-
                // present work happens on false), so re-asserting costs nothing;
                // AirPlayVideoPlayer.Play already does the same.
                _active = true;
                _onActive(true);   // shows the window, hides the idle overlay

                _onFrame((IntPtr)nv12);

                LastDisplaySize = (nv12->width, nv12->height);
                long n = Interlocked.Increment(ref _displayed);
                Log.Info("Photo", $"displayed key={assetKey} transition={transition} " +
                                  $"{decoded->width}×{decoded->height} → {nv12->width}×{nv12->height} " +
                                  $"({jpeg.Length} JPEG bytes, photo #{n})");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Photo", $"display key={assetKey} failed ({jpeg.Length}B)", ex);
                return false;
            }
            finally
            {
                if (decoded != null) ffmpeg.av_frame_free(&decoded);
            }
        }
    }

    /// <summary>
    /// The Photos session ended: clear the surface and restore the idle overlay.
    /// Idempotent — several teardown paths can reach it.
    /// </summary>
    public void Stop()
    {
        bool wasActive;
        lock (_sync)
        {
            wasActive = _active;
            _active   = false;
        }

        if (!wasActive) return;
        Log.Info("Photo", $"session ended — clearing the surface ({DisplayedCount} photo(s) displayed)");
        _onActive(false);
    }

    // ── FFmpeg internals ──────────────────────────────────────────────────────

    /// <summary>
    /// Decodes one JPEG to a freshly allocated frame the caller must free.
    ///
    /// The codec context is allocated per photo rather than reused: photos are
    /// rare (a handful per second at most) and each may have different
    /// dimensions, subsampling and colour range, so a clean context costs
    /// microseconds and removes every class of stale-state bug.
    /// </summary>
    private AVFrame* DecodeJpeg(byte[] jpeg, string assetKey)
    {
        AVCodecContext* ctx    = null;
        AVPacket*       packet = null;
        AVFrame*        frame  = null;

        try
        {
            AVCodec* codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_MJPEG);
            if (codec == null)
            {
                Log.Error("Photo", $"decode key={assetKey} failed: no MJPEG decoder in this FFmpeg build " +
                                   "— photos cannot be displayed (check native\\x64\\avcodec-61.dll)");
                return null;
            }

            ctx = ffmpeg.avcodec_alloc_context3(codec);
            if (ctx == null) { Log.Error("Photo", $"decode key={assetKey}: avcodec_alloc_context3 returned null"); return null; }

            int ret = ffmpeg.avcodec_open2(ctx, codec, null);
            if (ret < 0) { Log.Error("Photo", $"decode key={assetKey}: avcodec_open2 failed ({Err(ret)})"); return null; }

            packet = ffmpeg.av_packet_alloc();
            frame  = ffmpeg.av_frame_alloc();
            if (packet == null || frame == null) { Log.Error("Photo", $"decode key={assetKey}: packet/frame alloc failed"); return null; }

            fixed (byte* p = jpeg)
            {
                packet->data = p;
                packet->size = jpeg.Length;

                ret = ffmpeg.avcodec_send_packet(ctx, packet);
                if (ret < 0)
                {
                    Log.Error("Photo", $"decode key={assetKey}: avcodec_send_packet failed ({Err(ret)}) on {jpeg.Length}B");
                    return null;
                }
            }

            ret = ffmpeg.avcodec_receive_frame(ctx, frame);
            if (ret < 0)
            {
                Log.Error("Photo", $"decode key={assetKey}: avcodec_receive_frame failed ({Err(ret)}) — " +
                                   $"body is {jpeg.Length}B but not a decodable JPEG");
                return null;
            }

            if (frame->width <= 0 || frame->height <= 0)
            {
                Log.Error("Photo", $"decode key={assetKey}: decoded frame has no dimensions ({frame->width}×{frame->height})");
                return null;
            }

            AVFrame* result = frame;
            frame = null;      // ownership passes to the caller
            return result;
        }
        finally
        {
            if (frame  != null) ffmpeg.av_frame_free(&frame);
            if (packet != null) ffmpeg.av_packet_free(&packet);
            if (ctx    != null) ffmpeg.avcodec_free_context(&ctx);
        }
    }

    /// <summary>
    /// Scales a decoded JPEG frame into the reusable NV12 frame, downscaling to
    /// the display cap and converting full-range BT.601 → limited-range BT.709 so
    /// the presenter's shader reproduces the original colours. Returns a borrowed
    /// pointer valid until the next call (the presenter copies it synchronously).
    /// </summary>
    private AVFrame* Convert(AVFrame* src, string assetKey)
    {
        int srcW = src->width, srcH = src->height;

        AVPixelFormat srcFmt    = (AVPixelFormat)src->format;
        AVPixelFormat wireFmt   = AsFullRangeFormat(srcFmt, src->color_range);
        bool          fullRange = ReadAsFullRange(wireFmt);

        (int dstW, int dstH) = FitWithin(srcW, srcH);

        if (_sws == null || _swsSrcW != srcW || _swsSrcH != srcH ||
            _swsDstW != dstW || _swsDstH != dstH || _swsSrcFmt != wireFmt)
        {
            ReleaseScaler();

            _sws = ffmpeg.sws_getContext(
                srcW, srcH, wireFmt,
                dstW, dstH, AVPixelFormat.AV_PIX_FMT_NV12,
                ffmpeg.SWS_BILINEAR, null, null, null);
            if (_sws == null)
            {
                Log.Error("Photo", $"convert key={assetKey}: sws_getContext failed " +
                                   $"({srcW}×{srcH} {wireFmt} → {dstW}×{dstH} NV12)");
                return null;
            }

            _nv12 = ffmpeg.av_frame_alloc();
            if (_nv12 == null) { Log.Error("Photo", $"convert key={assetKey}: av_frame_alloc failed"); ReleaseScaler(); return null; }
            _nv12->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
            _nv12->width  = dstW;
            _nv12->height = dstH;
            int bufRet = ffmpeg.av_frame_get_buffer(_nv12, 32);
            if (bufRet < 0)
            {
                Log.Error("Photo", $"convert key={assetKey}: av_frame_get_buffer failed ({Err(bufRet)}) for {dstW}×{dstH} NV12");
                ReleaseScaler();
                return null;
            }

            _swsSrcW = srcW; _swsSrcH = srcH; _swsDstW = dstW; _swsDstH = dstH; _swsSrcFmt = wireFmt;
            Log.Info("Photo", $"scaler {srcW}×{srcH} {srcFmt} " +
                              $"({(fullRange ? "full" : "limited")} range, given to swscale as {wireFmt}) " +
                              $"→ {dstW}×{dstH} NV12 (limited range)");
        }

        // sws_scale's destination takes the 4-element array structs while
        // AVFrame.data/linesize are the 8-element ones — copy the two NV12 planes
        // (Y, interleaved UV) across, exactly as the video path does.
        var dstData = new byte_ptrArray4();
        dstData[0] = _nv12->data[0];
        dstData[1] = _nv12->data[1];
        var dstLines = new int_array4();
        dstLines[0] = _nv12->linesize[0];
        dstLines[1] = _nv12->linesize[1];

        int scaled = ffmpeg.sws_scale(_sws, src->data, src->linesize, 0, srcH, dstData, dstLines);
        if (scaled <= 0)
        {
            Log.Error("Photo", $"convert key={assetKey}: sws_scale produced {scaled} rows");
            return null;
        }

        return _nv12;
    }

    /// <summary>
    /// Returns the pixel format to hand libswscale so that it performs the
    /// full-range → limited-range conversion the presenter's shader expects.
    ///
    /// This looks like a detail and is not. The shader expands luma from
    /// [16,235]; JPEG stores it as [0,255]. Skip the conversion and every photo
    /// shows crushed blacks and blown highlights — the visible half of "photos
    /// didn't work too well".
    ///
    /// The conversion has to be requested through the <b>pixel format</b>, not
    /// through <c>sws_setColorspaceDetails</c>. Measured on the bundled FFmpeg
    /// 7.1 build with a flat JPEG (input luma 0 and 255, want 16 and 235):
    /// <code>
    ///   source format   colorspace call        unscaled     downscaled
    ///   YUVJ420P        none                   16 / 235     16 / 235   ✓
    ///   YUV420P         none                    0 / 255      0 / 255   no conversion
    ///   YUV420P         src 601 → dst 709      37 / 206     71 / 234   wrong, and size-dependent
    ///   YUV420P         src 601 → dst 601       0 / 255     16 / 235   size-dependent
    /// </code>
    /// Only the format carries the range through both of libswscale's paths: an
    /// identical source and destination size selects an unscaled plane-copy
    /// converter that ignores the range flags entirely, so a photo needing no
    /// downscale would come out different from one that did. The YUVJ formats
    /// are deprecated and libswscale logs a notice about them; that is the
    /// price of the only setting that is correct on both paths.
    ///
    /// Residual approximation: JPEG is BT.601 while the shader assumes BT.709,
    /// so hues are very slightly off. libswscale cannot convert a YUV matrix
    /// without a full RGB round trip (the 601→709 rows above are what that
    /// attempt actually produces), and the mirror and video paths already feed
    /// the same shader mixed-matrix content. The range error was the visible
    /// one; this one is not worth a second render path.
    /// </summary>
    private static AVPixelFormat AsFullRangeFormat(AVPixelFormat fmt, AVColorRange range)
    {
        // Already a JPEG-range format: libswscale reads the range from it.
        if (IsJpegFormat(fmt)) return fmt;

        // FFmpeg is migrating off YUVJ*, so a JPEG may now decode to a plain
        // YUV format carrying AVCOL_RANGE_JPEG instead. Map it back to the
        // deprecated twin, because that is what libswscale still acts on.
        if (range != AVColorRange.AVCOL_RANGE_JPEG) return fmt;

        return fmt switch
        {
            AVPixelFormat.AV_PIX_FMT_YUV420P => AVPixelFormat.AV_PIX_FMT_YUVJ420P,
            AVPixelFormat.AV_PIX_FMT_YUV422P => AVPixelFormat.AV_PIX_FMT_YUVJ422P,
            AVPixelFormat.AV_PIX_FMT_YUV440P => AVPixelFormat.AV_PIX_FMT_YUVJ440P,
            AVPixelFormat.AV_PIX_FMT_YUV444P => AVPixelFormat.AV_PIX_FMT_YUVJ444P,
            AVPixelFormat.AV_PIX_FMT_YUV411P => AVPixelFormat.AV_PIX_FMT_YUVJ411P,
            _                                => fmt,
        };
    }

    private static bool IsJpegFormat(AVPixelFormat fmt) => fmt
        is AVPixelFormat.AV_PIX_FMT_YUVJ420P
        or AVPixelFormat.AV_PIX_FMT_YUVJ422P
        or AVPixelFormat.AV_PIX_FMT_YUVJ440P
        or AVPixelFormat.AV_PIX_FMT_YUVJ444P
        or AVPixelFormat.AV_PIX_FMT_YUVJ411P;

    /// <summary>
    /// Grayscale — what a black-and-white (single-component) JPEG decodes to.
    /// These are the formats FFmpeg's MJPEG decoder emits for one component: 8-bit
    /// photos give GRAY8, 12/16-bit ones GRAY16.
    /// </summary>
    private static bool IsGrayFormat(AVPixelFormat fmt) => fmt
        is AVPixelFormat.AV_PIX_FMT_GRAY8
        or AVPixelFormat.AV_PIX_FMT_GRAY16LE
        or AVPixelFormat.AV_PIX_FMT_GRAY16BE;

    /// <summary>
    /// Whether libswscale will read this source format as full-range, and hence
    /// perform the conversion to the presenter's limited range. Its internal
    /// <c>handle_jpeg()</c> answers yes for the YUVJ twins <b>and</b> for every
    /// grayscale format — see <see cref="AsFullRangeFormat"/> for why the second
    /// half of that matters. Used only for the log line; the conversion itself is
    /// driven by the format handed to <c>sws_getContext</c>.
    /// </summary>
    private static bool ReadAsFullRange(AVPixelFormat fmt) => IsJpegFormat(fmt) || IsGrayFormat(fmt);

    /// <summary>
    /// Largest even-sized box with the source aspect ratio that fits the display
    /// cap. Even dimensions matter: NV12 chroma is half-resolution, and the
    /// presenter uploads the UV plane as <c>linesize × height/2</c> rows.
    /// </summary>
    private static (int Width, int Height) FitWithin(int w, int h)
    {
        double scale = Math.Min(1.0, Math.Min(MaxDisplayWidth / (double)w, MaxDisplayHeight / (double)h));
        int dw = Math.Max(2, (int)Math.Round(w * scale));
        int dh = Math.Max(2, (int)Math.Round(h * scale));
        return (dw & ~1, dh & ~1);
    }

    private void ReleaseScaler()
    {
        if (_sws != null)  { ffmpeg.sws_freeContext(_sws); _sws = null; }
        if (_nv12 != null) { AVFrame* f = _nv12; ffmpeg.av_frame_free(&f); _nv12 = null; }
        _swsSrcW = _swsSrcH = _swsDstW = _swsDstH = 0;
        _swsSrcFmt = AVPixelFormat.AV_PIX_FMT_NONE;
    }

    private static string Err(int code)
    {
        byte* buf = stackalloc byte[256];
        ffmpeg.av_strerror(code, buf, 256);
        return System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)buf) ?? code.ToString();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseScaler();
        }
    }
}
