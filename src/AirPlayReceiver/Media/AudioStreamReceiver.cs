using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AirPlayReceiver.Media;

/// <summary>
/// Receives the AirPlay screen-mirroring <b>audio</b> stream (RTSP stream type 96):
/// AAC-ELD over UDP/RTP. Opens the data + control UDP sockets, decrypts each RTP
/// payload (AES-CBC, <see cref="AudioStreamCrypto"/>), decodes it (AAC-ELD →
/// <see cref="AudioDecoder"/>) and plays it (<see cref="AudioOutput"/>).
///
/// This first cut plays packets in arrival order without a jitter/resend buffer or
/// NTP sync — fine on a LAN; reordering/sync can be layered on later. The control
/// socket is drained (sync + resend handling is a future refinement).
/// </summary>
public sealed class AudioStreamReceiver : IAsyncDisposable
{
    private UdpClient?               _data;
    private UdpClient?               _control;
    private CancellationTokenSource? _cts;

    private AudioStreamCrypto? _crypto;
    private AudioDecoder?      _decoder;
    private AudioOutput?       _output;

    private long _count;
    private bool _firstLogged;

    public int DataPort    { get; private set; }
    public int ControlPort { get; private set; }

    public void Configure(AudioStreamCrypto? crypto) => _crypto = crypto;

    public void Start()
    {
        _data    = new UdpClient(0);
        DataPort = ((IPEndPoint)_data.Client.LocalEndPoint!).Port;
        _control = new UdpClient(0);
        ControlPort = ((IPEndPoint)_control.Client.LocalEndPoint!).Port;

        // FFmpeg/NAudio init can fail (missing DLLs, no audio device). Guard it so a
        // failure still leaves the ports open (and mirroring video unaffected).
        try
        {
            _output  = new AudioOutput();
            _output.Start();
            _decoder = new AudioDecoder(pcm => _output!.Enqueue(pcm));
            _decoder.Initialize();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Audio] decode/output unavailable: {ex.Message}");
            _decoder = null;
            _output  = null;
        }

        _cts = new CancellationTokenSource();
        System.Diagnostics.Debug.WriteLine(
            $"[Audio] data UDP {DataPort}, control UDP {ControlPort} (decrypt={_crypto is not null}, play={_output is not null})");

        _ = ReceiveDataAsync(_cts.Token);
        _ = ReceiveControlAsync(_cts.Token);
    }

    private async Task ReceiveDataAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult res = await _data!.ReceiveAsync(ct);
                byte[] p = res.Buffer;

                if (p.Length <= 12) continue; // empty / keepalive (12-byte RTP header only)
                if (p.Length == 16 && p[12] == 0x00 && p[13] == 0x68 && p[14] == 0x34 && p[15] == 0x00)
                    continue; // "no data" keepalive

                int payloadLen = p.Length - 12;            // strip 12-byte RTP header
                byte[] payload = new byte[payloadLen];
                Buffer.BlockCopy(p, 12, payload, 0, payloadLen);

                _crypto?.Decrypt(payload, payloadLen);

                if (!_firstLogged)
                {
                    _firstLogged = true;
                    bool eld = payload[0] is 0x8c or 0x8d or 0x8e or 0x80 or 0x81 or 0x82;
                    System.Diagnostics.Debug.WriteLine(
                        $"[Audio] first packet {payloadLen}B, first byte 0x{payload[0]:X2} " +
                        $"({(eld ? "valid AAC-ELD — decryption OK" : "NOT AAC-ELD — key/iv mismatch?")})");
                }

                _decoder?.Decode(payload);
                if (++_count % 500 == 0)
                    System.Diagnostics.Debug.WriteLine($"[Audio] {_count} packets");
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Audio] data loop error: {ex.Message}"); }
    }

    private async Task ReceiveControlAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
                await _control!.ReceiveAsync(ct); // drain sync/resend (not acted on yet)
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _data?.Dispose();
        _control?.Dispose();
        _output?.Dispose();
        _decoder?.Dispose();
        await Task.CompletedTask;
        System.Diagnostics.Debug.WriteLine("[Audio] Stopped.");
    }
}
