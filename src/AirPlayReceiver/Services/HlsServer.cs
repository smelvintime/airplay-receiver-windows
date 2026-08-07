using AirPlayReceiver.Media;
using AirPlayReceiver.Protocol;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AirPlayReceiver.Services;

/// <summary>
/// Serves the HLS playlists the phone handed us over FCUP back to our own
/// player.
///
/// <b>Loopback only, by design.</b> The listener binds 127.0.0.1, never
/// <see cref="IPAddress.Any"/>: this is not a feature other devices may use, it
/// exists solely so the FFmpeg demuxer inside this process has a fetchable
/// <c>http://</c> origin to replace <c>mlhls://localhost</c>, which nothing but
/// the phone can resolve. Binding it to the network would republish someone's
/// YouTube session to the LAN and add an unauthenticated listener for no gain.
///
/// It serves playlists only. Media segments expand to absolute googlevideo.com
/// URLs (see <see cref="HlsVideo.ExpandCondensedPlaylist"/>) that the player
/// fetches straight from the CDN, so no media bytes ever pass through here.
///
/// Shape follows <see cref="RtspServer"/>: TcpListener, one task per accepted
/// connection, requests parsed with <see cref="RtspMessage"/>.
///
/// Reference: UxPlay lib/http_handlers.h http_handler_hls.
/// </summary>
public sealed class HlsServer : IAsyncDisposable
{
    /// <summary>Content type Apple and every HLS player expect for an m3u8.</summary>
    private const string PlaylistContentType = "application/x-mpegURL; charset=utf-8";

    /// <summary>
    /// How long <see cref="StopAsync"/> waits for in-flight request tasks after
    /// cancelling them. They unblock on the cancellation token; this is only
    /// insurance, and shutdown must not hang on it.
    /// </summary>
    private const int ShutdownDrainMs = 2000;

    private readonly HlsPlaylistStore _store;

    // The tasks serving accepted connections, so shutdown can wait for them
    // rather than returning while they still touch the store.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> _connectionTasks = new();

    private TcpListener?             _listener;
    private CancellationTokenSource? _cts;
    private Task?                    _acceptLoop;

    /// <summary>The ephemeral port the listener bound, or 0 before <see cref="Start"/>.</summary>
    public int Port { get; private set; }

    /// <summary>Origin to substitute for the phone's <c>mlhls://localhost</c> prefix.</summary>
    /// <remarks>
    /// Literal 127.0.0.1 rather than "localhost": on Windows "localhost" resolves
    /// to ::1 first, and a connect to [::1] against this v4-only listener fails —
    /// the player would report an unplayable stream with no other symptom.
    /// </remarks>
    public string Origin => $"http://127.0.0.1:{Port.ToString(CultureInfo.InvariantCulture)}";

    /// <param name="store">
    /// Cache of playback items. Requests carry the item's own path segment
    /// (<see cref="HlsVideo.LocalPathToken"/>), so which item a URL belongs to is
    /// read off the URL rather than assumed to be whatever is playing now.
    /// </param>
    public HlsServer(HlsPlaylistStore store) => _store = store;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>Binds an ephemeral loopback port and starts accepting. Idempotent.</summary>
    public void Start(CancellationToken ct = default)
    {
        if (_listener is not null) return;

        _cts      = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(backlog: 8);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        Log.Info("HLS", $"Playlist server listening on {Origin} (loopback only)");
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (_listener is null) return;

        _cts?.Cancel();
        _listener.Stop();
        _listener = null;

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("HLS", $"Accept loop ended with {ex.GetType().Name}: {ex.Message}"); }
            _acceptLoop = null;
        }

        Task[] live = _connectionTasks.Keys.ToArray();
        if (live.Length > 0)
        {
            Task all = Task.WhenAll(live);
            if (await Task.WhenAny(all, Task.Delay(ShutdownDrainMs)).ConfigureAwait(false) != all)
                Log.Warn("HLS", $"{live.Length} request task(s) still running after {ShutdownDrainMs}ms — " +
                                "continuing shutdown without them");
            else
                try { await all.ConfigureAwait(false); }
                catch (Exception ex) { Log.Warn("HLS", $"A request task ended with {ex.GetType().Name}: {ex.Message}"); }
        }

        Log.Info("HLS", "Playlist server stopped.");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    // ── Accept / serve ────────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                Log.Error("HLS", $"Accept error: {ex.Message}");
                break;
            }

            Task handler = HandleClientAsync(client, ct);
            _connectionTasks.TryAdd(handler, 0);
            _ = handler.ContinueWith(t => _connectionTasks.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            await using (NetworkStream stream = client.GetStream())
            {
                // HTTP/1.1 keep-alive: FFmpeg fetches the master and then each
                // child playlist, usually on one connection.
                int requestsRead = 0;
                while (!ct.IsCancellationRequested)
                {
                    // Same rule as the RTSP server: bound the wait for the first
                    // request so a local process that connects and says nothing
                    // cannot park a connection here indefinitely.
                    RtspMessage? request = await RtspMessage.ReadAsync(
                        stream, ct, requestsRead == 0 ? RtspMessage.FirstMessageIdleTimeout : null)
                        .ConfigureAwait(false);
                    if (request is null) break;   // client closed between requests
                    requestsRead++;

                    byte[] response = BuildResponse(request);
                    await stream.WriteAsync(response, ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);

                    if (request.Headers.TryGetValue("Connection", out string? conn) &&
                        conn.Contains("close", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // Players routinely abandon a connection mid-request when they seek.
            Log.Info("HLS", $"Connection ended: {ex.GetType().Name} {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error("HLS", "Request handler crashed", ex);
        }
    }

    private byte[] BuildResponse(RtspMessage request)
    {
        string path = request.Path;

        if (request.Method != "GET" && request.Method != "HEAD")
            return Respond(405, "Method Not Allowed", null);

        // Decline an h2c upgrade: we answer HTTP/1.1 and simply never send the
        // 101, which is how a server refuses an optional upgrade. (UxPlay abandons
        // the request instead, which leaves its own player holding an empty 200.)
        if (request.Headers.TryGetValue("Upgrade", out string? upgrade))
            Log.Warn("HLS", $"Upgrade: {upgrade} declined — continuing as HTTP/1.1 for {path}");

        // "/v3/index-720.m3u8" → item token "v3", playlist path "/index-720.m3u8".
        SplitPath(path, out string token, out string playlistPath);

        HlsVideo? video = _store.TryGetByLocalToken(token, out HlsVideo found) ? found : _store.Current;
        if (video is null)
        {
            // Nothing is playing and the token is unknown: a stale player still
            // polling is not an error we can serve, but it must not take the
            // receiver down (UxPlay asserts on this).
            Log.Warn("HLS", $"GET {path} but no playback item matches — 404");
            return Respond(404, "Not Found", null);
        }

        if (playlistPath.EndsWith("/master.m3u8", StringComparison.Ordinal))
        {
            if (video.MasterPlaylist is not { } master)
            {
                Log.Warn("HLS", $"GET {path} but the master playlist has not arrived yet — 404");
                return Respond(404, "Not Found", null);
            }
            Log.Info("HLS", $"GET {path} → master playlist for uuid={video.Uuid} ({master.Length}B)");
            return Respond(200, "OK", master, request.Method == "HEAD");
        }

        if (!video.TryGetMedia(playlistPath, out string mediaPlaylist, out int chunks, out double duration))
        {
            Log.Warn("HLS", $"GET {path} → no cached media playlist for that URI — 404");
            return Respond(404, "Not Found", null);
        }

        // Expansion is deliberately a serve-time step, not a store-time one: the
        // cache keeps exactly what the phone sent.
        string expanded = HlsVideo.ExpandCondensedPlaylist(mediaPlaylist);
        Log.Info("HLS", $"GET {path} → media playlist, {chunks} chunk(s), {duration.ToString("F3", CultureInfo.InvariantCulture)}s " +
                        $"({mediaPlaylist.Length}B stored → {expanded.Length}B served)");
        return Respond(200, "OK", expanded, request.Method == "HEAD");
    }

    /// <summary>
    /// Splits "/v3/index-720.m3u8" into the playback item's path segment ("v3")
    /// and the playlist path within it ("/index-720.m3u8").
    /// </summary>
    private static void SplitPath(string path, out string token, out string playlistPath)
    {
        int second = path.IndexOf('/', 1);
        if (second <= 0)
        {
            token        = string.Empty;
            playlistPath = path;
            return;
        }
        token        = path[1..second];
        playlistPath = path[second..];
    }

    private static byte[] Respond(int status, string reason, string? body, bool headOnly = false)
    {
        byte[] payload = body is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);

        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(reason).Append("\r\n");
        head.Append("Date: ").Append(DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture)).Append("\r\n");
        // No Access-Control-* headers, deliberately. The only client here is our
        // own FFmpeg, which does not consult CORS at all — so the wildcard bought
        // nothing and told any browser-hosted local page that guessed the
        // ephemeral port it could read cached playlists, short-lived signed CDN
        // URLs included.
        if (payload.Length > 0)
            head.Append("Content-Type: ").Append(PlaylistContentType).Append("\r\n");
        head.Append("Content-Length: ").Append(payload.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        head.Append("\r\n");

        byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
        if (payload.Length == 0 || headOnly) return headBytes;

        var result = new byte[headBytes.Length + payload.Length];
        headBytes.CopyTo(result, 0);
        payload.CopyTo(result, headBytes.Length);
        return result;
    }
}
