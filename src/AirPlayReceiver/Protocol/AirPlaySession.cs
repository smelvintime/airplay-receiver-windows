using AirPlayReceiver.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
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

    private readonly RtpReceiver?   _rtpReceiver;   // null if FFmpeg is unavailable
    private readonly PairingHandler _pairing;
    private readonly DeviceInfo     _deviceInfo;
    private readonly VideoDecoder?  _decoder;       // mirror video sink (null if FFmpeg absent)

    // Callbacks into the UI layer
    public event Action?         StreamStarted;
    public event Action?         StreamStopped;

    // ── Session state ─────────────────────────────────────────────────────────

    private bool   _isRecording;
    private string _sessionId = Guid.NewGuid().ToString("N")[..8];

    // ── AirPlay 2 mirroring media state (set up during SETUP) ─────────────────
    private MirrorStreamReceiver? _mirror;        // TCP video stream
    private TcpListener?          _eventListener; // event channel (accept + ignore)
    private UdpClient?            _timing;        // timing/NTP channel (minimal)
    private byte[]?               _streamEkey;    // FairPlay-encrypted stream key (from SETUP)
    private byte[]?               _streamEiv;     // stream AES IV (from SETUP)
    private byte[]?               _streamAesKey;  // FairPlay-decrypted 16-byte AES key
    private long                  _streamConnectionId;

    // ── Construction ──────────────────────────────────────────────────────────

    public AirPlaySession(RtpReceiver? rtpReceiver, PairingHandler pairing, DeviceInfo deviceInfo, VideoDecoder? decoder)
    {
        _rtpReceiver = rtpReceiver;
        _pairing     = pairing;
        _deviceInfo  = deviceInfo;
        _decoder     = decoder;
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

            System.Diagnostics.Debug.WriteLine($"[RTSP→] {msg}");

            byte[] response = msg.Method switch
            {
                "OPTIONS"       => HandleOptions(msg),
                "GET"           => HandleGet(msg),
                "GET_PARAMETER" => HandleGetParameter(msg),
                "POST"          => HandlePost(msg),
                "SETUP"         => HandleSetup(msg),
                "RECORD"        => HandleRecord(msg),
                "SET_PARAMETER" => HandleSetParameter(msg),
                "FLUSH"         => HandleFlush(msg),
                "TEARDOWN"      => HandleTeardown(msg),
                _               => BuildOk(msg.CSeq),
            };

            System.Diagnostics.Debug.WriteLine($"[RTSP←] {response.Length}B");
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

    private byte[] HandleGet(RtspMessage msg) => msg.Uri switch
    {
        "/info" => HandleInfo(msg),
        _       => BuildOk(msg.CSeq),
    };

    /// <summary>
    /// Returns the receiver's capability plist. iOS calls GET /info after SETUP to
    /// learn the display geometry it should encode the mirror stream at; an empty
    /// reply makes it abort. Structure follows UxPlay lib/raop_handlers.h.
    /// </summary>
    private byte[] HandleInfo(RtspMessage msg)
    {
        // The first /info (a "qualifier" request) carries a plist body; iOS already
        // has our TXT records from mDNS, so an empty 200 is accepted there. The
        // post-SETUP /info has no body and needs the full capability plist below.
        if (msg.Body is { Length: > 0 })
            return RtspMessage.BuildResponse(200, "OK", msg.CSeq);

        var displays = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["uuid"]           = "e0ff8a27-6738-3d56-8a16-cc53aacee925",
                ["widthPhysical"]  = (long)0,
                ["heightPhysical"] = (long)0,
                ["width"]          = (long)_deviceInfo.DisplayWidth,
                ["height"]         = (long)_deviceInfo.DisplayHeight,
                ["widthPixels"]    = (long)_deviceInfo.DisplayWidth,
                ["heightPixels"]   = (long)_deviceInfo.DisplayHeight,
                ["rotation"]       = false,
                ["refreshRate"]    = 1.0 / _deviceInfo.MaxFps,
                ["maxFPS"]         = (long)_deviceInfo.MaxFps,
                ["overscanned"]    = false,
                ["features"]       = (long)14,
            },
        };

        var audioFormat = new Dictionary<string, object?>
        {
            ["type"]               = (long)100,
            ["audioInputFormats"]  = (long)0x3fffffc,
            ["audioOutputFormats"] = (long)0x3fffffc,
        };

        var info = new Dictionary<string, object?>
        {
            ["deviceID"]                 = _deviceInfo.DeviceId,
            ["macAddress"]               = _deviceInfo.DeviceId,
            ["pk"]                       = _deviceInfo.PublicKey,
            ["features"]                 = _deviceInfo.Features,
            ["name"]                     = _deviceInfo.Name,
            ["pi"]                       = _deviceInfo.Pi,
            ["vv"]                       = (long)2,
            ["statusFlags"]              = (long)68,
            ["keepAliveLowPower"]        = (long)1,
            ["keepAliveSendStatsAsBody"] = true,
            ["sourceVersion"]            = _deviceInfo.SourceVersion,
            ["model"]                    = _deviceInfo.Model,
            ["initialVolume"]            = 0.0,
            ["audioFormats"]             = new List<object?> { audioFormat },
            ["displays"]                 = displays,
        };

        byte[] body = BinaryPlist.Write(info);
        System.Diagnostics.Debug.WriteLine($"[Info] returning device info plist ({body.Length}B)");

        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/x-apple-binary-plist",
        };
        return RtspMessage.BuildResponse(200, "OK", msg.CSeq, headers, body);
    }

    private byte[] HandleSetup(RtspMessage msg)
    {
        // AirPlay 2 SETUP carries a binary plist and happens in (up to) two phases
        // on the same RTSP connection:
        //   phase 1 — has "ekey"/"eiv" + "timingPort"; we reply eventPort/timingPort
        //   phase 2 — has a "streams" array (type 110 = mirroring); we open a TCP
        //             data port and reply with it.
        // A single request may also contain both.
        byte[] body = msg.Body ?? Array.Empty<byte>();

        object? parsed;
        try { parsed = BinaryPlist.Parse(body); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Setup] plist parse failed: {ex.Message}");
            return RtspMessage.BuildResponse(400, "Bad Request", msg.CSeq);
        }

        if (parsed is not Dictionary<string, object?> root)
            return RtspMessage.BuildResponse(400, "Bad Request", msg.CSeq);

        var response = new Dictionary<string, object?>();

        // ── Phase 1: key + timing setup ───────────────────────────────────────
        if (root.TryGetValue("ekey", out var ekeyObj) && ekeyObj is byte[] ekey &&
            root.TryGetValue("eiv",  out var eivObj)  && eivObj  is byte[] eiv)
        {
            _streamEkey = ekey;
            _streamEiv  = eiv;
            System.Diagnostics.Debug.WriteLine($"[Setup] phase 1: ekey={ekey.Length}B eiv={eiv.Length}B");

            // FairPlay-decrypt the stream key (needs the fp-setup phase-2 key message).
            if (_pairing.FairPlay.KeyMessage is { } keyMessage && ekey.Length == 72)
            {
                try
                {
                    _streamAesKey = Playfair.DecryptKey(keyMessage, ekey);
                    System.Diagnostics.Debug.WriteLine("[Setup] FairPlay stream key unwrapped (16B)");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Setup] FairPlay key unwrap failed: {ex.Message}");
                }
            }

            int eventPort  = OpenEventChannel();
            int timingPort = OpenTimingChannel();
            response["eventPort"]  = (long)eventPort;
            response["timingPort"] = (long)timingPort;
        }

        // ── Phase 2: stream setup ─────────────────────────────────────────────
        if (root.TryGetValue("streams", out var streamsObj) && streamsObj is List<object?> streams)
        {
            var responseStreams = new List<object?>();
            foreach (var streamObj in streams)
            {
                if (streamObj is not Dictionary<string, object?> stream) continue;
                long type = stream.TryGetValue("type", out var t) && t is long tl ? tl : -1;
                System.Diagnostics.Debug.WriteLine($"[Setup] phase 2: stream type={type}");

                if (type == 110) // video mirroring
                {
                    _streamConnectionId =
                        stream.TryGetValue("streamConnectionID", out var c) && c is long cl ? cl : 0;

                    _mirror = new MirrorStreamReceiver();

                    // Derive the per-stream AES key/IV and wire decryption + decode.
                    if (_streamAesKey is { } aeskey && _pairing.EcdhSecret is { } ecdh)
                    {
                        try
                        {
                            var crypto = new MirrorStreamCrypto(aeskey, ecdh, unchecked((ulong)_streamConnectionId));
                            _mirror.Configure(crypto, _decoder);
                            System.Diagnostics.Debug.WriteLine("[Setup] mirror stream crypto configured");
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Setup] mirror crypto setup failed: {ex.Message}");
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[Setup] mirror running without decryption (missing key/ecdh)");
                    }

                    _mirror.Start();

                    responseStreams.Add(new Dictionary<string, object?>
                    {
                        ["type"]     = (long)110,
                        ["dataPort"] = (long)_mirror.Port,
                    });
                }
            }
            response["streams"] = responseStreams;
        }

        byte[] responseBody = BinaryPlist.Write(response);
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/x-apple-binary-plist",
        };
        return RtspMessage.BuildResponse(200, "OK", msg.CSeq, headers, responseBody);
    }

    /// <summary>Opens a TCP listener for the iOS event channel that accepts and drains (we don't act on events yet).</summary>
    private int OpenEventChannel()
    {
        _eventListener = new TcpListener(IPAddress.Any, 0);
        _eventListener.Start();
        int port = ((IPEndPoint)_eventListener.LocalEndpoint).Port;
        _ = AcceptAndDrainEventsAsync(_eventListener);
        System.Diagnostics.Debug.WriteLine($"[Setup] event channel on TCP {port}");
        return port;
    }

    private static async Task AcceptAndDrainEventsAsync(TcpListener listener)
    {
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var c = client;
                        using var s = c.GetStream();
                        var buf = new byte[4096];
                        while (await s.ReadAsync(buf) > 0) { /* drain event channel */ }
                    }
                    catch { /* ignore */ }
                });
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
    }

    /// <summary>Opens a UDP socket for the timing/NTP channel (port is reported to iOS; NTP not yet implemented).</summary>
    private int OpenTimingChannel()
    {
        _timing = new UdpClient(0);
        int port = ((IPEndPoint)_timing.Client.LocalEndPoint!).Port;
        System.Diagnostics.Debug.WriteLine($"[Setup] timing channel on UDP {port}");
        return port;
    }

    private byte[] HandleRecord(RtspMessage msg)
    {
        // iOS sends RECORD to begin the RTP stream.
        // If pairing produced AES-CBC keys, hand them to the RTP receiver before
        // the stream starts; otherwise it runs in cleartext passthrough mode.
        if (_rtpReceiver is not null &&
            _pairing.AesKey is { } key && _pairing.AesIv is { } iv)
            _rtpReceiver.SetDecryptionKeys(key, iv);

        _isRecording = true;
        if (_rtpReceiver is not null)
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
        _rtpReceiver?.Flush();
        return BuildOk(msg.CSeq);
    }

    private byte[] HandleTeardown(RtspMessage msg)
    {
        System.Diagnostics.Debug.WriteLine("[RTSP] TEARDOWN received.");
        return BuildOk(msg.CSeq);
    }

    private byte[] HandleGetProperty(RtspMessage msg)   => BuildOk(msg.CSeq);
    private byte[] HandleSetProperty(RtspMessage msg)   => BuildOk(msg.CSeq);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[] BuildOk(int cseq)
        => RtspMessage.BuildResponse(200, "OK", cseq);

    private async Task StopStreamAsync()
    {
        if (_isRecording)
        {
            _isRecording = false;
            if (_rtpReceiver is not null)
                await _rtpReceiver.StopAsync();
            StreamStopped?.Invoke();
        }

        // Tear down AirPlay 2 mirroring media (set up during SETUP).
        if (_mirror is not null)
        {
            await _mirror.StopAsync();
            _mirror = null;
        }
        _eventListener?.Stop();
        _eventListener = null;
        _timing?.Dispose();
        _timing = null;
    }

    public async ValueTask DisposeAsync() => await StopStreamAsync();
}
