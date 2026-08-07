using AirPlayReceiver.Services;
using System;
using System.Collections.Generic;

namespace AirPlayReceiver.Protocol;

/// <summary>
/// The receiver-side asset cache behind AirPlay Photos' <c>X-Apple-AssetAction</c>
/// header (openairplay/airplay-spec, src/photos/photo_caching.md).
///
/// iOS preloads the next photos it expects to show with
/// <c>cacheOnly</c> + the JPEG body, then displays them later with
/// <c>displayCached</c> and an <b>empty</b> body — so the bytes never cross the
/// network twice. That only works if we really keep them: a
/// <c>displayCached</c> for a key we no longer hold must be answered
/// <b>412 Precondition Failed</b>, which is the sender's cue to re-push the JPEG.
/// A wrong 200 there leaves the previous photo on screen forever, because iOS
/// believes we displayed the new one.
///
/// Bounds: full-resolution iPhone photos run several megabytes each, so an
/// unbounded dictionary is a slow memory leak across a long slideshow. Both an
/// entry count and a total byte budget are enforced, evicting least-recently-
/// used first — eviction is safe precisely <i>because</i> 412 makes the sender
/// re-send.
///
/// Thread-safe: photos arrive on RTSP session threads and iOS uses several
/// short-lived connections per session.
/// </summary>
public sealed class PhotoCache
{
    /// <summary>Default entry cap — iOS preloads a handful of photos ahead, not hundreds.</summary>
    public const int DefaultMaxEntries = 48;

    /// <summary>Default byte budget (192 MB) — roughly 48 × 4 MB full-resolution JPEGs.</summary>
    public const long DefaultMaxBytes = 192L * 1024 * 1024;

    private readonly int  _maxEntries;
    private readonly long _maxBytes;

    private readonly object _sync = new();

    /// <summary>
    /// How an asset key is matched — and it must be the <b>same</b> rule for both
    /// structures below. <see cref="List{T}.Remove"/> compares strings ordinally,
    /// so a case-varying key would hit <see cref="_entries"/> while its
    /// <see cref="_mru"/> removal silently missed: the touch in
    /// <see cref="TryGet"/> would then append a duplicate on every
    /// <c>displayCached</c>, growing the list without bound, and eviction would
    /// start popping keys that are no longer the least-recently-used one.
    /// iOS spells its UUIDs consistently today, which is exactly why this would
    /// go unnoticed until some phone or spec revision didn't.
    /// </summary>
    private static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;

    // Key → JPEG bytes, plus a parallel MRU-ordered key list (most recent last).
    // A list rather than a LinkedList: at a few dozen entries the O(n) Remove is
    // far cheaper than the extra node bookkeeping, and it keeps eviction obvious.
    private readonly Dictionary<string, byte[]> _entries = new(KeyComparer);
    private readonly List<string> _mru = new();
    private long _bytes;

    // Counters, reported on eviction and in the handler's log lines so a field
    // log shows whether caching is actually saving transfers.
    private long _hits;
    private long _misses;
    private long _evictions;

    public PhotoCache(int maxEntries = DefaultMaxEntries, long maxBytes = DefaultMaxBytes)
    {
        if (maxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        if (maxBytes   <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _maxEntries = maxEntries;
        _maxBytes   = maxBytes;
    }

    /// <summary>Number of cached assets.</summary>
    public int Count { get { lock (_sync) return _entries.Count; } }

    /// <summary>Total bytes held.</summary>
    public long Bytes { get { lock (_sync) return _bytes; } }

    /// <summary>
    /// Length of the MRU list, which must always equal <see cref="Count"/>.
    /// Exposed for the harness: the two structures drifting apart is invisible
    /// from the outside until eviction starts dropping the wrong asset.
    /// </summary>
    public int MruCount { get { lock (_sync) return _mru.Count; } }

    public long Hits      { get { lock (_sync) return _hits; } }
    public long Misses    { get { lock (_sync) return _misses; } }
    public long Evictions { get { lock (_sync) return _evictions; } }

    /// <summary>
    /// Stores (or replaces) an asset and evicts LRU entries until both budgets
    /// hold. A single asset larger than the whole byte budget is rejected rather
    /// than allowed to evict everything else — the sender re-pushes it on 412.
    /// </summary>
    public bool Store(string assetKey, byte[] jpeg)
    {
        if (string.IsNullOrEmpty(assetKey) || jpeg is null || jpeg.Length == 0) return false;

        if (jpeg.Length > _maxBytes)
        {
            Log.Warn("Photo", $"cache store refused: key={assetKey} is {Mb(jpeg.Length)} — " +
                              $"larger than the whole {Mb(_maxBytes)} budget");
            return false;
        }

        lock (_sync)
        {
            if (_entries.TryGetValue(assetKey, out byte[]? existing))
            {
                _bytes -= existing.Length;
                RemoveFromMru(assetKey);
            }

            _entries[assetKey] = jpeg;
            _mru.Add(assetKey);
            _bytes += jpeg.Length;

            EvictWhileOverBudget();

            Log.Info("Photo", $"cached key={assetKey} {Mb(jpeg.Length)} " +
                              $"(cache now {_entries.Count}/{_maxEntries} entries, {Mb(_bytes)}/{Mb(_maxBytes)})");
        }
        return true;
    }

    /// <summary>
    /// Looks an asset up and marks it most-recently-used. A miss is what drives
    /// the 412 reply, so both outcomes are counted.
    /// </summary>
    public bool TryGet(string assetKey, out byte[]? jpeg)
    {
        jpeg = null;
        if (string.IsNullOrEmpty(assetKey)) return false;

        lock (_sync)
        {
            if (!_entries.TryGetValue(assetKey, out jpeg))
            {
                _misses++;
                return false;
            }

            // Touch: move to the MRU end so a photo iOS keeps revisiting survives.
            RemoveFromMru(assetKey);
            _mru.Add(assetKey);
            _hits++;
            return true;
        }
    }

    /// <summary>Drops everything (session teardown — the next phone's keys are unrelated).</summary>
    public void Clear()
    {
        lock (_sync)
        {
            if (_entries.Count == 0) return;
            Log.Info("Photo", $"cache cleared ({_entries.Count} entries, {Mb(_bytes)}; " +
                              $"lifetime hits={_hits} misses={_misses} evictions={_evictions})");
            _entries.Clear();
            _mru.Clear();
            _bytes = 0;
        }
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Drops <paramref name="assetKey"/> from the MRU list using
    /// <see cref="KeyComparer"/> — <see cref="List{T}.Remove"/> would use the
    /// ordinal default and leave the two structures disagreeing.
    /// Caller must hold <see cref="_sync"/>.
    /// </summary>
    private void RemoveFromMru(string assetKey)
    {
        int i = _mru.FindIndex(k => KeyComparer.Equals(k, assetKey));
        if (i >= 0) _mru.RemoveAt(i);
    }

    /// <summary>Caller must hold <see cref="_sync"/>.</summary>
    private void EvictWhileOverBudget()
    {
        while (_mru.Count > 0 && (_entries.Count > _maxEntries || _bytes > _maxBytes))
        {
            string oldest = _mru[0];
            _mru.RemoveAt(0);
            if (_entries.Remove(oldest, out byte[]? dropped))
            {
                _bytes -= dropped.Length;
                _evictions++;
                Log.Info("Photo", $"cache evicted key={oldest} {Mb(dropped.Length)} " +
                                  $"(now {_entries.Count} entries, {Mb(_bytes)}) — " +
                                  "a later displayCached for it will answer 412 and iOS will re-push");
            }
        }
    }

    private static string Mb(long bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024.0):F1}MB" : $"{bytes / 1024.0:F0}KB";
}
