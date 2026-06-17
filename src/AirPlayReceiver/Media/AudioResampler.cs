using FFmpeg.AutoGen;
using System;

namespace AirPlayReceiver.Media;

/// <summary>
/// Resamples interleaved 32-bit float stereo PCM between two sample rates using
/// FFmpeg swresample. Used to convert 44.1 kHz AirPlay audio to the WASAPI device's
/// native mix rate (typically 48 kHz), replacing Windows' built-in low-quality
/// resampler which introduces aliasing artifacts audible as high-frequency noise.
/// </summary>
public sealed unsafe class AudioResampler : IDisposable
{
    private SwrContext* _swr;
    private readonly int _inRate;
    private readonly int _outRate;

    public AudioResampler(int inputRate, int outputRate)
    {
        _inRate  = inputRate;
        _outRate = outputRate;
    }

    public void Initialize()
    {
        AVChannelLayout inLayout = default, outLayout = default;
        ffmpeg.av_channel_layout_default(&inLayout,  2);
        ffmpeg.av_channel_layout_default(&outLayout, 2);

        SwrContext* swr = null;
        int ret = ffmpeg.swr_alloc_set_opts2(
            &swr,
            &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, _outRate,
            &inLayout,  AVSampleFormat.AV_SAMPLE_FMT_FLT, _inRate,
            0, null);
        if (ret < 0 || swr == null)
            throw new InvalidOperationException($"swr_alloc_set_opts2 failed: {ret}");
        _swr = swr;

        ret = ffmpeg.swr_init(_swr);
        if (ret < 0)
            throw new InvalidOperationException($"swr_init failed: {ret}");

        System.Diagnostics.Debug.WriteLine($"[Audio] Resampler: {_inRate} → {_outRate} Hz (FLT stereo)");
    }

    /// <summary>
    /// Converts interleaved float32 stereo PCM from <c>_inRate</c> to <c>_outRate</c>.
    /// Returns a new byte array at the output rate, or empty on error.
    /// </summary>
    public byte[] Resample(byte[] pcmFlt)
    {
        if (_swr == null || pcmFlt.Length == 0) return Array.Empty<byte>();

        int inSamples = pcmFlt.Length / (2 * sizeof(float));
        int maxOut    = (int)Math.Ceiling((double)inSamples * _outRate / _inRate) + 16;
        byte[] outBuf = new byte[maxOut * 2 * sizeof(float)];

        fixed (byte* inPtr = pcmFlt)
        fixed (byte* outPtr = outBuf)
        {
            byte** inPtrs  = stackalloc byte*[1];
            byte** outPtrs = stackalloc byte*[1];
            inPtrs[0]  = inPtr;
            outPtrs[0] = outPtr;

            int n = ffmpeg.swr_convert(_swr, outPtrs, maxOut, inPtrs, inSamples);
            if (n <= 0) return Array.Empty<byte>();

            int outBytes = n * 2 * sizeof(float);
            if (outBytes == outBuf.Length) return outBuf;
            byte[] result = new byte[outBytes];
            Buffer.BlockCopy(outBuf, 0, result, 0, outBytes);
            return result;
        }
    }

    public void Dispose()
    {
        if (_swr != null)
        {
            fixed (SwrContext** p = &_swr) ffmpeg.swr_free(p);
            _swr = null;
        }
    }
}
