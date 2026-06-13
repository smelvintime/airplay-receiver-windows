using System;

namespace AirPlayReceiver.Protocol;

/// <summary>
/// Static description of this receiver, reported to iOS in the <c>GET /info</c>
/// response. The values here must be consistent with what mDNS advertises
/// (deviceID, features, pk) and tell iOS — via <c>displays</c> — what resolution
/// and frame rate to encode the mirror stream at.
/// </summary>
public sealed class DeviceInfo
{
    /// <summary>MAC-style hardware address, e.g. "AA:BB:CC:DD:EE:FF".</summary>
    public string DeviceId { get; init; } = "";

    /// <summary>Friendly name shown on the iPhone.</summary>
    public string Name { get; init; } = "";

    /// <summary>32-byte Ed25519 public key (same identity used for pairing / mDNS pk).</summary>
    public byte[] PublicKey { get; init; } = Array.Empty<byte>();

    /// <summary>64-bit AirPlay features bitmask (matches the mDNS "features" TXT value).</summary>
    public long Features { get; init; }

    public string Model         { get; init; } = "AppleTV6,2";
    public string SourceVersion { get; init; } = "220.68";

    /// <summary>Persistent device GUID string.</summary>
    public string Pi { get; init; } = "";

    // Mirror display geometry advertised to iOS.
    public int DisplayWidth  { get; init; } = 1920;
    public int DisplayHeight { get; init; } = 1080;
    public int MaxFps        { get; init; } = 60;
}
