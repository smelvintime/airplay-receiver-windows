using System;
using System.Runtime.InteropServices;

namespace AirPlayReceiver.Protocol;

/// <summary>
/// P/Invoke wrapper over the prebuilt native <c>playfair.dll</c>, which performs
/// Apple's FairPlay key-unwrap (the community "playfair" routine).
///
/// Given the 164-byte FairPlay key message (the <c>fp-setup</c> phase-2 request,
/// <see cref="FairPlayHandshake.KeyMessage"/>) and the 72-byte encrypted stream
/// key (<c>ekey</c> from <c>SETUP</c>), it returns the 16-byte AES key that —
/// combined with the pairing ECDH secret — derives the mirror stream key.
///
/// The DLL is cross-compiled from RPiPlay's lib/playfair sources (vendored under
/// vendor/playfair) and copied next to the executable at build time.
/// </summary>
public static class Playfair
{
    [DllImport("playfair.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void playfair_decrypt(byte[] message3, byte[] cipherText, byte[] keyOut);

    /// <param name="keyMessage">164-byte FairPlay key message (fp-setup phase 2 request).</param>
    /// <param name="ekey">72-byte encrypted stream key from SETUP.</param>
    /// <returns>The 16-byte decrypted AES key.</returns>
    public static byte[] DecryptKey(byte[] keyMessage, byte[] ekey)
    {
        if (keyMessage is not { Length: 164 })
            throw new ArgumentException("FairPlay key message must be 164 bytes", nameof(keyMessage));
        if (ekey is not { Length: 72 })
            throw new ArgumentException("ekey must be 72 bytes", nameof(ekey));

        byte[] keyOut = new byte[16];
        playfair_decrypt(keyMessage, ekey, keyOut);
        return keyOut;
    }
}
