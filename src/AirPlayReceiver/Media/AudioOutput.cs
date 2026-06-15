using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;

namespace AirPlayReceiver.Media;

/// <summary>
/// Plays interleaved 32-bit float PCM to the default Windows output device via
/// WASAPI (NAudio). Configures itself at the device's native mix sample rate so
/// that Windows does not perform additional resampling (which introduces
/// high-frequency aliasing artifacts audible during screen mirroring).
///
/// Two latency knobs:
///  - WASAPI shared-mode latency: 50 ms (down from NAudio's 200 ms default).
///  - Pre-fill gate: playback does not start until 100 ms of PCM is buffered,
///    preventing the underrun-induced "robotic" sound on the first few frames.
/// </summary>
public sealed class AudioOutput : IDisposable
{
    private WasapiOut?            _device;
    private BufferedWaveProvider? _buffer;
    private bool _playing;
    private int  _preFillBytes;

    /// <summary>Sample rate of the WASAPI device mix format (set during Start).</summary>
    public int DeviceSampleRate { get; private set; } = 48000;

    /// <summary>Current jitter-buffer fill level in milliseconds (diagnostic).</summary>
    public int BufferedMs => _buffer is null ? 0 : (int)_buffer.BufferedDuration.TotalMilliseconds;

    /// <summary>Total bytes dropped due to buffer overflow since start (diagnostic).</summary>
    public long DiscardedBytes { get; private set; }

    private float _volume = 1f;

    /// <summary>Sets the output gain (0.0–1.0). Applied to the WASAPI session.</summary>
    public void SetVolume(float linear)
    {
        _volume = Math.Clamp(linear, 0f, 1f);
        if (_device is not null) _device.Volume = _volume;
    }

    public void Start()
    {
        // Query the device's native mix format so we can feed it PCM at that rate,
        // avoiding Windows' low-quality built-in resampler (source of HF artifacts).
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            DeviceSampleRate = device.AudioClient.MixFormat.SampleRate;
        }
        catch
        {
            DeviceSampleRate = 48000;
        }

        // 100 ms pre-fill at device rate: rate × 2 ch × 4 bytes/sample × 0.1 s
        _preFillBytes = DeviceSampleRate * 2 * sizeof(float) / 10;

        var format = WaveFormat.CreateIeeeFloatWaveFormat(DeviceSampleRate, 2);
        _buffer = new BufferedWaveProvider(format)
        {
            BufferDuration          = TimeSpan.FromMilliseconds(600),
            DiscardOnBufferOverflow = true,
        };

        _device = new WasapiOut(AudioClientShareMode.Shared, 50);
        _device.Init(_buffer);
        _device.Volume = _volume;   // honour any volume set before Start()
        // Don't call Play() yet — we gate on _preFillBytes in Enqueue().
        System.Diagnostics.Debug.WriteLine($"[Audio] WASAPI output started ({DeviceSampleRate}/2 float, 50ms latency)");
    }

    /// <summary>Queues a decoded PCM frame for playback. Thread-safe.</summary>
    public void Enqueue(byte[] pcm)
    {
        if (_buffer is null) return;

        // Detect overflow before AddSamples silently drops (DiscardOnBufferOverflow).
        int free = _buffer.BufferLength - _buffer.BufferedBytes;
        if (pcm.Length > free) DiscardedBytes += pcm.Length - free;

        _buffer.AddSamples(pcm, 0, pcm.Length);

        if (!_playing && _buffer.BufferedBytes >= _preFillBytes)
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
