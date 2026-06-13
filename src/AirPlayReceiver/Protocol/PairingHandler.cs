using System;
using System.Collections.Generic;
using AirPlayReceiver.Protocol;

namespace AirPlayReceiver.Protocol;

/// <summary>
/// Stub for the AirPlay pairing sub-protocol.
///
/// Full implementation requires:
///   1. SRP-6a (Secure Remote Password) for /pair-setup
///      — Use BouncyCastle: Org.BouncyCastle.Crypto.Agreement.SrpUtilities
///      — PIN for screen mirroring is typically empty / disabled on iOS 13+
///
///   2. Curve25519 ECDH + Ed25519 signature for /pair-verify
///      — Use BouncyCastle: Org.BouncyCastle.Crypto.Parameters.X25519PrivateKeyParameters
///
///   3. FairPlay / fp-setup (AirPlay 2 only, hardware-bound on real Apple devices)
///      — In open-source implementations (UxPlay) this is bypassed by advertising
///        the correct feature flags that indicate FairPlay is NOT required.
///
/// References:
///   • UxPlay lib/raop_handler.c  — HandlePairSetup / HandlePairVerify
///   • UxPlay lib/crypto.c        — srp_verifier, curve25519_donna
///   • HAP spec (pairing TLV8 tag definitions)
///   • https://openairplay.github.io/airplay-spec/pairing.html
/// </summary>
public sealed class PairingHandler
{
    // ── Pairing state ─────────────────────────────────────────────────────────

    /// <summary>
    /// AES-128-CBC key derived from the pairing session.
    /// Populated after /pair-verify completes.
    /// Passed to RtpReceiver for stream decryption.
    /// </summary>
    public byte[]? AesKey { get; private set; }

    /// <summary>AES-CBC initialisation vector (16 bytes).</summary>
    public byte[]? AesIv  { get; private set; }

    // ── /pair-setup ───────────────────────────────────────────────────────────

    /// <summary>
    /// SRP-6a exchange, step 1 and 2.
    ///
    /// Request body: TLV8 with method tag (0x00 = pair-setup) and state tag.
    /// Response body: TLV8 with SRP public key and salt (step 1) or proof (step 2).
    ///
    /// TODO: Replace stub with real SRP using BouncyCastle.
    /// </summary>
    public byte[] HandlePairSetup(RtspMessage msg)
    {
        System.Diagnostics.Debug.WriteLine("[Pairing] /pair-setup received (stub — returning 200 OK)");

        // Stub: return an empty 200 so iOS proceeds.
        // Real implementation must parse the TLV8 state field and respond
        // with the appropriate SRP step.
        return RtspMessage.BuildResponse(200, "OK", msg.CSeq,
            new Dictionary<string, string>
            {
                ["Content-Type"] = "application/octet-stream",
            },
            body: Array.Empty<byte>());
    }

    // ── /pair-verify ─────────────────────────────────────────────────────────

    /// <summary>
    /// Curve25519 ECDH + Ed25519 signature verification.
    ///
    /// Request body (step 1): client ephemeral Curve25519 public key (32 B)
    ///                         + client Ed25519 public key (32 B)
    /// Response body (step 1): server ephemeral Curve25519 public key (32 B)
    ///                          + Ed25519 signature over (serverPub || clientPub)
    ///
    /// After step 2 verifies the client's signature, derive the AES-CBC key:
    ///   sharedSecret = Curve25519(serverPriv, clientPub)
    ///   aesKey = SHA512(sharedSecret + "Pair-Verify-AES-Key")[0..15]
    ///   aesIv  = SHA512(sharedSecret + "Pair-Verify-AES-IV")[0..15]
    ///
    /// TODO: Replace stub with real implementation using BouncyCastle.
    /// </summary>
    public byte[] HandlePairVerify(RtspMessage msg)
    {
        System.Diagnostics.Debug.WriteLine("[Pairing] /pair-verify received (stub — returning 200 OK)");

        // Stub: leave AesKey/AesIv null. The real exchange (Curve25519 ECDH +
        // SHA-512 key derivation) must populate them; until then RtpReceiver
        // stays in its cleartext passthrough mode rather than decrypting with a
        // bogus all-zero key. When this is implemented, AirPlaySession picks the
        // keys up and hands them to RtpReceiver before RECORD starts the stream.

        return RtspMessage.BuildResponse(200, "OK", msg.CSeq,
            new Dictionary<string, string>
            {
                ["Content-Type"] = "application/octet-stream",
            },
            body: Array.Empty<byte>());
    }

    // ── /pair-add ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds the device to the persistent pairing database.
    /// Only needed for HomeKit-style persistent pairing (not required for screen mirroring).
    /// </summary>
    public byte[] HandlePairAdd(RtspMessage msg)
    {
        System.Diagnostics.Debug.WriteLine("[Pairing] /pair-add (stub)");
        return RtspMessage.BuildResponse(200, "OK", msg.CSeq);
    }

    // ── /fp-setup ─────────────────────────────────────────────────────────────

    /// <summary>
    /// FairPlay setup — hardware-bound on real Apple TV hardware.
    /// Open-source receivers bypass this by advertising feature flags that
    /// indicate FairPlay encryption is not required.
    /// If iOS still sends it, return a non-fatal error to fall back to AES.
    /// </summary>
    public byte[] HandleFairPlaySetup(RtspMessage msg)
    {
        System.Diagnostics.Debug.WriteLine("[Pairing] /fp-setup — returning 421 (not implemented)");
        return RtspMessage.BuildResponse(421, "Misdirected Request", msg.CSeq);
    }
}
