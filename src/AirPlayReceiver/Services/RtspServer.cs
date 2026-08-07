using AirPlayReceiver.Protocol;
using System;
using System.Collections.Concurrent;
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
    /// <summary>
    /// Concurrent client connections accepted. iOS opens several per AirPlay
    /// session — control, reverse/event, and a short-lived one per photo or
    /// transport verb — so this is deliberately generous: it exists to stop an
    /// unauthenticated LAN device from parking thousands of sockets (each with its
    /// own session object and task), not to shape normal traffic.
    ///
    /// <c>Start(backlog: 4)</c> only bounds the kernel's pending-accept queue; it
    /// puts no limit at all on connections we have already accepted.
    /// </summary>
    private const int MaxConcurrentConnections = 64;

    /// <summary>
    /// Refusals are attacker-triggered, so logging every one is itself a log-flood
    /// lever (see Log's per-file cap). Log the first and then sparsely — enough for
    /// a field test to reveal that the cap was reached.
    /// </summary>
    private const int RefusalLogInterval = 100;

    private readonly int  _port;
    private readonly Func<AirPlaySession> _sessionFactory;
    private readonly ReverseChannelRegistry _reverseChannels;

    private TcpListener?          _listener;
    private CancellationTokenSource? _cts;
    private Task?                 _acceptLoop;

    // Live client connections, so the UI can end the current session on demand.
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();

    private long _refusedConnections;

    public event Action<AirPlaySession>? SessionStarted;
    public event Action<AirPlaySession>? SessionEnded;

    public RtspServer(int port, Func<AirPlaySession> sessionFactory, ReverseChannelRegistry reverseChannels)
    {
        _port            = port;
        _sessionFactory  = sessionFactory;
        _reverseChannels = reverseChannels;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken ct = default)
    {
        _cts      = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start(backlog: 4);

        Log.Info("RTSP", $"Listening on 0.0.0.0:{_port}");

        _acceptLoop = AcceptLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();
        if (_acceptLoop is not null)
            await _acceptLoop.ConfigureAwait(false);
        Log.Info("RTSP", "Stopped.");
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>
    /// Forcibly closes any live client connections, ending the current AirPlay
    /// session (the phone sees the receiver disconnect). Used when the user closes
    /// the window — we drop the session but keep advertising for the next connect.
    /// </summary>
    public void CloseActiveConnections()
    {
        foreach (var client in _clients.Keys)
        {
            try { client.Close(); }   // unblocks the session's ReadAsync → RunAsync ends
            catch { /* already gone */ }
        }
    }

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
                Log.Error("RTSP", $"Accept error: {ex.Message}");
                break;
            }

            // HandleClientAsync registers the client synchronously before its first
            // await, so this count is current by the time we come round again.
            int live = _clients.Count;
            if (live >= MaxConcurrentConnections)
            {
                long refused = Interlocked.Increment(ref _refusedConnections);
                if (refused == 1 || refused % RefusalLogInterval == 0)
                {
                    Log.Warn("RTSP", $"Refusing connection: {live} live connections is the " +
                                     $"{MaxConcurrentConnections} cap ({refused} refused so far)");
                }
                try { client.Close(); } catch { /* already gone */ }
                continue;
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
        Log.Info("RTSP", $"Connection from {remote}");

        var session = _sessionFactory();
        _clients.TryAdd(client, 0);
        SessionStarted?.Invoke(session);

        NetworkStream? stream = null;
        bool faulted = false;

        try
        {
            stream = client.GetStream();
            await session.RunAsync(stream, ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // ObjectDisposedException is expected when CloseActiveConnections() drops us.
            faulted = true;
            Log.Info("RTSP", $"Session ended ({remote}): {ex.Message}");
        }
        finally
        {
            _clients.TryRemove(client, out _);

            // Socket-ownership handoff: a connection the session upgraded to
            // reverse HTTP (PTTH) must survive — after the 101 it is the receiver's
            // only way to send requests TO the phone. Disposing it here (what this
            // method used to do unconditionally) is what made app-initiated video
            // and photo events impossible.
            if (!faulted && stream is not null && session.ReverseUpgrade is { } upgrade)
            {
                var channel = new ReverseChannel(upgrade.SessionId, upgrade.Purpose, stream, client);
                if (_reverseChannels.Register(channel))
                {
                    Log.Info("Reverse", $"Socket ownership transferred to registry: {remote} " +
                                        $"session={upgrade.SessionId} purpose={upgrade.Purpose}");
                }
                else
                {
                    // Refused (duplicate event channel / shutting down): Dispose
                    // closes the stream and the TcpClient we handed it.
                    Log.Warn("Reverse", $"Registry refused the channel for {remote} — closing socket");
                    channel.Dispose();
                }
            }
            else
            {
                stream?.Dispose();
                client.Dispose();
                Log.Info("RTSP", $"Closed {remote}");
            }

            SessionEnded?.Invoke(session);
        }
    }
}
