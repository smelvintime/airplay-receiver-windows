using AirPlayReceiver.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace AirPlayReceiver.Media;

/// <summary>
/// The HLS playlists of one AirPlay-video playback item, plus the bookkeeping
/// the FCUP exchange needs to fetch them.
///
/// iOS never gives us a fetchable URL for YouTube: <c>Content-Location</c> is
/// <c>mlhls://localhost/master.m3u8</c>, a scheme only the phone can resolve.
/// The receiver asks for each playlist over the reverse channel (the "FCUP
/// request"), the phone posts the bytes back on <c>POST /action</c>, and we
/// cache them here and re-serve them to our own player from
/// <see cref="Services.HlsServer"/> — with <c>mlhls://localhost</c> rewritten to
/// our loopback origin. Media segments are never proxied: after expansion the
/// chunk URIs are absolute googlevideo.com URLs the player fetches directly.
///
/// One instance per playback item (per <c>uuid</c> in the /play plist); the
/// cache of items lives in <see cref="HlsPlaylistStore"/>.
///
/// Reference: UxPlay lib/airplay_video.c (airplay_video_t).
/// </summary>
public sealed class HlsVideo
{
    /// <summary>One cached media (child) playlist, in the order the master listed it.</summary>
    private sealed class MediaItem
    {
        public required string Uri { get; init; }   // full mlhls://… URI from the master
        public string? Playlist;                    // raw, as received (expansion happens at serve time)
        public int     ChunkCount;
        public double  Duration;
        public bool    EndList;
        public int     Alias = -1;                  // index of the identical entry this one duplicates
    }

    private readonly List<MediaItem> _media = new();
    private readonly object _sync = new();

    /// <summary>The <c>uuid</c> iOS assigns the playback item; identifies it across pause/resume.</summary>
    public string Uuid { get; }

    /// <summary>The X-Apple-Session-ID that requested this item. Guards /action against a stale session.</summary>
    public string AppleSessionId { get; set; }

    /// <summary>The phone-side URL from /play, e.g. <c>mlhls://localhost/master.m3u8</c>.</summary>
    public string ContentLocation { get; }

    /// <summary><see cref="ContentLocation"/> minus the trailing <c>/master.m3u8</c> — the prefix every child URI shares.</summary>
    public string UriPrefix { get; }

    /// <summary>
    /// First path segment of this item's local URLs, e.g. <c>v1</c>.
    ///
    /// Every item's <c>Content-Location</c> ends in <c>/master.m3u8</c>, so
    /// mapping the phone's prefix straight onto our origin would give the video
    /// and the advertisement spliced in front of it the same local URL. Giving
    /// each item its own path segment keeps them apart, so a player still
    /// draining the previous item cannot be handed the next one's playlist.
    /// (UxPlay serves every item from bare <c>/master.m3u8</c>.)
    /// </summary>
    public string LocalPathToken { get; }

    /// <summary>Our loopback prefix for this item, e.g. <c>http://127.0.0.1:54321/v1</c>. Replaces <see cref="UriPrefix"/> on the way out.</summary>
    public string LocalUriPrefix { get; }

    /// <summary>What we hand the player once every playlist has arrived, e.g. <c>http://127.0.0.1:54321/v1/master.m3u8</c>.</summary>
    public string PlaybackLocation { get; }

    /// <summary><c>Start-Position-Seconds</c> from /play (absolute seconds).</summary>
    public double StartPositionSeconds { get; set; }

    /// <summary>Where playback was when a <c>playlistRemove</c> interrupted it (an ad rolling in).</summary>
    public double ResumePositionSeconds { get; set; }

    /// <summary>The rewritten master playlist, ready to serve.</summary>
    public string? MasterPlaylist { get; private set; }

    /// <summary>Number of child playlists the master listed.</summary>
    public int MediaUriCount { get { lock (_sync) return _media.Count; } }

    /// <summary>Index of the child playlist the next FCUP request should ask for.</summary>
    public int NextMediaUriIndex { get; set; }

    /// <summary>
    /// True once the master and every child playlist it listed have actually
    /// arrived — not merely been asked for. <see cref="NextMediaUriIndex"/> moves
    /// when a request is <i>sent</i>, so it cannot answer this question: during
    /// the last round trip it already equals <see cref="MediaUriCount"/> while one
    /// playlist is still in flight.
    /// </summary>
    public bool IsComplete
    {
        get
        {
            if (MasterPlaylist is null) return false;
            lock (_sync)
            {
                foreach (MediaItem m in _media)
                    if (m.Playlist is null && m.Alias < 0) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Longest child-playlist duration in seconds — the item's runtime, used to
    /// tell a real video from an advertisement when the cache needs space.
    /// </summary>
    public double DurationSeconds
    {
        get
        {
            lock (_sync)
            {
                double max = 0;
                foreach (MediaItem m in _media) if (m.Duration > max) max = m.Duration;
                return max;
            }
        }
    }

    /// <param name="origin">The HLS server's loopback origin, e.g. <c>http://127.0.0.1:54321</c>.</param>
    /// <param name="localPathToken">This item's own path segment under that origin.</param>
    public HlsVideo(string uuid, string appleSessionId, string contentLocation,
                    string uriPrefix, string origin, string localPathToken)
    {
        Uuid             = uuid;
        AppleSessionId   = appleSessionId;
        ContentLocation  = contentLocation;
        UriPrefix        = uriPrefix;
        LocalPathToken   = localPathToken;
        LocalUriPrefix   = $"{origin}/{localPathToken}";
        PlaybackLocation = LocalUriPrefix + contentLocation[uriPrefix.Length..];
    }

    /// <summary>The URI to request next, or null when the queue has drained.</summary>
    public string? MediaUriAt(int index)
    {
        lock (_sync) return index >= 0 && index < _media.Count ? _media[index].Uri : null;
    }

    /// <summary>Caches the master playlist (already rewritten) and the child URIs it listed.</summary>
    public void StoreMaster(string rewrittenMaster, IReadOnlyList<string> mediaUris)
    {
        lock (_sync)
        {
            MasterPlaylist = rewrittenMaster;
            _media.Clear();
            foreach (string uri in mediaUris)
                _media.Add(new MediaItem { Uri = uri });
        }
        NextMediaUriIndex = 0;
    }

    /// <summary>Outcome of <see cref="StoreMedia"/>.</summary>
    public enum MediaStoreResult
    {
        Stored,
        /// <summary>The URI already appeared earlier in the master (YouTube repeats renditions): aliased, text dropped.</summary>
        Duplicate,
        /// <summary>The slot already holds a playlist — an unsolicited response; the cached copy wins.</summary>
        AlreadyPresent,
        /// <summary>No slot with that index; the master listed fewer children.</summary>
        OutOfRange,
    }

    /// <summary>
    /// Caches one child playlist, matching UxPlay's store_media_playlist
    /// (including its de-duplication of repeated URIs).
    /// </summary>
    public MediaStoreResult StoreMedia(int index, string playlist, int chunkCount, double duration, bool endList)
    {
        lock (_sync)
        {
            if (index < 0 || index >= _media.Count) return MediaStoreResult.OutOfRange;
            MediaItem item = _media[index];
            if (item.Playlist is not null) return MediaStoreResult.AlreadyPresent;

            for (int i = 0; i < index; i++)
            {
                if (string.Equals(_media[i].Uri, item.Uri, StringComparison.Ordinal))
                {
                    item.Alias = _media[i].Alias >= 0 ? _media[i].Alias : i;
                    return MediaStoreResult.Duplicate;
                }
            }

            item.Playlist   = playlist;
            item.ChunkCount = chunkCount;
            item.Duration   = duration;
            item.EndList    = endList;
            return MediaStoreResult.Stored;
        }
    }

    /// <summary>
    /// Looks up a cached child playlist by the path our player asked for
    /// (e.g. <c>/index-v1.m3u8</c>), which is the tail of the stored
    /// <c>mlhls://…</c> URI. Returns the raw text — expansion is a serve-time step.
    /// </summary>
    public bool TryGetMedia(string requestPath, out string playlist, out int chunkCount, out double duration)
    {
        playlist = string.Empty;
        chunkCount = 0;
        duration = 0;

        lock (_sync)
        {
            foreach (MediaItem m in _media)
            {
                if (!m.Uri.EndsWith(requestPath, StringComparison.Ordinal)) continue;

                MediaItem target = m.Alias >= 0 && m.Alias < _media.Count ? _media[m.Alias] : m;
                if (target.Playlist is null) return false;

                playlist   = target.Playlist;
                chunkCount = target.ChunkCount;
                duration   = target.Duration;
                return true;
            }
        }
        return false;
    }

    // ── Playlist text transforms (pure — unit-tested by tests/HlsHarness) ─────

    /// <summary>
    /// Collects every child-playlist URI the master lists, in order. UxPlay scans
    /// for the shared prefix rather than parsing tags, which picks up both
    /// <c>#EXT-X-STREAM-INF</c> (URI on the following line) and
    /// <c>#EXT-X-MEDIA</c> (URI="…" attribute) without special-casing either.
    /// A URI runs from the prefix through the next <c>.m3u8</c>.
    /// </summary>
    public static List<string> CollectMediaUris(string master, string uriPrefix)
    {
        var uris = new List<string>();
        if (string.IsNullOrEmpty(uriPrefix)) return uris;

        int pos = 0;
        while (true)
        {
            int start = master.IndexOf(uriPrefix, pos, StringComparison.Ordinal);
            if (start < 0) break;

            int m3u8 = master.IndexOf("m3u8", start, StringComparison.Ordinal);
            if (m3u8 < 0) break;

            int end = m3u8 + "m3u8".Length;
            uris.Add(master[start..end]);
            pos = end;
        }
        return uris;
    }

    /// <summary>
    /// Rewrites the master so our player fetches the children from us:
    /// <c>mlhls://localhost</c> → <c>http://127.0.0.1:&lt;port&gt;</c>.
    /// Every occurrence, so <c>#EXT-X-MEDIA</c> URI attributes are covered too.
    /// </summary>
    public static string RewriteMaster(string master, string uriPrefix, string localUriPrefix)
        => string.IsNullOrEmpty(uriPrefix) ? master : master.Replace(uriPrefix, localUriPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Counts chunks and sums their durations, and reports whether the playlist
    /// is closed (<c>#EXT-X-ENDLIST</c>, i.e. VOD rather than a live edge).
    /// </summary>
    public static int AnalyzeMediaPlaylist(string playlist, out double duration, out bool endList)
    {
        const string Tag = "#EXTINF:";
        duration = 0;
        endList  = playlist.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal);

        int count = 0, pos = 0;
        while (true)
        {
            int at = playlist.IndexOf(Tag, pos, StringComparison.Ordinal);
            if (at < 0) break;

            int valueStart = at + Tag.Length;
            int valueEnd   = valueStart;
            while (valueEnd < playlist.Length &&
                   (char.IsAsciiDigit(playlist[valueEnd]) || playlist[valueEnd] is '.' or '-' or '+'))
            {
                valueEnd++;
            }

            if (valueEnd > valueStart &&
                double.TryParse(playlist.AsSpan(valueStart, valueEnd - valueStart),
                                NumberStyles.Float, CultureInfo.InvariantCulture, out double secs))
            {
                duration += secs;
            }

            count++;
            pos = valueEnd;
        }
        return count;
    }

    /// <summary>
    /// Expands a YouTube "condensed" media playlist into standard HLS.
    ///
    /// YouTube ships chunk URIs as suffixes and puts the shared part in a header:
    /// <code>
    /// #EXTM3U
    /// #YT-EXT-CONDENSED-URL:BASE-URI="https://…/expire/9999",PARAMS="sq,goap",PREFIX="s/"
    /// #EXTINF:5.000,
    /// s/0/clen%3D123
    /// </code>
    /// Each chunk line drops PREFIX, then its slash-separated values are re-paired
    /// with the PARAMS names and appended to BASE-URI, giving
    /// <c>https://…/expire/9999/sq/0/goap/clen%3D123</c>.
    ///
    /// Skipping this is not a subtle failure: the player asks the CDN for
    /// <c>/s/0/clen%3D123</c>, gets 404 on every segment, and surfaces a generic
    /// "cannot play" with nothing pointing at the playlist.
    ///
    /// Applied when the playlist is served, not when it is stored — the cached
    /// copy stays byte-identical to what the phone sent (UxPlay does the same).
    /// Playlists without the header are returned unchanged.
    ///
    /// Reference: UxPlay lib/airplay_video.c adjust_yt_condensed_playlist L1125.
    /// </summary>
    public static string ExpandCondensedPlaylist(string playlist)
    {
        const string Header = "#YT-EXT-CONDENSED-URL";

        // Detection is positional: the header must be the line right after
        // #EXTM3U. Anything else is already a standard playlist.
        int m3u = playlist.IndexOf("#EXTM3U", StringComparison.Ordinal);
        if (m3u < 0) return playlist;

        int nextLine = playlist.IndexOf('\n', m3u);
        if (nextLine < 0) return playlist;
        nextLine++;

        if (!playlist.AsSpan(nextLine).StartsWith(Header, StringComparison.Ordinal))
            return playlist;

        int headerEnd = playlist.IndexOf('\n', nextLine);
        string headerLine = (headerEnd < 0 ? playlist[nextLine..] : playlist[nextLine..headerEnd]).TrimEnd('\r');

        string? baseUri = QuotedAttribute(headerLine, "BASE-URI");
        string? prefix  = QuotedAttribute(headerLine, "PREFIX");
        string  parms   = QuotedAttribute(headerLine, "PARAMS") ?? string.Empty;

        if (baseUri is null || prefix is null || prefix.Length == 0)
        {
            Log.Warn("HLS", $"{Header} header is missing BASE-URI or PREFIX — serving the playlist unexpanded:\n{headerLine}");
            return playlist;
        }

        string[] names = parms.Length == 0 ? Array.Empty<string>() : parms.Split(',');

        var sb = new StringBuilder(playlist.Length + 64);
        int expanded = 0, skipped = 0;

        // Split/re-join on '\n' round-trips exactly, so line endings (LF or CRLF)
        // and the presence or absence of a final newline survive untouched.
        string[] lines = playlist.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string raw  = lines[i];
            string line = raw.TrimEnd('\r');

            if (line.Length == 0 || line[0] == '#')
            {
                // Comments, tags and blank lines pass through — including the
                // condensed header itself, which players ignore.
                sb.Append(raw);
            }
            else if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                // Already absolute, or a form we don't recognise: leave it alone
                // rather than corrupting a URI that might have worked.
                skipped++;
                sb.Append(raw);
            }
            else
            {
                sb.Append(ExpandChunkUri(line, prefix, baseUri, names));
                expanded++;
            }

            if (i < lines.Length - 1) sb.Append('\n');
        }

        Log.Info("HLS", $"Expanded YT-condensed playlist: {expanded} chunk URI(s) " +
                        $"({skipped} left as-is), PARAMS=\"{parms}\" PREFIX=\"{prefix}\" BASE-URI=\"{baseUri}\"");
        return sb.ToString();
    }

    private static string ExpandChunkUri(string line, string prefix, string baseUri, string[] names)
    {
        string rest = line[prefix.Length..];
        if (names.Length == 0) return baseUri + "/" + rest;

        var sb = new StringBuilder(baseUri, baseUri.Length + rest.Length + 32);
        int pos = 0;
        for (int i = 0; i < names.Length; i++)
        {
            string value;
            if (i < names.Length - 1)
            {
                int slash = rest.IndexOf('/', pos);
                if (slash < 0) { value = rest[pos..]; pos = rest.Length; }
                else           { value = rest[pos..slash]; pos = slash + 1; }
            }
            else
            {
                // The final parameter takes the whole remainder — its value may
                // itself contain '/' (percent-encoded chunk metadata does not, but
                // nothing in the format forbids it).
                value = rest[pos..];
            }
            sb.Append('/').Append(names[i]).Append('/').Append(value);
        }
        return sb.ToString();
    }

    /// <summary>Reads <c>NAME="value"</c> out of the condensed header line.</summary>
    private static string? QuotedAttribute(string header, string name)
    {
        int at = header.IndexOf(name + "=", StringComparison.Ordinal);
        if (at < 0) return null;

        int open = header.IndexOf('"', at + name.Length + 1);
        if (open < 0) return null;

        int close = header.IndexOf('"', open + 1);
        if (close < 0) return null;

        return header[(open + 1)..close];
    }
}

/// <summary>
/// The receiver's cache of AirPlay-video playback items, keyed by the <c>uuid</c>
/// iOS puts in the /play plist.
///
/// It exists because YouTube interrupts a video with an advertisement and then
/// resumes it: the phone sends <c>playlistRemove</c> for the video, plays the ad
/// as its own item, and re-sends /play with the <b>same uuid</b>. Keeping the
/// playlists means the resume costs no FCUP round trips and picks up where it
/// left off.
///
/// Lives for the app's run (one instance on <c>AirPlayService</c>), like the
/// reverse-channel registry — /play, /action and /reverse each arrive on their
/// own TCP connection, so nothing per-connection can hold this.
///
/// Reference: UxPlay raop_t.airplay_video[] / current_video (MAX_AIRPLAY_VIDEO 10).
/// </summary>
public sealed class HlsPlaylistStore
{
    /// <summary>Cache size, matching UxPlay's MAX_AIRPLAY_VIDEO.</summary>
    private const int MaxVideos = 10;

    /// <summary>
    /// Shorter than this (UxPlay: MIN_STORED_AIRPLAY_VIDEO_DURATION_SECONDS) and
    /// an item is almost certainly an advertisement — evict those first, they are
    /// never resumed.
    /// </summary>
    private const double AdvertisementDurationSeconds = 90;

    private readonly object _sync = new();
    private readonly Dictionary<string, HlsVideo> _videos    = new(StringComparer.Ordinal);   // by uuid
    private readonly Dictionary<string, HlsVideo> _byToken   = new(StringComparer.Ordinal);   // by local path segment
    private readonly List<string> _order = new();   // insertion order, for eviction
    private int _localTokenCounter;

    /// <summary>
    /// The item /action responses and playlist requests apply to, or null when
    /// nothing is playing. Null is a normal state — UxPlay asserts on it instead,
    /// which is the crash behind their issues #545/#550.
    /// </summary>
    public HlsVideo? Current { get; private set; }

    public int Count { get { lock (_sync) return _videos.Count; } }

    public bool TryGet(string uuid, out HlsVideo video)
    {
        lock (_sync)
        {
            if (!string.IsNullOrEmpty(uuid) && _videos.TryGetValue(uuid, out HlsVideo? found))
            {
                video = found;
                return true;
            }
        }
        video = null!;
        return false;
    }

    /// <summary>Finds the item a local playlist URL belongs to, by its path segment.</summary>
    public bool TryGetByLocalToken(string token, out HlsVideo video)
    {
        lock (_sync)
        {
            if (!string.IsNullOrEmpty(token) && _byToken.TryGetValue(token, out HlsVideo? found))
            {
                video = found;
                return true;
            }
        }
        video = null!;
        return false;
    }

    /// <summary>Reserves the next local URL path segment ("v1", "v2", …) for a new item.</summary>
    public string AllocateLocalToken()
        => "v" + Interlocked.Increment(ref _localTokenCounter).ToString(CultureInfo.InvariantCulture);

    /// <summary>Adds an item, evicting advertisements (then the oldest) to stay under the cap, and makes it current.</summary>
    public void AddAndMakeCurrent(HlsVideo video)
    {
        lock (_sync)
        {
            if (_videos.Remove(video.Uuid, out HlsVideo? previous))
            {
                _order.Remove(video.Uuid);
                _byToken.Remove(previous.LocalPathToken);
            }
            _videos[video.Uuid]            = video;
            _byToken[video.LocalPathToken] = video;
            _order.Add(video.Uuid);
            EvictLocked(video.Uuid);
        }
        Current = video;
        Log.Info("HLS", $"Playback item cached: uuid={video.Uuid} location={video.ContentLocation} " +
                        $"served from {video.LocalUriPrefix} (cache holds {Count})");
    }

    /// <summary>Points <see cref="Current"/> at an already-cached item (resume after an ad).</summary>
    public bool MakeCurrent(string uuid)
    {
        if (!TryGet(uuid, out HlsVideo video)) return false;
        Current = video;
        return true;
    }

    /// <summary>
    /// Clears <see cref="Current"/> without dropping the cached playlists, so the
    /// item can resume when iOS re-sends /play with the same uuid.
    /// </summary>
    public void ClearCurrent() => Current = null;

    private void EvictLocked(string keepUuid)
    {
        while (_videos.Count > MaxVideos)
        {
            string? victim = null;
            foreach (string uuid in _order)
            {
                if (uuid == keepUuid) continue;
                if (_videos.TryGetValue(uuid, out HlsVideo? v) && v.DurationSeconds < AdvertisementDurationSeconds)
                {
                    victim = uuid;
                    break;
                }
            }
            victim ??= _order.Find(u => u != keepUuid);
            if (victim is null) break;

            if (_videos.Remove(victim, out HlsVideo? evicted)) _byToken.Remove(evicted.LocalPathToken);
            _order.Remove(victim);
            Log.Info("HLS", $"Evicted cached playback item uuid={victim} to make room");
        }
    }
}
