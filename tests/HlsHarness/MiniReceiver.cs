using AirPlayReceiver.Protocol;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AirPlayReceiver.HlsHarness;

/// <summary>
/// The receiver side of the loopback simulation: an RTSP/HTTP listener that
/// dispatches the three verbs the FCUP exchange uses, exactly as
/// <c>AirPlaySession</c> and <c>RtspServer</c> do in the app.
///
/// It exists because <c>AirPlaySession</c> itself drags in WinUI, FFmpeg and the
/// pairing stack, none of which the playlist pipeline touches. The handlers here
/// are one-liners into the same <see cref="HlsCoordinator"/> the app calls, and
/// the /reverse socket handoff is the same handoff <c>RtspServer</c> performs —
/// so what is simulated is the transport, not the logic under test.
/// </summary>
internal sealed class MiniReceiver : IAsyncDisposable
{
    private readonly HlsCoordinator          _hls;
    private readonly ReverseChannelRegistry  _channels;
    private readonly TcpListener             _listener;
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; }

    public MiniReceiver(HlsCoordinator hls, ReverseChannelRegistry channels)
    {
        _hls      = hls;
        _channels = channels;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(backlog: 8);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        NetworkStream stream = client.GetStream();
        bool handedOver = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                RtspMessage? msg = await RtspMessage.ReadAsync(stream, ct);
                if (msg is null) break;

                string sessionId = msg.Headers.GetValueOrDefault(ReverseChannel.SessionIdHeader, string.Empty);

                if (msg.Path == "/reverse")
                {
                    string purpose = msg.Headers.GetValueOrDefault(ReverseChannel.PurposeHeader, ReverseChannel.PurposeEvent);
                    var headers = new Dictionary<string, string>
                    {
                        ["Upgrade"]    = "PTTH/1.0",
                        ["Connection"] = "Upgrade",
                    };
                    await stream.WriteAsync(
                        RtspMessage.BuildResponse(101, "Switching Protocols", msg.CSeq, headers, protocol: msg.Version), ct);

                    // Same socket-ownership handoff RtspServer does after the 101.
                    var channel = new ReverseChannel(sessionId, purpose, stream, client);
                    handedOver  = _channels.Register(channel);
                    if (!handedOver) channel.Dispose();
                    return;
                }

                byte[] response = msg.Path switch
                {
                    "/play"   => HandlePlay(msg, sessionId),
                    "/action" => _hls.HandleAction(msg),
                    _         => RtspMessage.BuildResponse(404, "Not Found", msg.CSeq, protocol: msg.Version),
                };

                await stream.WriteAsync(response, ct);
            }
        }
        catch { /* the phone hung up */ }
        finally
        {
            if (!handedOver) { stream.Dispose(); client.Dispose(); }
        }
    }

    /// <summary>
    /// Mirrors <c>AirPlaySession.HandlePlay</c>: pull Content-Location, uuid and
    /// Start-Position-Seconds out of the binary plist, then let the coordinator
    /// route it.
    /// </summary>
    private byte[] HandlePlay(RtspMessage msg, string sessionId)
    {
        byte[] body = msg.Body ?? Array.Empty<byte>();
        string? url = null, uuid = null;
        double start = 0;

        if (body.Length >= 8 && Encoding.ASCII.GetString(body, 0, 8) == "bplist00" &&
            BinaryPlist.Parse(body) is Dictionary<string, object?> dict)
        {
            url  = dict.GetValueOrDefault("Content-Location") as string;
            uuid = dict.GetValueOrDefault("uuid") as string;
            if (dict.GetValueOrDefault("Start-Position-Seconds") is double d) start = d;
        }

        if (string.IsNullOrEmpty(url) || !_hls.HandlePlay(sessionId, uuid, url, start))
            return RtspMessage.BuildResponse(400, "Bad Request", msg.CSeq, protocol: msg.Version);

        return RtspMessage.BuildResponse(200, "OK", msg.CSeq, protocol: msg.Version);
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}
