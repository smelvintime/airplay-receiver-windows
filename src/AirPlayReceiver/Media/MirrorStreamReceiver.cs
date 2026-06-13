using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AirPlayReceiver.Media;

/// <summary>
/// Receives the AirPlay 2 screen-mirroring video stream over a TCP connection.
///
/// Unlike AirPlay 1 audio (UDP RTP), mirroring video arrives on a TCP "data"
/// port negotiated in SETUP. Each frame is framed as:
///   • a 128-byte header — payload length is a little-endian int32 at offset 0,
///     payload type is a little-endian int16 at offset 4,
///   • followed by <c>payloadLength</c> bytes of payload.
///
/// Payload types (from RPiPlay lib/raop_rtp_mirror.c):
///   0 = encrypted H.264 video frame (AVCC length-prefixed NAL units),
///   1 = codec config (SPS/PPS),
///   2 = heartbeat / other.
///
/// This first cut listens, accepts the connection, and logs each frame's type
/// and size so we can confirm the stream is flowing. Decryption (AES from the
/// FairPlay/ECDH key material) and feeding the decoder come next.
/// </summary>
public sealed class MirrorStreamReceiver : IAsyncDisposable
{
    private const int HeaderSize = 128;

    private TcpListener?             _listener;
    private CancellationTokenSource? _cts;
    private Task?                    _acceptLoop;

    /// <summary>The TCP port the OS assigned; returned to iOS in the SETUP response.</summary>
    public int Port { get; private set; }

    /// <summary>Starts listening on an ephemeral TCP port and begins accepting the mirror connection.</summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        System.Diagnostics.Debug.WriteLine($"[Mirror] Listening for video on TCP {Port}");
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

                System.Diagnostics.Debug.WriteLine($"[Mirror] frame type={payloadType} size={payloadLength}");
                // TODO(next): type 1 → parse SPS/PPS; type 0 → AES-decrypt and feed the decoder.
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
