using FFmpeg.AutoGen;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AirPlayReceiver.Media;

/// <summary>
/// AirPlay <b>Video</b> (URL playback) — the mode iOS uses when you AirPlay a
/// video from an app (YouTube, Photos, …) rather than mirroring. iOS hands us a
/// media URL via <c>POST /play</c> and then drives transport with /rate (play/
/// pause), /scrub (seek) and /stop on many short-lived control connections, so
/// this object lives once on <c>AirPlayService</c> and every session delegates
/// to it.
///
/// Unlike mirroring (where iOS encodes H.264 NALs and streams them to us over a
/// TCP data channel), here <b>we</b> are the player: we open the URL with FFmpeg
/// (avformat), decode it, convert each frame to NV12 and hand it to the same
/// <see cref="Rendering.VideoPresenter"/> the mirror path renders to.
///
/// Note on DRM: FairPlay-Streaming protected content (Netflix, Apple TV+, …)
/// can't be opened — those URLs need a license exchange we don't implement. Non-
/// DRM HLS/MP4 (Photos, many web videos) decodes fine, provided the native
/// FFmpeg build has the matching protocol/TLS support (https, hls).
/// </summary>
public sealed class AirPlayVideoPlayer : IDisposable
{
    // ── Render sinks (wired by AirPlayService to the shared presenter) ─────────

    private readonly Action<IntPtr>   _onFrame;       // (AVFrame* as IntPtr) NV12 frame
    private readonly Action<int, int> _onDimensions;  // (width, height) when it changes
    private readonly Action<bool>     _onActive;      // true on play, false on stop
    private readonly Action<int>      _onRotation;    // display rotation in degrees (0/90/180/270)

    // ── Public transport state (read by /playback-info and /scrub) ─────────────

    private readonly object _stateLock = new();
    private string? _url;
    private double  _position;   // seconds, updated as frames are presented
    private double  _duration;   // seconds (0 = unknown / live)
    private double  _rate;       // 1.0 = play, 0.0 = paused

    public string? Url      { get { lock (_stateLock) return _url; } }
    public double  Position { get { lock (_stateLock) return _position; } }
    public double  Duration { get { lock (_stateLock) return _duration; } }
    public double  Rate     { get { lock (_stateLock) return _rate; } }
    public bool    ReadyToPlay { get { lock (_stateLock) return _url is not null; } }

    // ── Playback thread + control signals ──────────────────────────────────────

    private Task?                    _playTask;
    private CancellationTokenSource? _cts;

    // Play/pause: set = playing, reset = paused. The decode loop waits on it.
    private readonly ManualResetEventSlim _playing = new(initialState: true);

    // Pending seek target in seconds, or NaN when none. Applied by the loop.
    private double _seekTarget = double.NaN;

    public AirPlayVideoPlayer(
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

    // ── Transport verbs (called from AirPlaySession) ──────────────────────────

    public void Play(string url, double startPosition)
    {
        Debug.WriteLine($"[Video] PLAY url='{url}' startPosition={startPosition}");

        // A fresh /play replaces whatever was playing.
        StopInternal();

        lock (_stateLock)
        {
            _url      = url;
            _position = startPosition;
            _duration = 0;
            _rate     = 1.0;
        }

        _seekTarget = startPosition > 0 ? startPosition : double.NaN;
        _playing.Set();

        _cts      = new CancellationTokenSource();
        _playTask = Task.Factory.StartNew(
            () => PlaybackLoop(url, _cts.Token),
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        _onActive(true);
    }

    public void SetRate(double rate)
    {
        Debug.WriteLine($"[Video] rate={rate} ({(rate > 0 ? "play" : "pause")})");
        lock (_stateLock) _rate = rate;

        if (rate > 0) _playing.Set();    // resume
        else          _playing.Reset();  // pause
    }

    public void Scrub(double position)
    {
        Debug.WriteLine($"[Video] scrub to {position}s");
        lock (_stateLock) _position = position;
        _seekTarget = position;
        // A scrub while paused should still re-seek; the loop checks the target
        // before waiting on the pause gate.
    }

    public void Stop()
    {
        Debug.WriteLine("[Video] STOP");
        StopInternal();
        _onActive(false);
    }

    private void StopInternal()
    {
        _cts?.Cancel();
        _playing.Set();           // unblock the loop so it can observe cancellation
        try { _playTask?.Wait(2000); } catch { /* loop is tearing down */ }
        _playTask = null;
        _cts?.Dispose();
        _cts = null;

        lock (_stateLock)
        {
            _url      = null;
            _rate     = 0;
            _position = 0;
            _duration = 0;
        }
        _seekTarget = double.NaN;
    }

    // ── Decode / present loop ──────────────────────────────────────────────────

    private unsafe void PlaybackLoop(string url, CancellationToken ct)
    {
        AVFormatContext* fmt      = null;
        AVCodecContext*  codecCtx = null;
        AVFrame*         frame    = null;
        AVFrame*         nv12     = null;
        AVPacket*        packet   = null;
        SwsContext*      sws      = null;
        int              nv12W = 0, nv12H = 0;

        try
        {
            ffmpeg.avformat_network_init();

            fmt = ffmpeg.avformat_alloc_context();
            int ret = ffmpeg.avformat_open_input(&fmt, url, null, null);
            if (ret < 0) { Debug.WriteLine($"[Video] open_input failed ({Err(ret)}). DRM or unsupported protocol?"); return; }

            ret = ffmpeg.avformat_find_stream_info(fmt, null);
            if (ret < 0) { Debug.WriteLine($"[Video] find_stream_info failed ({Err(ret)})"); return; }

            if (fmt->duration > 0)
                lock (_stateLock) _duration = fmt->duration / (double)ffmpeg.AV_TIME_BASE;

            AVCodec* codec = null;
            int vIdx = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
            if (vIdx < 0 || codec == null) { Debug.WriteLine("[Video] no decodable video stream"); return; }

            AVStream* stream   = fmt->streams[vIdx];
            AVRational tb       = stream->time_base;

            codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            ffmpeg.avcodec_parameters_to_context(codecCtx, stream->codecpar);
            codecCtx->thread_count = 0; // let FFmpeg pick (throughput matters more than latency here)
            ret = ffmpeg.avcodec_open2(codecCtx, codec, null);
            if (ret < 0) { Debug.WriteLine($"[Video] avcodec_open2 failed ({Err(ret)})"); return; }

            frame  = ffmpeg.av_frame_alloc();
            nv12   = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();

            // Apply an initial seek (Start-Position) before the read loop.
            ApplyPendingSeek(fmt, codecCtx, vIdx, tb);

            var clock        = Stopwatch.StartNew();
            double baseWall  = -1;   // wall seconds at the baseline frame
            double basePts   = 0;    // media pts (seconds) of the baseline frame

            while (!ct.IsCancellationRequested)
            {
                // Honour pause: block here until /rate resumes (or we're stopped).
                if (!_playing.IsSet)
                {
                    baseWall = -1; // re-anchor pacing on resume so we don't fast-forward
                    _playing.Wait(ct);
                    if (ct.IsCancellationRequested) break;
                }

                // A scrub may have arrived; seek before reading the next packet.
                if (!double.IsNaN(_seekTarget))
                {
                    ApplyPendingSeek(fmt, codecCtx, vIdx, tb);
                    baseWall = -1;
                }

                ret = ffmpeg.av_read_frame(fmt, packet);
                if (ret < 0) { Debug.WriteLine("[Video] end of stream"); break; } // EOF or error

                if (packet->stream_index != vIdx) { ffmpeg.av_packet_unref(packet); continue; }

                ret = ffmpeg.avcodec_send_packet(codecCtx, packet);
                ffmpeg.av_packet_unref(packet);
                if (ret < 0) continue;

                while (!ct.IsCancellationRequested)
                {
                    ret = ffmpeg.avcodec_receive_frame(codecCtx, frame);
                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) break;
                    if (ret < 0) break;

                    int w = frame->width, h = frame->height;
                    if (w <= 0 || h <= 0) { ffmpeg.av_frame_unref(frame); continue; }

                    // (Re)build the NV12 scaler + destination frame on a size change.
                    if (sws == null || nv12W != w || nv12H != h)
                    {
                        if (sws != null) ffmpeg.sws_freeContext(sws);
                        sws = ffmpeg.sws_getContext(
                            w, h, (AVPixelFormat)frame->format,
                            w, h, AVPixelFormat.AV_PIX_FMT_NV12,
                            ffmpeg.SWS_BILINEAR, null, null, null);

                        ffmpeg.av_frame_unref(nv12);
                        nv12->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
                        nv12->width  = w;
                        nv12->height = h;
                        ffmpeg.av_frame_get_buffer(nv12, 32);

                        nv12W = w; nv12H = h;

                        // iPhone clips (Tinder profile videos, Photos, …) are encoded
                        // with landscape pixel dimensions plus a display-matrix rotation
                        // flag. FFmpeg does NOT auto-rotate via the decode API, so honour
                        // the flag here: tell the renderer to rotate, and report the
                        // *displayed* (rotation-applied) dimensions so the UI letterboxes
                        // to the right aspect instead of stretching to landscape.
                        int rotation = GetDisplayRotation(frame);
                        _onRotation(rotation);
                        if (rotation == 90 || rotation == 270)
                            _onDimensions(h, w);
                        else
                            _onDimensions(w, h);
                        Debug.WriteLine($"[Video] stream {w}×{h} rot={rotation}° fmt={(AVPixelFormat)frame->format} → NV12");
                    }

                    // sws_scale's destination takes the 4-element array structs, while
                    // AVFrame.data/linesize are the 8-element ones — copy the two NV12
                    // planes (Y, interleaved UV) across.
                    var dstData = new byte_ptrArray4();
                    dstData[0] = nv12->data[0];
                    dstData[1] = nv12->data[1];
                    var dstLines = new int_array4();
                    dstLines[0] = nv12->linesize[0];
                    dstLines[1] = nv12->linesize[1];
                    ffmpeg.sws_scale(sws, frame->data, frame->linesize, 0, h, dstData, dstLines);

                    // Pace to the frame's presentation timestamp.
                    long bestPts = frame->best_effort_timestamp;
                    double pts = bestPts == ffmpeg.AV_NOPTS_VALUE ? 0 : bestPts * ffmpeg.av_q2d(tb);
                    double now = clock.Elapsed.TotalSeconds;
                    if (baseWall < 0) { baseWall = now; basePts = pts; }
                    else
                    {
                        double wait = (pts - basePts) - (now - baseWall);
                        if (wait > 0)
                        {
                            if (wait > 1.0) wait = 1.0; // cap so a bad pts can't stall us
                            if (ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(wait))) break;
                        }
                    }

                    lock (_stateLock) _position = pts;
                    _onFrame((IntPtr)nv12);

                    ffmpeg.av_frame_unref(frame);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Debug.WriteLine($"[Video] playback error: {ex.Message}"); }
        finally
        {
            if (sws != null)      ffmpeg.sws_freeContext(sws);
            if (packet != null)   ffmpeg.av_packet_free(&packet);
            if (frame != null)    ffmpeg.av_frame_free(&frame);
            if (nv12 != null)     ffmpeg.av_frame_free(&nv12);
            if (codecCtx != null) ffmpeg.avcodec_free_context(&codecCtx);
            if (fmt != null)      ffmpeg.avformat_close_input(&fmt);
            Debug.WriteLine("[Video] playback loop ended");
        }
    }

    /// <summary>Seeks to the pending <see cref="_seekTarget"/> (if any) and flushes the decoder.</summary>
    private unsafe void ApplyPendingSeek(AVFormatContext* fmt, AVCodecContext* codecCtx, int vIdx, AVRational tb)
    {
        double target = _seekTarget;
        if (double.IsNaN(target)) return;
        _seekTarget = double.NaN;

        long ts = (long)(target / ffmpeg.av_q2d(tb));
        int ret = ffmpeg.av_seek_frame(fmt, vIdx, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
        if (ret < 0) { Debug.WriteLine($"[Video] seek to {target}s failed ({Err(ret)})"); return; }

        ffmpeg.avcodec_flush_buffers(codecCtx);
        lock (_stateLock) _position = target;
    }

    /// <summary>
    /// Reads the stream's display-matrix rotation from a decoded frame's side data
    /// and returns the clockwise angle (0/90/180/270) to apply for upright display.
    /// Follows FFmpeg's own autorotation convention (angle = -av_display_rotation_get).
    /// Returns 0 when no rotation metadata is present.
    /// </summary>
    private static unsafe int GetDisplayRotation(AVFrame* frame)
    {
        AVFrameSideData* sd = ffmpeg.av_frame_get_side_data(
            frame, AVFrameSideDataType.AV_FRAME_DATA_DISPLAYMATRIX);
        if (sd == null || sd->data == null) return 0;

        // The display matrix is 9 packed int32 values (a 3x3 affine transform).
        var matrix = new int_array9();
        int* m = (int*)sd->data;
        for (uint k = 0; k < 9; k++) matrix[k] = m[k];

        double angle = ffmpeg.av_display_rotation_get(matrix);
        if (double.IsNaN(angle)) return 0;

        int rot = ((int)Math.Round(-angle) % 360 + 360) % 360;
        return (rot + 45) / 90 * 90 % 360;   // snap to the nearest quarter turn
    }

    private static unsafe string Err(int code)
    {
        byte* buf = stackalloc byte[256];
        ffmpeg.av_strerror(code, buf, 256);
        return System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)buf) ?? code.ToString();
    }

    public void Dispose()
    {
        StopInternal();
        _playing.Dispose();
    }
}
