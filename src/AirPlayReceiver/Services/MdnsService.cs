using Makaretu.Dns;
using Makaretu.Dns.Resolving;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace AirPlayReceiver.Services;

/// <summary>
/// Advertises the AirPlay receiver via mDNS / DNS-SD so that iOS devices
/// discover it in the Screen Mirroring picker.
///
/// Two service types are registered (both required by iOS 14+):
///   • _airplay._tcp  — AirPlay 2 mirroring endpoint
///   • _raop._tcp     — Legacy RAOP record (iOS still checks for it)
///
/// Reference for TXT record keys and the `features` bitmask:
///   https://openairplay.github.io/airplay-spec/service_discovery.html
///   UxPlay lib/raop.c  →  raop_mdns_register()
/// </summary>
public sealed class MdnsService : IAsyncDisposable
{
    // ── Advertised identity ───────────────────────────────────────────────────
    //
    // We impersonate an Apple TV 3 (AppleTV3,2). That model is the one every
    // working open-source receiver claims, and it is the only identity coherent
    // with srcvers 220.68 — an AirPlay-1-era server version. Claiming a 4K model
    // (AppleTV6,2) alongside srcvers 220.68 is a combination that exists on no
    // real device, and it invites iOS to expect AirPlay 2 semantics (transient
    // pairing, PTP clock, HomeKit) that this receiver does not implement.
    //
    // A real Apple TV 3 advertises, in _airplay._tcp TXT:
    //   features=0x5A7FFFF7,0xE   flags=0x44   model=AppleTV3,2
    //   srcvers=220.68            protovers=1.0
    //
    // features — 64-bit capability bitmask, printed as "word1,word2" (lo,hi).
    //   Bit layout: https://openairplay.github.io/airplay-spec/features.html
    //
    //   Word 1 (0x5A7FFFF7) carries the bits that gate the *video* advertisement:
    //   bit 0 (Video) and bit 4 (VideoHTTPLiveStreams). Those two are exactly what
    //   UxPlay's `-hls` option turns on, and both are set here.
    //
    //   Word 2 = 0xE, matching real Apple TV 3 hardware. (0x1E — which PhairPlay
    //   also advertises — additionally sets bit 36, which is undocumented in every
    //   published source and is not needed by anything we do.)
    //
    //   DO NOT "helpfully" add bits to this mask:
    //     • bit 27 (SupportsLegacyPairing) must stay 1 — it is what keeps
    //       post-pair-verify traffic UNENCRYPTED, the only mode a non-Apple
    //       receiver can implement.
    //     • word-2 bits 38/43/46/48 (CoreUtils / SystemPairing / HKPairing /
    //       TransientPairing) must stay 0 — any of them makes iOS switch to
    //       HomeKit-style pairing with AES-GCM-128 whose key derivation is not
    //       public. Both 0xE and 0x1E satisfy this.
    private const string AirPlayFeatures  = "0x5A7FFFF7,0xE";
    private const string AirPlayModel     = "AppleTV3,2";   // Coherent with srcvers 220.68
    private const string AirPlaySrcVers   = "220.68";       // AirPlay server version
    private const string AirPlayProtoVers = "1.0";          // Advertised by real Apple TV 3
    private const string AirPlayOsVersion = "14.0";         // tvOS version string

    // ── Status flags ──────────────────────────────────────────────────────────
    //
    // The `flags` TXT field — not `features` — carries session/availability state.
    // 0x44 = bit 2 (device supports AirPlay from one iOS device at a time /
    // "PIN-less pairing supported") | bit 6 (device supports relay). It is what a
    // real Apple TV 3 advertises while idle, and it matches the `statusFlags`=68
    // this receiver already returns from GET /info.
    //
    // Bit 17 (0x20000) is ReceiverSessionIsActive. Real Apple TVs SET it when a
    // session starts and CLEAR it when the session ends, re-announcing the record
    // both times (verified against device dumps: an idle 4K Apple TV advertises
    // 0x10644 and 0x30e44 while receiving — same bit, different base flags).
    private const string StatusFlagsIdle   = "0x44";
    private const string StatusFlagsActive = "0x20044";

    // Cache-flush bit (RFC 6762 §10.2) OR'd into DnsClass.IN. Makaretu has no
    // first-class support for it, but DnsClass is a ushort enum so the value is
    // representable. Only ever applied to the record copies we put on the wire in
    // an announcement — never to the catalog records, whose Class must stay plain
    // IN so the responder still matches incoming QCLASS=IN questions.
    private const DnsClass CacheFlushIn = (DnsClass)0x8001;

    // ── Record lifetimes ──────────────────────────────────────────────────────
    //
    // These are the RFC 6762 §10 lifetimes, and also what real Apple TVs use.
    //
    // Makaretu already lands on the same numbers, but by a route worth pinning
    // down: Makaretu.Dns 2.0.1 declares ResourceRecord.DefaultTTL and
    // DefaultHostTTL as one DAY, and Makaretu.Dns.Multicast's MulticastService
    // *static constructor* overwrites both statics with 4500s/120s. Every record
    // therefore inherits the right TTL — but only if a MulticastService has been
    // constructed first. Stating the values here removes the dependency on that
    // static-initialisation order, and gets the effective TTL into the log.
    private static readonly TimeSpan SharedRecordTtl = TimeSpan.FromSeconds(4500); // PTR/SRV/TXT (75 min)
    private static readonly TimeSpan HostRecordTtl   = TimeSpan.FromSeconds(120);  // A/AAAA

    // Unsolicited keepalive announcement interval. iOS's mDNSResponder refreshes a
    // cached record at 80/85/90/95% of its TTL; announcing well inside that window
    // means the picker entry survives even if a refresh query is lost or never
    // reaches us. Standard practice for AirPlay receivers.
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(45);

    private readonly string     _deviceName;
    private readonly string     _deviceId;   // MAC-style: "AA:BB:CC:DD:EE:FF"
    private readonly int        _port;
    private readonly string     _publicKeyHex; // Ed25519 public key (32 bytes, hex)
    private readonly MulticastService _mdns;
    private readonly ServiceDiscovery _sd;
    private CancellationTokenSource?  _cts;

    // Profiles are kept so their TXT records can be updated and re-announced.
    private ServiceProfile? _airplayProfile;
    private ServiceProfile? _raopProfile;

    // Set once MulticastService.Start() has run — sending before that throws.
    private volatile bool _running;

    // 0 = idle, 1 = a session is active. Several callbacks (mirroring stream
    // start, AirPlay Video activation) report the same state, so the transition
    // must be applied exactly once — and opposite transitions must not interleave,
    // hence the lock rather than a bare Interlocked exchange.
    private int _sessionActive;
    private readonly object _sessionFlagLock = new();

    // Keepalive announcement loop; runs for the lifetime of the service.
    private Task? _keepAlive;

    // Queries seen for our service names, so the log can show whether the
    // responder is still being asked (and by extension still answering) during and
    // after a session — the alternative explanation for a device that vanishes
    // from the picker is that nothing is querying us at all.
    private long _queriesForUsTotal;
    private long _queriesForUsWindow;

    // ── Construction ──────────────────────────────────────────────────────────

    /// <param name="deviceName">Friendly name shown in the Screen Mirroring list.</param>
    /// <param name="publicKeyHex">The receiver's Ed25519 public key (hex) for the <c>pk</c> TXT record.</param>
    /// <param name="port">TCP port the RTSP server listens on (default 7000).</param>
    public MdnsService(string deviceName, string publicKeyHex, int port = 7000)
    {
        _deviceName   = deviceName;
        _port         = port;
        _deviceId     = GetMacAddress();
        _publicKeyHex = publicKeyHex;

        _mdns = new MulticastService();
        _sd   = new ServiceDiscovery(_mdns);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken ct = default)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = cts;

        // Diagnostics: log which interfaces we bind to and any AirPlay/RAOP
        // queries that arrive, so we can tell whether iOS is reaching us at all.
        // (Seeing a query here means the network path works and the problem is in
        //  our response; seeing none points to firewall/network/subnet isolation.)
        _mdns.NetworkInterfaceDiscovered += (_, e) =>
        {
            foreach (var nic in e.NetworkInterfaces)
                Log.Info("mDNS", $"Listening on interface: {nic.Name}");
        };

        // Per-query logging was invaluable while bringing discovery up, but iOS
        // polls these names several times a second, and the noise buries the RTSP
        // and video logs. Count them instead and report the count once per
        // keepalive tick — enough to prove the responder is still being reached,
        // without drowning the log.
        _mdns.QueryReceived += OnQueryReceived;

        RegisterAirPlayService();
        RegisterRaopService();
        NormaliseRecordTtls();

        _mdns.Start();
        _running = true;

        Log.Info("mDNS", $"Advertising '{_deviceName}' on port {_port}  deviceid={_deviceId}");

        // Advertise() only fills Makaretu's catalog; nothing goes on the wire until
        // something asks. Announce up front so the picker learns about us (and so
        // any stale record iOS cached from an earlier run is flushed) immediately.
        AnnounceAll("startup");

        _keepAlive = Task.Run(() => KeepAliveLoopAsync(cts.Token));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops advertising. Only reached on real application exit (tray → Exit →
    /// App.OnExitRequested → AirPlayService.StopAsync); nothing on the session
    /// teardown path calls this, so the responder keeps answering between sessions.
    ///
    /// Deliberately does NOT send a goodbye (TTL=0). Makaretu would do that through
    /// ServiceDiscovery.Unadvertise, which we never call — and ServiceDiscovery's
    /// own Dispose doesn't call it either, so no code path can purge the record
    /// from iOS's cache. Leaving it that way keeps "device vanished from the
    /// picker" unambiguous: it cannot be us having withdrawn the record.
    /// </summary>
    public Task StopAsync()
    {
        _running = false;
        _cts?.Cancel();
        _mdns.QueryReceived -= OnQueryReceived;
        _mdns.Stop();
        Log.Info("mDNS", $"Stopped. (no goodbye sent; {Interlocked.Read(ref _queriesForUsTotal)} queries " +
                         "for our names seen this run)");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Reports whether an AirPlay session is currently receiving, so the advertised
    /// <c>flags</c> (and the RAOP <c>sf</c> mirror of it) track session state the way
    /// a real Apple TV's do: bit 17 (ReceiverSessionIsActive) set while a session
    /// runs, cleared when it ends, with the record re-announced on each transition.
    ///
    /// Safe to call repeatedly with the same value and from any thread; only real
    /// transitions do work.
    /// </summary>
    public void SetSessionActive(bool active)
    {
        // The whole transition is serialised, not just the de-duplication: two
        // call sites drive this (the video player's onActive callback and the
        // session's StreamStarted/StreamStopped), so a fast start→end→start can
        // otherwise let opposite transitions interleave and leave the catalog's
        // live TXT — the record that answers real iOS queries — showing the wrong
        // state until the next keepalive corrects it.
        try
        {
            lock (_sessionFlagLock)
            {
                int desired = active ? 1 : 0;
                if (_sessionActive == desired) return;
                _sessionActive = desired;

                string flags = active ? StatusFlagsActive : StatusFlagsIdle;
                UpdateTxtProperty(_airplayProfile, "flags", flags);
                UpdateTxtProperty(_raopProfile,    "sf",    flags);

                Log.Info("mDNS", $"Session {(active ? "started" : "ended")} — flags/sf={flags}, re-announcing");
                AnnounceAll(active ? "session-start" : "session-end");
            }
        }
        catch (Exception ex)
        {
            // Called synchronously from RTSP/media callbacks; an mDNS bookkeeping
            // failure must never tear down a live session.
            Log.Error("mDNS", "Failed to update session flags", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();

        // Let the keepalive loop observe the cancellation and leave AnnounceProfile
        // before the MulticastService it sends through is disposed underneath it.
        if (_keepAlive is { } keepAlive)
        {
            try { await keepAlive.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) { Log.Warn("mDNS", $"Keepalive did not stop cleanly: {ex.GetType().Name}"); }
        }

        _sd.Dispose();
        _mdns.Dispose();
    }

    // ── Service registration ──────────────────────────────────────────────────

    private void RegisterAirPlayService()
    {
        // _airplay._tcp TXT record keys required by iOS:
        //   deviceid   — MAC address of this receiver
        //   features   — 64-bit capability bitmask (hex, "word1,word2")
        //   flags      — status/availability bitmask (0x44 idle, see StatusFlagsIdle)
        //   model      — device model string (spoofed as Apple TV 3)
        //   pi         — persistent device GUID (use MAC for simplicity)
        //   pk         — Ed25519 public key (32 bytes, hex-encoded)
        //                  — required for AirPlay 2 pairing; placeholder here.
        //   protovers  — AirPlay protocol version (1.0)
        //   srcvers    — AirPlay server version string

        var airplayProfile = new ServiceProfile(
            instanceName: _deviceName,
            serviceName:  "_airplay._tcp",
            port:         (ushort)_port
        );

        AddTxt(airplayProfile, "deviceid",  _deviceId);
        AddTxt(airplayProfile, "features",  AirPlayFeatures);
        AddTxt(airplayProfile, "flags",     StatusFlagsIdle);
        AddTxt(airplayProfile, "model",     AirPlayModel);
        AddTxt(airplayProfile, "pi",        GuidFromMac(_deviceId));
        AddTxt(airplayProfile, "pk",        _publicKeyHex);
        AddTxt(airplayProfile, "protovers", AirPlayProtoVers);
        AddTxt(airplayProfile, "srcvers",   AirPlaySrcVers);
        AddTxt(airplayProfile, "osvers",    AirPlayOsVersion);
        AddTxt(airplayProfile, "vv",        "2");  // vendor version

        _airplayProfile = airplayProfile;
        _sd.Advertise(airplayProfile);
        Log.Info("mDNS",
            $"_airplay._tcp advertised: features={AirPlayFeatures} flags={StatusFlagsIdle} " +
            $"model={AirPlayModel} srcvers={AirPlaySrcVers} protovers={AirPlayProtoVers}");
    }

    private void RegisterRaopService()
    {
        // _raop._tcp instance name must be prefixed with the device MAC
        // (no colons) followed by '@' and then the device friendly name.
        // Example: "AABBCCDDEEFF@MyReceiver"
        string macNoColons = _deviceId.Replace(":", "");
        string raopInstance = $"{macNoColons}@{_deviceName}";

        var raopProfile = new ServiceProfile(
            instanceName: raopInstance,
            serviceName:  "_raop._tcp",
            port:         (ushort)_port
        );

        // RAOP TXT keys (subset relevant to screen mirroring). Real Apple TVs
        // mirror four of the _airplay._tcp values into this record under short
        // names, and iOS cross-checks them — so they must not drift apart:
        //   ft ↔ features   sf ↔ flags   am ↔ model   vs ↔ srcvers
        AddTxt(raopProfile, "et",   "0,3,5");   // encryption types: none, RSA, FairPlay
        AddTxt(raopProfile, "md",   "0,1,2");   // metadata types
        AddTxt(raopProfile, "ft",   AirPlayFeatures);   // == _airplay features
        AddTxt(raopProfile, "sf",   StatusFlagsIdle);   // == _airplay flags
        AddTxt(raopProfile, "am",   AirPlayModel);      // == _airplay model
        AddTxt(raopProfile, "vs",   AirPlaySrcVers);    // == _airplay srcvers
        AddTxt(raopProfile, "da",   "true");    // digest auth
        AddTxt(raopProfile, "sr",   "44100");   // sample rate (audio)
        AddTxt(raopProfile, "ss",   "16");      // sample size
        AddTxt(raopProfile, "ch",   "2");       // channels
        AddTxt(raopProfile, "vn",   "65537");
        AddTxt(raopProfile, "tp",   "UDP");
        AddTxt(raopProfile, "pk",   _publicKeyHex);

        _raopProfile = raopProfile;
        _sd.Advertise(raopProfile);
        Log.Info("mDNS",
            $"_raop._tcp advertised: instance={raopInstance} ft={AirPlayFeatures} " +
            $"sf={StatusFlagsIdle} am={AirPlayModel} vs={AirPlaySrcVers}");
    }

    // ── Announcement ──────────────────────────────────────────────────────────
    //
    // Makaretu's ServiceDiscovery.Announce() is not usable here for two reasons:
    //
    //   1. It blocks the calling thread for a full second — it sends the message,
    //      then does Task.Delay(1000).Wait(), then sends it again. Our callers are
    //      RTSP/media threads.
    //   2. It cannot set the cache-flush bit. Without it, an updated TXT record is
    //      treated by iOS as a *shared* record: the new value is added alongside
    //      the old one rather than replacing it, and the stale one lingers for the
    //      remainder of its TTL. That makes a runtime `flags` update pointless.
    //
    // Neither is fixable through the 0.27.0 API, so we build the unsolicited
    // response ourselves and send it through MulticastService.SendAnswer. The
    // records are fresh copies, so the catalog's own records (which the responder
    // matches queries against) are never touched.

    /// <summary>
    /// Belt-and-braces keepalive: re-announces both profiles every
    /// <see cref="KeepAliveInterval"/> for the lifetime of the service, and reports
    /// how many queries for our names arrived in that window.
    ///
    /// This is cheap (two UDP packets) and safe with Makaretu — SendAnswer with
    /// checkDuplicate:false does nothing but serialise and send.
    /// </summary>
    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(KeepAliveInterval, ct);

                // Per-iteration guard: a single bad tick must not end the loop for
                // the life of the process. Losing the keepalive silently would
                // reinstate the exact "device drops out of the picker" failure
                // this loop exists to prevent.
                try
                {
                    long window = Interlocked.Exchange(ref _queriesForUsWindow, 0);
                    long total  = Interlocked.Read(ref _queriesForUsTotal);
                    Log.Info("mDNS",
                        $"Keepalive: {window} queries for our names in the last {KeepAliveInterval.TotalSeconds:F0}s " +
                        $"(total {total}), session={(Volatile.Read(ref _sessionActive) == 1 ? "active" : "idle")}");

                    AnnounceProfile(_airplayProfile, "keepalive");
                    AnnounceProfile(_raopProfile,    "keepalive");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Error("mDNS", "Keepalive tick failed; continuing", ex);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            Log.Error("mDNS", "Keepalive loop stopped", ex);
        }
    }

    /// <summary>Re-announces both service profiles, twice, one second apart (RFC 6762 §8.3).</summary>
    private void AnnounceAll(string reason)
    {
        if (!_running) return;

        // Fire and forget: the caller is a protocol or media thread and must not
        // wait on the inter-announcement delay.
        _ = Task.Run(async () =>
        {
            try
            {
                AnnounceProfile(_airplayProfile, reason);
                AnnounceProfile(_raopProfile,    reason);

                await Task.Delay(TimeSpan.FromSeconds(1), _cts?.Token ?? CancellationToken.None);

                AnnounceProfile(_airplayProfile, reason + "/repeat");
                AnnounceProfile(_raopProfile,    reason + "/repeat");
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception ex)
            {
                Log.Error("mDNS", $"Re-announce ({reason}) failed", ex);
            }
        });
    }

    /// <summary>
    /// Sends one unsolicited response describing <paramref name="profile"/>'s current
    /// records. The service-type PTR is a shared record and must NOT carry the
    /// cache-flush bit; SRV/TXT/A/AAAA are unique to this instance and must.
    /// </summary>
    private void AnnounceProfile(ServiceProfile? profile, string reason)
    {
        if (profile is null || !_running) return;

        try
        {
            var msg = new Message { QR = true, AA = true };

            msg.Answers.Add(new PTRRecord
            {
                Name       = profile.QualifiedServiceName,
                DomainName = profile.FullyQualifiedName,
                TTL        = SharedRecordTtl,
            });

            foreach (ResourceRecord r in profile.Resources)
            {
                switch (r)
                {
                    case TXTRecord txt:
                        msg.Answers.Add(new TXTRecord
                        {
                            Name    = txt.Name,
                            // Snapshot: Strings is swapped wholesale on update, so
                            // this either sees the old list or the new one, never a
                            // half-written one.
                            Strings = new List<string>(txt.Strings),
                            Class   = CacheFlushIn,
                            TTL     = txt.TTL,
                        });
                        break;

                    case SRVRecord srv:
                        msg.Answers.Add(new SRVRecord
                        {
                            Name     = srv.Name,
                            Priority = srv.Priority,
                            Weight   = srv.Weight,
                            Port     = srv.Port,
                            Target   = srv.Target,
                            Class    = CacheFlushIn,
                            TTL      = srv.TTL,
                        });
                        break;

                }
            }

            // The host's A/AAAA records are NOT in profile.Resources — Makaretu only
            // puts them there when a ServiceProfile is constructed with an explicit
            // address list, and Advertise() doesn't synthesise them either. They live
            // in the catalog under the SRV target, so pull them from there; otherwise
            // an address change (DHCP renewal) would never be flushed from iOS's cache.
            var srvTarget = profile.Resources.OfType<SRVRecord>().FirstOrDefault()?.Target;
            if (srvTarget is not null)
            {
                foreach (var node in _sd.NameServer.Catalog.Values)
                {
                    foreach (ResourceRecord r in node.Resources)
                    {
                        if (r is not AddressRecord addr || addr.Name != srvTarget) continue;

                        AddressRecord copy = AddressRecord.Create(addr.Name, addr.Address);
                        copy.Class = CacheFlushIn;
                        copy.TTL   = addr.TTL;
                        msg.Answers.Add(copy);
                    }
                }
            }

            _mdns.SendAnswer(msg, checkDuplicate: false);
            Log.Info("mDNS", $"Announced {profile.FullyQualifiedName} ({reason}, {msg.Answers.Count} records)");
        }
        catch (Exception ex)
        {
            Log.Error("mDNS", $"Announce of {profile.FullyQualifiedName} ({reason}) failed", ex);
        }
    }

    // ── Responder diagnostics ─────────────────────────────────────────────────

    /// <summary>
    /// Counts (never logs individually — iOS polls several times a second) queries
    /// that name one of our services. Runs on Makaretu's receive loop, so it must
    /// never throw.
    /// </summary>
    private void OnQueryReceived(object? sender, MessageEventArgs e)
    {
        try
        {
            foreach (Question q in e.Message.Questions)
            {
                string name = q.Name.ToString();
                if (name.IndexOf("_airplay._tcp", StringComparison.OrdinalIgnoreCase) < 0 &&
                    name.IndexOf("_raop._tcp",    StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                Interlocked.Increment(ref _queriesForUsTotal);
                Interlocked.Increment(ref _queriesForUsWindow);
                return;
            }
        }
        catch { /* diagnostics must never break the responder */ }
    }

    /// <summary>
    /// Stamps the RFC 6762 §10 lifetimes on every record Makaretu put in the
    /// catalog — including the PTRs that <c>Advertise</c> creates for us, which we
    /// otherwise never see. Called after both profiles are advertised, and logs the
    /// result so the TTL our records actually carry is on record in every run.
    ///
    /// TTL is not part of a record's rdata, so changing it in place does not
    /// disturb the hashes the catalog's record set is keyed on.
    /// </summary>
    private void NormaliseRecordTtls()
    {
        int shared = 0, host = 0;

        foreach (var node in _sd.NameServer.Catalog.Values)
        {
            foreach (ResourceRecord r in node.Resources)
            {
                if (r is AddressRecord) { r.TTL = HostRecordTtl;   host++;   }
                else                    { r.TTL = SharedRecordTtl; shared++; }
            }
        }

        Log.Info("mDNS",
            $"Record TTLs set: {shared} record(s) at {SharedRecordTtl.TotalSeconds:F0}s, " +
            $"{host} address record(s) at {HostRecordTtl.TotalSeconds:F0}s " +
            $"(Makaretu runtime defaults: {ResourceRecord.DefaultTTL.TotalSeconds:F0}s / " +
            $"{ResourceRecord.DefaultHostTTL.TotalSeconds:F0}s)");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AddTxt(ServiceProfile profile, string key, string value)
    {
        // ServiceProfile.Resources holds the TXT record; add key=value pairs.
        profile.AddProperty(key, value);
    }

    /// <summary>
    /// Rewrites one <c>key=value</c> entry of a profile's TXT record in place.
    ///
    /// The TXTRecord object is shared with Makaretu's catalog — the responder
    /// serialises that very instance when answering a query — so the whole Strings
    /// list is replaced by a single reference assignment rather than edited item by
    /// item. A concurrent responder therefore always serialises a complete list.
    /// </summary>
    private static void UpdateTxtProperty(ServiceProfile? profile, string key, string value)
    {
        if (profile is null) return;

        TXTRecord? txt = profile.Resources.OfType<TXTRecord>().FirstOrDefault();
        if (txt is null) return;

        string prefix  = key + "=";
        var    updated = new List<string>(txt.Strings);

        int i = updated.FindIndex(s => s.StartsWith(prefix, StringComparison.Ordinal));
        if (i >= 0) updated[i] = prefix + value;
        else        updated.Add(prefix + value);

        txt.Strings = updated;
    }

    /// <summary>
    /// Returns the MAC address of the first non-loopback, active Ethernet or
    /// Wi-Fi adapter. Used as the AirPlay device identifier.
    /// </summary>
    public static string GetMacAddress()
    {
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
                                         or NetworkInterfaceType.Tunnel)
                continue;

            byte[] macBytes = nic.GetPhysicalAddress().GetAddressBytes();
            if (macBytes.Length == 6)
                return string.Join(":", macBytes.Select(b => b.ToString("X2")));
        }

        // Fallback: generate a stable pseudo-MAC from the machine name.
        byte[] hash = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(Environment.MachineName));
        hash[0] = (byte)((hash[0] & 0xFE) | 0x02); // locally administered, unicast
        return string.Join(":", hash.Take(6).Select(b => b.ToString("X2")));
    }

    /// <summary>Converts "AA:BB:CC:DD:EE:FF" into a lowercase UUID-style string.</summary>
    public static string GuidFromMac(string mac)
    {
        string hex = mac.Replace(":", "").ToLowerInvariant().PadRight(32, '0');
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
    }
}
