using AirPlayReceiver.Media;
using AirPlayReceiver.Protocol;
using AirPlayReceiver.Rendering;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AirPlayReceiver.Services;

/// <summary>
/// Top-level service that wires together the discovery, protocol, and media layers.
///
/// Owned and started by App.xaml.cs.  Exposes Start/Stop for the application lifetime.
/// </summary>
public sealed class AirPlayService : IAsyncDisposable
{
    private readonly SwapChainPanel _panel;

    private MdnsService?    _mdns;
    private RtspServer?     _rtsp;
    private VideoPresenter? _presenter;
    private VideoDecoder?   _decoder;
    private RtpReceiver?    _rtpReceiver;
    private DeviceIdentity? _identity;
    private DeviceInfo?     _deviceInfo;

    // Shared AirPlay Video (URL playback) player, referenced by every session.
    // Constructed in StartAsync once the presenter it renders to exists.
    private AirPlayVideoPlayer? _videoPlayer;

    // Shared FCUP/HLS playlist exchange. Like the reverse channels it cannot live
    // on a session: /reverse, /play and /action each arrive on their own TCP
    // connection, and the playlist cache must outlive all of them.
    private HlsCoordinator? _hls;

    // Shared AirPlay Photos state: the JPEG→NV12 display path and the asset
    // cache behind X-Apple-AssetAction. Shared for the same reason the video
    // player is — iOS spreads one Photos session over several short-lived
    // control connections, so a per-session cache would 412 on every
    // displayCached that landed on a different connection than its cacheOnly.
    private PhotoDecoder? _photoDecoder;
    private PhotoHandler? _photos;

    // Shared reverse (PTTH) channels, referenced by every session. Lives for the
    // whole app run: an upgraded socket outlives the RTSP connection it arrived on.
    private readonly ReverseChannelRegistry _reverseChannels = new();

    /// <summary>
    /// The receiver→phone reverse-HTTP channels, keyed by X-Apple-Session-ID.
    /// Exposed so app-initiated video/photo features can push /event and FCUP
    /// requests to the phone.
    /// </summary>
    public ReverseChannelRegistry ReverseChannels => _reverseChannels;

    // The numeric form of the mDNS "features" TXT value (MdnsService.AirPlayFeatures),
    // returned by GET /info. The two representations must agree:
    //   "0x5A7FFFF7,0xE" → (0xE << 32) | 0x5A7FFFF7 == 0xE5A7FFFF7
    // See MdnsService for why word 2 is 0xE (what a real Apple TV 3 advertises).
    private const long AirPlayFeatures64 = 0xE5A7FFFF7L;

    // Callbacks into MainWindow (set via constructor — null-safe)
    private readonly MainWindow? _window;

    // ── Construction ──────────────────────────────────────────────────────────

    public AirPlayService(SwapChainPanel panel, MainWindow? window = null)
    {
        _panel  = panel;
        _window = window;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken ct = default)
    {
        string deviceName = BuildDeviceName();

        // ── 0. Device identity (shared by mDNS pk and the pairing handshake) ──
        _identity = DeviceIdentity.LoadOrCreate();

        string deviceId = MdnsService.GetMacAddress();
        _deviceInfo = new DeviceInfo
        {
            DeviceId  = deviceId,
            Name      = deviceName,
            PublicKey = _identity.PublicKeyBytes,
            Features  = AirPlayFeatures64,
            Pi        = MdnsService.GuidFromMac(deviceId),
        };

        // ── 1. Video presenter ────────────────────────────────────────────────
        _presenter = new VideoPresenter();
        _presenter.Initialize(_panel);

        // AirPlay Video (URL playback) renders to the same presenter as mirroring.
        // The two modes are mutually exclusive, so they can share the surface.
        _videoPlayer = new AirPlayVideoPlayer(
            onFrame:      framePtr => _presenter!.PresentFrame(framePtr),
            onDimensions: (w, h)   => _window?.UpdateStreamDimensions(w, h),
            onRotation:   deg      => _presenter?.SetRotation(deg),
            onActive:     active   =>
            {
                if (active) _window?.OnSessionStarted();
                else        _window?.OnSessionEnded();
                _presenter?.SetActive(active);
                _mdns?.SetSessionActive(active);
            },
            // A URL that won't open (DRM, dead host, 403) used to fail silently and
            // leave the phone spinning on 0:00 — surface it on the HUD instead.
            onNotice:     text     => _window?.ShowPersistentNotice(text));

        // ── 1b. FCUP/HLS playlist exchange ────────────────────────────────────
        // For YouTube and friends iOS hands us mlhls://localhost/master.m3u8,
        // which only the phone can resolve; the coordinator fetches the playlists
        // over the reverse channel and re-serves them from loopback, then starts
        // the same player a directly-fetchable URL would have used.
        _hls = new HlsCoordinator(
            channels: _reverseChannels,
            onPlay:   (url, seconds, fraction) => _videoPlayer!.Play(url, seconds, fraction),
            onPauseReportPosition: () =>
            {
                // playlistRemove: iOS is pulling the item (an ad is starting).
                // Pause and report where we were so the resume lands there.
                _videoPlayer!.SetRate(0);
                return _videoPlayer.Position;
            });

        // ── 1c. AirPlay Photos ────────────────────────────────────────────────
        // AirPlay Photos renders to that same presenter: a JPEG is decoded to
        // NV12 and pushed down the identical sink, so photos inherit the
        // letterboxing, fullscreen and idle-overlay behaviour for free and there
        // is only ever one thing on the surface.
        _photoDecoder = new PhotoDecoder(
            onFrame:      framePtr => _presenter!.PresentFrame(framePtr),
            onDimensions: (w, h)   => _window?.UpdateStreamDimensions(w, h),
            onRotation:   deg      => _presenter?.SetRotation(deg),
            onActive:     active   =>
            {
                if (active) _window?.OnSessionStarted();
                else        _window?.OnSessionEnded();
                _presenter?.SetActive(active);
                _mdns?.SetSessionActive(active);
            });

        _photos = new PhotoHandler(
            display:     _photoDecoder.Display,
            stopDisplay: _photoDecoder.Stop);

        // ── 2. Video decoder + RTP receiver ───────────────────────────────────
        // FFmpeg is optional on first launch: if the native DLLs aren't present
        // (see ARCHITECTURE.md §5) the decoder init throws, but discovery and the
        // RTSP server should still come up so the app runs and is discoverable.
        try
        {
            _decoder = new VideoDecoder(
                onFrame:      framePtr => _presenter!.PresentFrame(framePtr),
                onDimensions: (w, h)   => _window?.UpdateStreamDimensions(w, h),
                onHudUpdate:  text     => _window?.UpdateHud(text));

            _decoder.Initialize(FFmpeg.AutoGen.AVCodecID.AV_CODEC_ID_H264);
            _decoder.Start();

            _rtpReceiver = new RtpReceiver(_decoder);
        }
        catch (Exception ex)
        {
            Log.Error("AirPlay",
                $"Video decoder unavailable (ffmpeg.RootPath='{FFmpeg.AutoGen.ffmpeg.RootPath}') — " +
                "mirroring sessions will connect but show no video", ex);
            _window?.ShowPersistentNotice("Video decoding unavailable — FFmpeg DLLs missing (ARCHITECTURE.md §5)");
            _decoder     = null;
            _rtpReceiver = null;
        }

        // ── 3. RTSP server ────────────────────────────────────────────────────
        _rtsp = new RtspServer(port: 7000, sessionFactory: CreateSession, reverseChannels: _reverseChannels);
        _rtsp.SessionStarted += OnSessionStarted;
        _rtsp.SessionEnded   += OnSessionEnded;
        await _rtsp.StartAsync(ct);

        // ── 5. mDNS advertisement ─────────────────────────────────────────────
        _mdns = new MdnsService(deviceName, _identity.PublicKeyHex, port: 7000);
        await _mdns.StartAsync(ct);

        Log.Info("AirPlay", $"Service started. Advertising as '{deviceName}'");
    }

    public async Task StopAsync()
    {
        if (_mdns    is not null) await _mdns.StopAsync();
        if (_rtsp    is not null) await _rtsp.StopAsync();
        if (_decoder is not null) await _decoder.StopAsync();
        if (_hls     is not null) await _hls.DisposeAsync();

        // Reverse channels own sockets the RTSP server handed over, so nothing
        // else closes them.
        _reverseChannels.Dispose();

        _videoPlayer?.Dispose();
        _photoDecoder?.Dispose();
        _presenter?.Dispose();
        _decoder?.Dispose();

        Log.Info("AirPlay", "Service stopped.");
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>Ends the current AirPlay session (disconnects the phone) but keeps advertising.</summary>
    public void EndActiveSessions()
    {
        _rtsp?.CloseActiveConnections();
        // Upgraded reverse sockets are no longer tracked by the RTSP server, so
        // they have to be closed here too or the phone keeps a half-live session.
        _reverseChannels.CloseAll();
        // A Photos session ends on TEARDOWN, which we will never see now that the
        // control connections are gone — clear the surface and the asset cache here.
        _photos?.EndSession();
    }

    // ── Session factory ───────────────────────────────────────────────────────

    private AirPlaySession CreateSession()
    {
        var pairing = new PairingHandler(_identity!);
        var session = new AirPlaySession(_rtpReceiver, pairing, _deviceInfo!, _decoder, _videoPlayer!,
                                         _reverseChannels, _hls!, _photos!);

        // Mirroring frames arrive already upright, so clear any rotation left over
        // from a previous AirPlay Video session before this stream renders.
        // The mDNS `flags` advertisement tracks the same start/stop transitions the
        // window overlay does — a real Apple TV sets ReceiverSessionIsActive here.
        session.StreamStarted += () => { _presenter?.SetRotation(0); _window?.OnSessionStarted(); _presenter?.SetActive(true);  _mdns?.SetSessionActive(true); };
        session.StreamStopped += () => { _window?.OnSessionEnded();   _presenter?.SetActive(false); _mdns?.SetSessionActive(false); };

        return session;
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void OnSessionStarted(AirPlaySession session)
        => Log.Info("AirPlay", "Session started.");

    private void OnSessionEnded(AirPlaySession session)
        => Log.Info("AirPlay", "Session ended.");

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildDeviceName()
    {
        // Use a customisable name from app settings; fall back to machine name.
        string machine = Environment.MachineName;
        return $"{machine}'s AirPlay";
    }
}
