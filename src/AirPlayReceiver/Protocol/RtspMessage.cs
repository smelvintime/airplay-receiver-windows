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
    /// Returns null when the stream closes cleanly between messages.
    /// </summary>
    public static async Task<RtspMessage?> ReadAsync(
        Stream stream, CancellationToken ct = default)
    {
        // Read the request/status line + headers (terminated by a blank line).
        var headerLines = new List<string>();
        var lineBuffer  = new StringBuilder();

        while (true)
        {
            int b = await ReadByteAsync(stream, ct);
            if (b < 0) return null; // EOF

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
