using AirPlayReceiver.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AirPlayReceiver.Protocol;

/// <summary>
/// One reverse-HTTP ("PTTH") channel: a TCP connection the phone opened with
/// <c>POST /reverse</c> + <c>Upgrade: PTTH/1.0</c>, which we answered with
/// <c>101 Switching Protocols</c>.
///
/// After that 101 the roles on this socket are inverted:
///   • outbound = HTTP <b>requests</b> from the receiver to the phone
///     (<c>POST /event</c>, and the FCUP request that fetches HLS playlists),
///   • inbound  = HTTP <b>responses</b> from the phone ("HTTP/1.1 200 OK").
///
/// The inbound half must therefore never reach the RTSP request parser — see
/// UxPlay httpd.c: "reverse-http responses from the client must not be sent to
/// the llhttp parser". <see cref="DrainLoopAsync"/> reads and discards them so
/// the socket cannot back up, and detects the far end closing.
///
/// Sends are fire-and-forget (UxPlay does the same): the write completes, the
/// phone's 200 is drained asynchronously.
/// </summary>
public sealed class ReverseChannel : IDisposable
{
    /// <summary>X-Apple-Purpose of the single event channel iOS opens before /play.</summary>
    public const string PurposeEvent = "event";

    /// <summary>X-Apple-Purpose of the three parallel channels a Photos slideshow opens.</summary>
    public const string PurposeSlideshow = "slideshow";

    /// <summary>Header carrying the GUID that correlates every connection of one AirPlay session.</summary>
    public const string SessionIdHeader = "X-Apple-Session-ID";

    /// <summary>Header carrying the channel's purpose ("event" or "slideshow").</summary>
    public const string PurposeHeader = "X-Apple-Purpose";

    // User-Agent an Apple TV sends on reverse requests; some clients log it.
    private const string UserAgent = "AirPlay/320.20";

    /// <summary>
    /// Longest X-Apple-Session-ID accepted. A GUID in Apple's textual form is 36
    /// characters; the slack covers a sender that decorates it.
    /// </summary>
    private const int MaxSessionIdLength = 64;

    /// <summary>
    /// True when a session id is safe to echo into headers we write.
    ///
    /// The value arrives on an unauthenticated request and is interpolated
    /// straight into <see cref="SendAsync"/>'s request head, so what actually
    /// matters is the character class, not the exact syntax. This is a superset of
    /// the GUID iOS really sends (hex and dashes) rather than
    /// <c>Guid.TryParse</c>: a stricter test would silently refuse any sender that
    /// decorates the id, and we cannot test every sender — but nothing outside
    /// this class can terminate a header line, introduce a colon, or smuggle a
    /// control character.
    /// </summary>
    public static bool IsValidSessionId(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || sessionId.Length > MaxSessionIdLength) return false;

        foreach (char c in sessionId)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')) return false;
        }
        return true;
    }

    private readonly Stream     _stream;
    private readonly TcpClient? _client;      // null when the channel wraps a plain stream (tests)
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _readByte = new byte[1]; // drain loop is single-threaded

    private Task? _drainLoop;
    private int   _disposed;
    private int   _requestsSent;
    private int   _responsesDrained;

    /// <summary>The X-Apple-Session-ID GUID this channel belongs to.</summary>
    public string SessionId { get; }

    /// <summary>The X-Apple-Purpose this channel was opened for.</summary>
    public string Purpose { get; }

    /// <summary>Remote endpoint, for logs.</summary>
    public string Remote { get; }

    public bool IsOpen           => Volatile.Read(ref _disposed) == 0;
    public int  RequestsSent     => Volatile.Read(ref _requestsSent);
    public int  ResponsesDrained => Volatile.Read(ref _responsesDrained);

    /// <summary>Raised once, when the channel is disposed or the phone closes the socket.</summary>
    public event Action<ReverseChannel>? Closed;

    // ── Construction ──────────────────────────────────────────────────────────

    /// <summary>
    /// Takes ownership of an already-upgraded connection. <paramref name="client"/>
    /// is optional: pass it so disposal closes the socket the RTSP server handed
    /// over; omit it when wrapping a plain duplex stream.
    /// </summary>
    public ReverseChannel(string sessionId, string purpose, Stream stream, TcpClient? client = null)
    {
        SessionId = sessionId;
        Purpose   = purpose;
        _stream   = stream;
        _client   = client;

        string remote;
        try { remote = client?.Client.RemoteEndPoint?.ToString() ?? "(stream)"; }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { remote = "(closed)"; }
        Remote = remote;
    }

    /// <summary>
    /// Starts the inbound drain loop. Called once by
    /// <see cref="ReverseChannelRegistry.Register"/>; safe to skip when a caller
    /// wants to inspect the raw stream itself (tests).
    /// </summary>
    public void Start()
    {
        if (_drainLoop is not null || !IsOpen) return;
        _drainLoop = Task.Run(() => DrainLoopAsync(_cts.Token));
    }

    // ── Sending ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a receiver→phone request whose body is <paramref name="plistBody"/>
    /// serialized as an XML plist. Returns false (and logs) when the channel is
    /// closed or the write fails, so callers never have to guard with try/catch.
    /// </summary>
    public Task<bool> SendRequestAsync(
        string method, string path, IDictionary<string, object?>? plistBody, CancellationToken ct = default)
    {
        byte[] body;
        try
        {
            body = plistBody is null ? Array.Empty<byte>() : XmlPlist.Write(plistBody);
        }
        catch (Exception ex)
        {
            Log.Error("Reverse", $"{method} {path} session={SessionId}: plist serialization failed", ex);
            return Task.FromResult(false);
        }

        return SendAsync(method, path, body, plistBody is null ? null : XmlPlist.ContentType, ct);
    }

    /// <summary>
    /// Sends a playback state event: <c>POST /event</c> with
    /// <c>{ category, sessionID, state }</c>.
    ///
    /// Optional in practice — neither UxPlay nor apsdk sends these and YouTube
    /// plays fine — but iOS accepts them. Documented values:
    /// video: loading|playing|paused|stopped, photo: stopped,
    /// slideshow: loading|playing|stopped.
    /// </summary>
    /// <param name="sessionCounter">
    /// The small per-category counter iOS uses inside event plists — NOT the
    /// <see cref="SessionId"/> GUID.
    /// </param>
    public Task<bool> SendEventAsync(string category, string state, int sessionCounter = 0, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["category"]  = category,
            ["sessionID"] = sessionCounter,
            ["state"]     = state,
        };
        return SendRequestAsync("POST", "/event", body, ct);
    }

    /// <summary>Sends a request with an already-serialized body (or none).</summary>
    public async Task<bool> SendAsync(
        string method, string path, byte[] body, string? contentType, CancellationToken ct = default)
    {
        if (!IsOpen)
        {
            Log.Warn("Reverse", $"{method} {path} dropped — channel session={SessionId} purpose={Purpose} is closed");
            return false;
        }

        // Every interpolated value below goes through HeaderSafe. SessionId and the
        // path originate in a request from the network, and Encoding.ASCII.GetBytes
        // passes a CR or LF straight through — so an unsanitised value here is a
        // header-injection primitive against the phone, not merely a malformed
        // request. RtspMessage refuses control characters in the first place; this
        // is the second lock on the same door, at the point where bytes are written.
        var head = new StringBuilder();
        head.Append(method).Append(' ').Append(HeaderSafe(path)).Append(" HTTP/1.1\r\n");
        if (contentType is not null)
            head.Append("Content-Type: ").Append(HeaderSafe(contentType)).Append("\r\n");
        head.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        head.Append("User-Agent: ").Append(UserAgent).Append("\r\n");
        head.Append(SessionIdHeader).Append(": ").Append(HeaderSafe(SessionId)).Append("\r\n");
        head.Append("\r\n");
        byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(headBytes, ct).ConfigureAwait(false);
            if (body.Length > 0)
                await _stream.WriteAsync(body, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Log.Error("Reverse", $"{method} {path} session={SessionId}: write failed — closing channel", ex);
            Dispose();
            return false;
        }
        finally
        {
            _writeLock.Release();
        }

        Interlocked.Increment(ref _requestsSent);
        Log.Info("Reverse", $"→ {method} {path} session={SessionId} purpose={Purpose} " +
                            $"{headBytes.Length + body.Length}B (head {headBytes.Length}B, body {body.Length}B)");
        if (body.Length > 0)
            Log.Info("Reverse", $"→ body: {Preview(body)}");
        return true;
    }

    // ── Inbound: read and discard the phone's responses ───────────────────────

    private async Task DrainLoopAsync(CancellationToken ct)
    {
        Log.Info("Reverse", $"Drain loop started for session={SessionId} purpose={Purpose} remote={Remote}");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? statusLine = await ReadLineAsync(ct).ConfigureAwait(false);
                if (statusLine is null) break;              // EOF — phone closed the channel
                if (statusLine.Length == 0) continue;       // stray CRLF between responses

                // Guard from UxPlay httpd.c: everything arriving here is a
                // response, and must never reach the RTSP request parser.
                if (!statusLine.StartsWith("HTTP/", StringComparison.Ordinal))
                    Log.Warn("Reverse", $"← unexpected non-response on reverse channel: {statusLine}");

                int contentLength = 0;
                while (true)
                {
                    string? header = await ReadLineAsync(ct).ConfigureAwait(false);
                    if (header is null) return;             // EOF mid-headers
                    if (header.Length == 0) break;          // blank line → end of headers

                    int colon = header.IndexOf(':');
                    if (colon > 0 &&
                        header.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(header.AsSpan(colon + 1).Trim(), NumberStyles.Integer,
                                     CultureInfo.InvariantCulture, out int len))
                    {
                        contentLength = len;
                    }
                }

                int discarded = 0;
                if (contentLength > 0)
                {
                    var sink = new byte[Math.Min(contentLength, 8192)];
                    while (discarded < contentLength)
                    {
                        int n = await _stream.ReadAsync(
                            sink.AsMemory(0, Math.Min(sink.Length, contentLength - discarded)), ct).ConfigureAwait(false);
                        if (n == 0) break;                  // EOF mid-body
                        discarded += n;
                    }
                }

                Interlocked.Increment(ref _responsesDrained);
                Log.Info("Reverse", $"← {statusLine} (session={SessionId} purpose={Purpose}, " +
                                    $"discarded {discarded}B body, {ResponsesDrained} total)");
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            Log.Info("Reverse", $"Drain loop ended for session={SessionId} purpose={Purpose}: {ex.GetType().Name} {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error("Reverse", $"Drain loop crashed for session={SessionId} purpose={Purpose}", ex);
        }
        finally
        {
            // EOF or error means the phone is gone: close and unregister.
            Dispose();
        }
    }

    /// <summary>Reads one CRLF-terminated line, or null at EOF. Byte-at-a-time — these lines are short and rare.</summary>
    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder(64);
        while (true)
        {
            int n = await _stream.ReadAsync(_readByte.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0) return sb.Length == 0 ? null : sb.ToString();

            char c = (char)_readByte[0];
            if (c == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append(c);
        }
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { _cts.Cancel(); }     catch { /* already disposed */ }
        try { _stream.Dispose(); } catch { /* already gone */ }
        try { _client?.Dispose(); } catch { /* already gone */ }
        // _cts and _writeLock are deliberately not disposed: the drain loop may
        // still be unwinding on its token, and disposing them under it would
        // race. Neither holds an OS handle here.

        Log.Info("Reverse", $"Channel closed: session={SessionId} purpose={Purpose} remote={Remote} " +
                            $"(sent {RequestsSent}, drained {ResponsesDrained})");
        Closed?.Invoke(this);
    }

    /// <summary>
    /// Drops the C0/C1 control characters that could terminate or forge a header
    /// line on the way out. Tab survives — it is legal inside a field value and
    /// cannot split one.
    /// </summary>
    private static string HeaderSafe(string value)
    {
        int bad = 0;
        foreach (char c in value) if (c != '\t' && char.IsControl(c)) bad++;
        if (bad == 0) return value;

        var sb = new StringBuilder(value.Length - bad);
        foreach (char c in value) if (c == '\t' || !char.IsControl(c)) sb.Append(c);

        Log.Warn("Reverse", $"stripped {bad} control character(s) from an outbound header value");
        return sb.ToString();
    }
    private static string Preview(byte[] body)
    {
        const int Max = 1200;
        string text = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, Max));
        return body.Length > Max ? $"{text}…(+{body.Length - Max}B)" : text;
    }
}

/// <summary>
/// Registry of live <see cref="ReverseChannel"/>s, keyed by the
/// <c>X-Apple-Session-ID</c> GUID that every connection of one AirPlay session
/// carries — the only correlation key available, because /reverse arrives on its
/// own TCP connection, separate from the one carrying /play, /action and /scrub.
///
/// A session has at most one <see cref="ReverseChannel.PurposeEvent"/> channel
/// (a second one is refused, matching UxPlay's 421), but a Photos slideshow
/// opens three <see cref="ReverseChannel.PurposeSlideshow"/> channels, so
/// channels are stored per session as a list.
///
/// One instance lives for the app's lifetime, owned by
/// <c>AirPlayService</c> and handed to every <see cref="AirPlaySession"/> the
/// same way the shared <c>AirPlayVideoPlayer</c> is.
/// </summary>
public sealed class ReverseChannelRegistry : IDisposable
{
    // A single lock rather than a ConcurrentDictionary: registration must check
    // "does this session already have an event channel?" and add atomically, and
    // registrations happen a handful of times per session.
    private readonly object _sync = new();
    private readonly Dictionary<string, List<ReverseChannel>> _bySession = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>Number of live channels across all sessions.</summary>
    public int Count
    {
        get { lock (_sync) { int n = 0; foreach (var l in _bySession.Values) n += l.Count; return n; } }
    }

    /// <summary>
    /// Takes ownership of <paramref name="channel"/> and starts draining it.
    /// Returns false when the channel is a duplicate <c>purpose=event</c> for the
    /// session (the caller should close the socket) or the registry is disposed.
    /// </summary>
    public bool Register(ReverseChannel channel)
    {
        // Subscribe BEFORE the channel becomes reachable. This used to add to
        // _bySession, release the lock, and only then wire Closed — and a write
        // that failed in that window fired Closed with no subscriber, so Detach
        // never ran and the disposed channel stayed in the map forever. Because
        // the duplicate check below did not filter on IsOpen, that zombie then
        // 421ed every future /reverse for the session, permanently.
        channel.Closed += OnChannelClosed;

        lock (_sync)
        {
            if (_disposed)
            {
                Log.Warn("Reverse", $"Register refused (shutting down): session={channel.SessionId} purpose={channel.Purpose}");
                channel.Closed -= OnChannelClosed;
                return false;
            }

            if (!_bySession.TryGetValue(channel.SessionId, out var list))
                _bySession[channel.SessionId] = list = new List<ReverseChannel>(1);

            // Drop anything already closed: a channel the phone hung up on holds
            // no resources but would still block a legitimate re-registration.
            list.RemoveAll(c => !c.IsOpen);

            if (string.Equals(channel.Purpose, ReverseChannel.PurposeEvent, StringComparison.OrdinalIgnoreCase) &&
                list.Exists(c => string.Equals(c.Purpose, ReverseChannel.PurposeEvent, StringComparison.OrdinalIgnoreCase) && c.IsOpen))
            {
                Log.Warn("Reverse", $"Register refused — session={channel.SessionId} already has an event channel");
                channel.Closed -= OnChannelClosed;
                return false;
            }

            list.Add(channel);
        }

        channel.Start();
        Log.Info("Reverse", $"Channel registered: session={channel.SessionId} purpose={channel.Purpose} " +
                            $"remote={channel.Remote} (registry now holds {Count})");
        return true;
    }

    /// <summary>True when the session already owns an <c>event</c> channel (drives the 421 reply).</summary>
    public bool HasChannel(string sessionId, string purpose)
        => TryGet(sessionId, purpose, out _);

    /// <summary>Gets the session's event channel.</summary>
    public bool TryGet(string sessionId, [NotNullWhen(true)] out ReverseChannel? channel)
        => TryGet(sessionId, ReverseChannel.PurposeEvent, out channel);

    /// <summary>Gets the session's first live channel with the given purpose.</summary>
    public bool TryGet(string sessionId, string purpose, [NotNullWhen(true)] out ReverseChannel? channel)
    {
        channel = null;
        if (string.IsNullOrEmpty(sessionId)) return false;

        lock (_sync)
        {
            if (!_bySession.TryGetValue(sessionId, out var list)) return false;
            foreach (var c in list)
            {
                if (string.Equals(c.Purpose, purpose, StringComparison.OrdinalIgnoreCase) && c.IsOpen)
                {
                    channel = c;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>All live channels for a session with the given purpose (slideshow opens three).</summary>
    public IReadOnlyList<ReverseChannel> GetAll(string sessionId, string purpose)
    {
        var result = new List<ReverseChannel>();
        if (string.IsNullOrEmpty(sessionId)) return result;

        lock (_sync)
        {
            if (_bySession.TryGetValue(sessionId, out var list))
                foreach (var c in list)
                    if (string.Equals(c.Purpose, purpose, StringComparison.OrdinalIgnoreCase) && c.IsOpen)
                        result.Add(c);
        }
        return result;
    }

    /// <summary>Convenience: sends a state event on the session's event channel, if it has one.</summary>
    public Task<bool> SendEventAsync(string sessionId, string category, string state,
                                     int sessionCounter = 0, CancellationToken ct = default)
    {
        if (!TryGet(sessionId, out ReverseChannel? channel))
        {
            Log.Warn("Reverse", $"No event channel for session={sessionId} — dropping {category}/{state}");
            return Task.FromResult(false);
        }
        return channel.SendEventAsync(category, state, sessionCounter, ct);
    }

    /// <summary>Removes and disposes one channel.</summary>
    public void Remove(ReverseChannel channel)
    {
        Detach(channel);
        channel.Dispose();
    }

    /// <summary>Removes and disposes every channel of a session (called on session teardown).</summary>
    public void RemoveAllForSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;

        List<ReverseChannel>? list;
        lock (_sync)
        {
            if (!_bySession.Remove(sessionId, out list) || list.Count == 0) return;
        }

        Log.Info("Reverse", $"Session teardown: closing {list.Count} channel(s) for session={sessionId}");
        foreach (var c in list)
        {
            c.Closed -= OnChannelClosed;
            c.Dispose();
        }
    }

    /// <summary>
    /// Closes every channel but keeps the registry usable — the user ended the
    /// current AirPlay session and the next phone may connect right away.
    /// </summary>
    public void CloseAll() => CloseAllCore(markDisposed: false);

    public void Dispose() => CloseAllCore(markDisposed: true);

    private void CloseAllCore(bool markDisposed)
    {
        var all = new List<ReverseChannel>();
        lock (_sync)
        {
            if (_disposed) return;
            if (markDisposed) _disposed = true;
            foreach (var list in _bySession.Values) all.AddRange(list);
            _bySession.Clear();
        }

        if (all.Count > 0)
            Log.Info("Reverse", $"Closing {all.Count} channel(s) ({(markDisposed ? "registry disposed" : "session ended")})");
        foreach (var c in all)
        {
            c.Closed -= OnChannelClosed;
            c.Dispose();
        }
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    /// <summary>The channel closed itself (phone hung up, or a write failed): drop the reference.</summary>
    private void OnChannelClosed(ReverseChannel channel) => Detach(channel);

    private void Detach(ReverseChannel channel)
    {
        lock (_sync)
        {
            if (_bySession.TryGetValue(channel.SessionId, out var list) && list.Remove(channel) && list.Count == 0)
                _bySession.Remove(channel.SessionId);
        }
        channel.Closed -= OnChannelClosed;
    }
}
