using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using System;
using System.IO;

namespace AirPlayReceiver.Protocol;

/// <summary>
/// The receiver's long-term cryptographic identity: a single Ed25519 keypair
/// used in three places that must agree on the same key —
///   • the <c>pk</c> TXT record advertised over mDNS,
///   • the public key returned from <c>POST /pair-setup</c>, and
///   • the signature produced during <c>POST /pair-verify</c>.
///
/// The keypair is persisted under LocalAppData so the device keeps a stable
/// identity across restarts (iOS remembers paired receivers by this key).
/// </summary>
public sealed class DeviceIdentity
{
    private const int Ed25519SeedSize = 32; // bytes

    public Ed25519PrivateKeyParameters EdPrivate { get; }
    public Ed25519PublicKeyParameters  EdPublic  { get; }

    /// <summary>The 32-byte Ed25519 public key.</summary>
    public byte[] PublicKeyBytes => EdPublic.GetEncoded();

    /// <summary>The public key as a 64-char lowercase hex string (for the mDNS <c>pk</c> TXT key).</summary>
    public string PublicKeyHex => Convert.ToHexString(PublicKeyBytes).ToLowerInvariant();

    private DeviceIdentity(Ed25519PrivateKeyParameters edPrivate)
    {
        EdPrivate = edPrivate;
        EdPublic  = edPrivate.GeneratePublicKey();
    }

    /// <summary>
    /// Loads the persisted identity, or generates and persists a new one on first run.
    /// Persistence failures are non-fatal — the identity just won't survive a restart.
    /// </summary>
    public static DeviceIdentity LoadOrCreate()
    {
        string path = IdentityFilePath();

        try
        {
            if (File.Exists(path))
            {
                byte[] seed = File.ReadAllBytes(path);
                if (seed.Length == Ed25519SeedSize)
                    return new DeviceIdentity(new Ed25519PrivateKeyParameters(seed, 0));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Identity] Failed to load identity: {ex.Message}");
        }

        var generated = new Ed25519PrivateKeyParameters(new SecureRandom());
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, generated.GetEncoded());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Identity] Failed to persist identity: {ex.Message}");
        }

        return new DeviceIdentity(generated);
    }

    /// <summary>Signs <paramref name="message"/> with the device's Ed25519 private key (64-byte signature).</summary>
    public byte[] Sign(byte[] message)
    {
        var signer = new Ed25519Signer();
        signer.Init(forSigning: true, EdPrivate);
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }

    private static string IdentityFilePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AirPlayReceiver",
            "device_identity.key");
}
