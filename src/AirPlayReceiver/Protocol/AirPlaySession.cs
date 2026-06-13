using AirPlayReceiver.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AirPlayReceiver.Protocol;

/// <summary>
/// Handles one iOS-to-receiver AirPlay session.
///
/// Implements the RTSP verb dispatch loop and routes each message to the
/// appropriate handler.  Pairing cryptography (SRP-6a, Curve25519, Ed25519)
/// is delegated to <see cref="PairingHandler"/>.  RTP stream management is
/// delegated to <see cref="RtpReceiver"/>.
///
/// AirPlay RTSP verb sequence for screen mirroring:
///   OPTIONS  →  GET_PARAMETER (ping)  →  POST /pair-setup  →
///   POST /pair-verify  →  SETUP  →  RECORD  →  …  →  TEARDOWN
///
/// Reference: UxPlay lib/raop_handler.c
/// </summary>
public sealed class AirPlaySession : IAsyncDisposable
{
    // ── Dependencies injected at construction ─────────────────────────────────

    private readonly RtpReceiver    _rtpReceiver;
    private readonly PairingHandler _pairing;

    // Callbacks into the UI layer
    public event Action?         StreamStarted;
    public event Action?         StreamStopped;

    // ── Session state ─────────────────────────────────────────────────────────

    private bool   _isRecording;
    private int    _videoDataPort;   // UDP port for RTP video (set in SETUP)
    private int    _videoControlPort;
    private string _sessionId = Guid.NewGuid().ToString("N")[..8];

    // ── Construction ──────────────────────────────────────────────────────────

    public AirPlaySession(RtpReceiver rtpReceiver, PairingHandler pairing)
    {
        _rtpReceiver = rtpReceiver;
        _pairing     = pairing;
    }

    // ── Main loop ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads RTSP messages from <paramref name="stream"/> until the session
    /// ends or <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task RunAsync(Stream stream, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            RtspMessage? msg = await RtspMessage.ReadAsync(stream, ct);
            if (msg is null) break; // client closed connection

            Console.WriteLine($"[RTSP→] {msg}");

            byte[] response = msg.Method switch
            {
                "OPTIONS"       => HandleOptions(msg),
                "GET_PARAMETER" => HandleGetParameter(msg),
                "POST"          => HandlePost(msg),
                "SETUP"         => HandleSetup(msg),
                "RECORD"        => HandleRecord(msg),
                "SET_PARAMETER" => HandleSetParameter(msg),
                "FLUSH"         => HandleFlush(msg),
                "TEARDOWN"      => HandleTeardown(msg),
                _               => BuildOk(msg.CSeq),
            };

            Console.WriteLine($"[RTSP←] {response.Length}B");
            await stream.WriteAsync(response, ct);

            if (msg.Method == "TEARDOWN") break;
        }

        await StopStreamAsync();
    }

    // ── RTSP verb handlers ────────────────────────────────────────────────────

    private byte[] HandleOptions(RtspMessage msg)
    {
        // Declare every method we support.  iOS checks for RECORD here.
        var headers = new Dictionary<string, string>
        {
            ["Public"] = "ANNOUNCE, SETUP, RECORD, PAUSE, FLUSH, TEARDOWN, " +
                         "OPTIONS, GET_PARAMETER, SET_PARAMETER, POST, GET"
        };
        return RtspMessage.BuildResponse(200, "OK", msg.CSeq, headers);
    }

    private byte[] HandleGetParameter(RtspMessage msg)
    {
        // iOS sends GET_PARAMETER * as a keep-alive ping.  Reply with 200 OK,
        // empty body.  If the body contains parameter names, echo them back
        // with empty values (we don't implement server-side parameters).
        return BuildOk(msg.CSeq);
    }

    private byte[] HandlePost(RtspMessage msg)
    {
        // AirPlay uses HTTP-style POST for the pairing sub-protocol.
        return msg.Uri switch
        {
            "/pair-setup"        => _pairing.HandlePairSetup(msg),
            "/pair-verify"       => _pairing.HandlePairVerify(msg),
            "/pair-add"          => _pairing.HandlePairAdd(msg),
            "/fp-setup"          => _pairing.HandleFairPlaySetup(msg),
            "/getProperty"       => HandleGetProperty(msg),
            "/setProperty"       => HandleSetProperty(msg),
            _ => RtspMessage.BuildResponse(404, "Not Found", msg.CSeq),
        };
    }

    private byte[] HandleSetup(RtspMessage msg)
    {
        // The SETUP body is a binary plist (AirPlay 2) or custom TLV (AirPlay 1)
        // that describes the requested stream type and the client's RTP ports.
        //
        // Simplified handling: extract client's video data port from the
        // "Transport" header (AirPlay 1 style) as a starting point.
        // Full AirPlay 2 requires parsing the binary plist body.
        //
        // Transport header example:
        //   RTP/AVP/UDP;unicast;interleaved=0-1;mode=record;
        //   control_port=6001;timing_port=6002

        if (msg.Headers.TryGetValue("Transport", out string? transport))
        {
            ParseTransportHeader(transport);
        }

        // Choose our server-side ports.
        int serverDataPort    = _rtpReceiver.VideoDataPort;
        int serverControlPort = _rtpReceiver.VideoControlPort;

        var headers = new Dictionary<string, string>
        {
            ["Transport"] = $"RTP/AVP/UDP;unicast;mode=record;" +
                            $"server_port={serverDataPort};" +
                            $"control_port={serverControlPort}",
            ["Session"]   = _sessionId,
        };

        return RtspMessage.BuildResponse(200, "OK", msg.CSeq, headers);
    }

    private byte[] HandleRecord(RtspMessage msg)
    {
        // iOS sends RECORD to begin the RTP stream.
        _isRecording = true;
        _ = _rtpReceiver.StartAsync();
        StreamStarted?.Invoke();

        var headers = new Dictionary<string, string>
        {
            ["Session"]      = _sessionId,
            ["Audio-Latency"] = "11025",
        };
        return RtspMessage.BuildResponse(200, "OK", msg.CSeq, headers);
    }

    private byte[] HandleSetParameter(RtspMessage msg)
    {
        // SET_PARAMETER carries parameter updates (e.g. volume).
        // We parse but don't act on them in this skeleton.
        return BuildOk(msg.CSeq);
    }

    private byte[] HandleFlush(RtspMessage msg)
    {
        _rtpReceiver.Flush();
        return BuildOk(msg.CSeq);
    }

    private byte[] HandleTeardown(RtspMessage msg)
    {
        Console.WriteLine("[RTSP] TEARDOWN received.");
        return BuildOk(msg.CSeq);
    }

    private byte[] HandleGetProperty(RtspMessage msg)   => BuildOk(msg.CSeq);
    private byte[] HandleSetProperty(RtspMessage msg)   => BuildOk(msg.CSeq);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[] BuildOk(int cseq)
        => RtspMessage.BuildResponse(200, "OK", cseq);

    private void ParseTransportHeader(string transport)
    {
        foreach (string part in transport.Split(';'))
        {
            string p = part.Trim();
            if (p.StartsWith("control_port=", StringComparison.OrdinalIgnoreCase))
                int.TryParse(p[13..], out _videoControlPort);
        }
    }

    private async Task StopStreamAsync()
    {
        if (_isRecording)
        {
            _isRecording = false;
            await _rtpReceiver.StopAsync();
            StreamStopped?.Invoke();
        }
    }

    public async ValueTask DisposeAsync() => await StopStreamAsync();
}
