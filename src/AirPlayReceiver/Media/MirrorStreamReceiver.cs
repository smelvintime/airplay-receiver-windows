using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AirPlayReceiver.Media;

/// <summary>
/// Receives the AirPlay 2 screen-mirroring video stream over a TCP connection,
/// decrypts the frames (continuous AES-128-CTR) and feeds Annex-B H.264 to the
/// decoder.
///
/// Frame framing (RPiPlay lib/raop_rtp_mirror.c): a 128-byte header — payload
/// length is a little-endian int32 at offset 0, payload type a little-endian
/// int16 at offset 4 — followed by <c>payloadLength</c> bytes.
///
/// Payload types we handle:
///   0 = encrypted H.264 video (AVCC length-prefixed NALUs) → decrypt → Annex-B → decode
///   1 = codec config (avcC: SPS/PPS), cleartext → Annex-B → decode
/// Other types (2 heartbeat, 5, …) are ignored, matching RPiPlay; only type-0
/// frames advance the CTR keystream.
/// </summary>
public sealed class MirrorStreamReceiver : IAsyncDisposable
{
    private const int HeaderSize = 128;
    private static readonly byte[] StartCode = { 0x00, 0x00, 0x00, 0x01 };

    private TcpListener?             _listener;
    private CancellationTokenSource? _cts;
    private Task?                    _acceptLoop;

    private MirrorStreamCrypto? _crypto;   // null → no decryption (frames only logged)
    private VideoDecoder?       _decoder;  // null → no decode
    private bool                _firstFrameLogged;
    private byte[]?             _spsPps;   // cached Annex-B SPS+PPS, prepended to each IDR

    /// <summary>The TCP port the OS assigned; returned to iOS in the SETUP response.</summary>
    public int Port { get; private set; }

    /// <summary>Supplies the per-stream decryption keys and the decoder to feed. Call before <see cref="Start"/>.</summary>
    public void Configure(MirrorStreamCrypto? crypto, VideoDecoder? decoder)
    {
        _crypto  = crypto;
        _decoder = decoder;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        System.Diagnostics.Debug.WriteLine(
            $"[Mirror] Listening for video on TCP {Port} (decrypt={_crypto is not null}, decode={_decoder is not null})");
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
        System.Diagnostics.Debug.WriteLine("[Mirror] Stopped.");
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }

            System.Diagnostics.Debug.WriteLine($"[Mirror] Video connection from {client.Client.RemoteEndPoint}");
            _ = ReceiveLoopAsync(client, ct);
        }
    }

    private async Task ReceiveLoopAsync(TcpClient client, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        try
        {
            using var stream = client.GetStream();
            while (!ct.IsCancellationRequested)
            {
                if (!await ReadFullyAsync(stream, header, HeaderSize, ct)) break;

                int payloadLength = BitConverter.ToInt32(header, 0);   // little-endian
                int payloadType   = BitConverter.ToUInt16(header, 4) & 0xFF;

                if (payloadLength < 0 || payloadLength > 16 * 1024 * 1024)
                {
                    System.Diagnostics.Debug.WriteLine($"[Mirror] Bogus payload length {payloadLength}; dropping connection");
                    break;
                }

                byte[] payload = new byte[payloadLength];
                if (payloadLength > 0 && !await ReadFullyAsync(stream, payload, payloadLength, ct)) break;

                switch (payloadType)
                {
                    case 0: HandleVideoFrame(payload); break;
                    case 1: HandleCodecConfig(payload); break;
                    default: break; // heartbeat / other — ignore
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"[Mirror] Video connection ended: {ex.Message}");
        }
        finally
        {
            client.Dispose();
        }
    }

    // ── Video (type 0): decrypt, AVCC → Annex-B, decode ───────────────────────

    private void HandleVideoFrame(byte[] payload)
    {
        if (payload.Length == 0) return;

        _crypto?.Decrypt(payload, payload.Length);

        // Convert AVCC (4-byte big-endian NAL lengths) to Annex-B start codes in place.
        if (!ConvertAvccToAnnexBInPlace(payload))
        {
            // Malformed after decryption almost always means the key is wrong.
            System.Diagnostics.Debug.WriteLine("[Mirror] video frame not valid AVCC after decrypt (key mismatch?)");
            return;
        }

        int firstNalType = payload.Length > 4 ? payload[4] & 0x1F : -1;

        if (!_firstFrameLogged)
        {
            _firstFrameLogged = true;
            System.Diagnostics.Debug.WriteLine(
                $"[Mirror] first video frame decrypted OK ({payload.Length}B, first NAL type={firstNalType}) — decryption works");
        }

        // Prepend the cached SPS/PPS to every IDR keyframe (NAL type 5). The decoder
        // is opened once for the whole app and reused across sessions, so without the
        // parameter sets attached to each keyframe it decodes the very first IDR and
        // then can't resync — which shows up as a single frozen frame.
        byte[] toDecode = payload;
        if (firstNalType == 5 && _spsPps is { Length: > 0 })
        {
            toDecode = new byte[_spsPps.Length + payload.Length];
            Buffer.BlockCopy(_spsPps, 0, toDecode, 0, _spsPps.Length);
            Buffer.BlockCopy(payload, 0, toDecode, _spsPps.Length, payload.Length);
        }

        _decoder?.SubmitPacket(toDecode);
    }

    private static bool ConvertAvccToAnnexBInPlace(byte[] buf)
    {
        int offset = 0;
        while (offset + 4 <= buf.Length)
        {
            int nalLength = (buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3];
            if (nalLength < 0 || offset + 4 + nalLength > buf.Length) return false;
            buf[offset] = 0; buf[offset + 1] = 0; buf[offset + 2] = 0; buf[offset + 3] = 1;
            offset += 4 + nalLength;
        }
        return offset == buf.Length;
    }

    // ── Codec config (type 1): avcC SPS/PPS → Annex-B ─────────────────────────

    private void HandleCodecConfig(byte[] p)
    {
        // avcC layout: [0]=1 [1..3]=profile/compat/level [4]=lengthSizeMinusOne
        //              [5]=numSPS [6..7]=spsLen [8..]=SPS
        //              [..]=numPPS [..]=ppsLen [..]=PPS
        try
        {
            int spsLen = (p[6] << 8) | p[7];
            int spsOff = 8;
            int numPpsOff = spsOff + spsLen;
            int ppsLen = (p[numPpsOff + 1] << 8) | p[numPpsOff + 2];
            int ppsOff = numPpsOff + 3;
            if (ppsOff + ppsLen > p.Length) { System.Diagnostics.Debug.WriteLine("[Mirror] malformed avcC config"); return; }

            byte[] annexB = new byte[4 + spsLen + 4 + ppsLen];
            int o = 0;
            StartCode.CopyTo(annexB, o); o += 4;
            Buffer.BlockCopy(p, spsOff, annexB, o, spsLen); o += spsLen;
            StartCode.CopyTo(annexB, o); o += 4;
            Buffer.BlockCopy(p, ppsOff, annexB, o, ppsLen);

            // Cache the parameter sets and prepend them to each IDR (see HandleVideoFrame)
            // instead of submitting them as a standalone packet — a packet that contains
            // only SPS/PPS and no slice makes the decoder return INVALIDDATA.
            _spsPps = annexB;
            System.Diagnostics.Debug.WriteLine($"[Mirror] codec config: SPS={spsLen}B PPS={ppsLen}B (cached for keyframes)");
        }
        catch (IndexOutOfRangeException)
        {
            System.Diagnostics.Debug.WriteLine("[Mirror] malformed avcC config");
        }
    }

    private static async Task<bool> ReadFullyAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n == 0) return false; // peer closed
            read += n;
        }
        return true;
    }
}
