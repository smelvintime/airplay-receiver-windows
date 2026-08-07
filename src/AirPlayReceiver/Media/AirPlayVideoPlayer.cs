using AirPlayReceiver.Services;
using FFmpeg.AutoGen;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AirPlayReceiver.Media;

/// <summary>
/// Lifecycle of one AirPlay Video item. <see cref="Protocol.AirPlaySession"/> maps
/// this onto the /playback-info replies iOS expects: <see cref="Loading"/> answers
/// with an empty 200 ("still buffering"), <see cref="Playing"/> with the info
/// plist, and <see cref="Ended"/>/<see cref="Failed"/> with an empty 200 plus a
/// close — the only way UxPlay/Apple receivers say "this item is over".
/// </summary>
public enum VideoPlaybackState
{
    /// <summary>Nothing has been requested (or the last item was stopped).</summary>
    Idle,
    /// <summary>A /play was accepted; the format isn't open or no frame has decoded yet.</summary>
    Loading,
    /// <summary>Format open, frames decoding — the phone can show a live scrubber.</summary>
    Playing,
    /// <summary>Reached a genuine end of stream.</summary>
    Ended,
    /// <summary>Open or decode failed. The phone must be told, not left spinning on 0:00.</summary>
    Failed,
}

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
/// (avformat), decode <i>both</i> the video and the audio stream, convert each
/// video frame to NV12 for the same <see cref="Rendering.VideoPresenter"/> the
/// mirror path renders to, and push the decoded PCM through the same
/// <see cref="AudioOutput"/> (WASAPI) the mirror path plays through.
///
/// A/V sync: when the media has a playable audio stream the <b>audio clock is the
/// master</b> — audio plays out at its own natural rate and video frames are
/// scheduled against it (standard practice: the ear notices a 20 ms audio glitch,
/// the eye doesn't notice a dropped frame). With no audio we fall back to pacing
/// video against a wall clock anchored on the first frame's PTS.
///
/// Note on DRM: FairPlay-Streaming protected content (Netflix, Apple TV+, …)
/// can't be opened — those URLs need a license exchange we don't implement. Non-
/// DRM HLS/MP4 (Photos, many web videos) decodes fine, provided the native
/// FFmpeg build has the matching protocol/TLS support (https, hls).
/// </summary>
public sealed class AirPlayVideoPlayer : IDisposable
{
    // ── Tunables ───────────────────────────────────────────────────────────────

    /// <summary>
    /// UA sent on every HTTP request. FFmpeg's default ("Lavf/61.x") is rejected
    /// outright by several CDNs iOS hands us — googlevideo answers 403 — so we
    /// present ourselves as the AppleTV media stack, which is what the sender
    /// expects to be fetching its URL.
    /// </summary>
    private const string HttpUserAgent =
        "AppleCoreMedia/1.0.0.21A360 (Apple TV; U; CPU OS 17_0 like Mac OS X)";

    /// <summary>
    /// The only protocols FFmpeg may use for this URL and for everything it
    /// reaches from it.
    ///
    /// Without a whitelist FFmpeg allows every protocol it was built with, and the
    /// content behind the first URL decides which one gets used next: a playlist
    /// can name <c>file://</c>, <c>concat:</c> or <c>subfile:</c> and turn the
    /// demuxer into a file reader — the documented FFmpeg SSRF/LFI class. The
    /// first URL here comes from an unauthenticated LAN sender, so this is not a
    /// theoretical path.
    ///
    /// This is the narrowest set that still plays HLS: http/https for the
    /// manifests and segments, tcp/tls underneath them, and crypto for the AES-128
    /// segments an <c>#EXT-X-KEY</c> playlist uses. <c>file</c> is deliberately
    /// absent — we never play local media.
    ///
    /// Note what this deliberately does <b>not</b> do: refuse URLs whose host is
    /// loopback, private or link-local. That check cannot be applied here, because
    /// the receiver's own playlist server <i>is</i> loopback
    /// (<see cref="Services.HlsServer"/> serves every FCUP item from
    /// 127.0.0.1) and the sender itself is by definition on a private LAN. The
    /// protocol whitelist is what closes the file/LFI half of the class; the
    /// remaining "fetch a LAN URL" half is indistinguishable from the app's normal
    /// job.
    /// </summary>
    private const string ProtocolWhitelist = "http,https,tcp,tls,crypto";

    /// <summary>Per-read/connect timeout handed to the protocol layer (microseconds).</summary>
    private const string IoTimeoutMicros = "15000000";      // 15 s

    /// <summary>Hard ceiling on avformat_open_input + find_stream_info (interrupt callback).</summary>
    private const double OpenDeadlineSeconds = 25.0;

    /// <summary>How long StopInternal waits for the decode thread before giving up on it.</summary>
    private const int StopWaitMs = 5000;

    /// <summary>Consecutive transient av_read_frame errors tolerated before failing.</summary>
    private const int MaxReadRetries = 6;

    /// <summary>Never sleep the reader while the output holds less than this much audio.</summary>
    private const int AudioLowWaterMs = 120;

    /// <summary>A frame later than this against the master clock is dropped instead of shown.</summary>
    private const double LateDropSeconds = 0.100;

    /// <summary>Cap on back-to-back drops, so a bad clock can't blank the screen.</summary>
    private const int MaxConsecutiveDrops = 5;

    /// <summary>Wall-clock drift past this re-anchors the schedule instead of chasing it forever.</summary>
    private const double ReanchorSeconds = 1.0;

    /// <summary>Longest single sleep while waiting for the master clock (keeps cancellation snappy).</summary>
    private const double MaxClockSleepSeconds = 0.25;

    /// <summary>Interval of the one-line sync/health log line.</summary>
    private const double StatsIntervalSeconds = 5.0;

    /// <summary>
    /// How far before a seek target we are willing to decode-and-discard to land on
    /// the exact spot. av_seek_frame lands on the preceding keyframe, which on a
    /// long-GOP encode can be many seconds early; past this we accept where we are
    /// rather than burn CPU chasing it.
    /// </summary>
    private const double MaxSeekSkipSeconds = 30.0;

    // ── Render sinks (wired by AirPlayService to the shared presenter) ─────────

    private readonly Action<IntPtr>   _onFrame;       // (AVFrame* as IntPtr) NV12 frame
    private readonly Action<int, int> _onDimensions;  // (width, height) when it changes
    private readonly Action<bool>     _onActive;      // true on play, false on stop/end/failure
    private readonly Action<int>      _onRotation;    // display rotation in degrees (0/90/180/270)
    private readonly Action<string>?  _onNotice;      // user-visible one-line failure notice

    // ── Public transport state (read by /playback-info and /scrub) ─────────────

    private readonly object _stateLock = new();
    private string?            _url;
    private string?            _itemUuid;   // "uuid" from /play — identifies the item
    private double             _position;   // seconds, updated as frames are presented
    private double             _duration;   // seconds (0 = unknown / live)
    private double             _rate;       // 1.0 = play, 0.0 = paused
    private bool               _canSeek;    // the demuxer reported a seekable input
    private VideoPlaybackState _state = VideoPlaybackState.Idle;

    public string?            Url      { get { lock (_stateLock) return _url; } }
    public string?            ItemUuid { get { lock (_stateLock) return _itemUuid; } }
    public double             Position { get { lock (_stateLock) return _position; } }
    public double             Duration { get { lock (_stateLock) return _duration; } }
    public double             Rate     { get { lock (_stateLock) return _rate; } }
    public bool               CanSeek  { get { lock (_stateLock) return _canSeek; } }
    public VideoPlaybackState State    { get { lock (_stateLock) return _state; } }

    /// <summary>
    /// True once the format is open and the first frame has decoded. Note that
    /// /playback-info still reports <c>readyToPlay = true</c> whenever it answers
    /// with a body at all (that is what real receivers do); this flag decides
    /// whether it answers with a body or with the empty "still buffering" 200.
    /// </summary>
    public bool ReadyToPlay { get { lock (_stateLock) return _state == VideoPlaybackState.Playing; } }

    /// <summary>
    /// Extra request headers (Cookie / Referer / …) carried on the /play request,
    /// one per line. Handed to FFmpeg as the "headers" option so session-scoped
    /// CDN URLs don't 403. Set before <see cref="Play"/>; null when there are none.
    /// </summary>
    public string? PlayRequestHeaders { get; set; }

    /// <summary>True while an item is loading or playing — i.e. the decode thread should be alive.</summary>
    public bool IsActive
    {
        get
        {
            lock (_stateLock)
                return _state is VideoPlaybackState.Loading or VideoPlaybackState.Playing;
        }
    }

    /// <summary>True once the item is over (cleanly or not) and the phone should be told so.</summary>
    public bool IsFinished
    {
        get
        {
            lock (_stateLock)
                return _state is VideoPlaybackState.Ended or VideoPlaybackState.Failed;
        }
    }

    // ── Playback thread + control signals ──────────────────────────────────────

    private Task?                    _playTask;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Bumped by every Stop/Play. The decode loop captures it and drops its own
    /// state callbacks once it is stale, so a dying loop can never deactivate the
    /// presenter out from under the item that replaced it.
    /// </summary>
    private long _generation;

    // Play/pause: set = playing, reset = paused. The decode loop waits on it.
    private readonly ManualResetEventSlim _playing = new(initialState: true);

    // Pending seek target in seconds, or NaN when none. Applied by the loop.
    private double _seekTarget = double.NaN;

    // Legacy Start-Position (0..1 fraction of duration): only resolvable once the
    // demuxer has told us the duration, so the loop applies it after open.
    private double _startFraction;

    // Audio sink for the current item. Owned by the decode loop; SetVolume (called
    // from an RTSP thread) reaches it through _audioLock.
    private readonly object   _audioLock = new();
    private AudioOutput?      _audioOut;
    private float             _volume = 1f;

    public AirPlayVideoPlayer(
        Action<IntPtr>   onFrame,
        Action<int, int> onDimensions,
        Action<bool>     onActive,
        Action<int>      onRotation,
        Action<string>?  onNotice = null)
    {
        _onFrame      = onFrame;
        _onDimensions = onDimensions;
        _onActive     = onActive;
        _onRotation   = onRotation;
        _onNotice     = onNotice;
    }

    // ── Transport verbs (called from AirPlaySession) ──────────────────────────

    /// <summary>
    /// Starts playback of <paramref name="url"/>.
    /// </summary>
    /// <param name="startSeconds">
    /// Absolute start offset in seconds (iOS "Start-Position-Seconds"). Wins when > 0.
    /// </param>
    /// <param name="startFraction">
    /// Legacy "Start-Position": a 0..1 fraction of the duration. Only used when
    /// <paramref name="startSeconds"/> is 0, and only once the duration is known.
    /// </param>
    /// <param name="itemUuid">The item's "uuid" key, echoed in diagnostics.</param>
    public void Play(string url, double startSeconds, double startFraction = 0, string? itemUuid = null)
    {
        Log.Info("Video", $"PLAY url='{url}' startSeconds={F(startSeconds)} " +
                          $"startFraction={F(startFraction)} uuid={itemUuid ?? "(none)"}");

        // A fresh /play replaces whatever was playing.
        StopInternal();

        long gen = Volatile.Read(ref _generation);

        lock (_stateLock)
        {
            _url      = url;
            _itemUuid = itemUuid;
            _position = startSeconds;
            _duration = 0;
            _canSeek  = false;
            _rate     = 1.0;
            _state    = VideoPlaybackState.Loading;
        }

        _seekTarget    = startSeconds > 0 ? startSeconds : double.NaN;
        _startFraction = startSeconds > 0 ? 0 : startFraction;
        _playing.Set();

        var cts = new CancellationTokenSource();
        _cts      = cts;
        _playTask = Task.Factory.StartNew(
            () => PlaybackLoop(url, gen, cts.Token),
            cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        _onActive(true);
    }

    public void SetRate(double rate)
    {
        Log.Info("Video", $"rate={F(rate)} ({(rate > 0 ? "play" : "pause")})");
        lock (_stateLock) _rate = rate;

        if (rate > 0) _playing.Set();    // resume
        else          _playing.Reset();  // pause
    }

    public void Scrub(double position)
    {
        Log.Info("Video", $"scrub to {F(position)}s");
        lock (_stateLock) _position = position;
        _seekTarget = position;
        // A scrub while paused should still re-seek; the loop checks the target
        // before waiting on the pause gate.
    }

    /// <summary>Sets playback gain (0.0–1.0) for the video route's audio output.</summary>
    public void SetVolume(float linear)
    {
        lock (_audioLock)
        {
            _volume = Math.Clamp(linear, 0f, 1f);
            _audioOut?.SetVolume(_volume);
        }
    }

    public void Stop()
    {
        Log.Info("Video", "STOP");
        StopInternal();
        _onActive(false);
    }

    private void StopInternal()
    {
        // Invalidate the outgoing loop's callbacks before we stop waiting on it:
        // whatever it reports from here on refers to an item nobody is watching.
        Interlocked.Increment(ref _generation);

        CancellationTokenSource? cts  = _cts;
        Task?                    task = _playTask;

        cts?.Cancel();            // also trips the AVIO interrupt callback, so a
                                  // blocking network read aborts instead of hanging
        _playing.Set();           // unblock the loop so it can observe cancellation

        if (task is not null)
        {
            bool exited;
            try { exited = task.Wait(StopWaitMs); }
            catch { exited = true; /* loop faulted — it still ran its finally */ }

            if (!exited)
            {
                // Do NOT dispose the CTS here: the loop is still polling its token
                // (including from the AVIO interrupt callback, and via WaitHandle),
                // and a disposed source would throw inside FFmpeg's read path. Hand
                // the disposal to the loop's own completion instead — that also
                // guarantees the AVFormatContext is freed by its finally block
                // rather than leaked, which is what the old fire-and-forget
                // `_playTask?.Wait(2000); _cts?.Dispose();` did on every timeout.
                Log.Warn("Video", $"decode thread still running after {StopWaitMs}ms — " +
                                  "detaching; it will free its FFmpeg contexts when the read unblocks");
                task.ContinueWith(_ => cts?.Dispose(), TaskScheduler.Default);
            }
            else
            {
                cts?.Dispose();
            }
        }
        else
        {
            cts?.Dispose();
        }

        _playTask = null;
        _cts      = null;

        lock (_stateLock)
        {
            _url      = null;
            _itemUuid = null;
            _rate     = 0;
            _position = 0;
            _duration = 0;
            _canSeek  = false;
            _state    = VideoPlaybackState.Idle;
        }
        _seekTarget    = double.NaN;
        _startFraction = 0;
    }

    // ── Terminal state transitions ─────────────────────────────────────────────

    /// <summary>
    /// Marks the item as unplayable. Every failure path routes through here: before
    /// this existed each one just logged and returned, leaving /playback-info
    /// reporting readyToPlay=true and rate=1.0 forever, so the phone sat on a
    /// frozen 0:00 with no way to learn the receiver had died.
    /// </summary>
    private void Fail(long gen, string reason)
    {
        if (Volatile.Read(ref _generation) != gen) { Log.Info("Video", $"(stale) failure ignored: {reason}"); return; }

        lock (_stateLock)
        {
            _url   = null;
            _rate  = 0;
            _state = VideoPlaybackState.Failed;
        }

        Log.Error("Video", $"playback FAILED — {reason}");
        _onNotice?.Invoke($"AirPlay Video failed: {reason}");
        _onActive(false);
    }

    /// <summary>Marks a genuine end of stream (not an error) and releases the surface.</summary>
    private void Finish(long gen, string reason)
    {
        if (Volatile.Read(ref _generation) != gen) return;

        lock (_stateLock)
        {
            _url   = null;
            _rate  = 0;
            _state = VideoPlaybackState.Ended;
        }

        Log.Info("Video", $"playback ended — {reason}");
        _onActive(false);
    }

    // ── AVIO interrupt callback ────────────────────────────────────────────────

    /// <summary>
    /// State the interrupt callback polls. FFmpeg calls it from inside blocking
    /// reads, which is the only way cancellation (or a hard open deadline) can
    /// unstick a dead host — without it avformat_open_input can block forever and
    /// Stop() leaks the whole decode thread plus its AVFormatContext.
    /// </summary>
    private sealed class IoGuard
    {
        public CancellationToken Ct;
        public long              DeadlineTicks;   // Stopwatch timestamp; 0 = no deadline
        public volatile bool     DeadlineHit;
    }

    // Rooted for the process lifetime: FFmpeg keeps the raw function pointer, so
    // the delegate must never be collected or moved.
    private static readonly AVIOInterruptCB_callback InterruptDelegate = MakeInterruptDelegate();

    private static unsafe AVIOInterruptCB_callback MakeInterruptDelegate() => InterruptCallback;

    private static unsafe int InterruptCallback(void* opaque)
    {
        if (opaque == null) return 0;
        try
        {
            if (GCHandle.FromIntPtr((IntPtr)opaque).Target is not IoGuard guard) return 0;
            if (guard.Ct.IsCancellationRequested) return 1;
            if (guard.DeadlineTicks != 0 && Stopwatch.GetTimestamp() > guard.DeadlineTicks)
            {
                guard.DeadlineHit = true;
                return 1;
            }
        }
        catch { /* never let an exception cross back into native code */ }
        return 0;
    }

    // ── Decode / present loop ──────────────────────────────────────────────────

    private unsafe void PlaybackLoop(string url, long gen, CancellationToken ct)
    {
        AVFormatContext* fmt      = null;
        AVCodecContext*  codecCtx = null;
        AVCodecContext*  audioCtx = null;
        AVFrame*         frame    = null;
        AVFrame*         aFrame   = null;
        AVFrame*         nv12     = null;
        AVPacket*        packet   = null;
        SwsContext*      sws      = null;
        AVDictionary*    opts     = null;
        int              nv12W = 0, nv12H = 0;

        var      guard  = new IoGuard { Ct = ct };
        GCHandle handle = GCHandle.Alloc(guard);

        AudioOutput?    audioOut  = null;
        AudioResampler? resampler = null;

        try
        {
            ffmpeg.avformat_network_init();

            // ── Open options ──────────────────────────────────────────────────
            // Previously this call passed a null AVDictionary**, which meant the
            // default Lavf user-agent (403 from several CDNs), no timeouts at all
            // (a dead host blocked the thread forever) and no HLS reconnect.
            // First, and never conditional: everything below only matters if the
            // demuxer is confined to protocols we chose (see ProtocolWhitelist).
            ffmpeg.av_dict_set(&opts, "protocol_whitelist", ProtocolWhitelist, 0);
            ffmpeg.av_dict_set(&opts, "user_agent",       HttpUserAgent, 0);
            ffmpeg.av_dict_set(&opts, "rw_timeout",       IoTimeoutMicros, 0);  // AVIO read/write
            ffmpeg.av_dict_set(&opts, "timeout",          IoTimeoutMicros, 0);  // tcp/http connect
            ffmpeg.av_dict_set(&opts, "reconnect",           "1", 0);
            ffmpeg.av_dict_set(&opts, "reconnect_streamed",  "1", 0);
            ffmpeg.av_dict_set(&opts, "reconnect_on_network_error", "1", 0);
            ffmpeg.av_dict_set(&opts, "reconnect_delay_max", "5", 0);
            // The hls demuxer copies user_agent/headers/cookies/referer/rw_timeout
            // down to its segment requests, so setting them here covers playlists too.
            string? extraHeaders = BuildExtraHeaders(url);
            if (extraHeaders is not null)
            {
                ffmpeg.av_dict_set(&opts, "headers", extraHeaders, 0);
                Log.Info("Video", $"extra request headers: {extraHeaders.Replace("\r\n", " | ")}");
            }

            fmt = ffmpeg.avformat_alloc_context();

            // Poll cancellation from inside blocking reads, and give open a hard
            // ceiling so a black-hole host fails fast and visibly.
            fmt->interrupt_callback.callback = InterruptDelegate;
            fmt->interrupt_callback.opaque   = (void*)GCHandle.ToIntPtr(handle);
            guard.DeadlineTicks = Stopwatch.GetTimestamp() +
                                  (long)(OpenDeadlineSeconds * Stopwatch.Frequency);

            var openClock = Stopwatch.StartNew();
            int ret = ffmpeg.avformat_open_input(&fmt, url, null, &opts);
            if (ret < 0)
            {
                if (ct.IsCancellationRequested) return;
                Fail(gen, guard.DeadlineHit
                    ? $"no response within {F(OpenDeadlineSeconds)}s ({url})"
                    : $"could not open URL ({Err(ret)}) — DRM, dead host or unsupported protocol");
                return;
            }
            Log.Info("Video", $"open_input OK in {F(openClock.Elapsed.TotalSeconds)}s " +
                              $"(format={PtrToString(fmt->iformat->name)})");

            ret = ffmpeg.avformat_find_stream_info(fmt, null);
            if (ret < 0)
            {
                if (ct.IsCancellationRequested) return;
                Fail(gen, $"stream info unreadable ({Err(ret)})");
                return;
            }
            guard.DeadlineTicks = 0;   // open is done; from here rw_timeout governs

            double duration = fmt->duration > 0 ? fmt->duration / (double)ffmpeg.AV_TIME_BASE : 0;
            // AVFMTCTX_UNSEEKABLE covers live HLS; a zero duration means we have
            // nothing sensible to report as a seekable range either.
            bool canSeek = duration > 0 && (fmt->ctx_flags & ffmpeg.AVFMTCTX_UNSEEKABLE) == 0;
            lock (_stateLock) { _duration = duration; _canSeek = canSeek; }

            // ── Video stream ──────────────────────────────────────────────────
            AVCodec* codec = null;
            int vIdx = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
            if (vIdx < 0 || codec == null)
            {
                Fail(gen, "no decodable video stream (an image or audio-only URL?)");
                return;
            }

            AVStream*  stream = fmt->streams[vIdx];
            AVRational tb     = stream->time_base;

            codecCtx = ffmpeg.avcodec_alloc_context3(codec);
            ffmpeg.avcodec_parameters_to_context(codecCtx, stream->codecpar);
            codecCtx->pkt_timebase = tb;
            codecCtx->thread_count = 0; // let FFmpeg pick (throughput matters more than latency here)
            ret = ffmpeg.avcodec_open2(codecCtx, codec, null);
            if (ret < 0)
            {
                Fail(gen, $"video decoder unavailable ({PtrToString(codec->name)}: {Err(ret)})");
                return;
            }

            // ── Audio stream ──────────────────────────────────────────────────
            // The whole point of this block: before it existed the loop opened only
            // AVMEDIA_TYPE_VIDEO and dropped every non-video packet, so AirPlay
            // Video played completely SILENTLY — which is most of what "Photos
            // didn't work too well" actually was.
            AVCodec*   audioCodec = null;
            int        aIdx       = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, vIdx, &audioCodec, 0);
            AVRational aTb        = default;
            int        aRate      = 0;

            if (aIdx >= 0 && audioCodec != null)
            {
                try
                {
                    AVStream* aStream = fmt->streams[aIdx];
                    aTb = aStream->time_base;

                    audioCtx = ffmpeg.avcodec_alloc_context3(audioCodec);
                    ffmpeg.avcodec_parameters_to_context(audioCtx, aStream->codecpar);
                    audioCtx->pkt_timebase = aTb;
                    int aret = ffmpeg.avcodec_open2(audioCtx, audioCodec, null);
                    if (aret < 0) throw new InvalidOperationException($"avcodec_open2 failed ({Err(aret)})");

                    aRate = audioCtx->sample_rate;

                    // Same construction AudioStreamReceiver uses for mirroring audio:
                    // WASAPI at the device's own mix rate, with FFmpeg swresample in
                    // front of it when the media rate differs (Windows' built-in
                    // resampler adds audible HF aliasing).
                    audioOut = new AudioOutput();
                    audioOut.Start();
                    if (aRate != audioOut.DeviceSampleRate)
                    {
                        resampler = new AudioResampler(aRate, audioOut.DeviceSampleRate);
                        resampler.Initialize();
                    }

                    lock (_audioLock) { _audioOut = audioOut; audioOut.SetVolume(_volume); }

                    aFrame = ffmpeg.av_frame_alloc();
                    Log.Info("Video", $"audio stream #{aIdx}: {PtrToString(audioCodec->name)} " +
                                      $"{aRate}Hz {audioCtx->ch_layout.nb_channels}ch " +
                                      $"fmt={(AVSampleFormat)audioCtx->sample_fmt} → WASAPI {audioOut.DeviceSampleRate}Hz " +
                                      $"(resample={(resampler is not null ? "yes" : "no")})");
                }
                catch (Exception ex)
                {
                    // No audio device, missing swresample, exotic codec — video must
                    // still play, so degrade to the wall clock rather than fail.
                    Log.Warn("Video", $"audio unavailable, playing video only: {ex.Message}");
                    lock (_audioLock) _audioOut = null;
                    resampler?.Dispose(); resampler = null;
                    audioOut?.Dispose();  audioOut  = null;
                    if (audioCtx != null) ffmpeg.avcodec_free_context(&audioCtx);
                    if (aFrame  != null) ffmpeg.av_frame_free(&aFrame);
                    aIdx = -1;
                }
            }
            else
            {
                Log.Info("Video", "no audio stream in this media — pacing video on the wall clock");
            }

            bool audioMaster = aIdx >= 0 && audioOut is not null;
            Log.Info("Video", $"duration={F(duration)}s seekable={canSeek} clock={(audioMaster ? "audio" : "wall")}");

            frame  = ffmpeg.av_frame_alloc();
            nv12   = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();

            // Legacy Start-Position is a fraction of the duration — only resolvable
            // now that the demuxer has told us what the duration is.
            if (_startFraction > 0)
            {
                double resolved = duration > 0 ? _startFraction * duration : 0;
                Log.Info("Video", $"legacy Start-Position {F(_startFraction)} × {F(duration)}s → seek {F(resolved)}s");
                if (resolved > 0) _seekTarget = resolved;
                _startFraction = 0;
            }

            // Apply an initial seek (Start-Position) before the read loop.
            // skipUntil is the exact media time we must decode forward to: seeking
            // lands on the preceding keyframe, so without this a "resume at 218s"
            // (or a scrub to 30s) silently starts wherever the last keyframe was —
            // measured at 6.5s early on a long-GOP MP4.
            double skipUntil    = ApplyPendingSeek(fmt, codecCtx, audioCtx);
            long   skippedSince = 0;

            // ── Clock state ───────────────────────────────────────────────────
            var    clock     = Stopwatch.StartNew();
            double baseWall  = -1;   // wall seconds at the baseline frame
            double basePts   = 0;    // media pts (seconds) of the baseline frame
            double audioEnd  = double.NaN;  // media time at the end of the last enqueued PCM
            double presentCost = 0;  // EMA of PresentFrame's cost (it blocks on vsync)

            long   presented = 0, dropped = 0, decoded = 0, retries = 0;
            int    consecutiveDrops = 0, consecutiveErrors = 0;
            double nextStats = StatsIntervalSeconds;
            bool   firstFrame = true;

            while (!ct.IsCancellationRequested)
            {
                // Honour pause: block here until /rate resumes (or we're stopped).
                if (!_playing.IsSet)
                {
                    baseWall = -1; // re-anchor pacing on resume so we don't fast-forward
                    // Pause has to silence the output too: the WASAPI queue holds
                    // ~0.6 s of PCM that would otherwise keep playing after the user
                    // hits pause. The audio clock re-anchors from the next packet.
                    audioOut?.Clear();
                    audioEnd = double.NaN;
                    _playing.Wait(ct);
                    if (ct.IsCancellationRequested) break;
                }

                // A scrub may have arrived; seek before reading the next packet.
                if (!double.IsNaN(_seekTarget))
                {
                    skipUntil    = ApplyPendingSeek(fmt, codecCtx, audioCtx);
                    skippedSince = 0;
                    baseWall     = -1;
                    audioEnd     = double.NaN;
                    // Drop the queued PCM too, or the pre-seek audio keeps playing
                    // for up to a second and drags the audio clock backwards.
                    audioOut?.Clear();
                }

                ret = ffmpeg.av_read_frame(fmt, packet);
                if (ret < 0)
                {
                    // Previously ANY negative return was logged as "end of stream"
                    // and broke the loop — one 404 on a single HLS segment killed
                    // playback permanently. Separate the three cases.
                    if (ct.IsCancellationRequested) break;

                    if (ret == ffmpeg.AVERROR_EOF)
                    {
                        DrainDecoder(codecCtx, frame, ct);
                        Finish(gen, $"end of stream after {presented} frames");
                        break;
                    }

                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    {
                        // Live source with nothing buffered yet — not an error.
                        if (SleepCancelable(0.02, ct)) break;
                        continue;
                    }

                    if (ret == ffmpeg.AVERROR_EXIT || guard.DeadlineHit) break;   // our own interrupt

                    if (++consecutiveErrors > MaxReadRetries)
                    {
                        Fail(gen, $"read failed {consecutiveErrors}× in a row ({Err(ret)}) after {presented} frames");
                        break;
                    }

                    double backoff = Math.Min(0.1 * Math.Pow(2, consecutiveErrors - 1), 2.0);
                    retries++;
                    Log.Warn("Video", $"read error ({Err(ret)}), retry {consecutiveErrors}/{MaxReadRetries} in {F(backoff)}s");
                    if (SleepCancelable(backoff, ct)) break;
                    continue;
                }
                consecutiveErrors = 0;

                // ── Audio packets ─────────────────────────────────────────────
                if (aIdx >= 0 && packet->stream_index == aIdx)
                {
                    ret = ffmpeg.avcodec_send_packet(audioCtx, packet);
                    ffmpeg.av_packet_unref(packet);
                    if (ret < 0) continue;

                    while (true)
                    {
                        ret = ffmpeg.avcodec_receive_frame(audioCtx, aFrame);
                        if (ret < 0) break;

                        long aPts = aFrame->best_effort_timestamp;
                        double startPts = aPts == ffmpeg.AV_NOPTS_VALUE
                            ? (double.IsNaN(audioEnd) ? 0 : audioEnd)
                            : aPts * ffmpeg.av_q2d(aTb);

                        byte[] pcm = ToStereoFloat(aFrame);
                        ffmpeg.av_frame_unref(aFrame);
                        if (pcm.Length == 0) continue;

                        int samples = pcm.Length / (2 * sizeof(float));
                        audioEnd = startPts + (aRate > 0 ? samples / (double)aRate : 0);

                        // Everything between the keyframe we landed on and the seek
                        // target is decoded but not played, so the user hears the
                        // moment they asked for rather than a few seconds before it.
                        if (!double.IsNaN(skipUntil) && audioEnd < skipUntil) continue;

                        byte[] toPlay = resampler is not null ? resampler.Resample(pcm) : pcm;
                        if (toPlay.Length > 0) audioOut!.Enqueue(toPlay);
                    }
                    continue;
                }

                if (packet->stream_index != vIdx) { ffmpeg.av_packet_unref(packet); continue; }

                // ── Video packets ─────────────────────────────────────────────
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
                    decoded++;

                    // Post-seek catch-up: decode-and-discard until we reach the frame
                    // the user actually asked for. Bounded, so a target beyond the
                    // nearest keyframe reach (or past EOF) can't spin here.
                    if (!double.IsNaN(skipUntil))
                    {
                        long   skipTs  = frame->best_effort_timestamp;
                        double skipPts = skipTs == ffmpeg.AV_NOPTS_VALUE ? 0 : skipTs * ffmpeg.av_q2d(tb);
                        if (skipPts + 0.001 < skipUntil)
                        {
                            if (skipUntil - skipPts <= MaxSeekSkipSeconds)
                            {
                                skippedSince++;
                                ffmpeg.av_frame_unref(frame);
                                continue;
                            }
                            Log.Warn("Video", $"seek landed {F(skipUntil - skipPts)}s before the target — " +
                                              $"more than the {F(MaxSeekSkipSeconds)}s catch-up budget, playing from here");
                        }
                        else if (skippedSince > 0)
                        {
                            Log.Info("Video", $"seek catch-up: skipped {skippedSince} frames to land on {F(skipPts)}s");
                        }
                        skipUntil    = double.NaN;
                        skippedSince = 0;
                        baseWall     = -1;   // the schedule starts here, not at the keyframe
                    }

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
                        Log.Info("Video", $"stream {w}×{h} rot={rotation}° fmt={(AVPixelFormat)frame->format} → NV12");
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

                    // Read the timestamp before unref — it lives on the frame we
                    // just released back to the decoder.
                    long   bestPts = frame->best_effort_timestamp;
                    double pts     = bestPts == ffmpeg.AV_NOPTS_VALUE ? 0 : bestPts * ffmpeg.av_q2d(tb);
                    ffmpeg.av_frame_unref(frame);

                    // The first decoded frame is what "ready" actually means — the
                    // URL opened, the codec works and pixels exist.
                    if (firstFrame)
                    {
                        firstFrame = false;
                        if (Volatile.Read(ref _generation) == gen)
                            lock (_stateLock)
                                if (_state == VideoPlaybackState.Loading) _state = VideoPlaybackState.Playing;
                        Log.Info("Video", $"first frame decoded at {F(pts)}s — playback is live");
                    }

                    // ── Schedule against the master clock ─────────────────────
                    if (baseWall < 0) { baseWall = clock.Elapsed.TotalSeconds; basePts = pts; }

                    double MasterClock() => audioMaster && !double.IsNaN(audioEnd)
                        ? audioEnd - (audioOut!.BufferedMs / 1000.0)
                        : basePts + (clock.Elapsed.TotalSeconds - baseWall);

                    while (!ct.IsCancellationRequested)
                    {
                        // PresentFrame blocks on vsync (Present(1,…)), so the decode
                        // thread already absorbs up to a frame interval per frame.
                        // Subtract that measured cost instead of sleeping on top of it.
                        double delta = pts - MasterClock() - presentCost;
                        if (delta <= 0.001) break;

                        double sleep = Math.Min(delta, MaxClockSleepSeconds);
                        if (audioMaster)
                        {
                            // Never sleep the reader dry: while we wait, the audio
                            // device drains what is already queued and nothing refills
                            // it, so cap the wait at the queue depth. Without this the
                            // audio-master clock can deadlock against its own buffer.
                            double safe = (audioOut!.BufferedMs - AudioLowWaterMs) / 1000.0;
                            if (safe <= 0) break;
                            sleep = Math.Min(sleep, safe);
                        }
                        if (SleepCancelable(sleep, ct)) break;
                    }
                    if (ct.IsCancellationRequested) break;

                    double lateness = MasterClock() - pts;

                    if (!audioMaster && Math.Abs(lateness) > ReanchorSeconds)
                    {
                        // A long decode stall (or a pts discontinuity) shifted the whole
                        // schedule; chasing it would drop every frame from here on.
                        Log.Warn("Video", $"wall-clock drift {F(lateness)}s — re-anchoring schedule");
                        baseWall = clock.Elapsed.TotalSeconds; basePts = pts;
                        lateness = 0;
                    }

                    if (lateness > LateDropSeconds && consecutiveDrops < MaxConsecutiveDrops)
                    {
                        consecutiveDrops++;
                        dropped++;
                        lock (_stateLock) _position = pts;
                        continue;   // late frame: skip the present, keep the clock honest
                    }
                    consecutiveDrops = 0;

                    lock (_stateLock) _position = pts;

                    long t0 = Stopwatch.GetTimestamp();
                    _onFrame((IntPtr)nv12);
                    double cost = (Stopwatch.GetTimestamp() - t0) / (double)Stopwatch.Frequency;
                    presentCost = presentCost <= 0 ? cost : presentCost * 0.9 + cost * 0.1;
                    presented++;

                    if (clock.Elapsed.TotalSeconds >= nextStats)
                    {
                        nextStats = clock.Elapsed.TotalSeconds + StatsIntervalSeconds;
                        Log.Info("Video",
                            $"sync: pos={F(pts)}s/{F(duration)}s clock={(audioMaster ? "audio" : "wall")} " +
                            $"drift={F(lateness * 1000)}ms audioPts={F(audioEnd)}s audioBuf={(audioOut?.BufferedMs ?? -1)}ms " +
                            $"decoded={decoded} shown={presented} dropped={dropped} readRetries={retries} " +
                            $"present={F(presentCost * 1000)}ms");
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Video", "playback error", ex);
            Fail(gen, $"unexpected error: {ex.Message}");
        }
        finally
        {
            lock (_audioLock) if (ReferenceEquals(_audioOut, audioOut)) _audioOut = null;
            resampler?.Dispose();
            audioOut?.Dispose();

            if (sws != null)      ffmpeg.sws_freeContext(sws);
            if (packet != null)   ffmpeg.av_packet_free(&packet);
            if (frame != null)    ffmpeg.av_frame_free(&frame);
            if (aFrame != null)   ffmpeg.av_frame_free(&aFrame);
            if (nv12 != null)     ffmpeg.av_frame_free(&nv12);
            if (audioCtx != null) ffmpeg.avcodec_free_context(&audioCtx);
            if (codecCtx != null) ffmpeg.avcodec_free_context(&codecCtx);
            if (fmt != null)      ffmpeg.avformat_close_input(&fmt);
            if (opts != null)     ffmpeg.av_dict_free(&opts);
            handle.Free();
            Log.Info("Video", "playback loop ended");
        }
    }

    /// <summary>
    /// Flushes the decoder at EOF so it releases its buffered frames before we free
    /// it. The tail frames are discarded rather than presented — the presenter is
    /// about to go inactive anyway — but a decoder freed mid-flush logs errors.
    /// </summary>
    private unsafe void DrainDecoder(AVCodecContext* codecCtx, AVFrame* frame, CancellationToken ct)
    {
        ffmpeg.avcodec_send_packet(codecCtx, null);
        while (!ct.IsCancellationRequested)
        {
            if (ffmpeg.avcodec_receive_frame(codecCtx, frame) < 0) break;
            ffmpeg.av_frame_unref(frame);
        }
    }

    /// <summary>
    /// Seeks to the pending <see cref="_seekTarget"/> (if any) and flushes both
    /// decoders. Returns the target the caller must decode forward to, or NaN when
    /// nothing was pending or the seek failed.
    /// </summary>
    private unsafe double ApplyPendingSeek(AVFormatContext* fmt, AVCodecContext* codecCtx, AVCodecContext* audioCtx)
    {
        double target = _seekTarget;
        if (double.IsNaN(target)) return double.NaN;
        _seekTarget = double.NaN;

        // Stream index -1 seeks every stream at once, in AV_TIME_BASE units — the
        // audio track has to land on the same spot as the video or A/V sync starts
        // the item already broken.
        long ts  = (long)(target * ffmpeg.AV_TIME_BASE);
        int  ret = ffmpeg.av_seek_frame(fmt, -1, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
        if (ret < 0) { Log.Warn("Video", $"seek to {F(target)}s failed ({Err(ret)})"); return double.NaN; }

        ffmpeg.avcodec_flush_buffers(codecCtx);
        if (audioCtx != null) ffmpeg.avcodec_flush_buffers(audioCtx);
        lock (_stateLock) _position = target;
        Log.Info("Video", $"seeked to {F(target)}s (decoding forward to the exact frame)");
        return target;
    }

    /// <summary>
    /// Converts one decoded audio frame to the interleaved stereo float32 PCM
    /// <see cref="AudioOutput"/> (and <see cref="AudioResampler"/>) expect. Mono is
    /// duplicated to both channels; anything wider is folded to the front pair,
    /// which is all the WASAPI stereo output can carry.
    /// </summary>
    private unsafe byte[] ToStereoFloat(AVFrame* f)
    {
        int ch = f->ch_layout.nb_channels;
        int n  = f->nb_samples;
        if (ch <= 0 || n <= 0) return Array.Empty<byte>();

        var fmt     = (AVSampleFormat)f->format;
        var samples = new float[n * 2];

        for (int i = 0; i < n; i++)
        {
            float l = ReadSample(f, fmt, 0, ch, i);
            if (float.IsNaN(l))
            {
                if (!_warnedSampleFormat)
                {
                    _warnedSampleFormat = true;
                    Log.Warn("Video", $"unsupported audio sample format {fmt} — this item plays silently");
                }
                return Array.Empty<byte>();
            }
            float r = ch >= 2 ? ReadSample(f, fmt, 1, ch, i) : l;
            samples[i * 2]     = l;
            samples[i * 2 + 1] = r;
        }

        if (ch > 2 && !_warnedChannels)
        {
            _warnedChannels = true;
            Log.Info("Video", $"{ch}-channel audio folded to the front stereo pair");
        }

        byte[] outBytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, outBytes, 0, outBytes.Length);
        return outBytes;
    }

    private bool _warnedSampleFormat;
    private bool _warnedChannels;

    /// <summary>Reads one sample as float, or NaN when the format isn't one we handle.</summary>
    private static unsafe float ReadSample(AVFrame* f, AVSampleFormat fmt, int ch, int nbCh, int i) => fmt switch
    {
        AVSampleFormat.AV_SAMPLE_FMT_FLTP => ((float*)f->data[(uint)ch])[i],
        AVSampleFormat.AV_SAMPLE_FMT_FLT  => ((float*)f->data[0])[i * nbCh + ch],
        AVSampleFormat.AV_SAMPLE_FMT_S16P => ((short*)f->data[(uint)ch])[i] / 32768f,
        AVSampleFormat.AV_SAMPLE_FMT_S16  => ((short*)f->data[0])[i * nbCh + ch] / 32768f,
        AVSampleFormat.AV_SAMPLE_FMT_S32P => ((int*)f->data[(uint)ch])[i] / 2147483648f,
        AVSampleFormat.AV_SAMPLE_FMT_S32  => ((int*)f->data[0])[i * nbCh + ch] / 2147483648f,
        AVSampleFormat.AV_SAMPLE_FMT_DBLP => (float)((double*)f->data[(uint)ch])[i],
        AVSampleFormat.AV_SAMPLE_FMT_DBL  => (float)((double*)f->data[0])[i * nbCh + ch],
        AVSampleFormat.AV_SAMPLE_FMT_U8P  => (((byte*)f->data[(uint)ch])[i] - 128) / 128f,
        AVSampleFormat.AV_SAMPLE_FMT_U8   => (((byte*)f->data[0])[i * nbCh + ch] - 128) / 128f,
        _                                 => float.NaN,
    };

    private string? BuildExtraHeaders(string url)
    {
        string? headers = PlayRequestHeaders;
        if (string.IsNullOrWhiteSpace(headers)) return null;

        // Normalise to CRLF-terminated lines — FFmpeg appends them verbatim.
        string trimmed = headers.Replace("\r\n", "\n").Trim('\n');
        if (trimmed.Length == 0) return null;
        _ = url;   // reserved: per-host header filtering if a CDN ever needs it
        return string.Join("\r\n", trimmed.Split('\n')) + "\r\n";
    }

    /// <summary>Sleeps up to <paramref name="seconds"/>; returns true when cancelled.</summary>
    private static bool SleepCancelable(double seconds, CancellationToken ct)
    {
        if (seconds <= 0) return ct.IsCancellationRequested;
        try { return ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds)); }
        catch (ObjectDisposedException) { return true; }
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
        return Marshal.PtrToStringAnsi((IntPtr)buf) ?? code.ToString(CultureInfo.InvariantCulture);
    }

    private static unsafe string PtrToString(byte* p)
        => p == null ? "?" : Marshal.PtrToStringAnsi((IntPtr)p) ?? "?";

    /// <summary>Invariant-culture number formatting — log lines are grepped, not localised.</summary>
    private static string F(double v) => v.ToString("F3", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        StopInternal();
        _playing.Dispose();
    }
}
