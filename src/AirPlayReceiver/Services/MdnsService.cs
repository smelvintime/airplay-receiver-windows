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
    // ── AirPlay feature flags ─────────────────────────────────────────────────
    //
    // Each bit enables a capability iOS checks before showing the device.
    // This value mirrors the defaults used by UxPlay and is known-good for
    // screen mirroring with iOS 14–17.
    //
    // Bit layout documented at:
    //   https://openairplay.github.io/airplay-spec/features.html
    //
    private const string AirPlayFeatures  = "0x527FFFF7,0x00000001";
    private const string AirPlayModel     = "AppleTV6,2";   // Spoofed as Apple TV 4K
    private const string AirPlayOsVersion = "14.0";         // tvOS version string

    // AirPlay status flags (0x04 = available, not busy)
    private const string AirPlayStatusFlags = "0x04";

    private readonly string     _deviceName;
    private readonly string     _deviceId;   // MAC-style: "AA:BB:CC:DD:EE:FF"
    private readonly int        _port;
    private readonly MulticastService _mdns;
    private readonly ServiceDiscovery _sd;
    private CancellationTokenSource?  _cts;

    // ── Construction ──────────────────────────────────────────────────────────

    /// <param name="deviceName">Friendly name shown in the Screen Mirroring list.</param>
    /// <param name="port">TCP port the RTSP server listens on (default 7000).</param>
    public MdnsService(string deviceName, int port = 7000)
    {
        _deviceName = deviceName;
        _port       = port;
        _deviceId   = GetMacAddress();

        _mdns = new MulticastService();
        _sd   = new ServiceDiscovery(_mdns);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        RegisterAirPlayService();
        RegisterRaopService();

        _mdns.Start();

        Console.WriteLine($"[mDNS] Advertising '{_deviceName}' on port {_port}  deviceid={_deviceId}");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        _mdns.Stop();
        Console.WriteLine("[mDNS] Stopped.");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _sd.Dispose();
        _mdns.Dispose();
    }

    // ── Service registration ──────────────────────────────────────────────────

    private void RegisterAirPlayService()
    {
        // _airplay._tcp TXT record keys required by iOS:
        //   deviceid   — MAC address of this receiver
        //   features   — 64-bit capability bitmask (hex, comma-separated hi/lo)
        //   flags      — status (0x04 = idle/available)
        //   model      — device model string (spoofed as Apple TV)
        //   pi         — persistent device GUID (use MAC for simplicity)
        //   pk         — Ed25519 public key (32 bytes, hex-encoded)
        //                  — required for AirPlay 2 pairing; placeholder here.
        //   srcvers    — AirPlay server version string

        var airplayProfile = new ServiceProfile(
            instanceName: _deviceName,
            serviceType:  "_airplay._tcp",
            port:         (ushort)_port
        );

        AddTxt(airplayProfile, "deviceid",  _deviceId);
        AddTxt(airplayProfile, "features",  AirPlayFeatures);
        AddTxt(airplayProfile, "flags",     AirPlayStatusFlags);
        AddTxt(airplayProfile, "model",     AirPlayModel);
        AddTxt(airplayProfile, "pi",        GuidFromMac(_deviceId));
        AddTxt(airplayProfile, "pk",        PlaceholderPublicKey());
        AddTxt(airplayProfile, "srcvers",   "220.68");
        AddTxt(airplayProfile, "osvers",    AirPlayOsVersion);
        AddTxt(airplayProfile, "vv",        "2");  // vendor version

        _sd.Advertise(airplayProfile);
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
            serviceType:  "_raop._tcp",
            port:         (ushort)_port
        );

        // RAOP TXT keys (subset relevant to screen mirroring):
        AddTxt(raopProfile, "et",   "0,3,5");   // encryption types: none, RSA, FairPlay
        AddTxt(raopProfile, "md",   "0,1,2");   // metadata types
        AddTxt(raopProfile, "vs",   "220.68");  // server version
        AddTxt(raopProfile, "da",   "true");    // digest auth
        AddTxt(raopProfile, "sr",   "44100");   // sample rate (audio)
        AddTxt(raopProfile, "ss",   "16");      // sample size
        AddTxt(raopProfile, "ch",   "2");       // channels
        AddTxt(raopProfile, "vn",   "65537");
        AddTxt(raopProfile, "tp",   "UDP");
        AddTxt(raopProfile, "am",   AirPlayModel);
        AddTxt(raopProfile, "pk",   PlaceholderPublicKey());

        _sd.Advertise(raopProfile);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AddTxt(ServiceProfile profile, string key, string value)
    {
        // ServiceProfile.Resources holds the TXT record; add key=value pairs.
        profile.AddProperty(key, value);
    }

    /// <summary>
    /// Returns the MAC address of the first non-loopback, active Ethernet or
    /// Wi-Fi adapter. Used as the AirPlay device identifier.
    /// </summary>
    private static string GetMacAddress()
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
    private static string GuidFromMac(string mac)
    {
        string hex = mac.Replace(":", "").ToLowerInvariant().PadRight(32, '0');
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
    }

    /// <summary>
    /// 32-byte all-zero public key (hex).  Replace with a real Ed25519 key
    /// generated during first run and persisted to app settings.  iOS 14+
    /// validates this during pair-verify; a zeros key is accepted by iOS
    /// when pairing PIN is disabled (screen-mirroring-only mode).
    /// </summary>
    private static string PlaceholderPublicKey()
        => new string('0', 64);
}
