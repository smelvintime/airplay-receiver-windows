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

    // Must match the mDNS "features" TXT value (MdnsService.AirPlayFeatures):
    // 0x5A7FFFF7,0x1E → (0x1E << 32) | 0x5A7FFFF7. Word 2 = 0x1E keeps the device
    // reconnectable in the iOS picker (word 2 = 0x0 made it vanish after a session).
    private const long AirPlayFeatures64 = 0x1E5A7FFFF7L;

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
            });

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
            System.Diagnostics.Debug.WriteLine($"[AirPlay] Video decoder unavailable: {ex.Message}");
            _window?.UpdateHud("Video decoding unavailable — FFmpeg DLLs missing (ARCHITECTURE.md §5)");
            _decoder     = null;
            _rtpReceiver = null;
        }

        // ── 3. RTSP server ────────────────────────────────────────────────────
        _rtsp = new RtspServer(port: 7000, sessionFactory: CreateSession);
        _rtsp.SessionStarted += OnSessionStarted;
        _rtsp.SessionEnded   += OnSessionEnded;
        await _rtsp.StartAsync(ct);

        // ── 5. mDNS advertisement ─────────────────────────────────────────────
        _mdns = new MdnsService(deviceName, _identity.PublicKeyHex, port: 7000);
        await _mdns.StartAsync(ct);

        System.Diagnostics.Debug.WriteLine($"[AirPlay] Service started. Advertising as '{deviceName}'");
    }

    public async Task StopAsync()
    {
        if (_mdns    is not null) await _mdns.StopAsync();
        if (_rtsp    is not null) await _rtsp.StopAsync();
        if (_decoder is not null) await _decoder.StopAsync();

        _videoPlayer?.Dispose();
        _presenter?.Dispose();
        _decoder?.Dispose();

        System.Diagnostics.Debug.WriteLine("[AirPlay] Service stopped.");
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>Ends the current AirPlay session (disconnects the phone) but keeps advertising.</summary>
    public void EndActiveSessions() => _rtsp?.CloseActiveConnections();

    // ── Session factory ───────────────────────────────────────────────────────

    private AirPlaySession CreateSession()
    {
        var pairing = new PairingHandler(_identity!);
        var session = new AirPlaySession(_rtpReceiver, pairing, _deviceInfo!, _decoder, _videoPlayer!);

        // Mirroring frames arrive already upright, so clear any rotation left over
        // from a previous AirPlay Video session before this stream renders.
        session.StreamStarted += () => { _presenter?.SetRotation(0); _window?.OnSessionStarted(); _presenter?.SetActive(true); };
        session.StreamStopped += () => { _window?.OnSessionEnded();   _presenter?.SetActive(false); };

        return session;
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void OnSessionStarted(AirPlaySession session)
        => System.Diagnostics.Debug.WriteLine("[AirPlay] Session started.");

    private void OnSessionEnded(AirPlaySession session)
        => System.Diagnostics.Debug.WriteLine("[AirPlay] Session ended.");

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildDeviceName()
    {
        // Use a customisable name from app settings; fall back to machine name.
        string machine = Environment.MachineName;
        return $"{machine}'s AirPlay";
    }
}
