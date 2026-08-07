using AirPlayReceiver.Media;
using AirPlayReceiver.Protocol;
using FFmpeg.AutoGen;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AirPlayReceiver.Tools.PhotoHarness;

/// <summary>
/// Loopback harness for AirPlay Photos.
///
/// There is no iPhone in CI, so this stands in for one: it runs a real
/// <see cref="TcpListener"/> that parses requests with the receiver's own
/// <see cref="RtspMessage"/> and dispatches <c>PUT /photo</c> to the real
/// <see cref="PhotoHandler"/>, then drives it over a socket with raw HTTP bytes
/// shaped exactly like the ones in openairplay/airplay-spec.
///
/// The display side is the real <see cref="PhotoDecoder"/> too — FFmpeg decode
/// and the libswscale NV12 conversion actually run. Only the last hop, the
/// D3D11 upload, is replaced: the sink records each frame's dimensions and
/// plane pointers so "the photo reached the display path" is an assertion
/// rather than a claim (nobody can eyeball a swap chain in CI).
///
/// Usage: PhotoHarness [--ffmpeg &lt;dir&gt;]
/// Exit code 0 = all checks passed.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static int Main(string[] args)
    {
        Console.WriteLine("AirPlay Photos loopback harness");
        Console.WriteLine("===============================");

        if (!InitFFmpeg(args)) return 2;

        try
        {
            if (args.Contains("--diag")) { Diag(); return 0; }
            RunAll();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nHARNESS CRASHED: {ex}");
            _failed++;
        }

        Console.WriteLine();
        Console.WriteLine($"passed {_passed}, failed {_failed}");
        Console.WriteLine($"receiver log: {Services.Log.CurrentLogFile ?? "(unavailable)"}");
        return _failed == 0 ? 0 : 1;
    }

    private static void RunAll()
    {
        // Two real photos of different sizes, so the scaler-rebuild path is
        // exercised and a cache holding two distinct assets is meaningful.
        byte[] photoA = MakeJpeg(1600, 1200, seed: 0);
        byte[] photoB = MakeJpeg(1001,  667, seed: 90);   // odd dimensions on purpose
        byte[] photoHuge = MakeJpeg(5000, 3000, seed: 40); // over the 3840×2160 display cap

        Console.WriteLine($"test images: A={photoA.Length}B 1600x1200, B={photoB.Length}B 1001x667, " +
                          $"huge={photoHuge.Length}B 5000x3000\n");

        // ── Scenario 1: the documented single-photo flow ──────────────────────
        using (var rig = new Rig())
        {
            var r = rig.Put("/photo", photoA, key: "F92F9B91-954E-4D63-BB9A-EEC771ADE6E8",
                            transition: "Dissolve");
            Check("single photo → 200 OK", r.Status == 200, $"got {r.Status} {r.Reason}");
            Check("single photo → Content-Length: 0", r.ContentLength == "0", $"got '{r.ContentLength}'");
            Check("single photo reached the display path", rig.Sink.Frames.Count == 1,
                  $"sink saw {rig.Sink.Frames.Count} frame(s)");
            Check("frame is 1600x1200 NV12", rig.Sink.Frames is [(1600, 1200)],
                  $"sink saw {Describe(rig.Sink.Frames)}");
            Check("frame planes are non-null", rig.Sink.AllPlanesValid, "a Y or UV plane pointer was null");
            Check("window was activated once", rig.Sink.ActiveEvents is [true],
                  $"onActive: {string.Join(",", rig.Sink.ActiveEvents)}");
            Check("presenter rotation reset to 0", rig.Sink.Rotations is [0],
                  $"onRotation: {string.Join(",", rig.Sink.Rotations)}");
            Check("displaying also caches the asset", rig.Handler.Cache.Count == 1,
                  $"cache holds {rig.Handler.Cache.Count}");
        }

        // ── Scenario 2: cacheOnly → displayCached round trip ──────────────────
        using (var rig = new Rig())
        {
            const string Key = "11111111-2222-3333-4444-555555555555";

            var cached = rig.Put("/photo", photoB, key: Key, action: PhotoHandler.ActionCacheOnly);
            Check("cacheOnly → 200 OK", cached.Status == 200, $"got {cached.Status} {cached.Reason}");
            Check("cacheOnly displays nothing", rig.Sink.Frames.Count == 0,
                  $"sink saw {rig.Sink.Frames.Count} frame(s)");
            Check("cacheOnly stored the asset", rig.Handler.Cache.Count == 1,
                  $"cache holds {rig.Handler.Cache.Count}");
            Check("cacheOnly stored the exact bytes", rig.Handler.Cache.Bytes == photoB.Length,
                  $"cache holds {rig.Handler.Cache.Bytes}B, JPEG is {photoB.Length}B");

            // The point of the whole mechanism: an EMPTY body displays the photo.
            var shown = rig.Put("/photo", Array.Empty<byte>(), key: Key,
                                action: PhotoHandler.ActionDisplayCached, transition: "SlideLeft");
            Check("displayCached (empty body) → 200 OK", shown.Status == 200, $"got {shown.Status} {shown.Reason}");
            Check("displayCached displayed the cached JPEG", rig.Sink.Frames is [(1000, 666)],
                  $"sink saw {Describe(rig.Sink.Frames)}");   // 1001x667 rounded down to even
            Check("cache recorded a hit", rig.Handler.Cache.Hits == 1, $"hits={rig.Handler.Cache.Hits}");
            Check("no 412 was issued", rig.Handler.PreconditionFailed == 0,
                  $"412 count is {rig.Handler.PreconditionFailed}");
        }

        // ── Scenario 3: displayCached for a key we never saw → 412 ────────────
        using (var rig = new Rig())
        {
            var r = rig.Put("/photo", Array.Empty<byte>(), key: "DEADBEEF-0000-0000-0000-000000000000",
                            action: PhotoHandler.ActionDisplayCached);
            Check("displayCached unknown key → 412 Precondition Failed", r.Status == 412,
                  $"got {r.Status} {r.Reason}");
            Check("412 reason phrase", r.Reason == "Precondition Failed", $"got '{r.Reason}'");
            Check("412 displays nothing", rig.Sink.Frames.Count == 0,
                  $"sink saw {rig.Sink.Frames.Count} frame(s)");
            Check("412 was counted", rig.Handler.PreconditionFailed == 1,
                  $"412 count is {rig.Handler.PreconditionFailed}");
            Check("cache recorded a miss", rig.Handler.Cache.Misses == 1, $"misses={rig.Handler.Cache.Misses}");
        }

        // ── Scenario 4: eviction makes a once-cached key answer 412 ───────────
        // The reason a bounded cache is safe at all: the sender re-pushes on 412.
        using (var rig = new Rig(new PhotoCache(maxEntries: 2, maxBytes: PhotoCache.DefaultMaxBytes)))
        {
            rig.Put("/photo", photoA, key: "K1", action: PhotoHandler.ActionCacheOnly);
            rig.Put("/photo", photoB, key: "K2", action: PhotoHandler.ActionCacheOnly);
            rig.Put("/photo", photoA, key: "K3", action: PhotoHandler.ActionCacheOnly);   // evicts K1

            Check("cache respects the entry cap", rig.Handler.Cache.Count == 2,
                  $"cache holds {rig.Handler.Cache.Count}");
            Check("eviction happened", rig.Handler.Cache.Evictions == 1,
                  $"evictions={rig.Handler.Cache.Evictions}");

            var evicted = rig.Put("/photo", Array.Empty<byte>(), key: "K1",
                                  action: PhotoHandler.ActionDisplayCached);
            Check("displayCached evicted key → 412", evicted.Status == 412, $"got {evicted.Status} {evicted.Reason}");

            var survivor = rig.Put("/photo", Array.Empty<byte>(), key: "K3",
                                   action: PhotoHandler.ActionDisplayCached);
            Check("displayCached surviving key → 200", survivor.Status == 200, $"got {survivor.Status} {survivor.Reason}");
        }

        // ── Scenario 5: oversized photo is downscaled, aspect preserved ───────
        using (var rig = new Rig())
        {
            var r = rig.Put("/photo", photoHuge, key: "HUGE");
            Check("5000x3000 photo → 200 OK", r.Status == 200, $"got {r.Status} {r.Reason}");
            Check("5000x3000 downscaled to the 3840x2160 cap", rig.Sink.Frames is [(3600, 2160)],
                  $"sink saw {Describe(rig.Sink.Frames)}");   // 5:3 aspect, height-limited
        }

        // ── Scenario 6: JPEG full range is converted to the shader's limited range ──
        // The presenter's NV12→RGB shader expands luma from [16,235]. Handing it
        // raw JPEG luma (which uses the full [0,255]) is what makes photos come
        // out with crushed blacks and blown highlights, so assert the conversion
        // really happens rather than trusting the sws call silently succeeded.
        using (var rig = new Rig())
        {
            rig.Put("/photo", MakeFlatJpeg(64, 64, luma: 0), key: "BLACK");
            int black = rig.Sink.CenterLuma;
            Check("full-range black (0) → limited-range 16", Math.Abs(black - 16) <= 2, $"NV12 luma is {black}");

            rig.Put("/photo", MakeFlatJpeg(64, 64, luma: 255), key: "WHITE");
            int white = rig.Sink.CenterLuma;
            Check("full-range white (255) → limited-range 235", Math.Abs(white - 235) <= 2, $"NV12 luma is {white}");
        }

        // ── Scenario 7: malformed input must not break the session ────────────
        using (var rig = new Rig())
        {
            var garbage = rig.Put("/photo", Encoding.ASCII.GetBytes("this is not a JPEG at all"), key: "BAD");
            Check("undecodable body → still 200 (session survives)", garbage.Status == 200,
                  $"got {garbage.Status} {garbage.Reason}");
            Check("undecodable body displays nothing", rig.Sink.Frames.Count == 0,
                  $"sink saw {rig.Sink.Frames.Count} frame(s)");

            var empty = rig.Put("/photo", Array.Empty<byte>(), key: "EMPTY");
            Check("no action + empty body → 400", empty.Status == 400, $"got {empty.Status} {empty.Reason}");

            var noKey = rig.Put("/photo", Array.Empty<byte>(), action: PhotoHandler.ActionDisplayCached);
            Check("displayCached without an asset key → 400", noKey.Status == 400, $"got {noKey.Status} {noKey.Reason}");

            // The connection is still usable afterwards — that is the whole point.
            var ok = rig.Put("/photo", photoA, key: "AFTER-FAILURES");
            Check("session still works after failures", ok.Status == 200 && rig.Sink.Frames.Count == 1,
                  $"got {ok.Status}, {rig.Sink.Frames.Count} frame(s)");
        }

        // ── Scenario 8: session teardown clears surface and cache ─────────────
        using (var rig = new Rig())
        {
            rig.Put("/photo", photoA, key: "S1");
            Check("session is active while a photo is up", rig.Handler.SessionActive, "SessionActive was false");

            rig.Handler.EndSession();
            Check("teardown deactivated the surface", rig.Sink.ActiveEvents is [true, false],
                  $"onActive: {string.Join(",", rig.Sink.ActiveEvents)}");
            Check("teardown cleared the asset cache", rig.Handler.Cache.Count == 0,
                  $"cache holds {rig.Handler.Cache.Count}");
            Check("session no longer active", !rig.Handler.SessionActive, "SessionActive was true");
        }

        // ── Scenario 9: another producer switched the shared surface off ──────
        // A Photos album can contain a video: iOS sends POST /play and then POST
        // /stop, and the stop runs AirPlayVideoPlayer.Stop → SetActive(false) on
        // the very surface the photos are using. Nothing tells the photo decoder.
        // If it only activated the surface for the first photo of a session,
        // every photo after the clip would be dropped by PresentFrame while
        // Display still returned true and logged "displayed" — a black screen
        // with a log claiming success, healed only by a TEARDOWN that a POST
        // /stop does not imply.
        using (var rig = new Rig())
        {
            rig.Put("/photo", photoA, key: "BEFORE-CLIP");
            Check("photo before the clip reached the display path", rig.Sink.Frames.Count == 1,
                  $"sink saw {rig.Sink.Frames.Count} frame(s)");

            rig.Sink.DeactivateFromOtherProducer();   // POST /stop ended an AirPlay Video clip

            var after = rig.Put("/photo", photoB, key: "AFTER-CLIP");
            Check("photo after the clip → 200 OK", after.Status == 200, $"got {after.Status} {after.Reason}");
            Check("photo after the clip re-activated the surface", rig.Sink.ActiveEvents is [true, true],
                  $"onActive: {string.Join(",", rig.Sink.ActiveEvents)}");
            Check("photo after the clip actually reached the display path",
                  rig.Sink.Frames is [(1600, 1200), (1000, 666)],
                  $"sink saw {Describe(rig.Sink.Frames)}");
            Check("no photo was dropped by an inactive surface", rig.Sink.DroppedFrames == 0,
                  $"{rig.Sink.DroppedFrames} frame(s) dropped");
        }

        // ── Scenario 10: a key whose spelling varies must not grow the MRU ────
        // The entry dictionary matches asset keys case-insensitively; a List's
        // Remove does not. When the two disagree, every repeat displayCached
        // appends another MRU entry for an asset that is already there, and
        // eviction then starts popping keys that are not the least-recently-used
        // one — i.e. dropping a photo iOS is about to ask for.
        using (var rig = new Rig())
        {
            const string Upper = "AABBCCDD-1111-2222-3333-444455556666";
            string lower = Upper.ToLowerInvariant();

            rig.Put("/photo", photoB, key: Upper, action: PhotoHandler.ActionCacheOnly);
            for (int i = 0; i < 4; i++)
                rig.Put("/photo", Array.Empty<byte>(), key: lower, action: PhotoHandler.ActionDisplayCached);

            Check("a case-varying key hits the same cache entry", rig.Handler.Cache.Hits == 4,
                  $"hits={rig.Handler.Cache.Hits}, misses={rig.Handler.Cache.Misses}");
            Check("repeat displays keep one cache entry", rig.Handler.Cache.Count == 1,
                  $"cache holds {rig.Handler.Cache.Count}");
            Check("repeat displays do not grow the MRU list", rig.Handler.Cache.MruCount == 1,
                  $"MRU holds {rig.Handler.Cache.MruCount} for {rig.Handler.Cache.Count} entry/entries");

            rig.Put("/photo", photoB, key: lower, action: PhotoHandler.ActionCacheOnly);
            Check("re-storing under another spelling replaces rather than duplicates",
                  rig.Handler.Cache.Count == 1 && rig.Handler.Cache.MruCount == 1,
                  $"{rig.Handler.Cache.Count} entry/entries, MRU {rig.Handler.Cache.MruCount}");
            Check("the byte total counts that asset once", rig.Handler.Cache.Bytes == photoB.Length,
                  $"cache holds {rig.Handler.Cache.Bytes}B, JPEG is {photoB.Length}B");
        }
    }

    /// <summary>
    /// <c>--diag</c>: reproduces the measurement table quoted in
    /// <see cref="PhotoDecoder.AsFullRangeFormat"/>, which is why photos ask
    /// libswscale for the JPEG range conversion through the pixel format rather
    /// than through <c>sws_setColorspaceDetails</c>. Re-run it after an FFmpeg
    /// upgrade: if the first row stops reading 16/235 on both paths, the photo
    /// colour handling needs revisiting.
    /// </summary>
    private static unsafe void Diag()
    {
        Console.WriteLine("variant                                    in=0  in=255   (want 16 / 235)");
        foreach (var (name, fmt, details, dstCs) in new (string, AVPixelFormat, bool, int)[]
        {
            ("YUVJ420P, no setColorspaceDetails",  AVPixelFormat.AV_PIX_FMT_YUVJ420P, false, 0),
            ("YUV420P,  no setColorspaceDetails",  AVPixelFormat.AV_PIX_FMT_YUV420P,  false, 0),
            ("YUV420P,  details dst=BT.709",       AVPixelFormat.AV_PIX_FMT_YUV420P,  true,  ffmpeg.SWS_CS_ITU709),
            ("YUV420P,  details dst=BT.601",       AVPixelFormat.AV_PIX_FMT_YUV420P,  true,  ffmpeg.SWS_CS_ITU601),
            ("YUVJ420P, details dst=BT.709",       AVPixelFormat.AV_PIX_FMT_YUVJ420P, true,  ffmpeg.SWS_CS_ITU709),
        })
        {
            int a = ConvertFlat(0, fmt, details, dstCs);
            int b = ConvertFlat(255, fmt, details, dstCs);
            int c = ConvertFlat(0, fmt, details, dstCs, dst: 48);
            int d = ConvertFlat(255, fmt, details, dstCs, dst: 48);
            Console.WriteLine($"{name,-42} {a,4}  {b,5}    scaled: {c,4} {d,5}");
        }
    }

    /// <summary>Decodes a flat JPEG and runs one swscale configuration, returning the NV12 centre luma.</summary>
    private static unsafe int ConvertFlat(byte luma, AVPixelFormat srcFmt, bool setDetails, int dstCs, int dst = 64)
    {
        byte[] jpeg = MakeFlatJpeg(64, 64, luma);

        AVCodec* codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_MJPEG);
        AVCodecContext* ctx = ffmpeg.avcodec_alloc_context3(codec);
        ffmpeg.avcodec_open2(ctx, codec, null);
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        AVFrame* f = ffmpeg.av_frame_alloc();
        fixed (byte* p = jpeg) { pkt->data = p; pkt->size = jpeg.Length; ffmpeg.avcodec_send_packet(ctx, pkt); }
        ffmpeg.avcodec_receive_frame(ctx, f);

        SwsContext* sws = ffmpeg.sws_getContext(64, 64, srcFmt, dst, dst,
            AVPixelFormat.AV_PIX_FMT_NV12, ffmpeg.SWS_BILINEAR, null, null, null);

        if (setDetails)
        {
            int* s = ffmpeg.sws_getCoefficients(ffmpeg.SWS_CS_ITU601);
            int* d = ffmpeg.sws_getCoefficients(dstCs);
            var inv = new int_array4(); var tbl = new int_array4();
            for (uint i = 0; i < 4; i++) { inv[i] = s[i]; tbl[i] = d[i]; }
            ffmpeg.sws_setColorspaceDetails(sws, in inv, 1, in tbl, 0, 0, 1 << 16, 1 << 16);
        }

        AVFrame* nv12 = ffmpeg.av_frame_alloc();
        nv12->format = (int)AVPixelFormat.AV_PIX_FMT_NV12; nv12->width = dst; nv12->height = dst;
        ffmpeg.av_frame_get_buffer(nv12, 32);
        var dd = new byte_ptrArray4(); dd[0] = nv12->data[0]; dd[1] = nv12->data[1];
        var dl = new int_array4(); dl[0] = nv12->linesize[0]; dl[1] = nv12->linesize[1];
        ffmpeg.sws_scale(sws, f->data, f->linesize, 0, 64, dd, dl);

        int center = nv12->data[0][(dst / 2) * nv12->linesize[0] + dst / 2];

        ffmpeg.sws_freeContext(sws);
        ffmpeg.av_frame_free(&nv12); ffmpeg.av_frame_free(&f);
        ffmpeg.av_packet_free(&pkt); ffmpeg.avcodec_free_context(&ctx);
        return center;
    }

    // ── Assertions ────────────────────────────────────────────────────────────

    private static void Check(string what, bool ok, string detail)
    {
        if (ok) { _passed++; Console.WriteLine($"  PASS  {what}"); }
        else    { _failed++; Console.WriteLine($"  FAIL  {what}  ({detail})"); }
    }

    private static string Describe(List<(int W, int H)> frames)
        => frames.Count == 0 ? "no frames" : string.Join(", ", frames.Select(f => $"{f.W}x{f.H}"));

    // ── Test rig: loopback server + real handler + recording display sink ─────

    /// <summary>
    /// A live TCP listener speaking the receiver's own request parser and
    /// response builder, plus the real <see cref="PhotoHandler"/> and
    /// <see cref="PhotoDecoder"/> behind it.
    /// </summary>
    private sealed class Rig : IDisposable
    {
        private readonly TcpListener  _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task         _accept;

        public RecordingSink  Sink    { get; }
        public PhotoHandler   Handler { get; }

        public Rig(PhotoCache? cache = null)
        {
            Sink    = new RecordingSink();
            Handler = new PhotoHandler(Sink.Decoder.Display, Sink.Decoder.Stop, cache);

            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _accept = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => ServeAsync(client));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }

        /// <summary>
        /// The same dispatch AirPlaySession.HandlePut performs, over a real
        /// socket: parse with RtspMessage, route /photo to PhotoHandler, write
        /// the bytes it produced.
        /// </summary>
        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                using (client)
                await using (NetworkStream stream = client.GetStream())
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        RtspMessage? msg = await RtspMessage.ReadAsync(stream, _cts.Token);
                        if (msg is null) break;

                        byte[] response = msg is { Method: "PUT" } && msg.Path == "/photo"
                            ? Handler.Handle(msg)
                            : RtspMessage.BuildResponse(404, "Not Found", msg.CSeq, protocol: msg.Version);

                        await stream.WriteAsync(response, _cts.Token);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { }
        }

        /// <summary>Sends one PUT and returns the parsed response. One connection per call, as iOS does.</summary>
        public Response Put(string path, byte[] body, string? key = null, string? action = null, string? transition = null)
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, Port);
            using NetworkStream stream = client.GetStream();

            var head = new StringBuilder();
            head.Append($"PUT {path} HTTP/1.1\r\n");
            head.Append("User-Agent: MediaControl/1.0\r\n");
            head.Append($"{ReverseChannel.SessionIdHeader}: 1bd6ceeb-fffd-456c-a09c-996053a7a08c\r\n");
            if (key        is not null) head.Append($"{PhotoHandler.AssetKeyHeader}: {key}\r\n");
            if (action     is not null) head.Append($"{PhotoHandler.AssetActionHeader}: {action}\r\n");
            if (transition is not null) head.Append($"{PhotoHandler.TransitionHeader}: {transition}\r\n");
            head.Append($"Content-Length: {body.Length}\r\n");
            head.Append("\r\n");

            stream.Write(Encoding.ASCII.GetBytes(head.ToString()));
            if (body.Length > 0) stream.Write(body);
            stream.Flush();

            return Response.Read(stream);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try { _accept.Wait(2000); } catch { /* tearing down */ }
            Sink.Decoder.Dispose();
            _cts.Dispose();
        }
    }

    /// <summary>Minimal HTTP response reader — status line and headers are all we assert on.</summary>
    private sealed record Response(int Status, string Reason, string ContentLength)
    {
        public static Response Read(Stream stream)
        {
            string statusLine = ReadLine(stream);
            string[] parts = statusLine.Split(' ', 3);
            int status = parts.Length > 1 && int.TryParse(parts[1], out int s) ? s : -1;
            string reason = parts.Length > 2 ? parts[2] : string.Empty;

            string contentLength = string.Empty;
            while (true)
            {
                string line = ReadLine(stream);
                if (line.Length == 0) break;
                int colon = line.IndexOf(':');
                if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    contentLength = line[(colon + 1)..].Trim();
            }
            return new Response(status, reason, contentLength);
        }

        private static string ReadLine(Stream stream)
        {
            var sb = new StringBuilder(64);
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0) return sb.ToString();
                if (b == '\n') return sb.ToString().TrimEnd('\r');
                sb.Append((char)b);
            }
        }
    }

    /// <summary>
    /// Stands in for VideoPresenter + MainWindow: runs the real
    /// <see cref="PhotoDecoder"/> but records what reaches the sinks instead of
    /// uploading to the GPU, so the harness can assert that a frame of the right
    /// dimensions with valid NV12 planes actually arrived.
    /// </summary>
    private sealed class RecordingSink
    {
        public List<(int W, int H)> Frames       { get; } = new();
        public List<(int W, int H)> Dimensions   { get; } = new();
        public List<bool>           ActiveEvents { get; } = new();
        public List<int>            Rotations    { get; } = new();
        public bool AllPlanesValid { get; private set; } = true;

        /// <summary>NV12 luma at the centre of the most recent frame — proves the range conversion ran.</summary>
        public int CenterLuma { get; private set; } = -1;

        /// <summary>Frames the presenter would have thrown away because the surface was off.</summary>
        public int DroppedFrames { get; private set; }

        // Mirrors VideoPresenter's own active flag, which PresentFrame checks
        // before touching the swap chain. Modelled here because that flag is
        // shared by all three frame producers, so "the photo reached the display
        // path" has to mean it got past this — not merely that Display() returned.
        private bool _presenterActive;

        public PhotoDecoder Decoder { get; }

        public RecordingSink()
        {
            Decoder = new PhotoDecoder(
                onFrame:      OnFrame,
                onDimensions: (w, h) => Dimensions.Add((w, h)),
                onActive:     a      => { ActiveEvents.Add(a); _presenterActive = a; },
                onRotation:   d      => Rotations.Add(d));
        }

        /// <summary>
        /// Another producer switched the shared surface off — what a POST /stop
        /// ending an AirPlay Video clip does, via AirPlayVideoPlayer.Stop and
        /// AirPlayService's own onActive callback. It never reaches the photo
        /// decoder's sinks, which is precisely why the decoder cannot assume an
        /// earlier activation of its own still holds.
        /// </summary>
        public void DeactivateFromOtherProducer() => _presenterActive = false;

        private unsafe void OnFrame(IntPtr framePtr)
        {
            if (!_presenterActive) { DroppedFrames++; return; }

            AVFrame* f = (AVFrame*)framePtr;
            Frames.Add((f->width, f->height));

            // The presenter dereferences exactly these: two NV12 planes with
            // positive row pitches. A null here would be a black window.
            if (f->data[0] == null || f->data[1] == null || f->linesize[0] <= 0 || f->linesize[1] <= 0)
            {
                AllPlanesValid = false;
                return;
            }

            CenterLuma = f->data[0][(f->height / 2) * f->linesize[0] + f->width / 2];
        }
    }

    // ── Test image generation ─────────────────────────────────────────────────

    /// <summary>
    /// Encodes a real JPEG at the requested size with FFmpeg's MJPEG encoder.
    /// Generated rather than read from disk so the harness can pick dimensions
    /// that matter: odd sizes (even-rounding for NV12 chroma) and sizes past the
    /// receiver's display cap.
    /// </summary>
    /// <summary>A flat-luma JPEG (neutral chroma), for asserting the colour-range conversion.</summary>
    private static byte[] MakeFlatJpeg(int width, int height, byte luma)
        => MakeJpeg(width, height, seed: 0, flatLuma: luma);

    private static unsafe byte[] MakeJpeg(int width, int height, int seed, byte? flatLuma = null)
    {
        AVCodecContext* ctx    = null;
        AVFrame*        frame  = null;
        AVPacket*       packet = null;

        try
        {
            AVCodec* codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_MJPEG);
            if (codec == null) throw new InvalidOperationException("this FFmpeg build has no MJPEG encoder");

            ctx = ffmpeg.avcodec_alloc_context3(codec);
            ctx->width       = width;
            ctx->height      = height;
            ctx->pix_fmt     = AVPixelFormat.AV_PIX_FMT_YUVJ420P;
            ctx->color_range = AVColorRange.AVCOL_RANGE_JPEG;
            ctx->time_base   = new AVRational { num = 1, den = 1 };

            // Pin the quantizer to near-lossless. Without this the MJPEG encoder
            // picks a coarse default qscale, and the DC quantization error on a
            // flat test patch is tens of luma levels — enough to swamp the
            // colour-range assertions this harness makes.
            ctx->flags         |= ffmpeg.AV_CODEC_FLAG_QSCALE;
            ctx->global_quality = ffmpeg.FF_QP2LAMBDA * 1;
            ctx->qmin           = 1;
            ctx->qmax           = 1;

            int ret = ffmpeg.avcodec_open2(ctx, codec, null);
            if (ret < 0) throw new InvalidOperationException($"MJPEG avcodec_open2 failed: {ret}");

            frame = ffmpeg.av_frame_alloc();
            frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUVJ420P;
            frame->width       = width;
            frame->height      = height;
            frame->color_range = AVColorRange.AVCOL_RANGE_JPEG;
            frame->quality     = ffmpeg.FF_QP2LAMBDA * 1;
            ret = ffmpeg.av_frame_get_buffer(frame, 32);
            if (ret < 0) throw new InvalidOperationException($"av_frame_get_buffer failed: {ret}");

            // A colour-gradient test pattern: distinct in all three planes, so a
            // plane mix-up or a range error would be visible in a dumped frame.
            for (int y = 0; y < height; y++)
            {
                byte* row = frame->data[0] + y * frame->linesize[0];
                for (int x = 0; x < width; x++)
                    row[x] = flatLuma ?? (byte)((x * 255 / Math.Max(1, width - 1) + seed) & 0xFF);
            }
            for (int y = 0; y < height / 2; y++)
            {
                byte* row = frame->data[1] + y * frame->linesize[1];
                for (int x = 0; x < width / 2; x++)
                {
                    // 128 = neutral chroma, so a flat-luma image stays greyscale
                    // and its NV12 luma can be compared against the expected
                    // limited-range value without a hue term muddying it.
                    row[2 * x]     = flatLuma is not null ? (byte)128 : (byte)(y * 255 / Math.Max(1, height / 2 - 1));
                    row[2 * x + 1] = flatLuma is not null ? (byte)128 : (byte)(255 - x * 255 / Math.Max(1, width / 2 - 1));
                }
            }

            packet = ffmpeg.av_packet_alloc();
            ret = ffmpeg.avcodec_send_frame(ctx, frame);
            if (ret < 0) throw new InvalidOperationException($"avcodec_send_frame failed: {ret}");
            ffmpeg.avcodec_send_frame(ctx, null);   // flush

            ret = ffmpeg.avcodec_receive_packet(ctx, packet);
            if (ret < 0) throw new InvalidOperationException($"avcodec_receive_packet failed: {ret}");

            var bytes = new byte[packet->size];
            System.Runtime.InteropServices.Marshal.Copy((IntPtr)packet->data, bytes, 0, packet->size);
            return bytes;
        }
        finally
        {
            if (packet != null) ffmpeg.av_packet_free(&packet);
            if (frame  != null) ffmpeg.av_frame_free(&frame);
            if (ctx    != null) ffmpeg.avcodec_free_context(&ctx);
        }
    }

    // ── FFmpeg bootstrap ──────────────────────────────────────────────────────

    /// <summary>
    /// Points FFmpeg.AutoGen at the receiver's bundled native DLLs, the same way
    /// App.xaml.cs does. They are gitignored (ARCHITECTURE.md §5), so a clear
    /// message beats a DllNotFoundException stack.
    /// </summary>
    private static bool InitFFmpeg(string[] args)
    {
        string? explicitDir = null;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--ffmpeg") explicitDir = args[i + 1];

        foreach (string candidate in Candidates(explicitDir))
        {
            if (!File.Exists(Path.Combine(candidate, "avcodec-61.dll"))) continue;
            ffmpeg.RootPath = candidate;
            Console.WriteLine($"ffmpeg: {candidate}");
            return true;
        }

        Console.WriteLine("ffmpeg: avcodec-61.dll not found. Pass --ffmpeg <dir>, or place the FFmpeg 7.x");
        Console.WriteLine("        shared build in src\\AirPlayReceiver\\native\\x64 (ARCHITECTURE.md §5).");
        return false;
    }

    private static IEnumerable<string> Candidates(string? explicitDir)
    {
        if (explicitDir is not null) yield return explicitDir;

        string here = AppContext.BaseDirectory;
        yield return Path.Combine(here, "native", "x64");
        // tools\PhotoHarness\bin\x64\<cfg>\net8.0\win-x64 → repo root
        for (int up = 4; up <= 8; up++)
        {
            string root = here;
            for (int i = 0; i < up && root.Length > 3; i++) root = Path.GetDirectoryName(root) ?? root;
            yield return Path.Combine(root, "src", "AirPlayReceiver", "native", "x64");
        }
    }
}
