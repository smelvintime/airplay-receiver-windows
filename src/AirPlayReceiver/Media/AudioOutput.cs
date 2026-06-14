using NAudio.Wave;
using System;

namespace AirPlayReceiver.Media;

/// <summary>
/// Plays interleaved 32-bit float PCM (44.1 kHz stereo) to the default Windows
/// output device via WASAPI (NAudio). Decoded audio frames are queued into a
/// jitter buffer that WASAPI drains on its own thread.
/// </summary>
public sealed class AudioOutput : IDisposable
{
    private WasapiOut?            _device;
    private BufferedWaveProvider? _buffer;

    public void Start()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        _buffer = new BufferedWaveProvider(format)
        {
            BufferDuration         = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,   // drop if we ever fall behind, never block the receiver
        };

        _device = new WasapiOut();
        _device.Init(_buffer);
        _device.Play();
        System.Diagnostics.Debug.WriteLine("[Audio] WASAPI output started (44100/2 float)");
    }

    /// <summary>Queues a decoded PCM frame for playback. Thread-safe.</summary>
    public void Enqueue(byte[] pcm) => _buffer?.AddSamples(pcm, 0, pcm.Length);

    public void Dispose()
    {
        try { _device?.Stop(); } catch { /* shutting down */ }
        _device?.Dispose();
        _device = null;
        _buffer = null;
    }
}
