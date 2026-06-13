using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AirPlayReceiver.Media;

/// <summary>
/// Receives RTP packets on UDP, reassembles H.264/HEVC NAL units, decrypts
/// the payload, and feeds complete access units to <see cref="VideoDecoder"/>.
///
/// RTP packet layout for AirPlay video (RFC 3550 + Apple extensions):
///   [0]  V=2, P=0, X=0, CC=0
///   [1]  M bit, PT (96=H.264, 110=HEVC)
///   [2–3]  Sequence number (big-endian)
///   [4–7]  Timestamp (big-endian, 90kHz clock)
///   [8–11] SSRC
///   [12+]  Payload
///
/// H.264 payload fragmentation follows RFC 6184 (FU-A units for large NALUs).
/// AirPlay 1 encrypts the payload with AES-128-CBC (key from PairingHandler).
/// AirPlay 2 uses ChaCha20-Poly1305 (key from SETUP binary plist body).
///
/// Reference:
///   UxPlay lib/stream.c  — rtp_session_recv / stream_data_*
///   RFC 6184             — RTP Payload Format for H.264 Video
/// </summary>
public sealed class RtpReceiver : IAsyncDisposable
{
    // ── Port selection ────────────────────────────────────────────────────────

    /// <summary>UDP port for incoming RTP video data packets.</summary>
    public int VideoDataPort    { get; } = 7011;

    /// <summary>UDP port for RTCP control packets.</summary>
    public int VideoControlPort { get; } = 7012;

    // ── Dependencies ──────────────────────────────────────────────────────────

    private readonly VideoDecoder _decoder;

    // ── Decryption keys (set after pairing) ──────────────────────────────────

    private byte[]? _aesKey;
    private byte[]? _aesIv;

    // ── State ─────────────────────────────────────────────────────────────────

    private UdpClient?                _udpData;
    private CancellationTokenSource?  _cts;
    private Task?                     _receiveLoop;

    // FU-A reassembly buffer
    private byte[]? _fuBuffer;
    private int     _fuLength;
    private byte    _fuNalHeader;
    private ushort  _lastSeq;

    // ── Construction ──────────────────────────────────────────────────────────

    public RtpReceiver(VideoDecoder decoder)
    {
        _decoder = decoder;
    }

    // ── Key injection ─────────────────────────────────────────────────────────

    public void SetDecryptionKeys(byte[] aesKey, byte[] aesIv)
    {
        _aesKey = (byte[])aesKey.Clone();
        _aesIv  = (byte[])aesIv.Clone();
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public Task StartAsync()
    {
        _cts      = new CancellationTokenSource();
        _udpData  = new UdpClient(VideoDataPort);

        Console.WriteLine($"[RTP] Listening on UDP {VideoDataPort}");

        _receiveLoop = ReceiveLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _udpData?.Close();
        if (_receiveLoop is not null)
            await _receiveLoop.ConfigureAwait(false);
        Console.WriteLine("[RTP] Stopped.");
    }

    public void Flush()
    {
        _fuBuffer = null;
        _fuLength = 0;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    // ── Receive loop ──────────────────────────────────────────────────────────

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udpData!.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException ex)
            {
                Console.WriteLine($"[RTP] Socket error: {ex.Message}");
                break;
            }

            ProcessPacket(result.Buffer, result.Buffer.Length);
        }
    }

    // ── Packet processing ─────────────────────────────────────────────────────

    private void ProcessPacket(byte[] data, int length)
    {
        if (length < 12) return; // Too short to contain an RTP header

        // ── Parse RTP fixed header (RFC 3550) ─────────────────────────────────
        // byte[0]: V(2) P(1) X(1) CC(4)
        // byte[1]: M(1) PT(7)
        int version   = (data[0] >> 6) & 0x03;
        if (version != 2) return;

        bool marker    = (data[1] & 0x80) != 0;
        int  payloadType = data[1] & 0x7F;
        ushort seq     = (ushort)((data[2] << 8) | data[3]);
        uint  timestamp = (uint)((data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7]);
        // SSRC at [8–11] ignored for single-stream use

        int payloadOffset = 12;

        // Handle CSRC list (CC field)
        int cc = data[0] & 0x0F;
        payloadOffset += cc * 4;

        // Handle RTP extension header (X bit)
        if ((data[0] & 0x10) != 0 && payloadOffset + 4 <= length)
        {
            int extLen = ((data[payloadOffset + 2] << 8) | data[payloadOffset + 3]) * 4;
            payloadOffset += 4 + extLen;
        }

        if (payloadOffset >= length) return;

        // ── Decrypt payload ───────────────────────────────────────────────────
        byte[] payload = DecryptPayload(data, payloadOffset, length - payloadOffset);

        // ── Payload type dispatch ─────────────────────────────────────────────
        // PT 96 = H.264 (most common for AirPlay mirroring)
        // PT 110 = HEVC (AirPlay 2, iPhone 11+)
        if (payloadType == 96)
            ProcessH264Payload(payload, marker, seq);
        else if (payloadType == 110)
            ProcessHevcPayload(payload, marker, seq);
    }

    // ── H.264 NAL reassembly (RFC 6184) ──────────────────────────────────────

    private void ProcessH264Payload(byte[] payload, bool marker, ushort seq)
    {
        if (payload.Length == 0) return;

        byte nalHeader = payload[0];
        int  nalType   = nalHeader & 0x1F;

        switch (nalType)
        {
            case 1: // Non-IDR slice
            case 5: // IDR slice (keyframe)
            case 7: // SPS
            case 8: // PPS
                // Single NAL unit — wrap in Annex-B start code and submit.
                SubmitNalUnit(payload, 0, payload.Length);
                break;

            case 24: // STAP-A (multiple NALUs in one packet)
                ProcessStapA(payload);
                break;

            case 28: // FU-A (fragmented NALU)
                ProcessFuA(payload, marker);
                break;

            default:
                Console.WriteLine($"[RTP H.264] Unhandled NAL type {nalType}");
                break;
        }
    }

    private void ProcessStapA(byte[] payload)
    {
        // STAP-A: [1 byte type=24] [2B size] [NAL] [2B size] [NAL] …
        int offset = 1;
        while (offset + 2 <= payload.Length)
        {
            int size = (payload[offset] << 8) | payload[offset + 1];
            offset += 2;
            if (offset + size > payload.Length) break;
            SubmitNalUnit(payload, offset, size);
            offset += size;
        }
    }

    private void ProcessFuA(byte[] payload, bool marker)
    {
        // FU-A header byte: S(1) E(1) R(1) TYPE(5)
        if (payload.Length < 2) return;

        byte fuHeader = payload[1];
        bool start = (fuHeader & 0x80) != 0;
        bool end   = (fuHeader & 0x40) != 0;
        byte nalType = (byte)(fuHeader & 0x1F);

        if (start)
        {
            // Reconstruct the original NAL header from FU indicator + type.
            _fuNalHeader = (byte)((payload[0] & 0xE0) | nalType);
            _fuBuffer    = ArrayPool<byte>.Shared.Rent(256 * 1024);
            _fuLength    = 0;

            _fuBuffer[_fuLength++] = _fuNalHeader;
        }

        if (_fuBuffer is null) return; // start packet was lost; drop

        int fragmentLen = payload.Length - 2;
        if (_fuLength + fragmentLen > _fuBuffer.Length)
        {
            // Buffer overflow guard — discard and reset
            ArrayPool<byte>.Shared.Return(_fuBuffer);
            _fuBuffer = null;
            return;
        }

        Buffer.BlockCopy(payload, 2, _fuBuffer, _fuLength, fragmentLen);
        _fuLength += fragmentLen;

        if (end)
        {
            SubmitNalUnit(_fuBuffer, 0, _fuLength);
            ArrayPool<byte>.Shared.Return(_fuBuffer);
            _fuBuffer = null;
        }
    }

    private void ProcessHevcPayload(byte[] payload, bool marker, ushort seq)
    {
        // HEVC RTP payload format (RFC 7798) — simplified single-NAL path.
        // Full implementation must handle AP (aggregation) and FU (fragmentation)
        // packet types in the same manner as H.264 above.
        if (payload.Length > 0)
            SubmitNalUnit(payload, 0, payload.Length);
    }

    // ── NAL submission ────────────────────────────────────────────────────────

    private static readonly byte[] AnnexBStartCode = { 0x00, 0x00, 0x00, 0x01 };

    private void SubmitNalUnit(byte[] data, int offset, int length)
    {
        // Prepend Annex-B start code and hand to the decoder.
        byte[] annexB = new byte[4 + length];
        AnnexBStartCode.CopyTo(annexB, 0);
        Buffer.BlockCopy(data, offset, annexB, 4, length);

        _decoder.SubmitPacket(annexB);
    }

    // ── Decryption ────────────────────────────────────────────────────────────

    private byte[] DecryptPayload(byte[] data, int offset, int length)
    {
        // AirPlay 1: AES-128-CBC with key/IV from PairingHandler.
        // Only the first (length & ~0xF) bytes are encrypted; the remainder
        // (length % 16 bytes) is appended cleartext.
        //
        // AirPlay 2: ChaCha20-Poly1305 — key from SETUP binary plist body.
        //
        // TODO: plug in real decryption once PairingHandler is complete.
        // For now, treat the payload as cleartext (works for unencrypted streams).

        if (_aesKey is null || _aesIv is null)
        {
            // No keys yet — pass through (unencrypted, or pairing stub in use).
            byte[] clear = new byte[length];
            Buffer.BlockCopy(data, offset, clear, 0, length);
            return clear;
        }

        // AES-CBC decrypt (in-place; only the aligned block portion)
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key     = _aesKey;
        aes.IV      = _aesIv;
        aes.Mode    = System.Security.Cryptography.CipherMode.CBC;
        aes.Padding = System.Security.Cryptography.PaddingMode.None;

        int alignedLen  = length & ~0xF;
        int remainingLen = length - alignedLen;
        byte[] decrypted = new byte[length];

        if (alignedLen > 0)
        {
            using var decryptor = aes.CreateDecryptor();
            decryptor.TransformBlock(data, offset, alignedLen, decrypted, 0);
        }

        // Copy remaining cleartext bytes
        if (remainingLen > 0)
            Buffer.BlockCopy(data, offset + alignedLen, decrypted, alignedLen, remainingLen);

        // IV for the next packet = last 16 bytes of the ciphertext (CBC chaining)
        if (alignedLen >= 16)
            Buffer.BlockCopy(data, offset + alignedLen - 16, _aesIv, 0, 16);

        return decrypted;
    }
}
