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
        // Read the request/status line + headers (terminated by \r\n\r\n).
        var headerLines = new List<string>();
        var lineBuffer  = new StringBuilder();

        int prev = -1;
        while (true)
        {
            int b = await ReadByteAsync(stream, ct);
            if (b < 0) return null; // EOF

            if (b == '\n' && prev == '\r')
            {
                // Remove trailing \r
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

            prev = b;
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
            msg.Headers[name] = value;
        }

        // Read body if Content-Length is present
        if (msg.ContentLength > 0)
        {
            msg.Body = new byte[msg.ContentLength];
            int read = 0;
            while (read < msg.ContentLength)
            {
                int n = await stream.ReadAsync(msg.Body.AsMemory(read), ct);
                if (n == 0) break;
                read += n;
            }
        }

        return msg;
    }

    // ── Serialisation ─────────────────────────────────────────────────────────

    /// <summary>Serialises an RTSP response with the given status code and reason.</summary>
    public static byte[] BuildResponse(
        int statusCode,
        string reasonPhrase,
        int cseq,
        Dictionary<string, string>? headers = null,
        byte[]? body = null)
    {
        var sb = new StringBuilder();
        sb.Append($"RTSP/1.0 {statusCode} {reasonPhrase}\r\n");
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

    public override string ToString()
        => $"{Method} {Uri} {Version}  CSeq={CSeq}  body={Body?.Length ?? 0}B";
}
