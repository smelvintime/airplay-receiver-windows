using AirPlayReceiver.Services;
using System;
using System.Collections.Generic;
using System.Threading;

namespace AirPlayReceiver.Protocol;

/// <summary>
/// The <c>PUT /photo</c> endpoint — AirPlay Photos, which is a protocol entirely
/// separate from AirPlay Video: no <c>/play</c>, no FCUP, no HLS, no RTP. iOS
/// simply PUTs a raw JPEG (the body is <b>not</b> a plist) and expects
/// <c>200 OK, Content-Length: 0</c> once it is on screen.
///
/// <code>
/// PUT /photo HTTP/1.1
/// X-Apple-AssetKey:    F92F9B91-954E-4D63-BB9A-EEC771ADE6E8
/// X-Apple-AssetAction: cacheOnly | displayCached      (optional)
/// X-Apple-Transition:  Dissolve | SlideLeft | None …  (optional)
/// X-Apple-Session-ID:  &lt;GUID&gt;
/// Content-Length:      462848
///
/// &lt;JPEG DATA&gt;
/// </code>
///
/// The three asset actions (openairplay/airplay-spec, src/photos/photo_caching.md):
/// <list type="table">
///   <item><term>(absent)</term>     <description>body is a JPEG — display it now</description></item>
///   <item><term>cacheOnly</term>    <description>body is a JPEG — store it under the asset key, display nothing</description></item>
///   <item><term>displayCached</term><description>body is empty — display the JPEG already stored under the key</description></item>
/// </list>
///
/// <b>412 Precondition Failed is load-bearing.</b> A <c>displayCached</c> for a
/// key we evicted (or never saw) must answer 412; that is the sender's signal to
/// re-push the bytes. Answering 200 there strands the previous photo on screen
/// for the rest of the session, because iOS believes the new one is up.
///
/// This type is deliberately free of any UI, FFmpeg or WinUI dependency: display
/// is a delegate. That keeps the protocol behaviour — actions, statuses, caching
/// — testable over a loopback socket without a GPU (see tools/PhotoHarness).
/// </summary>
public sealed class PhotoHandler
{
    /// <summary>UUID identifying the image, and the cache key.</summary>
    public const string AssetKeyHeader = "X-Apple-AssetKey";

    /// <summary>Caching behaviour: absent, "cacheOnly" or "displayCached".</summary>
    public const string AssetActionHeader = "X-Apple-AssetAction";

    /// <summary>Requested transition effect ("Dissolve", "SlideLeft", "None", …).</summary>
    public const string TransitionHeader = "X-Apple-Transition";

    public const string ActionCacheOnly     = "cacheOnly";
    public const string ActionDisplayCached = "displayCached";

    /// <summary>Displays a JPEG. Returns false when it could not be decoded.</summary>
    /// <param name="jpeg">Raw JPEG bytes.</param>
    /// <param name="assetKey">X-Apple-AssetKey, for logging.</param>
    /// <param name="transition">X-Apple-Transition, for logging.</param>
    public delegate bool DisplayPhoto(byte[] jpeg, string assetKey, string transition);

    private readonly DisplayPhoto _display;
    private readonly Action       _stopDisplay;
    private readonly PhotoCache   _cache;

    private long _requests;
    private long _displayed;
    private long _cachedOnly;
    private long _preconditionFailed;

    public PhotoHandler(DisplayPhoto display, Action stopDisplay, PhotoCache? cache = null)
    {
        _display     = display     ?? throw new ArgumentNullException(nameof(display));
        _stopDisplay = stopDisplay ?? throw new ArgumentNullException(nameof(stopDisplay));
        _cache       = cache ?? new PhotoCache();
    }

    /// <summary>The asset cache, exposed for teardown and for harness assertions.</summary>
    public PhotoCache Cache => _cache;

    // Set by the first photo request that carried an asset — a display OR a
    // cacheOnly preload — and cleared by EndSession. Drives the photo/stopped
    // event: iOS considers the session open from the moment it pushed an asset,
    // whether or not we displayed it or our decode of it succeeded.
    private int _sessionOpen;

    /// <summary>True between the first asset of a session and its teardown.</summary>
    public bool SessionActive => Volatile.Read(ref _sessionOpen) != 0;

    public long Requests           => Interlocked.Read(ref _requests);
    public long Displayed          => Interlocked.Read(ref _displayed);
    public long CachedOnly         => Interlocked.Read(ref _cachedOnly);
    public long PreconditionFailed => Interlocked.Read(ref _preconditionFailed);

    // ── Request handling ──────────────────────────────────────────────────────

    /// <summary>
    /// Handles one <c>PUT /photo</c> and returns the serialised response.
    /// Never throws: a malformed photo must not kill the RTSP connection.
    /// </summary>
    public byte[] Handle(RtspMessage msg)
    {
        Interlocked.Increment(ref _requests);

        string assetKey   = Header(msg, AssetKeyHeader);
        string action     = Header(msg, AssetActionHeader);
        string transition = Header(msg, TransitionHeader);
        string sessionId  = Header(msg, ReverseChannel.SessionIdHeader);
        byte[] body       = msg.Body ?? Array.Empty<byte>();

        Log.Info("Photo", $"PUT /photo key={(assetKey.Length == 0 ? "(none)" : assetKey)} " +
                          $"action={(action.Length == 0 ? "(display)" : action)} " +
                          $"transition={(transition.Length == 0 ? "(none)" : transition)} " +
                          $"session={(sessionId.Length == 0 ? "(none)" : sessionId)} body={body.Length}B");

        try
        {
            if (string.Equals(action, ActionDisplayCached, StringComparison.OrdinalIgnoreCase))
                return HandleDisplayCached(msg, assetKey, transition);

            if (string.Equals(action, ActionCacheOnly, StringComparison.OrdinalIgnoreCase))
                return HandleCacheOnly(msg, assetKey, body);

            if (action.Length > 0)
            {
                // An action we don't know. Treat a non-empty body as "display now"
                // rather than refusing — showing the photo beats a blank screen —
                // but log it loudly, because it means the spec moved on.
                Log.Warn("Photo", $"unknown {AssetActionHeader}='{action}' (key={assetKey}) — " +
                                  $"treating as an immediate display of the {body.Length}B body");
            }

            return HandleDisplayNow(msg, assetKey, transition, body);
        }
        catch (Exception ex)
        {
            // Belt and braces: the display delegate reaches FFmpeg and D3D11.
            Log.Error("Photo", $"PUT /photo key={assetKey} action={action} threw — answering 200 so the session survives", ex);
            return PhotoOk(msg);
        }
    }

    /// <summary>
    /// Ends the photo session: clears the surface and the asset cache. Called on
    /// RTSP TEARDOWN — <b>not</b> when a control connection merely closes, since
    /// iOS opens several short-lived ones per session and any of them dropping
    /// would otherwise wipe the photo the user is still looking at.
    /// </summary>
    public void EndSession()
    {
        Interlocked.Exchange(ref _sessionOpen, 0);
        _stopDisplay();
        _cache.Clear();
        Log.Info("Photo", $"session ended — requests={Requests} displayed={Displayed} " +
                          $"cacheOnly={CachedOnly} 412s={PreconditionFailed}");
    }

    // ── The three asset actions ───────────────────────────────────────────────

    private byte[] HandleDisplayNow(RtspMessage msg, string assetKey, string transition, byte[] body)
    {
        if (body.Length == 0)
        {
            Log.Warn("Photo", $"display key={assetKey}: no {AssetActionHeader} header and an empty body — " +
                              "nothing to show; answering 400");
            return RtspMessage.BuildResponse(400, "Bad Request", msg.CSeq, protocol: msg.Version);
        }

        // Cache it even though this is an immediate display: iOS is free to
        // follow up with displayCached for the same key, and holding the bytes
        // is what lets that second request skip the transfer.
        if (assetKey.Length > 0) _cache.Store(assetKey, body);

        Show(body, assetKey, transition, "display");
        return PhotoOk(msg);
    }

    private byte[] HandleCacheOnly(RtspMessage msg, string assetKey, byte[] body)
    {
        if (assetKey.Length == 0)
        {
            Log.Warn("Photo", $"{ActionCacheOnly} without {AssetKeyHeader} — nothing to key the asset on; answering 400");
            return RtspMessage.BuildResponse(400, "Bad Request", msg.CSeq, protocol: msg.Version);
        }
        if (body.Length == 0)
        {
            Log.Warn("Photo", $"{ActionCacheOnly} key={assetKey} with an empty body — nothing to cache; answering 400");
            return RtspMessage.BuildResponse(400, "Bad Request", msg.CSeq, protocol: msg.Version);
        }

        // A preload opens the session too. TEARDOWN only runs EndSession when
        // SessionActive is set, so a phone that preloads and then disconnects
        // without ever displaying (the user backing out of an album) would
        // otherwise leave the whole byte budget — up to 192 MB — and the keys of
        // a phone that has gone away resident for the rest of the app's life.
        Interlocked.Exchange(ref _sessionOpen, 1);

        _cache.Store(assetKey, body);
        Interlocked.Increment(ref _cachedOnly);
        Log.Info("Photo", $"{ActionCacheOnly} key={assetKey} {body.Length}B stored — not displayed (preload)");
        return PhotoOk(msg);
    }

    private byte[] HandleDisplayCached(RtspMessage msg, string assetKey, string transition)
    {
        if (assetKey.Length == 0)
        {
            Log.Warn("Photo", $"{ActionDisplayCached} without {AssetKeyHeader} — cannot resolve an asset; answering 400");
            return RtspMessage.BuildResponse(400, "Bad Request", msg.CSeq, protocol: msg.Version);
        }

        if (!_cache.TryGet(assetKey, out byte[]? jpeg) || jpeg is null)
        {
            // The contract that makes the cache safe to bound: 412 tells iOS to
            // re-push the JPEG. Never answer 200 for a key we don't hold.
            Interlocked.Increment(ref _preconditionFailed);
            Log.Warn("Photo", $"{ActionDisplayCached} key={assetKey}: CACHE MISS — answering " +
                              $"412 Precondition Failed so iOS re-sends the JPEG " +
                              $"(cache holds {_cache.Count} entries, 412s this run: {PreconditionFailed})");
            return RtspMessage.BuildResponse(412, "Precondition Failed", msg.CSeq, protocol: msg.Version);
        }

        Log.Info("Photo", $"{ActionDisplayCached} key={assetKey}: CACHE HIT ({jpeg.Length}B, no transfer needed)");
        Show(jpeg, assetKey, transition, ActionDisplayCached);
        return PhotoOk(msg);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void Show(byte[] jpeg, string assetKey, string transition, string origin)
    {
        Interlocked.Exchange(ref _sessionOpen, 1);

        if (_display(jpeg, assetKey, transition))
        {
            Interlocked.Increment(ref _displayed);
        }
        else
        {
            // Still 200: a photo we can't decode is our problem, and a 4xx/5xx
            // here can make Photos abandon the whole session rather than move on
            // to the next image. The Error line above is the evidence.
            Log.Error("Photo", $"{origin} key={assetKey}: display FAILED ({jpeg.Length}B) — " +
                               "answering 200 anyway so the Photos session continues");
        }
    }

    /// <summary>The spec's reply: 200 OK with an explicit <c>Content-Length: 0</c>.</summary>
    private static byte[] PhotoOk(RtspMessage msg)
        => RtspMessage.BuildResponse(200, "OK", msg.CSeq,
               new Dictionary<string, string> { ["Content-Length"] = "0" },
               protocol: msg.Version);

    private static string Header(RtspMessage msg, string name)
        => msg.Headers.TryGetValue(name, out string? v) ? v.Trim() : string.Empty;
}
