using AirPlayReceiver.Services;
using AirPlayReceiver.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    private readonly RtpReceiver?      _rtpReceiver;   // null if FFmpeg is unavailable
    private readonly PairingHandler    _pairing;
    private readonly DeviceInfo        _deviceInfo;
    private readonly VideoDecoder?     _decoder;       // mirror video sink (null if FFmpeg absent)
    private readonly AirPlayVideoPlayer _videoPlayer;  // shared AirPlay Video (URL playback) state
    private readonly ReverseChannelRegistry _reverseChannels; // shared receiver→phone (PTTH) channels
    private readonly HlsCoordinator     _hls;          // shared FCUP/HLS playlist exchange
    private readonly PhotoHandler      _photos;        // shared AirPlay Photos state (PUT /photo + asset cache)

    // Callbacks into the UI layer
    public event Action?         StreamStarted;
    public event Action?         StreamStopped;

    // ── Session state ─────────────────────────────────────────────────────────

    private bool   _isRecording;
    private string _sessionId = Guid.NewGuid().ToString("N")[..8];

    // The GUID iOS puts in X-Apple-Session-ID on every connection of one AirPlay
    // session. It correlates this control connection with the separate TCP
    // connection carrying the reverse (PTTH) channel, so teardown can close it.
    private string? _appleSessionId;

    // True once iOS sent TEARDOWN on this connection — the one unambiguous
    // "session is over" signal, and the only one that closes reverse channels.
    private bool _teardownReceived;

    // Set by HandlePlay when an AirPlay-video /play is accepted; the read loop
    // tears mirroring down after the response is on the wire.
    private bool _mirroringTeardownPending;

    /// <summary>
    /// Set by <see cref="HandleReverse"/> once we answered 101 Switching
    /// Protocols: this connection is no longer an RTSP request stream, it is a
    /// receiver→phone reverse channel. <see cref="Services.RtspServer"/> reads
    /// this after <see cref="RunAsync"/> returns and hands the socket to the
    /// <see cref="ReverseChannelRegistry"/> instead of disposing it.
    /// </summary>
    public (string SessionId, string Purpose)? ReverseUpgrade { get; private set; }

    // True when this connection is the one that handed a URL to the shared video
    // player, so its teardown (and only its teardown) stops playback.
    private bool   _startedVideoPlayback;

    // ── AirPlay 2 mirroring media state (set up during SETUP) ─────────────────
    private MirrorStreamReceiver? _mirror;        // TCP video stream
    private AudioStreamReceiver?  _audio;         // UDP/RTP AAC-ELD audio (type 96)
    private TcpListener?          _eventListener; // event channel (accept + ignore)
    private UdpClient?            _timing;        // timing/NTP channel
    private CancellationTokenSource? _timingCts; // cancels the timing receive loop
    private byte[]?               _streamEkey;    // FairPlay-encrypted stream key (from SETUP)
    private byte[]?               _streamEiv;     // stream AES IV (from SETUP)
    private byte[]?               _streamAesKey;  // FairPlay-decrypted 16-byte AES key
    private long                  _streamConnectionId;

    // ── Construction ──────────────────────────────────────────────────────────

    public AirPlaySession(RtpReceiver? rtpReceiver, PairingHandler pairing, DeviceInfo deviceInfo,
                          VideoDecoder? decoder, AirPlayVideoPlayer videoPlayer,
                          ReverseChannelRegistry reverseChannels, HlsCoordinator hls,
                          PhotoHandler photos)
    {
        _rtpReceiver     = rtpReceiver;
        _pairing         = pairing;
        _deviceInfo      = deviceInfo;
        _decoder         = decoder;
        _videoPlayer     = videoPlayer;
        _reverseChannels = reverseChannels;
        _hls             = hls;
        _photos          = photos;
    }

    // ── Main loop ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads RTSP messages from <paramref name="stream"/> until the session
    /// ends or <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task RunAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                RtspMessage? msg = await RtspMessage.ReadAsync(stream, ct);
                if (msg is null) break; // client closed connection

                Log.Info("RTSP→", $"{msg}");

                // Remember the AirPlay session GUID (present on every video-out
                // request) so teardown can close this session's reverse channels.
                if (msg.Headers.TryGetValue(ReverseChannel.SessionIdHeader, out string? appleSid) &&
                    !string.IsNullOrEmpty(appleSid))
                {
                    _appleSessionId = appleSid;
                }

                byte[] response = msg.Method switch
                {
                    "OPTIONS"       => HandleOptions(msg),
                    "GET"           => HandleGet(msg),
                    "GET_PARAMETER" => HandleGetParameter(msg),
                    "POST"          => HandlePost(msg),
                    "PUT"           => HandlePut(msg),
                    "SETUP"         => HandleSetup(msg),
                    "RECORD"        => HandleRecord(msg),
                    "SET_PARAMETER" => HandleSetParameter(msg),
                    "FLUSH"         => HandleFlush(msg),
                    "TEARDOWN"      => HandleTeardown(msg),
                    _               => BuildOk(msg.CSeq),
                };

                Log.Info("RTSP←", $"{response.Length}B");
                await stream.WriteAsync(response, ct);

                // A video-out /play makes any mirroring media set up on this
                // session obsolete. Tearing it down here (rather than inside the
                // handler) keeps /play's reply immediate and the teardown async.
                if (_mirroringTeardownPending)
                {
                    _mirroringTeardownPending = false;
                    Log.Info("Video", "AirPlay video accepted — stopping mirroring receivers for this session");
                    await StopStreamAsync();
                }

                if (msg.Method == "TEARDOWN")
                {
                    _teardownReceived = true;
                    break;
                }

                // The 101 for /reverse inverted this socket's roles: from here on
                // it carries our requests out and the phone's responses back in.
                // Stop parsing it as an RTSP request stream (UxPlay httpd.c does
                // the same) and let RtspServer hand the socket to the registry.
                if (ReverseUpgrade is { } upgrade)
                {
                    Log.Info("Reverse", $"Upgrade accepted — leaving RTSP loop for session={upgrade.SessionId} " +
                                        $"purpose={upgrade.Purpose}; socket ownership passes to the registry");
                    break;
                }
            }
        }
        finally
        {
            // Always clean up, even when the connection drops mid-stream (the read
            // loop throws IOException on an abrupt disconnect). Otherwise the RTP
            // port (7011) is never released — so the next session's RECORD fails to
            // rebind and reconnect breaks — and the presenter/overlay never reset,
            // leaving the last frame frozen instead of returning to idle.
            await StopStreamAsync();

            // Close this AirPlay session's reverse channels on an explicit
            // TEARDOWN only. A dropped control connection is not enough: iOS uses
            // several connections per session (all carrying the same
            // X-Apple-Session-ID), so closing on any of them would kill the event
            // channel mid-playback. When the phone really goes away it closes the
            // reverse socket too, and the channel's drain loop unregisters itself
            // on EOF. Never from the upgraded connection — its session ends by
            // design, and it must not tear down the channel it just handed over.
            if (_teardownReceived && ReverseUpgrade is null && _appleSessionId is not null)
                _reverseChannels.RemoveAllForSession(_appleSessionId);
        }
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
        // AirPlay uses HTTP-style POST for the pairing sub-protocol and for the
        // AirPlay Video (URL playback) control verbs. Match on the path so query
        // strings (e.g. /rate?value=1.0) route correctly.
        return msg.Path switch
        {
            "/pair-setup"  => _pairing.HandlePairSetup(msg),
            "/pair-verify" => _pairing.HandlePairVerify(msg),
            "/pair-add"    => _pairing.HandlePairAdd(msg),
            "/fp-setup"    => _pairing.HandleFairPlaySetup(msg),
            "/fp-setup2"   => HandleFpSetup2(msg),
            "/getProperty" => HandleGetProperty(msg),
            "/setProperty" => HandleSetProperty(msg),
            // ── AirPlay Video control ─────────────────────────────────────────
            "/play"        => HandlePlay(msg),
            "/rate"        => HandleRate(msg),
            "/scrub"       => HandleScrub(msg),
            "/stop"        => HandleStop(msg),
            "/reverse"     => HandleReverse(msg),
            "/action"      => HandleAction(msg),
            "/feedback"    => Ok(msg),
            _ => RtspMessage.BuildResponse(404, "Not Found", msg.CSeq, protocol: msg.Version),
        };
    }

    private byte[] HandlePut(RtspMessage msg)
    {
        // AirPlay Photos is a protocol of its own: no /play, no FCUP, no RTP —
        // a raw JPEG arrives on PUT /photo and has to appear on screen.
        if (msg.Path.Equals("/photo", StringComparison.OrdinalIgnoreCase))
            return _photos.Handle(msg);

        // Slideshow settings (PUT /slideshows/1). Not implemented — see
        // LogSlideshowRequest; the 200 matches what this receiver already
        // answered, and the dump gives the next field test something to work from.
        if (msg.Path.StartsWith("/slideshows", StringComparison.OrdinalIgnoreCase))
        {
            LogSlideshowRequest(msg);
            return Ok(msg);
        }

        return msg.Path switch
        {
            "/setProperty" => HandleSetProperty(msg),
            _              => Ok(msg),
        };
    }

    private byte[] HandleGet(RtspMessage msg)
    {
        if (msg.Path.Equals("/slideshow-features", StringComparison.OrdinalIgnoreCase) ||
            msg.Path.StartsWith("/slideshows", StringComparison.OrdinalIgnoreCase))
        {
            LogSlideshowRequest(msg);
            return Ok(msg);
        }

        return msg.Path switch
        {
            "/info"          => HandleInfo(msg),
            "/playback-info" => HandlePlaybackInfo(msg),
            "/server-info"   => HandleServerInfo(msg),
            "/scrub"         => HandleScrub(msg),
            _                => Ok(msg),
        };
    }

    /// <summary>
    /// Slideshows are <b>not implemented</b>. They are a much larger protocol than
    /// single photos: <c>GET /slideshow-features</c> advertises transition themes,
    /// <c>PUT /slideshows/1</c> carries settings, and the receiver then <i>pulls</i>
    /// each image with <c>GET /slideshows/1/assets/N</c> over three parallel
    /// <c>X-Apple-Purpose: slideshow</c> reverse channels, reading a binary-plist
    /// response body. That last part is the blocker: <see cref="ReverseChannel"/>'s
    /// drain loop deliberately discards inbound bodies, so pulling assets needs a
    /// request/response correlator built on top of it.
    ///
    /// Answering 200 with an empty body is what this receiver already did, so this
    /// changes no behaviour — it only makes the requests visible. Deliberately not
    /// advertising an empty <c>themes</c> array either: without a device to test
    /// against, a truthful "I don't know" beats a guess that might make Photos
    /// abandon the session instead of falling back to single-photo pushes, which
    /// <b>are</b> implemented.
    /// </summary>
    private static void LogSlideshowRequest(RtspMessage msg)
    {
        LogVideoHeaders(msg);
        byte[] body = msg.Body ?? Array.Empty<byte>();
        Log.Warn("Photo", $"slideshow endpoint {msg.Method} {msg.Uri} is NOT IMPLEMENTED — " +
                          $"answering 200 with an empty body (request body {body.Length}B)");
        if (body.Length > 0)
            Log.Info("Photo", $"slideshow body:\n{Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 4096))}");
    }

    // ── AirPlay Video (URL playback) handlers ─────────────────────────────────

    private byte[] HandlePlay(RtspMessage msg)
    {
        // The /play body carries the video URL. It may be a binary plist
        // ("Content-Location" + the start-position keys) or a small text body
        // ("Content-Location: <url>\nStart-Position: <pos>"). Parse both and log
        // everything so we can see exactly what iOS sends.
        byte[] body = msg.Body ?? Array.Empty<byte>();
        string? url  = null;
        string? uuid = null;         // identifies the playback item across pause/resume
        double  startSeconds  = 0;   // "Start-Position-Seconds" — absolute seconds
        double  startFraction = 0;   // legacy "Start-Position"  — 0..1 of the duration

        // Full diagnostic dump: headers (X-Apple-Session-ID etc.) + the entire plist,
        // so we can see whether Content-Location is a direct URL or an HLS
        // …/master.m3u8 (which needs the FCUP reverse-proxy path).
        LogVideoHeaders(msg);

        try
        {
            if (body.Length >= 8 && Encoding.ASCII.GetString(body, 0, 8) == "bplist00" &&
                BinaryPlist.Parse(body) is Dictionary<string, object?> dict)
            {
                var dump = new StringBuilder();
                DumpPlist(dict, dump, 1);
                Log.Info("Video", $"/play plist ({body.Length}B):\n{dump}");
                url  = dict.TryGetValue("Content-Location", out var u) ? u as string : null;
                uuid = dict.TryGetValue("uuid", out var uid) ? uid as string : null;

                // Modern iOS (AirPlay/870+) sends BOTH start keys and they mean
                // different things: "Start-Position-Seconds" is absolute seconds
                // (a real capture had 218.135603) while the legacy "Start-Position"
                // is a 0..1 FRACTION of the duration (0.0 in that same capture).
                // We used to read the legacy key as seconds, so every resume
                // restarted the item from 0:00.
                if (dict.TryGetValue("Start-Position-Seconds", out var sps)) startSeconds  = PlistDouble(sps);
                if (dict.TryGetValue("Start-Position",         out var sp))  startFraction = PlistDouble(sp);
            }
            else
            {
                string text = Encoding.UTF8.GetString(body);
                Log.Info("Video", $"/play text body:\n{text}");
                foreach (string line in text.Split('\n'))
                {
                    string l = line.Trim();
                    if (l.StartsWith("Content-Location:", StringComparison.OrdinalIgnoreCase))
                        url = l["Content-Location:".Length..].Trim();
                    else if (l.StartsWith("Start-Position-Seconds:", StringComparison.OrdinalIgnoreCase))
                        double.TryParse(l["Start-Position-Seconds:".Length..].Trim(),
                                        System.Globalization.CultureInfo.InvariantCulture, out startSeconds);
                    else if (l.StartsWith("Start-Position:", StringComparison.OrdinalIgnoreCase))
                        double.TryParse(l["Start-Position:".Length..].Trim(),
                                        System.Globalization.CultureInfo.InvariantCulture, out startFraction);
                    else if (l.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase))
                        uuid = l["uuid:".Length..].Trim();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Video", $"/play parse failed: {ex.Message}");
        }

        if (string.IsNullOrEmpty(url))
        {
            Log.Warn("Video", $"/play had no Content-Location (body={body.Length}B)");
            return RtspMessage.BuildResponse(400, "Bad Request", msg.CSeq, protocol: msg.Version);
        }

        Log.Info("Video", startSeconds > 0
            ? $"/play start from Start-Position-Seconds = {Inv(startSeconds)}s"
            : startFraction > 0
                ? $"/play start from legacy Start-Position fraction {Inv(startFraction)} " +
                  "(resolved against the duration once the demuxer reports it)"
                : "/play start from the beginning");

        // Context for whichever route ends up calling the shared player: the
        // forwarded Cookie/Referer, and this connection's claim on playback so
        // only its teardown stops it.
        _videoPlayer.PlayRequestHeaders = ForwardableHeaders(msg);
        _startedVideoPlayback = true;

        // Routing (mlhls/nfhls → FCUP exchange, http(s) → straight to the player,
        // anything else → refuse) lives in HlsCoordinator so the FCUP loop can be
        // exercised without the RTSP layer. The legacy fraction only reaches the
        // direct route — an FCUP item's position is always absolute seconds.
        if (!_hls.HandlePlay(_appleSessionId, uuid, url, startSeconds, startFraction))
        {
            _startedVideoPlayback = false;
            return RtspMessage.BuildResponse(400, "Bad Request", msg.CSeq, protocol: msg.Version);
        }

        // An AirPlay-video session and a mirroring session are mutually exclusive:
        // once video-out is accepted, the RTP/NTP/mirror receivers set up during
        // SETUP are stale, and leaving them running means two producers fighting
        // over one presenter (UxPlay tears the same receivers down at raop.c:301).
        // Deferred until after the 200 is written, so /play still answers at once.
        _mirroringTeardownPending = true;

        return Ok(msg);
    }

    /// <summary>
    /// Hands a URL to the shared player and records that <i>this</i> session owns
    /// the playback. Ownership matters at teardown: AirPlay Video drives transport
    /// over many short-lived control connections, and only the one that started
    /// playback may stop it when it drops (see <see cref="StopStreamAsync"/>).
    /// </summary>
    private void StartVideoPlayback(string url, double startSeconds, double startFraction, string? uuid, RtspMessage msg)
    {
        _videoPlayer.PlayRequestHeaders = ForwardableHeaders(msg);
        _startedVideoPlayback = true;
        _videoPlayer.Play(url, startSeconds, startFraction, uuid);
    }

    /// <summary>
    /// Cookie/Referer from the control request, CRLF-joined for FFmpeg's "headers"
    /// option. Session-scoped CDN URLs 403 without them.
    /// </summary>
    private static string? ForwardableHeaders(RtspMessage msg)
    {
        var lines = new List<string>();
        foreach (string name in new[] { "Cookie", "Referer" })
            if (msg.Headers.TryGetValue(name, out string? v) && !string.IsNullOrWhiteSpace(v))
                lines.Add($"{name}: {v}");
        return lines.Count == 0 ? null : string.Join("\r\n", lines);
    }

    /// <summary>Plist reals arrive as double, ints as long — normalise both.</summary>
    private static double PlistDouble(object? v) => v switch
    {
        double d => d,
        long   l => l,
        int    i => i,
        string s when double.TryParse(s, System.Globalization.NumberStyles.Float,
                                      System.Globalization.CultureInfo.InvariantCulture, out double p) => p,
        _ => 0.0,
    };

    /// <summary>Invariant-culture number text. iOS cannot parse "12,5".</summary>
    private static string Inv(double v)
        => v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    private byte[] HandleRate(RtspMessage msg)
    {
        if (double.TryParse(msg.QueryValue("value"), System.Globalization.CultureInfo.InvariantCulture, out double rate))
            _videoPlayer.SetRate(rate);
        return Ok(msg);
    }

    private byte[] HandleScrub(RtspMessage msg)
    {
        // POST/PUT sets the position; GET reports it.
        if (msg.Method == "GET")
        {
            // Invariant culture is not optional here: on a comma-decimal locale
            // the default interpolation emits "duration: 12,5", which iOS cannot
            // parse — the scrubber then reads as a zero-length track.
            string text = $"duration: {Inv(_videoPlayer.Duration)}\r\n" +
                          $"position: {Inv(_videoPlayer.Position)}\r\n";
            return RtspMessage.BuildResponse(200, "OK", msg.CSeq,
                new Dictionary<string, string> { ["Content-Type"] = "text/parameters" },
                Encoding.UTF8.GetBytes(text), protocol: msg.Version);
        }

        if (double.TryParse(msg.QueryValue("position"), System.Globalization.CultureInfo.InvariantCulture, out double pos))
            _videoPlayer.Scrub(pos);
        return Ok(msg);
    }

    private byte[] HandleStop(RtspMessage msg)
    {
        _videoPlayer.Stop();
        return Ok(msg);
    }

    /// <summary>
    /// The reverse-HTTP (PTTH) handshake. iOS opens a dedicated TCP connection
    /// and sends, before /play:
    /// <code>
    /// POST /reverse HTTP/1.1
    /// Connection: Upgrade
    /// Upgrade: PTTH/1.0
    /// X-Apple-Purpose: event          (or "slideshow")
    /// X-Apple-Session-ID: &lt;GUID&gt;
    /// </code>
    /// We answer 101 and record the upgrade; <see cref="RunAsync"/> then leaves
    /// the RTSP loop and <see cref="Services.RtspServer"/> hands the live socket
    /// to <see cref="ReverseChannelRegistry"/>, which owns it from then on.
    /// </summary>
    private byte[] HandleReverse(RtspMessage msg)
    {
        LogVideoHeaders(msg);

        string sessionId = msg.Headers.TryGetValue(ReverseChannel.SessionIdHeader, out string? sid) ? sid.Trim() : string.Empty;
        string purpose   = msg.Headers.TryGetValue(ReverseChannel.PurposeHeader, out string? p) && !string.IsNullOrWhiteSpace(p)
                         ? p.Trim()
                         : ReverseChannel.PurposeEvent;

        if (string.IsNullOrEmpty(sessionId))
        {
            // Without the GUID there is no way to correlate this socket with the
            // /play connection, so the channel would be unreachable. Refuse it.
            Log.Warn("Reverse", $"/reverse without {ReverseChannel.SessionIdHeader} — cannot correlate, refusing with 400");
            return RtspMessage.BuildResponse(400, "Bad Request", msg.CSeq, protocol: msg.Version);
        }

        // One event channel per session (UxPlay answers a second one with 421);
        // a Photos slideshow legitimately opens three "slideshow" channels.
        if (string.Equals(purpose, ReverseChannel.PurposeEvent, StringComparison.OrdinalIgnoreCase) &&
            _reverseChannels.HasChannel(sessionId, ReverseChannel.PurposeEvent))
        {
            Log.Warn("Reverse", $"/reverse purpose=event but session={sessionId} already has one — replying 421");
            return RtspMessage.BuildResponse(421, "Misdirected Request", msg.CSeq, protocol: msg.Version);
        }

        Log.Info("Reverse", $"/reverse upgrade accepted: session={sessionId} purpose={purpose} " +
                            $"(upgrade='{(msg.Headers.TryGetValue("Upgrade", out string? u) ? u : "?")}')");

        ReverseUpgrade = (sessionId, purpose);

        var headers = new Dictionary<string, string>
        {
            ["Upgrade"]    = "PTTH/1.0",
            ["Connection"] = "Upgrade",
        };
        return RtspMessage.BuildResponse(101, "Switching Protocols", msg.CSeq, headers, protocol: msg.Version);
    }

    /// <summary>
    /// POST /action carries FCUP responses — the iOS device returning HLS playlist
    /// bytes over the control channel (FCUP_Response_URL / FCUP_Response_Data / …) —
    /// plus the playlistInsert/playlistRemove edits iOS uses to splice
    /// advertisements in and out. <see cref="HlsCoordinator"/> acts on them; the
    /// dump here is diagnostics only.
    /// </summary>
    private byte[] HandleAction(RtspMessage msg)
    {
        LogVideoHeaders(msg);
        byte[] body = msg.Body ?? Array.Empty<byte>();
        if (body.Length >= 8 && Encoding.ASCII.GetString(body, 0, 8) == "bplist00")
        {
            try
            {
                var dump = new StringBuilder();
                DumpPlist(BinaryPlist.Parse(body), dump, 1);
                Log.Info("Video", $"/action plist ({body.Length}B):\n{dump}");
            }
            catch (Exception ex) { Log.Warn("Video", $"/action parse failed: {ex.Message}"); }
        }
        else
        {
            Log.Info("Video", $"/action non-plist body ({body.Length}B)");
        }

        return _hls.HandleAction(msg);
    }

    /// <summary>
    /// iOS polls this a few times a second while an AirPlay Video item is up. Three
    /// distinct answers, all of them load-bearing:
    ///
    ///   • loading  → an <b>empty 200</b> with no Content-Type and no Content-Length.
    ///     That is how a receiver says "still buffering"; iOS keeps polling.
    ///   • playing  → the info plist below.
    ///   • ended/failed → an empty 200 plus <c>Connection: close</c>. There is no
    ///     "stopped" event in the protocol — the receiver ends an item by dropping
    ///     the connection. This is what stops the phone spinning on a frozen 0:00
    ///     after the player has died, which it did on every failure path before.
    /// </summary>
    private byte[] HandlePlaybackInfo(RtspMessage msg)
    {
        VideoPlaybackState state    = _videoPlayer.State;
        double             position = _videoPlayer.Position;
        double             duration = _videoPlayer.Duration;

        Log.Info("Video", $"GET /playback-info (state={state} pos={Inv(position)} dur={Inv(duration)})");

        if (state is VideoPlaybackState.Idle or VideoPlaybackState.Loading)
            return RtspMessage.BuildResponse(200, "OK", msg.CSeq, protocol: msg.Version);

        if (state is VideoPlaybackState.Ended or VideoPlaybackState.Failed)
        {
            // NOTE: we can only *ask* the client to close — RunAsync (owned by the
            // reverse/dispatch work) is what would drop the socket on our side.
            Log.Info("Video", $"playback {state} — empty 200 + Connection: close so the phone stops polling");
            return RtspMessage.BuildResponse(200, "OK", msg.CSeq,
                new Dictionary<string, string> { ["Connection"] = "close" }, protocol: msg.Version);
        }

        // Both range arrays used to be empty, so iOS saw no seekable region at all
        // and the scrubber was dead. One range each, matching what UxPlay emits.
        var loadedTimeRanges = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["start"]    = position,
                ["duration"] = duration > position ? duration - position : 0.0,
            },
        };

        var seekableTimeRanges = new List<object?>();
        if (_videoPlayer.CanSeek && duration > 0)
            seekableTimeRanges.Add(new Dictionary<string, object?>
            {
                ["start"]    = 0.0,
                ["duration"] = duration,
            });

        var info = new Dictionary<string, object?>
        {
            ["duration"]                = duration,
            ["position"]                = position,
            ["rate"]                    = _videoPlayer.Rate,
            // Real receivers report readyToPlay=true whenever they answer with a
            // body at all; "not ready" is the empty 200 above, not a false here.
            ["readyToPlay"]             = true,
            ["playbackBufferEmpty"]     = false,
            ["playbackBufferFull"]      = false,
            ["playbackLikelyToKeepUp"]  = true,
            ["loadedTimeRanges"]        = loadedTimeRanges,
            ["seekableTimeRanges"]      = seekableTimeRanges,
        };

        // TODO(integration): UxPlay answers /playback-info as text/x-apple-plist+xml,
        // not a binary plist. Flip the writer and this Content-Type together once
        // Protocol/XmlPlist.cs (branch feat/reverse-channel) lands — changing one
        // without the other would break a path that currently works.
        return RtspMessage.BuildResponse(200, "OK", msg.CSeq,
            new Dictionary<string, string> { ["Content-Type"] = "application/x-apple-binary-plist" },
            BinaryPlist.Write(info), protocol: msg.Version);
    }

    /// <summary>
    /// GET /server-info — a capability gate separate from both the mDNS TXT record
    /// and GET /info. iOS reads all three.
    ///
    /// The features value here is deliberately NOT the 64-bit mDNS mask. UxPlay
    /// (lib/http_handlers.h, http_handler_server_info) and apsdk both hardcode
    /// 0x27F — bits 0-6 plus bit 9 — no matter what they advertise over mDNS, and
    /// both are known-working receivers. Reporting the full mirroring mask here
    /// claims AirPlay 2 capabilities this endpoint's consumers don't expect.
    ///
    /// UxPlay serves this as text/x-apple-plist+xml. We have no XML plist writer
    /// yet (Protocol/XmlPlist.cs is in flight), so this keeps the binary plist
    /// encoding already used everywhere else in this file.
    /// </summary>
    private byte[] HandleServerInfo(RtspMessage msg)
    {
        var info = new Dictionary<string, object?>
        {
            ["deviceid"]       = _deviceInfo.DeviceId,
            ["macAddress"]     = _deviceInfo.DeviceId,
            ["features"]       = (long)0x27F,
            ["model"]          = _deviceInfo.Model,
            ["osBuildVersion"] = "12B435",
            ["protovers"]      = "1.0",
            ["srcvers"]        = _deviceInfo.SourceVersion,
            ["vv"]             = (long)2,
        };
        return RtspMessage.BuildResponse(200, "OK", msg.CSeq,
            new Dictionary<string, string> { ["Content-Type"] = "application/x-apple-binary-plist" },
            BinaryPlist.Write(info), protocol: msg.Version);
    }

    /// <summary>200 OK echoing the request's protocol (RTSP/1.0 or HTTP/1.1).</summary>
    private static byte[] Ok(RtspMessage msg)
        => RtspMessage.BuildResponse(200, "OK", msg.CSeq, protocol: msg.Version);

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
        Log.Info("Info", $"returning device info plist ({body.Length}B)");

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
            Log.Error("Setup", $"plist parse failed: {ex.Message}");
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
            Log.Info("Setup", $"phase 1: ekey={ekey.Length}B eiv={eiv.Length}B");

            // FairPlay-decrypt the stream key (needs the fp-setup phase-2 key message).
            if (_pairing.FairPlay.KeyMessage is { } keyMessage && ekey.Length == 72)
            {
                try
                {
                    _streamAesKey = Playfair.DecryptKey(keyMessage, ekey);
                    Log.Info("Setup", "FairPlay stream key unwrapped (16B)");
                }
                catch (Exception ex)
                {
                    Log.Error("Setup", "FairPlay key unwrap failed — mirror decryption key unavailable; video will be BLACK", ex);
                }
            }
            else
            {
                // This gate failing silently was the biggest blind spot in the
                // pipeline: without the unwrapped key, SETUP phase 2 configures
                // the mirror stream with no decryption and every video frame is
                // rejected — a live session with a solid black window.
                string reason = _pairing.FairPlay.KeyMessage is null
                    ? "fp-setup key message is null (FairPlay handshake incomplete)"
                    : $"ekey length is {ekey.Length}B (expected 72)";
                Log.Error("Setup",
                    $"FairPlay stream key NOT unwrapped — {reason}. Mirror decryption key unavailable; video will be BLACK");
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
                Log.Info("Setup", $"phase 2: stream type={type}");

                if (type == 110) // video mirroring
                {
                    if (stream.TryGetValue("streamConnectionID", out var c) && c is long cl)
                    {
                        _streamConnectionId = cl;
                    }
                    else
                    {
                        _streamConnectionId = 0;
                        Log.Error("Setup",
                            "streamConnectionID missing or not an integer in SETUP — falling back to 0; " +
                            "the AES-CTR keystream will be derived from the wrong ID and video will not decrypt");
                    }

                    _mirror = new MirrorStreamReceiver();

                    // Derive the per-stream AES key/IV and wire decryption + decode.
                    if (_streamAesKey is { } aeskey && _pairing.EcdhSecret is { } ecdh)
                    {
                        try
                        {
                            var crypto = new MirrorStreamCrypto(aeskey, ecdh, unchecked((ulong)_streamConnectionId));
                            _mirror.Configure(crypto, _decoder);
                            Log.Info("Setup", "mirror stream crypto configured");
                        }
                        catch (Exception ex)
                        {
                            Log.Error("Setup", "mirror crypto setup failed — frames cannot be decrypted; video will be BLACK", ex);
                        }
                    }
                    else
                    {
                        Log.Error("Setup",
                            $"mirror running WITHOUT decryption (streamAesKey={(_streamAesKey is null ? "null" : "present")}, " +
                            $"ecdhSecret={(_pairing.EcdhSecret is null ? "null" : "present")}) — " +
                            "encrypted frames cannot be decoded; video WILL be black");
                    }

                    _mirror.Start();

                    responseStreams.Add(new Dictionary<string, object?>
                    {
                        ["type"]     = (long)110,
                        ["dataPort"] = (long)_mirror.Port,
                    });
                }
                else if (type == 96) // screen-mirroring audio (AAC-ELD over UDP/RTP)
                {
                    // iOS retries this SETUP until it gets a data/control port, so
                    // open the audio receiver once and reuse its ports on retries.
                    if (_audio is null)
                    {
                        long ct  = stream.TryGetValue("ct",  out var ctv)  && ctv  is long ctl  ? ctl  : -1;
                        long spf = stream.TryGetValue("spf", out var spfv) && spfv is long spfl ? spfl : -1;
                        Log.Info("Setup", $"phase 2: audio ct={ct} spf={spf}");

                        _audio = new AudioStreamReceiver();
                        if (_streamAesKey is { } akey && _streamEiv is { } audioEiv)
                            _audio.Configure(new AudioStreamCrypto(akey, _pairing.EcdhSecret, audioEiv));
                        else
                            Log.Warn("Setup", "audio without decryption (missing key/eiv)");
                        _audio.Start();
                    }

                    responseStreams.Add(new Dictionary<string, object?>
                    {
                        ["type"]        = (long)96,
                        ["dataPort"]    = (long)_audio.DataPort,
                        ["controlPort"] = (long)_audio.ControlPort,
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
        Log.Info("Setup", $"event channel on TCP {port}");
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
        // The listener throws when it's stopped during teardown. AcceptTcpClientAsync
        // can surface this as SocketException, ObjectDisposedException, or (after Stop)
        // InvalidOperationException — none should escape this fire-and-forget task.
        catch (Exception ex)
        {
            Log.Info("Setup", $"event channel closed: {ex.Message}");
        }
    }

    /// <summary>Opens the UDP timing channel, reports its port to iOS, and starts the NTP response loop.</summary>
    private int OpenTimingChannel()
    {
        _timing    = new UdpClient(0);
        _timingCts = new CancellationTokenSource();
        _ = RunTimingChannelAsync(_timingCts.Token);
        int port = ((IPEndPoint)_timing.Client.LocalEndPoint!).Port;
        Log.Info("Setup", $"timing channel on UDP {port}");
        return port;
    }

    /// <summary>
    /// Responds to AirPlay NTP timing requests (packet type 0xD2) with timing
    /// responses (0xD3). Without these replies the sender periodically resyncs
    /// its audio clock, causing brief pauses in audio delivery (heard as pops).
    /// </summary>
    private async Task RunTimingChannelAsync(CancellationToken ct)
    {
        if (_timing is null) return;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult req = await _timing.ReceiveAsync(ct);
                byte[] p = req.Buffer;
                if (p.Length < 32 || p[1] != 0xD2) continue;  // not a timing request

                ulong recvTime = NtpNow();

                // Echo reference time (bytes 8–15) and fill in received + send times.
                byte[] resp = new byte[32];
                resp[0] = 0x80;
                resp[1] = 0xD3;   // timing response
                resp[2] = p[2];   // sequence number (big-endian echo)
                resp[3] = p[3];
                Buffer.BlockCopy(p, 8, resp, 8, 8);   // reference time
                WriteUInt64BE(resp, 16, recvTime);      // received time
                WriteUInt64BE(resp, 24, NtpNow());      // send time

                await _timing.SendAsync(resp, 32, req.RemoteEndPoint);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        catch (Exception ex) { Log.Warn("Timing", $"error: {ex.Message}"); }
    }

    // .NET ticks for the NTP epoch (1900-01-01 00:00:00 UTC).
    // = .NET ticks for Unix epoch (621355968000000000) − 70 years in ticks (22089888000000000)
    private const long NtpEpochTicks = 599266080000000000L;

    private static ulong NtpNow()
    {
        long ticks = DateTime.UtcNow.Ticks - NtpEpochTicks;
        ulong secs = (ulong)(ticks / TimeSpan.TicksPerSecond);
        ulong frac = (ulong)(ticks % TimeSpan.TicksPerSecond) * 0x100000000UL / (ulong)TimeSpan.TicksPerSecond;
        return (secs << 32) | frac;
    }

    private static void WriteUInt64BE(byte[] buf, int offset, ulong v)
    {
        buf[offset]     = (byte)(v >> 56);
        buf[offset + 1] = (byte)(v >> 48);
        buf[offset + 2] = (byte)(v >> 40);
        buf[offset + 3] = (byte)(v >> 32);
        buf[offset + 4] = (byte)(v >> 24);
        buf[offset + 5] = (byte)(v >> 16);
        buf[offset + 6] = (byte)(v >> 8);
        buf[offset + 7] = (byte)v;
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
        // SET_PARAMETER carries text/parameters updates. The one we act on is
        // "volume: <dB>", sent when the phone's volume changes. AirPlay volume is
        // in decibels: 0.0 = max, down to about -30 = quiet, and -144 = muted.
        if (msg.Body is { Length: > 0 } body)
        {
            string text = System.Text.Encoding.ASCII.GetString(body);
            foreach (string line in text.Split('\n'))
            {
                int colon = line.IndexOf(':');
                if (colon < 0) continue;

                string key = line[..colon].Trim();
                string val = line[(colon + 1)..].Trim();
                if (!key.Equals("volume", StringComparison.OrdinalIgnoreCase)) continue;

                if (float.TryParse(val, System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out float db))
                {
                    float gain = db <= -144f ? 0f : Math.Clamp((float)Math.Pow(10.0, db / 20.0), 0f, 1f);
                    // _audio only exists for screen mirroring. During AirPlay Video
                    // (URL playback) it is null, so the phone's volume slider used to
                    // do nothing at all — route it to the video player's own output.
                    _audio?.SetVolume(gain);
                    if (_videoPlayer.IsActive) _videoPlayer.SetVolume(gain);
                    Log.Info("Audio", $"volume {Inv(db)} dB → gain {Inv(gain)} " +
                                      $"(mirror={_audio is not null}, video={_videoPlayer.IsActive})");
                }
            }
        }
        return BuildOk(msg.CSeq);
    }

    private byte[] HandleFlush(RtspMessage msg)
    {
        _rtpReceiver?.Flush();
        return BuildOk(msg.CSeq);
    }

    private byte[] HandleTeardown(RtspMessage msg)
    {
        Log.Info("RTSP", "TEARDOWN received.");

        // A photo session has no /stop verb — TEARDOWN is the only unambiguous
        // "the user is done" signal, so this is where the surface is cleared and
        // the asset cache dropped. Deliberately not in RunAsync's finally: iOS
        // uses several short-lived control connections per session, and any one
        // of them closing would otherwise wipe the photo still being looked at.
        if (_photos.SessionActive)
        {
            // Tell the phone first, while its reverse channel is still open —
            // RunAsync's finally closes it moments later.
            if (_appleSessionId is not null)
            {
                Log.Info("Photo", $"sending photo/stopped event on session={_appleSessionId}");
                _ = _reverseChannels.SendEventAsync(_appleSessionId, "photo", "stopped");
            }
            else
            {
                Log.Info("Photo", $"no {ReverseChannel.SessionIdHeader} seen on this connection — " +
                                  "skipping the photo/stopped event");
            }

            _photos.EndSession();
        }

        return BuildOk(msg.CSeq);
    }

    private byte[] HandleGetProperty(RtspMessage msg)
    {
        string prop = msg.Uri.Contains('?') ? msg.Uri[(msg.Uri.IndexOf('?') + 1)..] : "(none)";
        Log.Info("Video", $"getProperty '{prop}' (state={_videoPlayer.State})");
        // BuildOk hardcodes RTSP/1.0; these arrive as HTTP/1.1 on the video-out
        // control connections, so echo the request's protocol like every other
        // video handler does.
        return Ok(msg);
    }

    /// <summary>Dumps a video-out request's headers — the FCUP path keys off X-Apple-Session-ID and friends.</summary>
    private static void LogVideoHeaders(RtspMessage msg)
    {
        string hdrs = string.Join("\n  ", msg.Headers.Select(kv => $"{kv.Key}: {kv.Value}"));
        Log.Info("Video", $"{msg.Method} {msg.Uri} headers:\n  {hdrs}");
    }

    /// <summary>
    /// AirPlay video-out hands the receiver its media via <c>setProperty?selectedMediaArray</c>
    /// (a binary plist), not the older <c>POST /play</c> verb that newer iOS only uses for
    /// some apps. Dump every setProperty plist so we can see what iOS sends, and when it's the
    /// media array, pull out the playable URL and start the player.
    /// </summary>
    private byte[] HandleSetProperty(RtspMessage msg)
    {
        string prop = msg.Uri.Contains('?') ? msg.Uri[(msg.Uri.IndexOf('?') + 1)..] : "(none)";
        byte[] body = msg.Body ?? Array.Empty<byte>();

        if (body.Length >= 8 && Encoding.ASCII.GetString(body, 0, 8) == "bplist00")
        {
            try
            {
                object? parsed = BinaryPlist.Parse(body);
                var sb = new StringBuilder();
                DumpPlist(parsed, sb, 1);
                Log.Info("Video", $"setProperty '{prop}' ({body.Length}B):\n{sb}");

                if (prop.Equals("selectedMediaArray", StringComparison.OrdinalIgnoreCase))
                {
                    var candidates = new List<MediaCandidate>();
                    CollectUrls(parsed, "", candidates);

                    // The old code took urls[0] from a blind recursive sweep, so a
                    // thumbnail, poster or ad-tracking pixel — which routinely come
                    // first — was what FFmpeg tried to open.
                    MediaCandidate? best = null;
                    foreach (var c in candidates)
                        if (c.Score > 0 && (best is null || c.Score > best.Score)) best = c;

                    foreach (var c in candidates)
                        Log.Info("Video", $"  media candidate {c.KeyPath} score={c.Score} " +
                                          $"[{(ReferenceEquals(c, best) ? "CHOSEN" : "rejected")}: {c.Reason}] {c.Url}");

                    if (best is not null)
                    {
                        double startSeconds  = FindPlistDouble(parsed, "Start-Position-Seconds");
                        double startFraction = FindPlistDouble(parsed, "Start-Position");
                        string? uuid         = FindPlistString(parsed, "uuid");
                        Log.Info("Video", $"selectedMediaArray → {best.Url} " +
                                          $"(startSeconds={Inv(startSeconds)} startFraction={Inv(startFraction)} uuid={uuid ?? "(none)"})");
                        StartVideoPlayback(best.Url, startSeconds, startFraction, uuid, msg);
                    }
                    else if (candidates.Count > 0)
                    {
                        Log.Warn("Video", $"selectedMediaArray had {candidates.Count} URL(s) but none looked like " +
                                          "playable media — see the per-candidate reasons above");
                    }
                    else
                    {
                        Log.Warn("Video", "selectedMediaArray had no http(s) URL — dump above shows what iOS sent");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Video", $"setProperty '{prop}' plist parse failed: {ex.Message}");
            }
        }
        else
        {
            Log.Info("Video", $"setProperty '{prop}' non-plist body ({body.Length}B)");
        }

        return Ok(msg);
    }

    /// <summary>
    /// FairPlay phase-2 for the video-out session: a FairPlay variant we don't
    /// implement (UxPlay doesn't either). We reject with 421 (matching UxPlay,
    /// with the binary-plist content type, keeping the connection alive) so iOS
    /// can fall back to the Legacy path and proceed to /play.
    ///
    /// The FairPlay version is at byte 4 of the body: 0x03 is the variant UxPlay's
    /// /fp-setup handles and that should fall back; anything else (e.g. 0x04) means
    /// iOS wants a newer FairPlay we can't do, and playback won't be possible.
    /// </summary>
    private byte[] HandleFpSetup2(RtspMessage msg)
    {
        byte[] body = msg.Body ?? Array.Empty<byte>();
        int version = body.Length > 4 ? body[4] : -1;
        string note = version == 0x03 ? "v0x03 (should fall back to Legacy)"
                                      : $"v0x{version:X2} — NOT 0x03, playback likely impossible";
        Log.Info("Pairing", $"/fp-setup2 ({body.Length}B, FairPlay {note}) — rejecting 421");

        var headers = new Dictionary<string, string> { ["Content-Type"] = "application/x-apple-binary-plist" };
        return RtspMessage.BuildResponse(421, "Misdirected Request", msg.CSeq, headers, protocol: msg.Version);
    }

    // ── Plist diagnostics ──────────────────────────────────────────────────────

    private static void DumpPlist(object? node, StringBuilder sb, int indent)
    {
        string pad = new(' ', indent * 2);
        switch (node)
        {
            case Dictionary<string, object?> d:
                foreach (var (k, v) in d)
                {
                    if (v is Dictionary<string, object?> or List<object?>)
                    {
                        sb.AppendLine($"{pad}{k}:");
                        DumpPlist(v, sb, indent + 1);
                    }
                    else sb.AppendLine($"{pad}{k} = {Scalar(v)}");
                }
                break;
            case List<object?> list:
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] is Dictionary<string, object?> or List<object?>)
                    {
                        sb.AppendLine($"{pad}[{i}]:");
                        DumpPlist(list[i], sb, indent + 1);
                    }
                    else sb.AppendLine($"{pad}[{i}] = {Scalar(list[i])}");
                }
                break;
            default:
                sb.AppendLine($"{pad}{Scalar(node)}");
                break;
        }
    }

    private static string Scalar(object? v) => v switch
    {
        null            => "null",
        byte[] b        => $"<{b.Length} bytes>",
        // Truncate long strings (e.g. an embedded media playlist) so one value
        // can't flood the Output window, but keep enough to read the shape.
        string s        => s.Length > 2000 ? $"\"{s[..2000]}…\"(+{s.Length - 2000} more)" : $"\"{s}\"",
        _               => v.ToString() ?? "null",
    };

    // ── selectedMediaArray URL selection ──────────────────────────────────────

    /// <summary>One http(s) string found in a media plist, with why we do (not) trust it.</summary>
    private sealed record MediaCandidate(string Url, string KeyPath, int Score, string Reason);

    private static readonly string[] PlaylistExtensions = { ".m3u8", ".m3u", ".mpd" };
    private static readonly string[] MediaExtensions =
        { ".mp4", ".m4v", ".mov", ".ts", ".mkv", ".webm", ".avi", ".3gp", ".m4a", ".mp3", ".aac" };
    private static readonly string[] ImageExtensions =
        { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".heif", ".bmp", ".svg", ".tif", ".tiff" };

    /// <summary>Keys whose values are decoration, never the thing to play.</summary>
    private static readonly string[] ArtworkKeyHints =
        { "artwork", "thumb", "image", "poster", "icon", "preview", "logo", "banner", "chapter" };

    /// <summary>Keys that carry analytics/ad beacons — opening one plays nothing.</summary>
    private static readonly string[] TrackingKeyHints =
        { "tracking", "beacon", "impression", "advert", "analytics", "metric", "telemetry" };

    /// <summary>Keys that name the actual media, best match first.</summary>
    private static readonly string[] MediaKeyHints =
        { "content-location", "contentlocation", "mediaurl", "media-url", "asseturl", "asset-url",
          "playbackurl", "streamurl", "playlist", "location", "url", "uri" };

    /// <summary>
    /// Walks a setProperty plist and scores every http(s) string it finds, so the
    /// caller can pick the media URL rather than whichever URL happened to come
    /// first. Scoring is deliberately explicit and logged: when a real device sends
    /// a shape we guessed wrong, the log says exactly what was rejected and why.
    /// </summary>
    private static void CollectUrls(object? node, string path, List<MediaCandidate> found)
    {
        switch (node)
        {
            case Dictionary<string, object?> d:
                foreach (var (k, v) in d) CollectUrls(v, path.Length == 0 ? k : $"{path}.{k}", found);
                break;
            case List<object?> list:
                for (int i = 0; i < list.Count; i++) CollectUrls(list[i], $"{path}[{i}]", found);
                break;
            case string s when s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                            || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase):
                found.Add(ScoreMediaUrl(s, path));
                break;
        }
    }

    private static MediaCandidate ScoreMediaUrl(string url, string keyPath)
    {
        string lowerPath = keyPath.ToLowerInvariant();

        foreach (string hint in ArtworkKeyHints)
            if (lowerPath.Contains(hint))
                return new MediaCandidate(url, Describe(keyPath), -100, $"key mentions '{hint}' (artwork)");

        foreach (string hint in TrackingKeyHints)
            if (lowerPath.Contains(hint))
                return new MediaCandidate(url, Describe(keyPath), -100, $"key mentions '{hint}' (tracking)");

        // Extension comes from the path only — a query string like "?thumb=1.jpg"
        // must not disqualify a perfectly good .m3u8.
        string ext = UrlExtension(url);
        foreach (string image in ImageExtensions)
            if (ext == image)
                return new MediaCandidate(url, Describe(keyPath), -100, $"'{ext}' is an image");

        int    score  = 0;
        string reason;
        if (Array.IndexOf(PlaylistExtensions, ext) >= 0) { score += 100; reason = $"'{ext}' playlist"; }
        else if (Array.IndexOf(MediaExtensions, ext) >= 0) { score += 80; reason = $"'{ext}' media file"; }
        else if (ext.Length == 0) { score += 10; reason = "no extension (redirect or API URL)"; }
        else { score += 5; reason = $"unknown extension '{ext}'"; }

        for (int i = 0; i < MediaKeyHints.Length; i++)
            if (lowerPath.Contains(MediaKeyHints[i]))
            {
                // Earlier hints are more specific, so they are worth more.
                int bonus = 60 - i * 4;
                score += bonus;
                reason += $" + key '{MediaKeyHints[i]}' (+{bonus})";
                break;
            }

        return new MediaCandidate(url, Describe(keyPath), score, reason);
    }

    /// <summary>The lowercase file extension of a URL's path, or "" when it has none.</summary>
    private static string UrlExtension(string url)
    {
        int cut = url.IndexOfAny(new[] { '?', '#' });
        string path = cut < 0 ? url : url[..cut];
        int slash = path.LastIndexOf('/');
        int dot   = path.LastIndexOf('.');
        return dot > slash && dot >= 0 ? path[dot..].ToLowerInvariant() : string.Empty;
    }

    private static string Describe(string keyPath) => keyPath.Length == 0 ? "(root)" : keyPath;

    /// <summary>First value of <paramref name="key"/> anywhere in the plist, as a double (0 when absent).</summary>
    private static double FindPlistDouble(object? node, string key)
        => FindPlistValue(node, key) is { } v ? PlistDouble(v) : 0.0;

    /// <summary>First value of <paramref name="key"/> anywhere in the plist, as a string (null when absent).</summary>
    private static string? FindPlistString(object? node, string key)
        => FindPlistValue(node, key) as string;

    private static object? FindPlistValue(object? node, string key)
    {
        switch (node)
        {
            case Dictionary<string, object?> d:
                foreach (var (k, v) in d)
                {
                    if (k.Equals(key, StringComparison.OrdinalIgnoreCase) && v is not null) return v;
                    if (FindPlistValue(v, key) is { } nested) return nested;
                }
                break;
            case List<object?> list:
                foreach (var v in list)
                    if (FindPlistValue(v, key) is { } nested) return nested;
                break;
        }
        return null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[] BuildOk(int cseq)
        => RtspMessage.BuildResponse(200, "OK", cseq);

    private async Task StopStreamAsync()
    {
        // AirPlay Video (URL playback) lives on the shared player, not on this
        // session — but when the phone leaves the network it just drops the socket
        // instead of sending POST /stop, and FFmpeg would then loop forever on a
        // stream nobody is watching. Only the session that actually started
        // playback may end it: video-out drives transport over many short-lived
        // control connections, and any of the others closing means nothing.
        if (_startedVideoPlayback)
        {
            _startedVideoPlayback = false;
            if (_videoPlayer.IsActive)
            {
                Log.Info("Video", "session teardown — stopping URL playback");
                _videoPlayer.Stop();
            }
        }

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
        if (_audio is not null)
        {
            await _audio.DisposeAsync();
            _audio = null;
        }
        _eventListener?.Stop();
        _eventListener = null;
        _timingCts?.Cancel();
        _timingCts = null;
        _timing?.Dispose();
        _timing = null;
    }

    public async ValueTask DisposeAsync() => await StopStreamAsync();
}
