using AirPlayReceiver.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AirPlayReceiver.Protocol;

/// <summary>
/// Parsed representation of an RTSP/HTTP request or response.
/// AirPlay uses an HTTP-compatible framing (request-line, headers, blank line, body).
/// </summary>
public sealed class RtspMessage
{
    /// <summary>
    /// Largest request body accepted, checked <b>before</b> a single byte is
    /// allocated.
    ///
    /// Content-Length arrives unauthenticated on port 7000 and used to be handed
    /// straight to <c>new byte[]</c>, so one small packet claiming
    /// <c>Content-Length: 2147483647</c> forced a ~2 GB allocation. The largest
    /// legitimate body is a photo — a full-resolution iPhone JPEG is a few MB, and
    /// <see cref="PhotoCache"/> refuses any single asset over its 192 MB budget —
    /// so 64 MB sits far above anything real while keeping the allocation bounded.
    /// </summary>
    public const int MaxBodyBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Longest single request line or header line accepted. The reader below is
    /// byte-at-a-time into a StringBuilder with no natural end, so without this a
    /// sender that never emits a newline grows one allocation until the process
    /// dies. iOS's longest real header is a few hundred bytes of Cookie; 8 KB is
    /// what common HTTP servers allow for the same reason.
    /// </summary>
    public const int MaxHeaderLineBytes = 8 * 1024;

    /// <summary>
    /// Ceiling on the whole header block. Without it, a sender that simply never
    /// emits the blank line runs the same attack one short line at a time.
    /// </summary>
    public const int MaxHeaderBytes = 64 * 1024;

    /// <summary>
    /// How long a connection may take to deliver one complete message once its
    /// first byte has arrived — the classic slowloris dribble, bounded. Deliberately
    /// generous: the worst legitimate case is a multi-megabyte photo body over
    /// congested Wi-Fi.
    /// </summary>
    public static readonly TimeSpan MessageReadTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a freshly accepted connection may stay silent before its first
    /// byte. Callers pass this for the first message only (see
    /// <c>AirPlaySession.RunAsync</c>): an established AirPlay control connection
    /// is legitimately quiet for long stretches — mirroring carries its media on
    /// UDP — but a connection that never says anything at all is only there to
    /// hold a slot against the server's concurrency cap.
    /// </summary>
    public static readonly TimeSpan FirstMessageIdleTimeout = TimeSpan.FromSeconds(15);

    // Request fields
    public string Method    { get; init; } = string.Empty;
    public string Uri       { get; init; } = string.Empty;
    public string Version   { get; init; } = "RTSP/1.0";

    // Common fields
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[]? Body { get; set; }

    // Convenience
    public int    CSeq        => Headers.TryGetValue("CSeq", out var v) ? int.Parse(v) : 0;
    public string ContentType => Headers.TryGetValue("Content-Type", out var v) ? v : string.Empty;

    /// <summary>The request path without the query string (e.g. "/rate" for "/rate?value=1.0").</summary>
    public string Path => Uri.Split('?', 2)[0];

    /// <summary>Returns a query-string parameter value (e.g. <c>value</c> from "/rate?value=1.0"), or null.</summary>
    public string? QueryValue(string name)
    {
        int q = Uri.IndexOf('?');
        if (q < 0) return null;
        foreach (string pair in Uri[(q + 1)..].Split('&'))
        {
            int eq = pair.IndexOf('=');
            string key = eq < 0 ? pair : pair[..eq];
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return eq < 0 ? string.Empty : pair[(eq + 1)..];
        }
        return null;
    }
    public int    ContentLength
    {
        get
        {
            if (Headers.TryGetValue("Content-Length", out var v) && int.TryParse(v, out int n))
                return n;
            return 0;
        }
    }

    // ── Parsing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads one complete RTSP/HTTP message from <paramref name="stream"/>.
    /// Returns null when the stream closes cleanly between messages, and also when
    /// the message is refused (oversized, malformed or too slow) — in every case
    /// the caller's answer is the same: stop reading this connection.
    /// </summary>
    /// <param name="idleTimeout">
    /// Optional bound on the wait for the message's <i>first</i> byte. Pass
    /// <see cref="FirstMessageIdleTimeout"/> on a freshly accepted connection and
    /// null afterwards; see that constant for why an established connection has no
    /// idle bound.
    /// </param>
    public static async Task<RtspMessage?> ReadAsync(
        Stream stream, CancellationToken ct = default, TimeSpan? idleTimeout = null)
    {
        // Two deadlines on one timer: the idle bound (if the caller asked for one)
        // until the first byte, then MessageReadTimeout for the rest. The token the
        // callers hand us is the service-lifetime token, which never fires while
        // the app is up, so a connection needs its own clock.
        using var deadline = new CancellationTokenSource();
        using var linked   = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        CancellationToken read = linked.Token;
        if (idleTimeout is { } idle) deadline.CancelAfter(idle);

        bool started = false;
        try
        {
            return await ReadCoreAsync(stream, read, deadline, () => started = true);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            Log.Warn("RTSP", started
                ? $"Message not complete within {MessageReadTimeout.TotalSeconds:F0}s — closing the connection"
                : $"No request within {idleTimeout?.TotalSeconds ?? 0:F0}s of connecting — closing the connection");
            return null;
        }
    }

    private static async Task<RtspMessage?> ReadCoreAsync(
        Stream stream, CancellationToken ct, CancellationTokenSource deadline, Action onFirstByte)
    {
        // Read the request/status line + headers (terminated by a blank line).
        var headerLines = new List<string>();
        var lineBuffer  = new StringBuilder();
        int headerBytes = 0;
        bool first      = true;

        while (true)
        {
            int b = await ReadByteAsync(stream, ct);
            if (b < 0) return null; // EOF

            if (first)
            {
                // The message has started: swap the idle bound for the completion
                // bound (CancelAfter restarts the timer).
                first = false;
                onFirstByte();
                deadline.CancelAfter(MessageReadTimeout);
            }

            if (++headerBytes > MaxHeaderBytes)
            {
                Log.Error("RTSP", $"Header block exceeded {MaxHeaderBytes}B without a blank line " +
                                  "— closing the connection");
                return null;
            }

            // Terminate on ANY \n and strip one optional preceding \r — the same
            // rule ReverseChannel.ReadLineAsync already uses.
            //
            // Requiring CRLF (what this used to do) meant a lone \n fell through to
            // the else branch and was appended as ordinary data, so
            // "X-Apple-Session-ID: abc\nX-Evil: 1\r\n" parsed as ONE header whose
            // value still carried the embedded LF (.Trim() only trims the ends).
            // That value is written verbatim into the headers we send the phone
            // (ReverseChannel.SendAsync) and into FFmpeg's "headers" option, so a
            // bare LF was an outbound header-injection primitive for anything on
            // the LAN that reached /reverse or /play first.
            if (b == '\n')
            {
                if (lineBuffer.Length > 0 && lineBuffer[^1] == '\r')
                    lineBuffer.Length--;

                string line = lineBuffer.ToString();
                lineBuffer.Clear();

                if (line.Length == 0)
                    break; // blank line → end of headers

                headerLines.Add(line);
            }
            else
            {
                if (lineBuffer.Length >= MaxHeaderLineBytes)
                {
                    Log.Error("RTSP", $"Header line exceeded {MaxHeaderLineBytes}B without a newline " +
                                      "— closing the connection");
                    return null;
                }
                lineBuffer.Append((char)b);
            }
        }

        if (headerLines.Count == 0) return null;

        // Parse request line: "METHOD URI VERSION"
        string[] parts = headerLines[0].Split(' ', 3);
        if (parts.Length < 3) return null;

        var msg = new RtspMessage
        {
            Method  = parts[0],
            Uri     = parts[1],
            Version = parts[2],
        };

        // Parse headers
        for (int i = 1; i < headerLines.Count; i++)
        {
            int colon = headerLines[i].IndexOf(':');
            if (colon < 0) continue;
            string name  = headerLines[i][..colon].Trim();
            string value = headerLines[i][(colon + 1)..].Trim();

            // Belt and braces behind the bare-LF fix above: a stray \r (or any
            // other C0 control) inside a header still reaches the wire verbatim
            // when we echo the value onwards, so refuse the whole message rather
            // than pass a smuggled control character on. No real sender emits one.
            if (ContainsControlCharacter(name) || ContainsControlCharacter(value))
            {
                Log.Error("RTSP", $"{msg.Method} {msg.Uri}: header '{name}' carries a control character " +
                                  "— refusing the message (header smuggling)");
                return null;
            }

            msg.Headers[name] = value;
        }

        // Read body if Content-Length is present
        if (msg.ContentLength > MaxBodyBytes)
        {
            // Returning null ends the read loop, so the caller closes the socket:
            // we cannot resynchronise on a body we refuse to read.
            Log.Error("RTSP", $"{msg.Method} {msg.Uri}: Content-Length {msg.ContentLength} exceeds the " +
                              $"{MaxBodyBytes}B ceiling — refusing the message and closing the connection");
            return null;
        }

        if (msg.ContentLength > 0)
        {
            msg.Body = new byte[msg.ContentLength];
            int read = 0;
            while (read < msg.ContentLength)
            {
                int n = await stream.ReadAsync(msg.Body.AsMemory(read), ct);
                if (n == 0) break;   // EOF mid-body
                read += n;
            }

            // A short read used to be swallowed: the `read` count was dropped and
            // the zero-padded tail handed on as if it were the whole body. Nothing
            // downstream can tell the difference, and PhotoHandler caches what it
            // is given — so one truncated PUT /photo poisons that asset key for the
            // rest of the session, because a later displayCached hits the cache and
            // never triggers the 412 that would make the sender re-push it.
            if (read != msg.ContentLength)
            {
                Log.Error("RTSP", $"{msg.Method} {msg.Uri}: body truncated at {read}B of {msg.ContentLength}B " +
                                  "— refusing the message (a partial body is not valid data)");
                return null;
            }
        }

        return msg;
    }

    // ── Serialisation ─────────────────────────────────────────────────────────

    /// <summary>Serialises a response with the given status code and reason.</summary>
    /// <param name="protocol">Protocol/version for the status line (echo the request's — "RTSP/1.0" or "HTTP/1.1").</param>
    public static byte[] BuildResponse(
        int statusCode,
        string reasonPhrase,
        int cseq,
        Dictionary<string, string>? headers = null,
        byte[]? body = null,
        string protocol = "RTSP/1.0")
    {
        var sb = new StringBuilder();
        sb.Append($"{protocol} {statusCode} {reasonPhrase}\r\n");
        sb.Append($"CSeq: {cseq}\r\n");

        if (headers is not null)
            foreach (var (k, v) in headers)
                sb.Append($"{k}: {v}\r\n");

        if (body is { Length: > 0 })
            sb.Append($"Content-Length: {body.Length}\r\n");

        sb.Append("\r\n");

        byte[] header = Encoding.ASCII.GetBytes(sb.ToString());

        if (body is { Length: > 0 })
        {
            var result = new byte[header.Length + body.Length];
            header.CopyTo(result, 0);
            body.CopyTo(result, header.Length);
            return result;
        }

        return header;
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken ct)
    {
        var buf = new byte[1];
        int n   = await stream.ReadAsync(buf.AsMemory(), ct);
        return n == 0 ? -1 : buf[0];
    }

    /// <summary>
    /// True when <paramref name="s"/> holds a C0/C1 control character. Tab is
    /// allowed: RFC 7230 permits it inside a field value, and it cannot terminate
    /// a header line on any writer we feed.
    /// </summary>
    private static bool ContainsControlCharacter(string s)
    {
        foreach (char c in s)
            if (c != '\t' && (c < ' ' || (c >= '\u007F' && c <= '\u009F')))
                return true;
        return false;
    }

    public override string ToString()
        => $"{Method} {Uri} {Version}  CSeq={CSeq}  body={Body?.Length ?? 0}B";
}
