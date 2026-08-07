namespace AirPlayReceiver.HlsHarness;

/// <summary>
/// Playlist fixtures in the shapes iOS actually hands over for a YouTube
/// AirPlay session: a master whose children are <c>mlhls://</c> URIs only the
/// phone can resolve, YouTube's condensed media playlists, and a plain one.
/// </summary>
internal static class Fixtures
{
    /// <summary>What /play carries as Content-Location for a YouTube video.</summary>
    public const string ContentLocation = "mlhls://localhost/master.m3u8";

    /// <summary>Content-Location minus the final path segment — the prefix that gets rewritten.</summary>
    public const string UriPrefix = "mlhls://localhost";

    /// <summary>
    /// Master playlist. Five URI occurrences: two <c>#EXT-X-MEDIA</c> attributes,
    /// three <c>#EXT-X-STREAM-INF</c> lines — of which the last repeats
    /// video-720, which is what YouTube does and what dedupe has to catch.
    /// </summary>
    public const string Master =
        "#EXTM3U\n" +
        "#EXT-X-INDEPENDENT-SEGMENTS\n" +
        "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"English\",LANGUAGE=\"en\",DEFAULT=YES,AUTOSELECT=YES,URI=\"mlhls://localhost/audio-en.m3u8\"\n" +
        "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"Spanish\",LANGUAGE=\"es\",DEFAULT=NO,AUTOSELECT=NO,URI=\"mlhls://localhost/audio-es.m3u8\"\n" +
        "#EXT-X-STREAM-INF:BANDWIDTH=800000,CODECS=\"avc1.4d401e,mp4a.40.2\",RESOLUTION=640x360,AUDIO=\"aud\"\n" +
        "mlhls://localhost/video-360.m3u8\n" +
        "#EXT-X-STREAM-INF:BANDWIDTH=2400000,CODECS=\"avc1.4d401f,mp4a.40.2\",RESOLUTION=1280x720,AUDIO=\"aud\"\n" +
        "mlhls://localhost/video-720.m3u8\n" +
        "#EXT-X-STREAM-INF:BANDWIDTH=2400000,CODECS=\"avc1.4d401f,mp4a.40.2\",RESOLUTION=1280x720,AUDIO=\"aud\",PROGRAM-ID=2\n" +
        "mlhls://localhost/video-720.m3u8\n";

    /// <summary>The child URIs <see cref="Master"/> lists, in order — including the repeat.</summary>
    public static readonly string[] MasterChildUris =
    {
        "mlhls://localhost/audio-en.m3u8",
        "mlhls://localhost/audio-es.m3u8",
        "mlhls://localhost/video-360.m3u8",
        "mlhls://localhost/video-720.m3u8",
        "mlhls://localhost/video-720.m3u8",
    };

    /// <summary>The googlevideo origin the condensed chunk URIs expand against.</summary>
    public const string CondensedBaseUri =
        "https://rr3---sn-4g5e6nez.googlevideo.com/videoplayback/id/9a8b7c6d/itag/232/source/youtube/expire/1712345678";

    /// <summary>
    /// A YouTube condensed media playlist: the chunk URIs are suffixes, and the
    /// shared origin plus the names of the path parameters they interleave with
    /// live in the <c>#YT-EXT-CONDENSED-URL</c> header.
    /// </summary>
    public const string CondensedMedia =
        "#EXTM3U\n" +
        "#YT-EXT-CONDENSED-URL:BASE-URI=\"" + CondensedBaseUri + "\",PARAMS=\"sq,goap\",PREFIX=\"s/\",SKIP-SEGMENTS=1\n" +
        "#EXT-X-TARGETDURATION:5\n" +
        "#EXT-X-VERSION:3\n" +
        "#EXT-X-MEDIA-SEQUENCE:0\n" +
        "#EXT-X-PLAYLIST-TYPE:VOD\n" +
        "#EXTINF:5.000,\n" +
        "s/0/clen%3D1234%3Bdur%3D5.000\n" +
        "#EXTINF:5.000,\n" +
        "s/1/clen%3D2345%3Bdur%3D5.000\n" +
        "#EXTINF:4.240,\n" +
        "s/2/clen%3D3456%3Bdur%3D4.240\n" +
        "#EXT-X-ENDLIST\n";

    /// <summary>What <see cref="CondensedMedia"/> must expand to, chunk by chunk.</summary>
    public static readonly string[] CondensedExpandedChunks =
    {
        CondensedBaseUri + "/sq/0/goap/clen%3D1234%3Bdur%3D5.000",
        CondensedBaseUri + "/sq/1/goap/clen%3D2345%3Bdur%3D5.000",
        CondensedBaseUri + "/sq/2/goap/clen%3D3456%3Bdur%3D4.240",
    };

    /// <summary>An ordinary (already absolute) media playlist — expansion must leave it alone.</summary>
    public const string PlainMedia =
        "#EXTM3U\n" +
        "#EXT-X-TARGETDURATION:5\n" +
        "#EXT-X-VERSION:3\n" +
        "#EXT-X-MEDIA-SEQUENCE:0\n" +
        "#EXT-X-PLAYLIST-TYPE:VOD\n" +
        "#EXTINF:5.000,\n" +
        "https://rr3---sn-4g5e6nez.googlevideo.com/videoplayback/id/9a8b7c6d/itag/140/sq/0/audio.ts\n" +
        "#EXTINF:5.000,\n" +
        "https://rr3---sn-4g5e6nez.googlevideo.com/videoplayback/id/9a8b7c6d/itag/140/sq/1/audio.ts\n" +
        "#EXT-X-ENDLIST\n";

    /// <summary>The playlist body the phone returns for a given child URI.</summary>
    public static string MediaFor(string uri)
        => uri.Contains("audio", StringComparison.Ordinal) ? PlainMedia : CondensedMedia;

    /// <summary>The advertisement master iOS splices in with playlistInsert.</summary>
    public const string AdContentLocation = "mlhls://localhost/ad/master.m3u8";

    public const string AdMaster =
        "#EXTM3U\n" +
        "#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360\n" +
        "mlhls://localhost/ad/video-360.m3u8\n";
}
