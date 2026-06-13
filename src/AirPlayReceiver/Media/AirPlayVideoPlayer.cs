using System;

namespace AirPlayReceiver.Media;

/// <summary>
/// Shared state for AirPlay <b>Video</b> (URL playback) — the mode iOS uses when
/// you AirPlay a video from an app (YouTube, Photos, …) rather than mirroring.
/// iOS opens many short-lived control connections (/play, /rate, /scrub,
/// /playback-info, /stop), so this lives once on <c>AirPlayService</c> and every
/// session delegates to it.
///
/// This first cut tracks state and logs what iOS sends so we can see the exact
/// URL/format. Actual FFmpeg-based playback to the presenter is the next step.
/// </summary>
public sealed class AirPlayVideoPlayer
{
    /// <summary>The video URL iOS asked us to play (null when stopped).</summary>
    public string? Url { get; private set; }

    public double Position { get; private set; }   // seconds
    public double Duration { get; private set; }   // seconds (0 = unknown)
    public double Rate     { get; private set; }    // 1.0 = play, 0.0 = paused
    public bool   ReadyToPlay => Url is not null;

    public void Play(string url, double startPosition)
    {
        Url      = url;
        Position = startPosition;
        Rate     = 1.0;
        System.Diagnostics.Debug.WriteLine($"[Video] PLAY url='{url}' startPosition={startPosition}");
        // TODO(next): open the URL with FFmpeg (avformat) and render to the presenter.
    }

    public void SetRate(double rate)
    {
        Rate = rate;
        System.Diagnostics.Debug.WriteLine($"[Video] rate={rate} ({(rate > 0 ? "play" : "pause")})");
    }

    public void Scrub(double position)
    {
        Position = position;
        System.Diagnostics.Debug.WriteLine($"[Video] scrub to {position}s");
    }

    public void Stop()
    {
        System.Diagnostics.Debug.WriteLine("[Video] STOP");
        Url      = null;
        Rate     = 0;
        Position = 0;
        Duration = 0;
    }
}
