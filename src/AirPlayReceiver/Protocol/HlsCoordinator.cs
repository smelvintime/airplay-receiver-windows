using AirPlayReceiver.Media;
using AirPlayReceiver.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AirPlayReceiver.Protocol;

/// <summary>
/// Drives the FCUP exchange that makes YouTube (and other <c>mlhls://</c>
/// senders) playable.
///
/// <b>Why this exists.</b> For app-sourced video iOS does not give the receiver
/// a URL it can fetch. <c>POST /play</c> carries
/// <c>Content-Location: mlhls://localhost/master.m3u8</c> — a scheme that only
/// resolves inside the phone. The receiver has to ask for each playlist:
/// <code>
///   receiver → phone   POST /event   (reverse channel)  "FCUP request", XML plist
///   phone → receiver   POST /action  (normal channel)   binary plist, playlist in a &lt;data&gt; blob
/// </code>
/// repeated once per playlist — master first, then every child it lists, strictly
/// one outstanding request at a time — and only when that queue drains does
/// playback start, against our own loopback <see cref="HlsServer"/>.
///
/// <b>Lifetime.</b> One instance for the app run, like the reverse-channel
/// registry: /reverse, /play and /action each arrive on a different TCP
/// connection, so no per-connection object can hold this state.
///
/// References: UxPlay lib/fcup_request.h, lib/http_handlers.h
/// (http_handler_action, http_handler_play, http_handler_hls).
/// </summary>
public sealed class HlsCoordinator : IAsyncDisposable
{
    /// <summary>
    /// The User-Agent an Apple TV sends inside the FCUP request headers.
    ///
    /// The unbalanced <c>(</c> is not a typo and must not be "fixed": this exact
    /// byte sequence is what UxPlay ships (lib/fcup_request.h:30) and what phones
    /// are known to answer. Treat it as an opaque token, not a formatted string.
    /// </summary>
    private const string FcupUserAgent =
        "AppleCoreMedia/1.0.0.11B554a (Apple TV; U; CPU OS 7_0_4 like Mac OS X; en_us";

    // Constants apsdk uses and UxPlay copied; the phone does not appear to
    // interpret them, but it does expect the keys to be present.
    private const long FcupSessionId  = 1;
    private const long FcupClientInfo = 1;
    private const long FcupClientRef  = 40030004;

    private readonly ReverseChannelRegistry _channels;
    // (url, startPositionSeconds, legacyStartFraction). The fraction is only ever
    // non-zero on the direct route: an FCUP item's position is always absolute.
    private readonly Action<string, double, double> _onPlay;
    private readonly Func<double>           _onPauseReportPosition; // pause; returns position in seconds

    // RequestID → the playback item the request was issued for. iOS echoes the id
    // back, which lets a late response for the previous item be told apart from
    // the current one — the case that actually happens when an ad is inserted
    // mid-cycle. (UxPlay reads the id for logging only and routes on URL alone.)
    private readonly ConcurrentDictionary<int, HlsVideo> _pending = new();
    private int _fcupRequestId;

    /// <summary>Cache of playback items, keyed by the /play <c>uuid</c>.</summary>
    public HlsPlaylistStore Store { get; } = new();

    /// <summary>The loopback playlist server our player fetches from.</summary>
    public HlsServer Server { get; }

    /// <param name="channels">Registry of reverse (PTTH) channels; FCUP requests go out on the session's event channel.</param>
    /// <param name="onPlay">Starts the player. Called once per item, after every playlist has arrived.</param>
    /// <param name="onPauseReportPosition">Pauses the player and reports where it was, for <c>playlistRemove</c>.</param>
    public HlsCoordinator(ReverseChannelRegistry channels,
                          Action<string, double, double> onPlay,
                          Func<double> onPauseReportPosition)
    {
        _channels              = channels;
        _onPlay                = onPlay;
        _onPauseReportPosition = onPauseReportPosition;
        Server                 = new HlsServer(Store);
    }

    public async ValueTask DisposeAsync() => await Server.DisposeAsync().ConfigureAwait(false);

    // ── /play routing ─────────────────────────────────────────────────────────

    /// <summary>How a <c>Content-Location</c> should be played.</summary>
    public enum PlayRoute
    {
        /// <summary><c>mlhls://</c> or <c>nfhls://</c>: only the phone can fetch it, so run the FCUP exchange.</summary>
        Hls,

        /// <summary><c>http(s)://</c>: hand it straight to the player, no FCUP.</summary>
        Direct,

        /// <summary>Anything else (notably <c>file://</c>) — refuse.</summary>
        Unsupported,
    }

    /// <summary>
    /// Classifies a /play <c>Content-Location</c>.
    ///
    /// Note the <see cref="PlayRoute.Direct"/> branch: UxPlay gates /play on
    /// <c>strstr(location, "/master.m3u8")</c> and 400s everything else, so every
    /// sender that hands over a plain https URL fails there (their issues
    /// #538/#555). Those URLs need no FCUP at all — the player can fetch them.
    /// </summary>
    public static PlayRoute Classify(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return PlayRoute.Unsupported;

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return PlayRoute.Direct;
        }

        if (url.StartsWith("mlhls://", StringComparison.OrdinalIgnoreCase) ||   // YouTube
            url.StartsWith("nfhls://", StringComparison.OrdinalIgnoreCase))     // Netflix
        {
            string path = url.Split('?', 2)[0];
            return path.EndsWith("/master.m3u8", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith("/index.m3u8",  StringComparison.OrdinalIgnoreCase)
                 ? PlayRoute.Hls
                 : PlayRoute.Unsupported;
        }

        return PlayRoute.Unsupported;
    }

    /// <summary>
    /// Routes an accepted <c>POST /play</c>. Returns false when the location is
    /// one we cannot play, so the caller answers 400; otherwise it returns
    /// immediately (true) and the FCUP exchange, if any, runs in the background —
    /// iOS must get its 200 without waiting for playlists.
    /// </summary>
    public bool HandlePlay(string? appleSessionId, string? uuid, string? url, double startPositionSeconds,
                           double legacyStartFraction = 0)
    {
        PlayRoute route = Classify(url);
        Log.Info("HLS", $"/play route={route} url='{url}' uuid={uuid ?? "(none)"} " +
                        $"start={startPositionSeconds.ToString("F3", CultureInfo.InvariantCulture)}s " +
                        $"session={appleSessionId ?? "(none)"}");

        switch (route)
        {
            case PlayRoute.Direct:
                _onPlay(url!, startPositionSeconds, legacyStartFraction);
                return true;

            case PlayRoute.Hls:
                if (string.IsNullOrEmpty(appleSessionId))
                {
                    // Without the GUID there is no reverse channel to ask on, so
                    // the playlists can never arrive.
                    Log.Error("HLS", $"/play {url} needs the FCUP exchange but carried no {ReverseChannel.SessionIdHeader}");
                    return false;
                }
                StartOrResume(appleSessionId!, uuid, url!, startPositionSeconds);
                return true;

            default:
                Log.Warn("HLS", $"/play Content-Location '{url}' is not a scheme we can play — refusing");
                return false;
        }
    }

    /// <summary>
    /// Resumes a cached item when iOS re-sends /play with a uuid we already
    /// fetched (what happens when an advertisement ends), otherwise begins a
    /// fresh FCUP cycle.
    /// </summary>
    private void StartOrResume(string appleSessionId, string? uuid, string url, double startPositionSeconds)
    {
        // iOS always sends a uuid; fall back to the URL so a sender that omits it
        // still gets caching rather than a NullReferenceException.
        string key = string.IsNullOrEmpty(uuid) ? url : uuid!;

        if (Store.TryGet(key, out HlsVideo cached) && cached.IsComplete)
        {
            cached.AppleSessionId       = appleSessionId;
            cached.StartPositionSeconds = startPositionSeconds;
            Store.MakeCurrent(key);

            double at = Math.Max(cached.ResumePositionSeconds, startPositionSeconds);
            Log.Info("HLS", $"Resuming cached item uuid={key} at {at.ToString("F3", CultureInfo.InvariantCulture)}s " +
                            $"— no FCUP round trips needed ({cached.MediaUriCount} playlist(s) already held)");
            _onPlay(cached.PlaybackLocation, at, 0);
            return;
        }

        BeginFcupCycle(appleSessionId, key, url, startPositionSeconds);
    }

    /// <summary>Creates the playback item and issues the first FCUP request (the master playlist).</summary>
    private void BeginFcupCycle(string appleSessionId, string uuid, string url, double startPositionSeconds)
    {
        Server.Start();   // idempotent; the origin below needs the bound port

        // uri_prefix is the location minus its final path segment: every child URI
        // in the master shares it, and it is what gets swapped for our origin.
        int cut = url.LastIndexOf('/');
        if (cut <= 0)
        {
            Log.Error("HLS", $"Cannot derive a URI prefix from '{url}' — refusing to start the FCUP cycle");
            return;
        }
        string uriPrefix = url[..cut];

        var video = new HlsVideo(uuid, appleSessionId, url, uriPrefix, Server.Origin, Store.AllocateLocalToken())
        {
            StartPositionSeconds = startPositionSeconds,
        };
        Store.AddAndMakeCurrent(video);

        Log.Info("HLS", $"FCUP cycle starting for uuid={uuid}: prefix='{uriPrefix}' → '{video.LocalUriPrefix}', " +
                        $"player will be pointed at {video.PlaybackLocation}");
        SendFcupRequest(video, url);
    }

    // ── /action ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Handles <c>POST /action</c>: FCUP responses carrying playlist bytes, plus
    /// the playlist edits iOS uses to splice advertisements in and out.
    ///
    /// Always answers 200 with an empty body except for a failed session guard —
    /// the phone treats a 400 as a reason to abandon the session, so an action we
    /// merely cannot act on (no current item, unknown type) is logged, not
    /// rejected. UxPlay asserts on the no-current-item case instead, which is the
    /// crash behind their issues #545/#550.
    /// </summary>
    public byte[] HandleAction(RtspMessage msg)
    {
        string? sessionId = msg.Headers.TryGetValue(ReverseChannel.SessionIdHeader, out string? sid) ? sid.Trim() : null;
        if (string.IsNullOrEmpty(sessionId))
        {
            Log.Error("HLS", $"/action without {ReverseChannel.SessionIdHeader} — 400");
            return BadRequest(msg);
        }

        // Only a sender that completed the /reverse (PTTH) upgrade for this session
        // may drive playback from here.
        //
        // The Store.Current check below cannot carry that weight on its own:
        // Store.Current is null whenever nothing is playing, and stays null through
        // any Direct-route (plain http(s)) playback, because HandlePlay's Direct
        // branch never touches the Store. In the receiver's normal idle state the
        // check is therefore a no-op, and any LAN device could POST a hand-built
        // playlistInsert with an invented session id and reach the player.
        //
        // A phone that never opened an event channel could not answer an FCUP
        // request either, so it has no legitimate reason to be posting /action.
        if (!_channels.HasChannel(sessionId!, ReverseChannel.PurposeEvent))
        {
            Log.Error("HLS", $"/action for session={sessionId} which holds no live reverse event channel " +
                             "— refusing to drive playback, 400");
            return BadRequest(msg);
        }

        HlsVideo? current = Store.Current;
        if (current is not null &&
            !string.IsNullOrEmpty(current.AppleSessionId) &&
            !string.Equals(current.AppleSessionId, sessionId, StringComparison.Ordinal))
        {
            Log.Error("HLS", $"/action {ReverseChannel.SessionIdHeader} changed since /play:\n" +
                             $"  was: \"{current.AppleSessionId}\"\n  now: \"{sessionId}\" — 400");
            return BadRequest(msg);
        }

        byte[] body = msg.Body ?? Array.Empty<byte>();
        if (body.Length < 8 || Encoding.ASCII.GetString(body, 0, 8) != "bplist00")
        {
            // UxPlay checks the Content-Type header; checking the magic is the same
            // test without trusting a header the phone sometimes omits.
            Log.Error("HLS", $"/action body is not a binary plist ({body.Length}B, " +
                             $"Content-Type='{msg.ContentType}') — 400");
            return BadRequest(msg);
        }

        Dictionary<string, object?>? root;
        try
        {
            root = BinaryPlist.Parse(body) as Dictionary<string, object?>;
        }
        catch (Exception ex)
        {
            Log.Error("HLS", $"/action plist parse failed ({body.Length}B) — 400", ex);
            return BadRequest(msg);
        }

        if (root is null || root.GetValueOrDefault("type") is not string type)
        {
            Log.Error("HLS", "/action plist has no string 'type' — 400");
            return BadRequest(msg);
        }

        var parameters = root.GetValueOrDefault("params") as Dictionary<string, object?>;

        try
        {
            switch (type)
            {
                case "unhandledURLResponse":
                    if (parameters is null) { Log.Error("HLS", "/action unhandledURLResponse without 'params' — 400"); return BadRequest(msg); }
                    HandleUnhandledUrlResponse(sessionId!, parameters);
                    break;

                case "playlistRemove":
                    HandlePlaylistRemove(parameters);
                    break;

                case "playlistInsert":
                    HandlePlaylistInsert(sessionId!, parameters);
                    break;

                default:
                    Log.Info("HLS", $"/action type '{type}' is not one we handle — acknowledging");
                    break;
            }
        }
        catch (Exception ex)
        {
            // A malformed action must never take down the RTSP connection: iOS
            // would drop the whole AirPlay session.
            Log.Error("HLS", $"/action type '{type}' handler threw — acknowledging anyway", ex);
        }

        return RtspMessage.BuildResponse(200, "OK", msg.CSeq, protocol: msg.Version);
    }

    /// <summary>One playlist arriving from the phone: cache it, then ask for the next.</summary>
    private void HandleUnhandledUrlResponse(string sessionId, Dictionary<string, object?> parameters)
    {
        int    requestId  = ToInt(parameters.GetValueOrDefault("FCUP_Response_RequestID"));
        int    statusCode = ToInt(parameters.GetValueOrDefault("FCUP_Response_StatusCode"));
        string? url       = parameters.GetValueOrDefault("FCUP_Response_URL") as string;
        byte[]? data      = parameters.GetValueOrDefault("FCUP_Response_Data") as byte[];

        if (url is null || data is null)
        {
            Log.Error("HLS", $"FCUP response #{requestId} status={statusCode} is missing " +
                             $"{(url is null ? "FCUP_Response_URL" : "FCUP_Response_Data")} — dropping");
            return;
        }

        // The blob is NOT null-terminated: decode exactly Length bytes. (UxPlay
        // mallocs len+1 and writes the terminator itself, http_handlers.h:568.)
        string playlist = Encoding.UTF8.GetString(data, 0, data.Length);

        // Correlate by the echoed RequestID first: after a playlistInsert two
        // cycles can briefly overlap, and the URL alone cannot tell them apart.
        _pending.TryRemove(requestId, out HlsVideo? issuedFor);
        HlsVideo? video = issuedFor ?? Store.Current;

        if (video is null)
        {
            Log.Error("HLS", $"FCUP response #{requestId} for {url} arrived with no playback item to attach it to " +
                             "(session already torn down?) — ignoring");
            return;
        }

        if (issuedFor is null)
        {
            Log.Warn("HLS", $"FCUP response #{requestId} for {url} does not match any request we issued — " +
                            $"attributing it to the current item (uuid={video.Uuid})");
        }
        else if (!ReferenceEquals(issuedFor, Store.Current))
        {
            Log.Warn("HLS", $"FCUP response #{requestId} belongs to uuid={issuedFor.Uuid}, which is no longer current " +
                            $"(current is {Store.Current?.Uuid ?? "(none)"}) — storing it against its own item");
        }

        if (video.IsComplete)
        {
            // Every playlist for this item already arrived, so nothing is
            // outstanding and this response was not asked for. Applying it would
            // overwrite a cached playlist and restart playback from the top —
            // which is what happens if you trust the payload blindly.
            Log.Warn("HLS", $"FCUP response #{requestId} for {url} arrived after uuid={video.Uuid} was already " +
                            "complete — ignoring (nothing was outstanding)");
            return;
        }

        Log.Info("HLS", $"FCUP response #{requestId} status={statusCode} url={url} ({data.Length}B playlist) " +
                        $"for uuid={video.Uuid}");

        if (IsMasterResponse(url, video)) StoreMaster(video, playlist);
        else                              StoreMedia(video, url, playlist);

        PumpNext(video, sessionId);
    }

    /// <summary>
    /// True when this response is the master playlist. UxPlay tests only for
    /// "/master.m3u8"; also accepting an exact match on what we asked for first
    /// keeps senders whose location ends in <c>/index.m3u8</c> working.
    /// </summary>
    private static bool IsMasterResponse(string url, HlsVideo video)
        => url.Contains("/master.m3u8", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(url, video.ContentLocation, StringComparison.Ordinal);

    private static void StoreMaster(HlsVideo video, string playlist)
    {
        List<string> uris = HlsVideo.CollectMediaUris(playlist, video.UriPrefix);
        string rewritten  = HlsVideo.RewriteMaster(playlist, video.UriPrefix, video.LocalUriPrefix);
        video.StoreMaster(rewritten, uris);

        Log.Info("HLS", $"Master playlist stored for uuid={video.Uuid}: {uris.Count} child playlist(s), " +
                        $"'{video.UriPrefix}' rewritten to '{video.LocalUriPrefix}'");
        for (int i = 0; i < uris.Count; i++)
            Log.Info("HLS", $"  child[{i}] {uris[i]}");

        if (uris.Count == 0)
            Log.Warn("HLS", $"Master playlist listed no URI starting with '{video.UriPrefix}' — " +
                            "playback will start with nothing to play");
    }

    private static void StoreMedia(HlsVideo video, string url, string playlist)
    {
        int chunks = HlsVideo.AnalyzeMediaPlaylist(playlist, out double duration, out bool endList);

        // The response answers the request we issued last, i.e. the index before
        // the one we would ask for next (UxPlay: --uri_num).
        int index = video.NextMediaUriIndex - 1;

        switch (video.StoreMedia(index, playlist, chunks, duration, endList))
        {
            case HlsVideo.MediaStoreResult.Stored:
                Log.Info("HLS", $"Media playlist [{index}] {url}: {chunks} chunk(s), " +
                                $"{duration.ToString("F3", CultureInfo.InvariantCulture)}s total, " +
                                $"{(endList ? "closed (#EXT-X-ENDLIST)" : "no #EXT-X-ENDLIST — live edge")}");
                break;

            case HlsVideo.MediaStoreResult.Duplicate:
                Log.Info("HLS", $"Media playlist [{index}] {url} duplicates an earlier URI — aliased, not stored twice");
                break;

            case HlsVideo.MediaStoreResult.AlreadyPresent:
                Log.Warn("HLS", $"Media playlist [{index}] {url} was already cached — keeping the copy we have");
                break;

            default:
                Log.Error("HLS", $"Media playlist for {url} has no slot at index {index} " +
                                 $"(the master listed {video.MediaUriCount}) — dropping it");
                break;
        }
    }

    /// <summary>
    /// Issues the next FCUP request, or starts playback when the queue drains.
    ///
    /// Strictly one request in flight: the wiki is explicit that the next request
    /// may only follow the client's response to the previous one, so this is
    /// driven from inside the /action handler and never pipelined.
    /// </summary>
    private void PumpNext(HlsVideo video, string sessionId)
    {
        int next = video.NextMediaUriIndex;
        if (next < video.MediaUriCount)
        {
            string? uri = video.MediaUriAt(next);
            if (uri is null) return;

            video.NextMediaUriIndex = next + 1;
            Log.Info("HLS", $"Requesting child playlist {next + 1}/{video.MediaUriCount} for uuid={video.Uuid}");
            SendFcupRequest(video, uri);
            return;
        }

        double at = Math.Max(video.ResumePositionSeconds, video.StartPositionSeconds);
        Log.Info("HLS", $"All {video.MediaUriCount} playlist(s) received for uuid={video.Uuid} — " +
                        $"starting playback at {video.PlaybackLocation} " +
                        $"from {at.ToString("F3", CultureInfo.InvariantCulture)}s");
        _onPlay(video.PlaybackLocation, at, 0);
    }

    /// <summary>
    /// <c>playlistRemove</c>: iOS is taking the current item off the playlist —
    /// typically an advertisement about to roll. Pause, remember where we were,
    /// and <b>keep</b> the playlists: the same uuid comes back on a later /play
    /// and resumes from here without a single FCUP round trip.
    /// </summary>
    private void HandlePlaylistRemove(Dictionary<string, object?>? parameters)
    {
        string? uuid = (parameters?.GetValueOrDefault("item") as Dictionary<string, object?>)
                       ?.GetValueOrDefault("uuid") as string;

        if (string.IsNullOrEmpty(uuid))
        {
            // UxPlay asserts the uuid is present and dies if it is not.
            Log.Warn("HLS", "playlistRemove carried no item.uuid — nothing to remove");
            return;
        }

        HlsVideo? current = Store.Current;
        if (current is null || !string.Equals(current.Uuid, uuid, StringComparison.Ordinal))
        {
            Log.Warn("HLS", $"playlistRemove uuid={uuid} does not match the current item " +
                            $"({current?.Uuid ?? "(none)"}) — leaving playback alone");
            return;
        }

        double position = 0;
        try { position = _onPauseReportPosition(); }
        catch (Exception ex) { Log.Error("HLS", "Pausing for playlistRemove failed", ex); }

        current.ResumePositionSeconds = position;
        Store.ClearCurrent();

        Log.Info("HLS", $"playlistRemove uuid={uuid}: paused at {position.ToString("F3", CultureInfo.InvariantCulture)}s, " +
                        $"{current.MediaUriCount} playlist(s) retained for resume");
    }

    /// <summary>
    /// <c>playlistInsert</c>: iOS is splicing a new item in — in practice the
    /// advertisement before a YouTube video, and then the video itself once the
    /// ad finishes.
    ///
    /// UxPlay logs "FIXME: playlistInsert is not yet implemented"
    /// (http_handlers.h:509) and stops there, which is exactly why the ad plays
    /// and the main content never starts on that receiver. We act on it: switch
    /// to the item and, if we have not fetched it before, begin a fresh FCUP
    /// cycle for its uuid.
    ///
    /// The item dictionary's shape is not documented, so the location is looked
    /// up under every key name it has been seen with and then, failing that, by
    /// scanning the item for a value with a scheme we recognise. Everything is
    /// logged verbatim — when this misbehaves in the field the dump is the only
    /// evidence available.
    /// </summary>
    private void HandlePlaylistInsert(string sessionId, Dictionary<string, object?>? parameters)
    {
        if (parameters?.GetValueOrDefault("item") is not Dictionary<string, object?> item)
        {
            Log.Warn("HLS", "playlistInsert carried no 'item' dictionary — nothing to insert");
            return;
        }

        var dump = new StringBuilder();
        DumpNode(item, dump, 1);
        Log.Info("HLS", $"playlistInsert item:\n{dump}");

        string? uuid  = item.GetValueOrDefault("uuid") as string;
        string? url   = FindLocation(item);
        double  start = FindStartPosition(item);

        if (string.IsNullOrEmpty(uuid) && string.IsNullOrEmpty(url))
        {
            Log.Error("HLS", "playlistInsert had neither a uuid nor a playable location — cannot act on it " +
                             "(item dump above)");
            return;
        }

        string key = string.IsNullOrEmpty(uuid) ? url! : uuid!;

        // Already fetched — this is the "ad finished, back to the video" case.
        if (Store.TryGet(key, out HlsVideo cached) && cached.IsComplete)
        {
            cached.AppleSessionId = sessionId;
            Store.MakeCurrent(key);

            double at = Math.Max(cached.ResumePositionSeconds, start);
            Log.Info("HLS", $"playlistInsert uuid={key} is already cached — switching to it and playing from " +
                            $"{at.ToString("F3", CultureInfo.InvariantCulture)}s with no FCUP round trips");
            _onPlay(cached.PlaybackLocation, at, 0);
            return;
        }

        switch (Classify(url))
        {
            case PlayRoute.Hls:
                Log.Info("HLS", $"playlistInsert uuid={key} location={url} — beginning a fresh FCUP cycle " +
                                "(this is the step UxPlay leaves as a FIXME, so ads never hand back to content there)");
                BeginFcupCycle(sessionId, key, url!, start);
                break;

            case PlayRoute.Direct:
                Log.Info("HLS", $"playlistInsert uuid={key} carried a directly fetchable URL — playing it without FCUP: {url}");
                _onPlay(url!, start, 0);
                break;

            default:
                // iOS often follows the insert with its own /play for the item, so
                // this is not necessarily fatal — but it is the case to look at
                // first if content never starts after an advertisement.
                Log.Warn("HLS", $"playlistInsert uuid={key} carried no location we can play " +
                                $"(found '{url ?? "(none)"}') — waiting for iOS to send /play for it");
                break;
        }
    }

    // ── FCUP request ──────────────────────────────────────────────────────────

    /// <summary>
    /// Sends one FCUP request on the session's reverse channel.
    ///
    /// Fire-and-forget, like UxPlay's raw <c>send()</c>: the phone's
    /// <c>HTTP/1.1 200 OK</c> is drained by <see cref="ReverseChannel"/> and
    /// carries nothing — the answer comes back later as <c>POST /action</c>.
    /// </summary>
    private void SendFcupRequest(HlsVideo video, string url)
    {
        int requestId = Interlocked.Increment(ref _fcupRequestId);
        _pending[requestId] = video;

        if (!_channels.TryGet(video.AppleSessionId, out ReverseChannel? channel))
        {
            _pending.TryRemove(requestId, out _);
            Log.Error("HLS", $"No reverse event channel for session={video.AppleSessionId} — " +
                             $"cannot request {url}. Playback cannot start without it.");
            return;
        }

        Dictionary<string, object?> body = BuildFcupRequest(url, requestId, video.AppleSessionId);
        Log.Info("HLS", $"→ FCUP request #{requestId} for {url} (session={video.AppleSessionId})");

        _ = channel.SendRequestAsync("POST", "/event", body, CancellationToken.None)
                   .ContinueWith(t =>
                   {
                       if (t.Status != TaskStatus.RanToCompletion || !t.Result)
                       {
                           _pending.TryRemove(requestId, out _);
                           Log.Error("HLS", $"FCUP request #{requestId} for {url} was not sent — " +
                                            "the playlist will never arrive and playback will stall");
                       }
                   }, TaskScheduler.Default);
    }

    /// <summary>
    /// Builds the FCUP request body. Structure is exact — note that the wrapper
    /// key is <c>request</c> (not "fcup-request"), that <c>sessionID</c> appears
    /// both at the root and inside it, and that both are the literal 1.
    ///
    /// Reference: UxPlay lib/fcup_request.h create_fcup_request.
    /// </summary>
    internal static Dictionary<string, object?> BuildFcupRequest(string url, int requestId, string appleSessionId)
        => new()
        {
            ["sessionID"] = FcupSessionId,
            ["type"]      = "unhandledURLRequest",
            ["request"]   = new Dictionary<string, object?>
            {
                ["FCUP_Response_ClientInfo"] = FcupClientInfo,
                ["FCUP_Response_ClientRef"]  = FcupClientRef,
                ["FCUP_Response_RequestID"]  = (long)requestId,
                ["FCUP_Response_URL"]        = url,
                ["sessionID"]                = FcupSessionId,
                ["FCUP_Response_Headers"]    = new Dictionary<string, object?>
                {
                    ["X-Playback-Session-Id"] = appleSessionId,
                    ["User-Agent"]            = FcupUserAgent,
                },
            },
        };

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[] BadRequest(RtspMessage msg)
        => RtspMessage.BuildResponse(400, "Bad Request", msg.CSeq, protocol: msg.Version);

    private static int ToInt(object? v) => v switch
    {
        long l   => (int)l,
        int i    => i,
        double d => (int)d,
        _        => 0,
    };

    /// <summary>Key names a playlist item's location has been seen under, most specific first.</summary>
    private static readonly string[] LocationKeys =
    {
        "Content-Location", "contentLocation", "content-location", "location", "url", "URL", "assetURL",
    };

    private static string? FindLocation(Dictionary<string, object?> item)
    {
        foreach (string key in LocationKeys)
            if (item.GetValueOrDefault(key) is string s && Classify(s) != PlayRoute.Unsupported)
                return s;

        // Nothing under a known key: take the first value anywhere in the item
        // that looks like something we could play.
        return FindPlayableString(item);
    }

    private static string? FindPlayableString(object? node) => node switch
    {
        string s when Classify(s) != PlayRoute.Unsupported => s,
        Dictionary<string, object?> d => FirstPlayable(d.Values),
        List<object?> l               => FirstPlayable(l),
        _                             => null,
    };

    private static string? FirstPlayable(IEnumerable<object?> values)
    {
        foreach (object? v in values)
            if (FindPlayableString(v) is { } found) return found;
        return null;
    }

    private static double FindStartPosition(Dictionary<string, object?> item)
    {
        foreach (string key in new[] { "Start-Position-Seconds", "startPositionSeconds", "startPosition", "position" })
        {
            object? v = item.GetValueOrDefault(key);
            if (v is double d) return d;
            if (v is long l)   return l;
        }
        return 0;
    }

    private static void DumpNode(object? node, StringBuilder sb, int indent)
    {
        string pad = new(' ', indent * 2);
        switch (node)
        {
            case Dictionary<string, object?> d:
                foreach ((string k, object? v) in d)
                {
                    if (v is Dictionary<string, object?> or List<object?>)
                    {
                        sb.Append(pad).Append(k).Append(":\n");
                        DumpNode(v, sb, indent + 1);
                    }
                    else sb.Append(pad).Append(k).Append(" = ").Append(ScalarText(v)).Append('\n');
                }
                break;

            case List<object?> list:
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] is Dictionary<string, object?> or List<object?>)
                    {
                        sb.Append(pad).Append('[').Append(i).Append("]:\n");
                        DumpNode(list[i], sb, indent + 1);
                    }
                    else sb.Append(pad).Append('[').Append(i).Append("] = ").Append(ScalarText(list[i])).Append('\n');
                }
                break;

            default:
                sb.Append(pad).Append(ScalarText(node)).Append('\n');
                break;
        }
    }

    private static string ScalarText(object? v) => v switch
    {
        null     => "null",
        byte[] b => $"<{b.Length} bytes>",
        string s => s.Length > 512 ? $"\"{s[..512]}…\"(+{s.Length - 512} more)" : $"\"{s}\"",
        _        => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "null",
    };
}
