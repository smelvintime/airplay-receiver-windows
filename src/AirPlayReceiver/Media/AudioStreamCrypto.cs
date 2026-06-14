using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using System;
using System.Security.Cryptography;
using System.Text;

namespace AirPlayReceiver.Media;

/// <summary>
/// Decrypts the AirPlay screen-mirroring <b>audio</b> stream (RTSP stream type 96,
/// AAC-ELD over UDP/RTP).
///
/// Unlike the video mirror stream (a single continuous AES-CTR keystream — see
/// <see cref="MirrorStreamCrypto"/>), each audio RTP payload is decrypted
/// independently with <b>AES-128-CBC</b>, re-initialising the IV to the per-stream
/// IV (<c>eiv</c>) at the start of every packet. Only whole 16-byte blocks are
/// encrypted; any trailing bytes (payload length not a multiple of 16) are left in
/// the clear and copied through verbatim — this is the classic RAOP behaviour
/// (RPiPlay <c>lib/raop_buffer.c</c> → <c>raop_buffer_decrypt</c>).
///
/// Key derivation matches the mirror video path, since AirPlay 2 mirroring keys
/// both streams off the FairPlay key + the pair-verify ECDH shared secret:
///   eaeskey = SHA512(aeskey[0..16] || ecdhSecret[0..32])[0..16]
/// When no ECDH secret is available we fall back to the raw FairPlay key.
/// </summary>
public sealed class AudioStreamCrypto
{
    private readonly byte[] _key;          // AES-128 key (16 bytes)
    private readonly byte[] _iv;           // per-stream IV (16 bytes), reset each packet

    public AudioStreamCrypto(byte[] aeskey, byte[]? ecdhSecret, byte[] eiv)
    {
        if (aeskey is null) throw new ArgumentNullException(nameof(aeskey));
        if (eiv    is null) throw new ArgumentNullException(nameof(eiv));

        // AirPlay 2 mirroring derives the audio key the same way as the video key:
        // SHA512(aeskey || ecdh)[0..16]. Without an ECDH secret (legacy / unpaired),
        // use the FairPlay-decrypted key directly.
        _key = ecdhSecret is { Length: > 0 }
            ? Slice(Sha512(Concat(Slice(aeskey, 16), Slice(ecdhSecret, 32))), 16)
            : Slice(aeskey, 16);

        _iv = Slice(eiv, 16);
    }

    /// <summary>
    /// Decrypts the first <paramref name="length"/> bytes of <paramref name="data"/>
    /// in place. The IV is reset to the stream IV for each call, so packets may be
    /// decrypted in any order. Trailing bytes beyond the last full 16-byte block are
    /// left untouched (sent in the clear by the source).
    /// </summary>
    public void Decrypt(byte[] data, int length)
    {
        int blocksLen = length & ~0xf;   // floor to 16-byte boundary
        if (blocksLen == 0) return;      // nothing to decrypt; tail stays in the clear

        var cbc = new CbcBlockCipher(new AesEngine());
        cbc.Init(forEncryption: false, new ParametersWithIV(new KeyParameter(_key), _iv));

        for (int off = 0; off < blocksLen; off += 16)
            cbc.ProcessBlock(data, off, data, off);
    }

    private static byte[] Sha512(byte[] input) => SHA512.HashData(input);

    private static byte[] Slice(byte[] src, int length)
    {
        byte[] r = new byte[length];
        Array.Copy(src, r, Math.Min(length, src.Length));
        return r;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        byte[] r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }
}
