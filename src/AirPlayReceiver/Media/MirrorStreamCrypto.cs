using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using System;
using System.Security.Cryptography;
using System.Text;

namespace AirPlayReceiver.Media;

/// <summary>
/// Derives the AirPlay mirror stream AES-128 key/IV and performs the continuous
/// AES-CTR decryption of the video (type 0) frames.
///
/// Key derivation (RPiPlay lib/mirror_buffer.c → mirror_buffer_init_aes):
///   eaeskey   = SHA512(aeskey[0..16] || ecdhSecret[0..32])[0..16]
///   streamKey = SHA512("AirPlayStreamKey"+streamConnectionID || eaeskey)[0..16]
///   streamIv  = SHA512("AirPlayStreamIV" +streamConnectionID || eaeskey)[0..16]
///
/// The frames form a single continuous CTR keystream (only type-0 video frames
/// advance it), so one instance decrypts the whole session in order.
/// </summary>
public sealed class MirrorStreamCrypto
{
    private readonly IBlockCipher _aes;             // AES-ECB, used to produce CTR keystream blocks
    private readonly byte[]       _counter   = new byte[16];
    private readonly byte[]       _keystream = new byte[16];
    private int                   _ksPos     = 16;  // force a fresh keystream block on first byte

    public MirrorStreamCrypto(byte[] aeskey, byte[] ecdhSecret, ulong streamConnectionID)
    {
        // eaeskey = SHA512(aeskey[0..16] || ecdh[0..32])[0..16]
        byte[] eaeskey = Sha512(Concat(Slice(aeskey, 16), Slice(ecdhSecret, 32)));

        byte[] streamKey = Sha512(Concat(Encoding.ASCII.GetBytes("AirPlayStreamKey" + streamConnectionID),
                                         Slice(eaeskey, 16)));
        byte[] streamIv  = Sha512(Concat(Encoding.ASCII.GetBytes("AirPlayStreamIV"  + streamConnectionID),
                                         Slice(eaeskey, 16)));

        _aes = new AesEngine();
        _aes.Init(forEncryption: true, new KeyParameter(Slice(streamKey, 16)));
        Array.Copy(streamIv, _counter, 16);
    }

    /// <summary>Decrypts <paramref name="length"/> bytes of <paramref name="data"/> in place, continuing the CTR stream.</summary>
    public void Decrypt(byte[] data, int length)
    {
        for (int i = 0; i < length; i++)
        {
            if (_ksPos == 16)
            {
                _aes.ProcessBlock(_counter, 0, _keystream, 0);
                IncrementCounter();
                _ksPos = 0;
            }
            data[i] ^= _keystream[_ksPos++];
        }
    }

    private void IncrementCounter()
    {
        for (int j = 15; j >= 0; j--)
            if (++_counter[j] != 0) break; // stop at first non-carry
    }

    private static byte[] Sha512(byte[] input) => SHA512.HashData(input);

    private static byte[] Slice(byte[] src, int length)
    {
        byte[] r = new byte[length];
        Array.Copy(src, r, length);
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
