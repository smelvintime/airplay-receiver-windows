using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace AirPlayReceiver.Protocol;

/// <summary>
/// Implements AirPlay's classic Ed25519 / Curve25519 pairing — the scheme iOS
/// uses for code-less screen mirroring (the same one RPiPlay/UxPlay implement).
///
///   POST /pair-setup   →  returns the receiver's 32-byte Ed25519 public key.
///   POST /pair-verify  →  two-step ECDH + signature exchange:
///       step 1 (flag byte = 1): client sends its Curve25519 + Ed25519 public
///                               keys; we reply with our ephemeral Curve25519
///                               public key + an AES-CTR-encrypted signature.
///       step 2 (flag byte = 0): client sends its encrypted signature; we
///                               decrypt and verify it.
///
/// Key derivation (must match iOS byte-for-byte):
///   sharedSecret = X25519(ourEphemeralPriv, clientCurvePub)
///   aesKey = SHA512("Pair-Verify-AES-Key" || sharedSecret)[0..15]
///   aesIv  = SHA512("Pair-Verify-AES-IV"  || sharedSecret)[0..15]
///   signature(step1) = Ed25519(ourCurvePub || clientCurvePub)
///   the client's signature in step 2 is over (clientCurvePub || ourCurvePub)
///
/// The AES-128-CTR keystream is logically one stream across both steps: step 1
/// uses bytes [0..63] to encrypt our signature, so step 2 decrypts the client's
/// signature with bytes [64..127] (we re-key and skip the first 64 bytes).
///
/// Reference: RPiPlay lib/pairing.c, UxPlay lib/pairing.c
/// </summary>
public sealed class PairingHandler
{
    private const string SaltKey = "Pair-Verify-AES-Key";
    private const string SaltIv  = "Pair-Verify-AES-IV";

    private readonly DeviceIdentity _identity;

    /// <summary>FairPlay (fp-setup) handshake state for this session.</summary>
    public FairPlayHandshake FairPlay { get; } = new();

    // ── pair-verify per-session state (set in step 1, used in step 2) ─────────
    private byte[]? _verifyAesKey;
    private byte[]? _verifyAesIv;
    private byte[]? _serverCurvePub;
    private byte[]? _clientCurvePub;
    private byte[]? _clientEdPub;

    // ── Stream decryption keys (populated later by SETUP; null for now) ───────
    public byte[]? AesKey { get; private set; }
    public byte[]? AesIv  { get; private set; }

    /// <summary>The X25519 shared secret from pair-verify, reused to derive the mirror stream key.</summary>
    public byte[]? EcdhSecret { get; private set; }

    public PairingHandler(DeviceIdentity identity) => _identity = identity;

    // ── /pair-setup ───────────────────────────────────────────────────────────

    /// <summary>
    /// Classic pair-setup: respond with our raw 32-byte Ed25519 public key.
    /// (The 32-byte request body is the client's key, which this scheme ignores.)
    /// </summary>
    public byte[] HandlePairSetup(RtspMessage msg)
    {
        System.Diagnostics.Debug.WriteLine("[Pairing] /pair-setup → returning 32-byte Ed25519 public key");
        return RtspMessage.BuildResponse(200, "OK", msg.CSeq,
            OctetStreamHeaders(),
            body: _identity.PublicKeyBytes);
    }

    // ── /pair-verify ─────────────────────────────────────────────────────────

    public byte[] HandlePairVerify(RtspMessage msg)
    {
        byte[] body = msg.Body ?? Array.Empty<byte>();
        if (body.Length < 4)
            return RtspMessage.BuildResponse(400, "Bad Request", msg.CSeq);

        // First byte non-zero ⇒ step 1 (handshake); zero ⇒ step 2 (finish).
        return body[0] != 0
            ? HandlePairVerifyStep1(msg, body)
            : HandlePairVerifyStep2(msg, body);
    }

    private byte[] HandlePairVerifyStep1(RtspMessage msg, byte[] body)
    {
        // Layout: [0]=flag(1) [1..3]=reserved [4..35]=client Curve25519 pub
        //         [36..67]=client Ed25519 pub
        if (body.Length < 4 + 32 + 32)
            return RtspMessage.BuildResponse(400, "Bad Request", msg.CSeq);

        _clientCurvePub = body[4..36];
        _clientEdPub    = body[36..68];

        // Our ephemeral Curve25519 (X25519) keypair.
        var gen = new X25519KeyPairGenerator();
        gen.Init(new X25519KeyGenerationParameters(new SecureRandom()));
        var kp         = gen.GenerateKeyPair();
        var serverPriv = (X25519PrivateKeyParameters)kp.Private;
        _serverCurvePub = ((X25519PublicKeyParameters)kp.Public).GetEncoded();

        // Shared secret = X25519(ourPriv, clientPub).
        var agreement = new X25519Agreement();
        agreement.Init(serverPriv);
        byte[] shared = new byte[agreement.AgreementSize];
        agreement.CalculateAgreement(new X25519PublicKeyParameters(_clientCurvePub, 0), shared, 0);
        EcdhSecret = shared;

        _verifyAesKey = DeriveKey(SaltKey, shared);
        _verifyAesIv  = DeriveKey(SaltIv,  shared);

        // Sign (ourCurvePub || clientCurvePub) and encrypt with AES-CTR bytes [0..63].
        byte[] signature   = _identity.Sign(Concat(_serverCurvePub, _clientCurvePub));
        byte[] encrypted   = AesCtr(_verifyAesKey, _verifyAesIv, signature, skipBytes: 0);
        byte[] responseBody = Concat(_serverCurvePub, encrypted); // 32 + 64 = 96 bytes

        System.Diagnostics.Debug.WriteLine("[Pairing] /pair-verify step 1 → 96-byte response");
        return RtspMessage.BuildResponse(200, "OK", msg.CSeq, OctetStreamHeaders(), responseBody);
    }

    private byte[] HandlePairVerifyStep2(RtspMessage msg, byte[] body)
    {
        if (_verifyAesKey is null || _verifyAesIv is null ||
            _serverCurvePub is null || _clientCurvePub is null || _clientEdPub is null)
        {
            System.Diagnostics.Debug.WriteLine("[Pairing] /pair-verify step 2 arrived before step 1");
            return RtspMessage.BuildResponse(400, "Bad Request", msg.CSeq);
        }

        // Layout: [0]=0 [1..3]=reserved [4..67]=encrypted client signature (64).
        if (body.Length < 4 + 64)
            return RtspMessage.BuildResponse(400, "Bad Request", msg.CSeq);

        byte[] encryptedClientSig = body[4..68];
        // Decrypt with keystream bytes [64..127] (step 1 consumed [0..63]).
        byte[] clientSig = AesCtr(_verifyAesKey, _verifyAesIv, encryptedClientSig, skipBytes: 64);

        bool ok = VerifyEd25519(_clientEdPub, Concat(_clientCurvePub, _serverCurvePub), clientSig);
        System.Diagnostics.Debug.WriteLine(ok
            ? "[Pairing] /pair-verify step 2 → signature OK, pairing complete"
            : "[Pairing] /pair-verify step 2 → signature verification FAILED");

        // iOS expects an empty 200 OK to finish the handshake either way.
        return RtspMessage.BuildResponse(200, "OK", msg.CSeq, OctetStreamHeaders(), Array.Empty<byte>());
    }

    // ── /pair-add ─────────────────────────────────────────────────────────────

    public byte[] HandlePairAdd(RtspMessage msg)
    {
        System.Diagnostics.Debug.WriteLine("[Pairing] /pair-add (no-op)");
        return RtspMessage.BuildResponse(200, "OK", msg.CSeq);
    }

    // ── /fp-setup ─────────────────────────────────────────────────────────────

    public byte[] HandleFairPlaySetup(RtspMessage msg)
    {
        byte[] body = msg.Body ?? Array.Empty<byte>();
        byte[]? response = FairPlay.Process(body);

        if (response is null)
        {
            System.Diagnostics.Debug.WriteLine($"[Pairing] /fp-setup unrecognised (body={body.Length}B)");
            return RtspMessage.BuildResponse(400, "Bad Request", msg.CSeq);
        }

        System.Diagnostics.Debug.WriteLine(
            $"[Pairing] /fp-setup → {response.Length}-byte response (request {body.Length}B)");
        return RtspMessage.BuildResponse(200, "OK", msg.CSeq, OctetStreamHeaders(), response);
    }

    // ── Crypto helpers ────────────────────────────────────────────────────────

    /// <summary>SHA-512 of (salt || sharedSecret), truncated to a 16-byte AES-128 key.</summary>
    private static byte[] DeriveKey(string salt, byte[] sharedSecret)
    {
        byte[] hash = SHA512.HashData(Concat(Encoding.ASCII.GetBytes(salt), sharedSecret));
        byte[] key  = new byte[16];
        Array.Copy(hash, key, 16);
        return key;
    }

    /// <summary>
    /// AES-128-CTR over <paramref name="data"/>, discarding the first
    /// <paramref name="skipBytes"/> bytes of keystream first. Encrypt and decrypt
    /// are the same operation in CTR mode.
    /// </summary>
    private static byte[] AesCtr(byte[] key, byte[] iv, byte[] data, int skipBytes)
    {
        var cipher = new SicBlockCipher(new AesEngine());
        cipher.Init(forEncryption: true, new ParametersWithIV(new KeyParameter(key), iv));

        int    totalBlocks = (skipBytes + data.Length + 15) / 16;
        byte[] zero        = new byte[16];
        byte[] keystream   = new byte[totalBlocks * 16];
        for (int b = 0; b < totalBlocks; b++)
            cipher.ProcessBlock(zero, 0, keystream, b * 16);

        byte[] result = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
            result[i] = (byte)(data[i] ^ keystream[skipBytes + i]);
        return result;
    }

    private static bool VerifyEd25519(byte[] publicKey, byte[] message, byte[] signature)
    {
        var verifier = new Ed25519Signer();
        verifier.Init(forSigning: false, new Ed25519PublicKeyParameters(publicKey, 0));
        verifier.BlockUpdate(message, 0, message.Length);
        return verifier.VerifySignature(signature);
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        byte[] r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }

    private static Dictionary<string, string> OctetStreamHeaders()
        => new() { ["Content-Type"] = "application/octet-stream" };
}
