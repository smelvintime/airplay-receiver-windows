using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;

namespace AirPlayReceiver.Media;

/// <summary>
/// Plays interleaved 32-bit float PCM (44.1 kHz stereo) to the default Windows
/// output device via WASAPI (NAudio).
///
/// Two latency knobs:
///  - WASAPI shared-mode latency: 50 ms (down from NAudio's 200 ms default).
///  - Pre-fill gate: playback does not start until 100 ms of PCM is buffered,
///    preventing the underrun-induced "robotic" sound that occurs when WASAPI
///    starts draining an almost-empty buffer on the first few frames.
/// </summary>
public sealed class AudioOutput : IDisposable
{
    // 44100 samples/s × 2 ch × 4 bytes/sample × 0.1 s = 35 280 bytes ≈ 100 ms
    private const int PreFillBytes = 44100 * 2 * sizeof(float) / 10;

    private WasapiOut?            _device;
    private BufferedWaveProvider? _buffer;
    private bool _playing;

    public void Start()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        _buffer = new BufferedWaveProvider(format)
        {
            BufferDuration          = TimeSpan.FromMilliseconds(300),
            DiscardOnBufferOverflow = true,
        };

        _device = new WasapiOut(AudioClientShareMode.Shared, 50);
        _device.Init(_buffer);
        // Don't call Play() yet — we gate on PreFillBytes in Enqueue().
        System.Diagnostics.Debug.WriteLine("[Audio] WASAPI output started (44100/2 float, 50ms latency)");
    }

    /// <summary>Queues a decoded PCM frame for playback. Thread-safe.</summary>
    public void Enqueue(byte[] pcm)
    {
        if (_buffer is null) return;
        _buffer.AddSamples(pcm, 0, pcm.Length);

        if (!_playing && _buffer.BufferedBytes >= PreFillBytes)
        {
            _playing = true;
            _device?.Play();
            System.Diagnostics.Debug.WriteLine("[Audio] pre-fill reached — playback started");
        }
    }

    public void Dispose()
    {
        try { _device?.Stop(); } catch { /* shutting down */ }
        _device?.Dispose();
        _device = null;
        _buffer = null;
    }
}
