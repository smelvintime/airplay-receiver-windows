using FFmpeg.AutoGen;
using System;
using System.Runtime.InteropServices;

namespace AirPlayReceiver.Media;

/// <summary>
/// Decodes AirPlay screen-mirroring audio (AAC-ELD, 44.1 kHz stereo, 480 samples
/// per frame) into interleaved 32-bit float PCM via FFmpeg, and hands each decoded
/// frame to <paramref name="onPcm"/>.
///
/// AAC-ELD needs its AudioSpecificConfig as decoder extradata. The mirroring
/// stream's ASC is the 4 bytes <c>F8 E8 50 00</c> (ER AAC-ELD, 44100 Hz, 2 ch),
/// per RPiPlay/UxPlay. Each RTP audio payload is one raw AAC access unit (no ADTS).
/// </summary>
public sealed unsafe class AudioDecoder : IDisposable
{
    private static readonly byte[] AacEldAsc = { 0xF8, 0xE8, 0x50, 0x00 };

    private AVCodecContext* _ctx;
    private AVPacket*       _pkt;
    private AVFrame*        _frame;

    private readonly Action<byte[]> _onPcm;   // interleaved float32, 44100/2
    private bool _loggedFirst;

    public AudioDecoder(Action<byte[]> onPcm) => _onPcm = onPcm;

    public void Initialize()
    {
        AVCodec* codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_AAC);
        if (codec == null) throw new InvalidOperationException("No AAC decoder available");

        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        _ctx->sample_rate = 44100;
        ffmpeg.av_channel_layout_default(&_ctx->ch_layout, 2);

        // Hand the AAC-ELD AudioSpecificConfig as extradata so the decoder selects
        // the ELD object type and 480-sample framing.
        _ctx->extradata = (byte*)ffmpeg.av_mallocz((ulong)(AacEldAsc.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
        Marshal.Copy(AacEldAsc, 0, (IntPtr)_ctx->extradata, AacEldAsc.Length);
        _ctx->extradata_size = AacEldAsc.Length;

        int ret = ffmpeg.avcodec_open2(_ctx, codec, null);
        if (ret < 0) throw new InvalidOperationException($"avcodec_open2 (AAC-ELD) failed: {ret}");

        _pkt   = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        System.Diagnostics.Debug.WriteLine("[Audio] AAC-ELD decoder opened (44100/2, ASC F8E85000)");
    }

    /// <summary>Decodes one raw AAC-ELD access unit, emitting interleaved float PCM frames.</summary>
    public void Decode(byte[] aac)
    {
        fixed (byte* p = aac)
        {
            _pkt->data = p;
            _pkt->size = aac.Length;
            int sret = ffmpeg.avcodec_send_packet(_ctx, _pkt);
            if (sret < 0)
            {
                if (!_loggedFirst) System.Diagnostics.Debug.WriteLine($"[Audio] avcodec_send_packet error {sret}");
                return;
            }
        }

        while (true)
        {
            int rret = ffmpeg.avcodec_receive_frame(_ctx, _frame);
            if (rret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || rret == ffmpeg.AVERROR_EOF) break;
            if (rret < 0)
            {
                System.Diagnostics.Debug.WriteLine($"[Audio] avcodec_receive_frame error {rret}");
                break;
            }

            byte[] pcm = ToInterleavedFloat(_frame);
            ffmpeg.av_frame_unref(_frame);
            if (pcm.Length == 0) continue;

            if (!_loggedFirst)
            {
                _loggedFirst = true;
                System.Diagnostics.Debug.WriteLine($"[Audio] first frame decoded → {pcm.Length}B PCM (playing)");
            }
            _onPcm(pcm);
        }
    }

    private static byte[] ToInterleavedFloat(AVFrame* f)
    {
        int ch = f->ch_layout.nb_channels;
        int n  = f->nb_samples;
        if (ch <= 0 || n <= 0) return Array.Empty<byte>();

        var samples = new float[n * ch];
        switch ((AVSampleFormat)f->format)
        {
            case AVSampleFormat.AV_SAMPLE_FMT_FLTP:                 // planar float (native AAC output)
                for (int c = 0; c < ch; c++)
                {
                    float* src = (float*)f->data[(uint)c];
                    for (int i = 0; i < n; i++) samples[i * ch + c] = src[i];
                }
                break;
            case AVSampleFormat.AV_SAMPLE_FMT_FLT:                  // packed float
            {
                float* src = (float*)f->data[0];
                for (int i = 0; i < n * ch; i++) samples[i] = src[i];
                break;
            }
            case AVSampleFormat.AV_SAMPLE_FMT_S16P:                 // planar int16
                for (int c = 0; c < ch; c++)
                {
                    short* src = (short*)f->data[(uint)c];
                    for (int i = 0; i < n; i++) samples[i * ch + c] = src[i] / 32768f;
                }
                break;
            case AVSampleFormat.AV_SAMPLE_FMT_S16:                  // packed int16
            {
                short* src = (short*)f->data[0];
                for (int i = 0; i < n * ch; i++) samples[i] = src[i] / 32768f;
                break;
            }
            default:
                return Array.Empty<byte>();
        }

        byte[] outBytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, outBytes, 0, outBytes.Length);
        return outBytes;
    }

    public void Dispose()
    {
        if (_frame != null) { fixed (AVFrame** p = &_frame) ffmpeg.av_frame_free(p); }
        if (_pkt   != null) { fixed (AVPacket** p = &_pkt) ffmpeg.av_packet_free(p); }
        if (_ctx   != null) { fixed (AVCodecContext** p = &_ctx) ffmpeg.avcodec_free_context(p); } // frees extradata too
    }
}
