using AirPlayReceiver.Protocol;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace AirPlayReceiver.HlsHarness;

/// <summary>
/// The iPhone side of the loopback simulation.
///
/// Speaks the real wire protocol: opens the reverse (PTTH) channel and keeps it,
/// posts /play and /action on separate short-lived connections the way iOS does,
/// reads the receiver's FCUP requests off the reverse socket and answers each
/// one with a 200 plus, later, a <c>POST /action</c> carrying the playlist in a
/// binary-plist <c>&lt;data&gt;</c> blob.
/// </summary>
internal sealed class FakePhone : IAsyncDisposable
{
    private readonly int    _receiverPort;
    private readonly TcpClient      _reverse = new();
    private NetworkStream? _reverseStream;

    public string SessionId { get; } = Guid.NewGuid().ToString().ToUpperInvariant();

    public FakePhone(int receiverPort) => _receiverPort = receiverPort;

    // ── Reverse (PTTH) channel ────────────────────────────────────────────────

    /// <summary>Performs the <c>POST /reverse</c> + <c>101 Switching Protocols</c> upgrade.</summary>
    public async Task<string> OpenReverseChannelAsync()
    {
        await _reverse.ConnectAsync("127.0.0.1", _receiverPort);
        _reverseStream = _reverse.GetStream();

        string request =
            "POST /reverse HTTP/1.1\r\n" +
            "Upgrade: PTTH/1.0\r\n" +
            "Connection: Upgrade\r\n" +
            "X-Apple-Purpose: event\r\n" +
            $"{ReverseChannel.SessionIdHeader}: {SessionId}\r\n" +
            "CSeq: 0\r\n" +
            "Content-Length: 0\r\n" +
            "\r\n";
        await _reverseStream.WriteAsync(Encoding.ASCII.GetBytes(request));
        await _reverseStream.FlushAsync();

        (string statusLine, _, _) = await ReadHttpAsync(_reverseStream);
        return statusLine;
    }

    /// <summary>
    /// Waits for the receiver's next FCUP request on the reverse socket, answers
    /// it with a bare 200 (as iOS does), and returns the parsed XML plist body.
    /// </summary>
    public async Task<FcupRequest> ReadFcupRequestAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        RtspMessage msg = await RtspMessage.ReadAsync(_reverseStream!, cts.Token)
                          ?? throw new InvalidOperationException("reverse channel closed while waiting for an FCUP request");

        await _reverseStream!.WriteAsync(
            Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"), cts.Token);
        await _reverseStream.FlushAsync(cts.Token);

        string bodyText = Encoding.UTF8.GetString(msg.Body ?? Array.Empty<byte>());
        var root = XmlPlist.Parse(bodyText) as Dictionary<string, object?>
                   ?? throw new InvalidOperationException("FCUP request body is not a plist dictionary");

        return new FcupRequest(msg.Method, msg.Path, msg.Headers, msg.ContentType, root, bodyText);
    }

    /// <summary>
    /// True when the receiver has sent nothing on the reverse channel after
    /// <paramref name="settle"/>. Checked with <see cref="TcpClient.Available"/>
    /// rather than a cancelled read, so the socket stays usable for the next step.
    /// </summary>
    public async Task<bool> NoPendingReverseDataAsync(TimeSpan settle)
    {
        await Task.Delay(settle);
        return _reverse.Available == 0;
    }

    // ── Forward requests (phone → receiver) ───────────────────────────────────

    /// <summary>Sends <c>POST /play</c> with the binary plist iOS sends for a video.</summary>
    public Task<HttpReply> PlayAsync(string contentLocation, string uuid, double startPositionSeconds)
    {
        var plist = new Dictionary<string, object?>
        {
            ["uuid"]                   = uuid,
            ["Content-Location"]       = contentLocation,
            ["Start-Position-Seconds"] = startPositionSeconds,
            ["clientProcName"]         = "YouTube",
            ["rate"]                   = 1.0,
        };
        return PostAsync("/play", BinaryPlist.Write(plist));
    }

    /// <summary>Sends the FCUP response for one playlist: <c>POST /action</c>, type unhandledURLResponse.</summary>
    public Task<HttpReply> FcupResponseAsync(int requestId, string url, string playlist)
    {
        var plist = new Dictionary<string, object?>
        {
            ["type"] = "unhandledURLResponse",
            ["params"] = new Dictionary<string, object?>
            {
                ["FCUP_Response_StatusCode"] = 200L,
                ["FCUP_Response_RequestID"]  = (long)requestId,
                ["FCUP_Response_URL"]        = url,
                // Not null-terminated, exactly like the real blob.
                ["FCUP_Response_Data"]       = Encoding.UTF8.GetBytes(playlist),
            },
        };
        return PostAsync("/action", BinaryPlist.Write(plist));
    }

    public Task<HttpReply> PlaylistRemoveAsync(string uuid)
    {
        var plist = new Dictionary<string, object?>
        {
            ["type"] = "playlistRemove",
            ["params"] = new Dictionary<string, object?>
            {
                ["item"] = new Dictionary<string, object?> { ["uuid"] = uuid },
            },
        };
        return PostAsync("/action", BinaryPlist.Write(plist));
    }

    public Task<HttpReply> PlaylistInsertAsync(string uuid, string? contentLocation)
    {
        var item = new Dictionary<string, object?> { ["uuid"] = uuid };
        if (contentLocation is not null) item["Content-Location"] = contentLocation;

        var plist = new Dictionary<string, object?>
        {
            ["type"]   = "playlistInsert",
            ["params"] = new Dictionary<string, object?> { ["item"] = item },
        };
        return PostAsync("/action", BinaryPlist.Write(plist));
    }

    /// <summary>Posts a request with a wrong session id, to exercise the /action guard.</summary>
    public Task<HttpReply> PostWithSessionAsync(string path, byte[] body, string? sessionId)
        => PostAsync(path, body, sessionId);

    private async Task<HttpReply> PostAsync(string path, byte[] body, string? sessionIdOverride = null)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", _receiverPort);
        await using NetworkStream stream = client.GetStream();

        string sid = sessionIdOverride ?? SessionId;
        var head = new StringBuilder();
        head.Append("POST ").Append(path).Append(" HTTP/1.1\r\n");
        head.Append("Content-Type: application/x-apple-binary-plist\r\n");
        head.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        head.Append("User-Agent: AirPlay/670.6.2\r\n");
        head.Append("CSeq: 1\r\n");
        if (sid.Length > 0) head.Append(ReverseChannel.SessionIdHeader).Append(": ").Append(sid).Append("\r\n");
        head.Append("\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
        await stream.WriteAsync(body);
        await stream.FlushAsync();

        (string statusLine, Dictionary<string, string> headers, byte[] responseBody) = await ReadHttpAsync(stream);
        return new HttpReply(statusLine, headers, responseBody);
    }

    // ── Raw HTTP response reader ──────────────────────────────────────────────

    private static async Task<(string StatusLine, Dictionary<string, string> Headers, byte[] Body)> ReadHttpAsync(Stream stream)
    {
        var lines  = new List<string>();
        var line   = new StringBuilder();
        var single = new byte[1];

        while (true)
        {
            int n = await stream.ReadAsync(single.AsMemory(0, 1));
            if (n == 0) break;
            if (single[0] == '\n')
            {
                string text = line.ToString().TrimEnd('\r');
                line.Clear();
                if (text.Length == 0) break;
                lines.Add(text);
            }
            else line.Append((char)single[0]);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Count; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon > 0) headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        byte[] body = Array.Empty<byte>();
        if (headers.TryGetValue("Content-Length", out string? cl) &&
            int.TryParse(cl, NumberStyles.Integer, CultureInfo.InvariantCulture, out int length) && length > 0)
        {
            body = new byte[length];
            int read = 0;
            while (read < length)
            {
                int n = await stream.ReadAsync(body.AsMemory(read));
                if (n == 0) break;
                read += n;
            }
        }

        return (lines.Count > 0 ? lines[0] : string.Empty, headers, body);
    }

    public ValueTask DisposeAsync()
    {
        _reverseStream?.Dispose();
        _reverse.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed record FcupRequest(
    string Method,
    string Path,
    Dictionary<string, string> Headers,
    string ContentType,
    Dictionary<string, object?> Body,
    string BodyXml)
{
    public Dictionary<string, object?>? Request => Body.GetValueOrDefault("request") as Dictionary<string, object?>;
    public Dictionary<string, object?>? RequestHeaders => Request?.GetValueOrDefault("FCUP_Response_Headers") as Dictionary<string, object?>;
    public string? Url       => Request?.GetValueOrDefault("FCUP_Response_URL") as string;
    public int     RequestId => Request?.GetValueOrDefault("FCUP_Response_RequestID") is long l ? (int)l : -1;
}

internal sealed record HttpReply(string StatusLine, Dictionary<string, string> Headers, byte[] Body)
{
    public int Status => int.TryParse(StatusLine.Split(' ').ElementAtOrDefault(1), out int s) ? s : -1;
    public string Text => Encoding.UTF8.GetString(Body);
}
