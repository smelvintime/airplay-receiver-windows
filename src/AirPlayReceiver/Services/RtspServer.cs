using AirPlayReceiver.Protocol;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AirPlayReceiver.Services;

/// <summary>
/// Bare-TCP RTSP server that iOS connects to after mDNS discovery.
///
/// AirPlay's RTSP dialect deviates from RFC 2326:
///   • Uses HTTP-style POST for /pair-setup and /pair-verify
///   • SETUP carries a binary TLV body (not SDP)
///   • RECORD starts the RTP stream (not PLAY)
///
/// This class handles the network layer (accept → read → dispatch → write).
/// Protocol logic per verb lives in <see cref="AirPlaySession"/>.
/// </summary>
public sealed class RtspServer : IAsyncDisposable
{
    private readonly int  _port;
    private readonly Func<AirPlaySession> _sessionFactory;

    private TcpListener?          _listener;
    private CancellationTokenSource? _cts;
    private Task?                 _acceptLoop;

    public event Action<AirPlaySession>? SessionStarted;
    public event Action<AirPlaySession>? SessionEnded;

    public RtspServer(int port, Func<AirPlaySession> sessionFactory)
    {
        _port           = port;
        _sessionFactory = sessionFactory;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken ct = default)
    {
        _cts      = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start(backlog: 4);

        Console.WriteLine($"[RTSP] Listening on 0.0.0.0:{_port}");

        _acceptLoop = AcceptLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();
        if (_acceptLoop is not null)
            await _acceptLoop.ConfigureAwait(false);
        Console.WriteLine("[RTSP] Stopped.");
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    // ── Accept loop ───────────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException ex)
            {
                Console.WriteLine($"[RTSP] Accept error: {ex.Message}");
                break;
            }

            // Each iOS connection gets its own session on a dedicated Task.
            // iOS only opens one at a time for screen mirroring, but we support
            // multiple to be safe (e.g. reconnect before teardown is processed).
            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint;
        Console.WriteLine($"[RTSP] Connection from {remote}");

        var session = _sessionFactory();
        SessionStarted?.Invoke(session);

        try
        {
            using var stream = client.GetStream();
            await session.RunAsync(stream, ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            Console.WriteLine($"[RTSP] Session ended ({remote}): {ex.Message}");
        }
        finally
        {
            client.Dispose();
            SessionEnded?.Invoke(session);
            Console.WriteLine($"[RTSP] Closed {remote}");
        }
    }
}
