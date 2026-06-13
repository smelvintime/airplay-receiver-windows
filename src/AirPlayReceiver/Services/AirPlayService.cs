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

        // ── 1. Video presenter ────────────────────────────────────────────────
        _presenter = new VideoPresenter();
        _presenter.Initialize(_panel);

        // ── 2. Video decoder ──────────────────────────────────────────────────
        _decoder = new VideoDecoder(
            onFrame:      frame  => _presenter.PresentFrame(frame),
            onDimensions: (w, h) => _window?.UpdateStreamDimensions(w, h),
            onHudUpdate:  text   => _window?.UpdateHud(text));

        _decoder.Initialize(FFmpeg.AutoGen.AVCodecID.AV_CODEC_ID_H264);
        _decoder.Start();

        // ── 3. RTP receiver ───────────────────────────────────────────────────
        _rtpReceiver = new RtpReceiver(_decoder);

        // ── 4. RTSP server ────────────────────────────────────────────────────
        _rtsp = new RtspServer(port: 7000, sessionFactory: CreateSession);
        _rtsp.SessionStarted += OnSessionStarted;
        _rtsp.SessionEnded   += OnSessionEnded;
        await _rtsp.StartAsync(ct);

        // ── 5. mDNS advertisement ─────────────────────────────────────────────
        _mdns = new MdnsService(deviceName, port: 7000);
        await _mdns.StartAsync(ct);

        Console.WriteLine($"[AirPlay] Service started. Advertising as '{deviceName}'");
    }

    public async Task StopAsync()
    {
        if (_mdns    is not null) await _mdns.StopAsync();
        if (_rtsp    is not null) await _rtsp.StopAsync();
        if (_decoder is not null) await _decoder.StopAsync();

        _presenter?.Dispose();
        _decoder?.Dispose();

        Console.WriteLine("[AirPlay] Service stopped.");
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    // ── Session factory ───────────────────────────────────────────────────────

    private AirPlaySession CreateSession()
    {
        var pairing = new PairingHandler();
        var session = new AirPlaySession(_rtpReceiver!, pairing);

        session.StreamStarted += () => _window?.OnSessionStarted();
        session.StreamEnded   += () => _window?.OnSessionEnded();

        return session;
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void OnSessionStarted(AirPlaySession session)
        => Console.WriteLine("[AirPlay] Session started.");

    private void OnSessionEnded(AirPlaySession session)
        => Console.WriteLine("[AirPlay] Session ended.");

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildDeviceName()
    {
        // Use a customisable name from app settings; fall back to machine name.
        string machine = Environment.MachineName;
        return $"{machine}'s AirPlay";
    }
}
