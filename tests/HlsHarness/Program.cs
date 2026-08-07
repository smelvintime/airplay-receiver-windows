using AirPlayReceiver.Media;
using AirPlayReceiver.Protocol;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text;

namespace AirPlayReceiver.HlsHarness;

/// <summary>
/// Proves the FCUP/HLS pipeline end to end without an iPhone.
///
/// Part 1 checks the playlist transforms against fixtures in the shapes iOS
/// really sends. Part 2 runs the whole loop over loopback TCP: a fake phone
/// performs the /reverse upgrade, posts /play with an <c>mlhls://</c> location,
/// answers each FCUP request the receiver sends with a canned playlist, and then
/// fetches the rewritten master and expanded media playlists back out of the
/// receiver's own HLS server — exactly what FFmpeg will do.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== AirPlay FCUP/HLS harness ===");

        try
        {
            RunPlaylistUnitChecks();
            await RunEndToEndAsync();
        }
        catch (Exception ex)
        {
            Fail("harness", $"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        Console.WriteLine();
        Console.WriteLine($"Summary: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    // ── Part 1: playlist transforms ───────────────────────────────────────────

    private static void RunPlaylistUnitChecks()
    {
        Section("Unit: playlist transforms");

        // -- master: collect the child URIs, in order, including the repeat -----
        List<string> uris = HlsVideo.CollectMediaUris(Fixtures.Master, Fixtures.UriPrefix);
        Check("master child URIs collected in order",
              uris.SequenceEqual(Fixtures.MasterChildUris),
              $"got [{string.Join(", ", uris)}]");
        Check("both #EXT-X-MEDIA and #EXT-X-STREAM-INF URIs are picked up",
              uris.Count == 5 && uris[0].EndsWith("/audio-en.m3u8", StringComparison.Ordinal)
                              && uris[2].EndsWith("/video-360.m3u8", StringComparison.Ordinal),
              $"count={uris.Count}");

        // -- master: rewrite the prefix to our loopback origin ------------------
        const string origin = "http://127.0.0.1:54321";
        string rewritten = HlsVideo.RewriteMaster(Fixtures.Master, Fixtures.UriPrefix, origin);
        Check("mlhls://localhost rewritten to http://localhost:<port>",
              !rewritten.Contains("mlhls://", StringComparison.Ordinal) &&
              rewritten.Contains(origin + "/audio-en.m3u8", StringComparison.Ordinal) &&
              rewritten.Contains(origin + "/video-720.m3u8", StringComparison.Ordinal),
              rewritten);
        Check("rewrite covers the URI= attribute of #EXT-X-MEDIA",
              rewritten.Contains($"URI=\"{origin}/audio-es.m3u8\"", StringComparison.Ordinal),
              rewritten);
        Check("rewrite leaves everything else byte-identical",
              rewritten.Replace(origin, Fixtures.UriPrefix, StringComparison.Ordinal) == Fixtures.Master,
              "round trip differed");

        // -- media: chunk count, duration, ENDLIST -----------------------------
        int chunks = HlsVideo.AnalyzeMediaPlaylist(Fixtures.CondensedMedia, out double duration, out bool endList);
        Check("condensed media playlist analysed",
              chunks == 3 && Math.Abs(duration - 14.24) < 0.001 && endList,
              $"chunks={chunks} duration={duration.ToString("F3", CultureInfo.InvariantCulture)} endlist={endList}");

        int liveChunks = HlsVideo.AnalyzeMediaPlaylist(
            Fixtures.CondensedMedia.Replace("#EXT-X-ENDLIST\n", string.Empty, StringComparison.Ordinal),
            out _, out bool liveEnd);
        Check("missing #EXT-X-ENDLIST reported as an open (live) playlist",
              liveChunks == 3 && !liveEnd, $"chunks={liveChunks} endlist={liveEnd}");

        // -- YT-condensed expansion --------------------------------------------
        string expanded = HlsVideo.ExpandCondensedPlaylist(Fixtures.CondensedMedia);
        bool allChunks = Fixtures.CondensedExpandedChunks.All(c => expanded.Contains(c, StringComparison.Ordinal));
        Check("condensed chunk URIs expand to absolute CDN URLs", allChunks, expanded);
        Check("no condensed suffix survives expansion",
              !expanded.Contains("\ns/", StringComparison.Ordinal),
              expanded);
        Check("expansion keeps the tag lines untouched",
              expanded.Contains("#EXT-X-TARGETDURATION:5", StringComparison.Ordinal) &&
              expanded.Contains("#EXTINF:4.240,", StringComparison.Ordinal) &&
              expanded.TrimEnd('\n').EndsWith("#EXT-X-ENDLIST", StringComparison.Ordinal),
              expanded);
        Check("expanded playlist still has the same chunk count",
              HlsVideo.AnalyzeMediaPlaylist(expanded, out _, out _) == 3,
              expanded);

        Check("a playlist without the condensed header is returned unchanged",
              ReferenceEquals(HlsVideo.ExpandCondensedPlaylist(Fixtures.PlainMedia), Fixtures.PlainMedia) ||
              HlsVideo.ExpandCondensedPlaylist(Fixtures.PlainMedia) == Fixtures.PlainMedia,
              "plain playlist was modified");

        Check("the master playlist is not mistaken for a condensed one",
              HlsVideo.ExpandCondensedPlaylist(Fixtures.Master) == Fixtures.Master,
              "master playlist was modified");

        // CRLF: UxPlay searches for the literal "#EXTM3U\n" and walks off the end
        // of a NULL pointer when a playlist uses CRLF. Ours must simply work.
        string crlf = Fixtures.CondensedMedia.Replace("\n", "\r\n", StringComparison.Ordinal);
        string crlfExpanded = HlsVideo.ExpandCondensedPlaylist(crlf);
        Check("CRLF line endings are handled",
              Fixtures.CondensedExpandedChunks.All(c => crlfExpanded.Contains(c, StringComparison.Ordinal)) &&
              crlfExpanded.Contains("\r\n", StringComparison.Ordinal),
              crlfExpanded);

        // -- dedupe -------------------------------------------------------------
        var video = new HlsVideo("uuid-1", "SID", Fixtures.ContentLocation, Fixtures.UriPrefix, origin, "v7");
        video.StoreMaster(rewritten, Fixtures.MasterChildUris);
        Check("playback location points at this item's own path on our server",
              video.PlaybackLocation == origin + "/v7/master.m3u8", video.PlaybackLocation);

        HlsVideo.MediaStoreResult dup0 = video.StoreMedia(3, Fixtures.CondensedMedia, 3, 14.24, true);
        HlsVideo.MediaStoreResult dup1 = video.StoreMedia(4, Fixtures.CondensedMedia, 3, 14.24, true);
        HlsVideo.MediaStoreResult again = video.StoreMedia(3, Fixtures.PlainMedia, 2, 10, true);
        Check("first copy of a repeated URI is stored", dup0 == HlsVideo.MediaStoreResult.Stored, $"{dup0}");
        Check("second copy of the same URI is reported as a duplicate", dup1 == HlsVideo.MediaStoreResult.Duplicate, $"{dup1}");
        Check("a second playlist for an already-filled slot never overwrites it",
              again == HlsVideo.MediaStoreResult.AlreadyPresent, $"{again}");
        Check("the duplicate slot resolves to the stored copy",
              video.TryGetMedia("/video-720.m3u8", out string got, out int gotChunks, out _) &&
              got == Fixtures.CondensedMedia && gotChunks == 3,
              "lookup failed");
        Check("a URI that was never fetched is not served",
              !video.TryGetMedia("/audio-en.m3u8", out _, out _, out _),
              "unfetched playlist was served");

        // -- FCUP request body --------------------------------------------------
        Dictionary<string, object?> fcup = HlsCoordinator.BuildFcupRequest(Fixtures.ContentLocation, 1, "SESSION-GUID");
        string xml = XmlPlist.WriteToString(fcup);
        Check("FCUP request nests under the key 'request'",
              xml.Contains("<key>request</key>", StringComparison.Ordinal), xml);
        Check("FCUP request carries the magic ClientRef 40030004",
              xml.Contains("<integer>40030004</integer>", StringComparison.Ordinal), xml);
        Check("FCUP User-Agent is byte-identical to the known-working string (unbalanced paren included)",
              xml.Contains("<string>AppleCoreMedia/1.0.0.11B554a (Apple TV; U; CPU OS 7_0_4 like Mac OS X; en_us</string>",
                           StringComparison.Ordinal),
              xml);

        // -- /play routing ------------------------------------------------------
        Check("mlhls master.m3u8 routes to the FCUP exchange",
              HlsCoordinator.Classify("mlhls://localhost/master.m3u8") == HlsCoordinator.PlayRoute.Hls, "");
        Check("nfhls index.m3u8 routes to the FCUP exchange",
              HlsCoordinator.Classify("nfhls://localhost/index.m3u8") == HlsCoordinator.PlayRoute.Hls, "");
        Check("https URLs go straight to the player (UxPlay 400s these — #538/#555)",
              HlsCoordinator.Classify("https://example.com/v.m3u8") == HlsCoordinator.PlayRoute.Direct, "");
        Check("file:// is refused",
              HlsCoordinator.Classify("file:///C:/clip.m3u8") == HlsCoordinator.PlayRoute.Unsupported, "");
        Check("null/empty Content-Location is refused",
              HlsCoordinator.Classify(null) == HlsCoordinator.PlayRoute.Unsupported &&
              HlsCoordinator.Classify("") == HlsCoordinator.PlayRoute.Unsupported, "");
    }

    // ── Part 2: the whole loop over loopback ──────────────────────────────────

    private static async Task RunEndToEndAsync()
    {
        Section("End-to-end: fake phone → receiver → FCUP → local HLS server");

        var playEvents = new BlockingCollection<(string Url, double Position)>();
        int pauseCalls = 0;
        double pausePosition = 7.5;

        var registry = new ReverseChannelRegistry();
        await using var hls = new HlsCoordinator(
            channels: registry,
            onPlay:   (url, pos) => playEvents.Add((url, pos)),
            onPauseReportPosition: () => { pauseCalls++; return pausePosition; });

        await using var receiver = new MiniReceiver(hls, registry);
        await using var phone    = new FakePhone(receiver.Port);
        using var http           = new HttpClient();

        const string videoUuid = "11111111-2222-3333-4444-555555555555";
        const string adUuid    = "99999999-8888-7777-6666-555555555555";

        // 1 ── reverse (PTTH) upgrade ------------------------------------------
        string upgrade = await phone.OpenReverseChannelAsync();
        Check("POST /reverse answered 101 Switching Protocols",
              upgrade.Contains("101", StringComparison.Ordinal), upgrade);
        Check("upgraded socket registered as the session's event channel",
              await WaitUntilAsync(() => registry.Count == 1, Timeout), $"registry holds {registry.Count}");

        // 2 ── POST /play with an mlhls:// location -----------------------------
        HttpReply play = await phone.PlayAsync(Fixtures.ContentLocation, videoUuid, 12.5);
        Check("POST /play answered 200 immediately (before any playlist arrived)",
              play.Status == 200, play.StatusLine);

        // 3 ── the receiver asks for the master playlist ------------------------
        FcupRequest master = await phone.ReadFcupRequestAsync(Timeout);
        Check("FCUP request arrives as POST /event on the reverse channel",
              master.Method == "POST" && master.Path == "/event", $"{master.Method} {master.Path}");
        Check("FCUP request is an XML plist",
              master.ContentType.Contains("text/x-apple-plist+xml", StringComparison.Ordinal), master.ContentType);
        Check("FCUP request carries the session GUID as a header",
              master.Headers.GetValueOrDefault(ReverseChannel.SessionIdHeader) == phone.SessionId,
              master.Headers.GetValueOrDefault(ReverseChannel.SessionIdHeader) ?? "(absent)");
        Check("FCUP request body has the documented shape",
              master.Body.GetValueOrDefault("sessionID") is 1L &&
              master.Body.GetValueOrDefault("type") as string == "unhandledURLRequest" &&
              master.Request?.GetValueOrDefault("FCUP_Response_ClientInfo") is 1L &&
              master.Request?.GetValueOrDefault("FCUP_Response_ClientRef") is 40030004L &&
              master.Request?.GetValueOrDefault("sessionID") is 1L,
              master.BodyXml);
        Check("first FCUP RequestID is 1",  master.RequestId == 1, $"id={master.RequestId}");
        Check("first FCUP request asks for the master playlist",
              master.Url == Fixtures.ContentLocation, master.Url ?? "(none)");
        Check("FCUP headers carry X-Playback-Session-Id and the Apple TV User-Agent",
              master.RequestHeaders?.GetValueOrDefault("X-Playback-Session-Id") as string == phone.SessionId &&
              master.RequestHeaders?.GetValueOrDefault("User-Agent") as string ==
                  "AppleCoreMedia/1.0.0.11B554a (Apple TV; U; CPU OS 7_0_4 like Mac OS X; en_us",
              master.BodyXml);

        // 4 ── phone answers with the master ------------------------------------
        HttpReply masterAck = await phone.FcupResponseAsync(master.RequestId, master.Url!, Fixtures.Master);
        Check("POST /action carrying the master playlist answered 200",
              masterAck.Status == 200 && masterAck.Body.Length == 0,
              $"{masterAck.StatusLine} body={masterAck.Body.Length}B");

        // 5 ── one child playlist at a time, never pipelined ---------------------
        for (int i = 0; i < Fixtures.MasterChildUris.Length; i++)
        {
            FcupRequest child = await phone.ReadFcupRequestAsync(Timeout);
            Check($"FCUP request {i + 2} asks for child playlist [{i}]",
                  child.Url == Fixtures.MasterChildUris[i],
                  $"wanted {Fixtures.MasterChildUris[i]}, got {child.Url}");
            Check($"FCUP RequestID increments to {i + 2}", child.RequestId == i + 2, $"id={child.RequestId}");

            HttpReply ack = await phone.FcupResponseAsync(child.RequestId, child.Url!, Fixtures.MediaFor(child.Url!));
            Check($"POST /action for child [{i}] answered 200", ack.Status == 200, ack.StatusLine);
        }

        // 6 ── queue drained → playback starts -----------------------------------
        Check("player started once every playlist had arrived",
              playEvents.TryTake(out (string Url, double Position) started, (int)Timeout.TotalMilliseconds),
              "player was never started");
        string expectedLocation = $"{hls.Server.Origin}/v1/master.m3u8";
        Check("player was pointed at our own loopback master playlist",
              started.Url == expectedLocation, $"wanted {expectedLocation}, got {started.Url}");
        Check("player started at Start-Position-Seconds (12.5, not the legacy fraction)",
              Math.Abs(started.Position - 12.5) < 0.001, started.Position.ToString("F3", CultureInfo.InvariantCulture));
        Check("no extra FCUP request was sent after the queue drained",
              await phone.NoPendingReverseDataAsync(TimeSpan.FromMilliseconds(300)), "reverse channel had pending bytes");

        // 7 ── serve the playlists back, as FFmpeg will --------------------------
        HttpResponseMessage masterResponse = await http.GetAsync(expectedLocation);
        string masterBody = await masterResponse.Content.ReadAsStringAsync();
        Check("GET /master.m3u8 returns 200", (int)masterResponse.StatusCode == 200, masterResponse.StatusCode.ToString());
        Check("served master has the HLS content type",
              masterResponse.Content.Headers.ContentType?.ToString() == "application/x-mpegURL; charset=utf-8",
              masterResponse.Content.Headers.ContentType?.ToString() ?? "(none)");
        Check("served master has the CORS headers",
              masterResponse.Headers.TryGetValues("Access-Control-Allow-Origin", out var acao) && acao.First() == "*" &&
              masterResponse.Headers.TryGetValues("Access-Control-Allow-Headers", out var acah) &&
              acah.First() == "Content-type",
              "missing CORS headers");
        Check("served master has a Date header", masterResponse.Headers.Date is not null, "no Date");
        Check("served master contains no mlhls:// URI",
              !masterBody.Contains("mlhls://", StringComparison.Ordinal), masterBody);
        Check("served master points its children at us",
              masterBody.Contains($"{hls.Server.Origin}/v1/video-720.m3u8", StringComparison.Ordinal) &&
              masterBody.Contains($"URI=\"{hls.Server.Origin}/v1/audio-en.m3u8\"", StringComparison.Ordinal),
              masterBody);

        string video360 = await http.GetStringAsync($"{hls.Server.Origin}/v1/video-360.m3u8");
        Check("served media playlist is expanded at serve time",
              Fixtures.CondensedExpandedChunks.All(c => video360.Contains(c, StringComparison.Ordinal)),
              video360);
        Check("served media playlist has no condensed suffixes left",
              !video360.Contains("\ns/", StringComparison.Ordinal), video360);

        string audioEn = await http.GetStringAsync($"{hls.Server.Origin}/v1/audio-en.m3u8");
        Check("a plain media playlist is served byte-identical", audioEn == Fixtures.PlainMedia, audioEn);

        string video720 = await http.GetStringAsync($"{hls.Server.Origin}/v1/video-720.m3u8");
        Check("the deduplicated URI still serves its playlist",
              video720.Contains(Fixtures.CondensedExpandedChunks[0], StringComparison.Ordinal), video720);

        HttpResponseMessage missing = await http.GetAsync($"{hls.Server.Origin}/v1/not-a-playlist.m3u8");
        Check("an unknown playlist is a 404", (int)missing.StatusCode == 404, missing.StatusCode.ToString());

        // 8 ── playlistRemove: pause, keep the playlists -------------------------
        HttpReply removed = await phone.PlaylistRemoveAsync(videoUuid);
        Check("POST /action playlistRemove answered 200", removed.Status == 200, removed.StatusLine);
        Check("playlistRemove paused the player and read its position", pauseCalls == 1, $"pauseCalls={pauseCalls}");

        // 9 ── re-/play with the same uuid resumes with no FCUP -------------------
        HttpReply replay = await phone.PlayAsync(Fixtures.ContentLocation, videoUuid, 0);
        Check("POST /play for a cached uuid answered 200", replay.Status == 200, replay.StatusLine);
        Check("cached item resumed immediately",
              playEvents.TryTake(out (string Url, double Position) resumed, (int)Timeout.TotalMilliseconds),
              "player was not restarted");
        Check("resume used the position captured by playlistRemove",
              resumed.Url == expectedLocation && Math.Abs(resumed.Position - pausePosition) < 0.001,
              $"{resumed.Url} @ {resumed.Position.ToString("F3", CultureInfo.InvariantCulture)}");
        Check("resume cost no FCUP round trips",
              await phone.NoPendingReverseDataAsync(TimeSpan.FromMilliseconds(300)), "an FCUP request was sent");

        // 10 ── playlistInsert starts a fresh cycle (UxPlay's unimplemented case) --
        HttpReply inserted = await phone.PlaylistInsertAsync(adUuid, Fixtures.AdContentLocation);
        Check("POST /action playlistInsert answered 200", inserted.Status == 200, inserted.StatusLine);

        FcupRequest adMaster = await phone.ReadFcupRequestAsync(Timeout);
        Check("playlistInsert started a fresh FCUP cycle for the inserted item",
              adMaster.Url == Fixtures.AdContentLocation, adMaster.Url ?? "(none)");
        await phone.FcupResponseAsync(adMaster.RequestId, adMaster.Url!, Fixtures.AdMaster);

        FcupRequest adChild = await phone.ReadFcupRequestAsync(Timeout);
        Check("inserted item's child playlist is requested",
              adChild.Url == "mlhls://localhost/ad/video-360.m3u8", adChild.Url ?? "(none)");
        await phone.FcupResponseAsync(adChild.RequestId, adChild.Url!, Fixtures.CondensedMedia);

        Check("inserted item starts playing once its playlists arrive",
              playEvents.TryTake(out (string Url, double Position) adStarted, (int)Timeout.TotalMilliseconds),
              "inserted item never started");
        Check("inserted item plays from its own loopback path, not the previous item's",
              adStarted.Url == $"{hls.Server.Origin}/v2/master.m3u8" && adStarted.Url != expectedLocation,
              adStarted.Url);

        string adMasterBody = await http.GetStringAsync($"{hls.Server.Origin}/v2/master.m3u8");
        Check("inserted item's master is rewritten too",
              adMasterBody.Contains($"{hls.Server.Origin}/v2/video-360.m3u8", StringComparison.Ordinal) &&
              !adMasterBody.Contains("mlhls://", StringComparison.Ordinal),
              adMasterBody);
        Check("the earlier item is still served from its own path after the switch",
              (await http.GetStringAsync(expectedLocation)).Contains(
                  $"{hls.Server.Origin}/v1/video-720.m3u8", StringComparison.Ordinal),
              "the first item's master was displaced by the inserted one");

        // 11 ── guards -----------------------------------------------------------
        HttpReply wrongSession = await phone.PostWithSessionAsync(
            "/action", BinaryPlist.Write(new Dictionary<string, object?> { ["type"] = "playlistRemove" }),
            "00000000-0000-0000-0000-000000000000");
        Check("/action with a changed X-Apple-Session-ID is 400", wrongSession.Status == 400, wrongSession.StatusLine);

        HttpReply noSession = await phone.PostWithSessionAsync(
            "/action", BinaryPlist.Write(new Dictionary<string, object?> { ["type"] = "playlistRemove" }), string.Empty);
        Check("/action without X-Apple-Session-ID is 400", noSession.Status == 400, noSession.StatusLine);

        HttpReply junk = await phone.PostWithSessionAsync("/action", Encoding.ASCII.GetBytes("not a plist at all"), null);
        Check("/action with a non-plist body is 400 (and does not crash)", junk.Status == 400, junk.StatusLine);

        HttpReply unknownType = await phone.PostWithSessionAsync(
            "/action", BinaryPlist.Write(new Dictionary<string, object?>
            {
                ["type"] = "somethingWeHaveNeverSeen",
                ["params"] = new Dictionary<string, object?>(),
            }), null);
        Check("/action with an unknown type is acknowledged, not rejected", unknownType.Status == 200, unknownType.StatusLine);

        HttpReply orphanFcup = await phone.PostWithSessionAsync(
            "/action", BinaryPlist.Write(new Dictionary<string, object?>
            {
                ["type"] = "unhandledURLResponse",
                ["params"] = new Dictionary<string, object?>
                {
                    ["FCUP_Response_RequestID"] = 987L,
                    ["FCUP_Response_URL"]       = "mlhls://localhost/stale.m3u8",
                    ["FCUP_Response_Data"]      = Encoding.UTF8.GetBytes(Fixtures.PlainMedia),
                },
            }), null);
        Check("an unsolicited FCUP response survives without crashing (UxPlay asserts here — #545/#550)",
              orphanFcup.Status == 200, orphanFcup.StatusLine);

        // 12 ── the other two /play branches -------------------------------------
        HttpReply direct = await phone.PlayAsync("https://example.com/stream/v.m3u8", "direct-uuid", 3.0);
        Check("a plain https Content-Location is accepted", direct.Status == 200, direct.StatusLine);
        Check("…and goes straight to the player with no FCUP",
              playEvents.TryTake(out (string Url, double Position) directPlay, (int)Timeout.TotalMilliseconds) &&
              directPlay.Url == "https://example.com/stream/v.m3u8" && Math.Abs(directPlay.Position - 3.0) < 0.001,
              $"{directPlay.Url} @ {directPlay.Position}");
        Check("no FCUP request was sent for the direct URL",
              await phone.NoPendingReverseDataAsync(TimeSpan.FromMilliseconds(300)), "an FCUP request was sent");

        HttpReply refused = await phone.PlayAsync("file:///C:/Users/x/clip.m3u8", "file-uuid", 0);
        Check("a file:// Content-Location is refused with 400", refused.Status == 400, refused.StatusLine);
    }

    // ── Assertion plumbing ────────────────────────────────────────────────────

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"-- {title} --");
    }

    private static void Check(string name, bool ok, string detail)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"[PASS] {name}");
        }
        else
        {
            Fail(name, detail);
        }
    }

    private static void Fail(string name, string detail)
    {
        _failed++;
        Console.WriteLine($"[FAIL] {name}");
        if (!string.IsNullOrEmpty(detail))
            foreach (string line in detail.Split('\n'))
                Console.WriteLine($"       | {line.TrimEnd('\r')}");
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }
}
