# AirPlay Receiver for Windows

Turn a Windows 10/11 PC into an AirPlay target, so an iPhone or iPad can mirror
its screen to it, play a video to it, or show a photo on it.

## What works

| Mode | How you use it | Notes |
|------|----------------|-------|
| **Screen mirroring** | Control Center → Screen Mirroring | Full path is implemented: pairing, FairPlay key unwrap, AES-CTR stream decryption, H.264 decode, AAC-ELD audio. |
| **AirPlay Video (URL playback)** | The AirPlay button inside an app that hands over a normal `https://` media URL | The receiver becomes the player: it fetches and decodes the URL itself. Play/pause, scrub and volume come from the phone. |
| **YouTube** | AirPlay button in the YouTube app | YouTube hands over `mlhls://localhost/master.m3u8`, which only the phone can resolve, so the receiver pulls each playlist back over the reverse channel (the "FCUP" exchange) and re-serves them to its own player from loopback. **VOD only.** |
| **Photos (single photo)** | AirPlay button on a photo in Photos | The phone `PUT`s a JPEG; it is decoded and shown on the same surface video uses, so it inherits letterboxing and fullscreen. |

## What does not work

- **DRM-protected video** — Netflix, Disney+, Prime Video, the Apple TV app.
  These use FairPlay Streaming, whose content keys never leave Apple silicon.
  This is not a missing feature; no non-Apple receiver can play them.
- **YouTube live streams.** The FCUP exchange fetches each playlist once, so a
  sliding live-edge playlist is never refreshed. Uploaded videos (VOD) play.
- **AirPlay from a browser tab** (Safari/Chrome page mirroring) — untested and
  not supported.
- **Photo slideshows.** Only the single-photo push is implemented. A slideshow
  asks the *receiver* to pull each image back over a reverse channel, which
  needs a request/response correlator this receiver does not have yet.

### Known limitation

HLS playback (the YouTube path especially) can stall for ~2–3 seconds at some
segment boundaries over a slow or distant connection. Demux, decode and present
share one thread, so a slow segment fetch blocks the frames already decoded.
Moving the demuxer to its own thread is the fix and is not done yet.

## Download and run

> **⚠️ Skip the old 1.0.0 and 1.1.0 releases.** They contain a stale,
> hand-built `AirPlayReceiverSetup.exe` that shipped without the video decoder
> DLLs — it installs fine but mirrors only a black screen. Download
> `AirPlayReceiver-win-x64.zip` from the **newest** release instead.

1. Go to the [**Releases** page](../../releases) and download
   `AirPlayReceiver-win-x64.zip` from the newest release. (Ignore any release
   that only offers `AirPlayReceiverSetup.exe` — see the warning above.)
2. Right-click the zip → **Extract All**.
3. Open the extracted folder and run **`AirPlayReceiver.exe`**.
4. Windows SmartScreen will warn that the app is unrecognized (it isn't code
   signed). Click **More info** → **Run anyway**.
5. Windows Firewall will prompt on first launch — **allow access on Private
   networks**. The receiver needs UDP 5353, TCP 7000, and UDP 7011, and it
   won't be discoverable if you decline.
6. On your iPhone: open Control Center → **Screen Mirroring** → pick your PC.
   For video or a photo, use the AirPlay button inside the app instead.

No installer and no .NET install are needed — the zip is self-contained. Press
**F11** or double-click the window to toggle fullscreen.

### If there are no releases yet

Every run of the build workflow also leaves a downloadable zip attached to it:
open the [**Actions** tab](../../actions/workflows/release.yml), click the most
recent successful run, and grab `AirPlayReceiver-win-x64` under **Artifacts**.
(Downloading Actions artifacts requires being signed in to GitHub.)

## Requirements

- Windows 10 version 1809 (build 17763) or newer, **64-bit (x64 only)**
- The PC and the iPhone/iPad on the **same Wi-Fi network and subnet**
- The release zip already bundles the native **FFmpeg 7.x** DLLs it needs.
  Building from source means supplying them yourself — see [BUILD.md](BUILD.md).

## Troubleshooting

**Start with the log.** Every run writes one to
`%LOCALAPPDATA%\AirPlayReceiver\logs\airplay-<date>-<pid>.log` (paste that path
into Explorer's address bar; the ten most recent runs are kept). Its first lines
are an environment banner that reports `PRESENT`/`MISSING` for each native DLL —
a `MISSING avcodec-61.dll` or `MISSING playfair.dll` explains a black window
immediately.

**Then look for `[Pipeline]`.** During a mirroring session the log gets one
summary line every five seconds, and it localises a black screen in one glance:

```
[Pipeline] rx=312 rejected=0 submitted=312 decoded=311 presented=311 decrypt=yes (5s)
```

| What you see | What it means |
|--------------|---------------|
| `rx=0` | The video stream never arrived — firewall, or the phone couldn't reach the port. |
| `rejected` ≈ `rx` | Frames arrive but decrypt to garbage — the FairPlay key unwrap failed (check `playfair.dll`). |
| `submitted>0`, `decoded=0` | The decoder is the problem (missing or mismatched FFmpeg). |
| `decoded>0`, `presented=0` | The renderer is the problem. |

The window itself also says so: if a session has been live for five seconds with
no frame presented, the HUD shows "Session active but no video frames" and the
path of the log file.

| Symptom | Fix |
|---------|-----|
| PC doesn't appear under Screen Mirroring | Same Wi-Fi network? Firewall allowed for Private networks? Some guest/corporate networks block mDNS between devices. |
| "Windows protected your PC" | SmartScreen — click **More info** → **Run anyway**. |
| App closes right away | Check that you extracted the whole zip rather than running the exe from inside it. |
| Connects, but the window stays black | Read the log as above — the `[Pipeline]` line names the failing stage. |
| A video from Netflix / Disney+ / Prime / Apple TV won't start | Expected: FairPlay DRM, which cannot be played on a non-Apple receiver. |
| A YouTube video won't start | Live streams aren't supported. Try an uploaded video. |
| YouTube playback pauses for a couple of seconds now and then | Known limitation — see the note above. |

## Building from source

See **[BUILD.md](BUILD.md)** for the Visual Studio setup, and
**[ARCHITECTURE.md](ARCHITECTURE.md)** for how the protocol and pipeline fit
together.

## Releasing

Pushing a version tag — either `v1.2.3` or bare `1.2.3` — builds the app and
publishes a release with the zip attached:

```
git tag v0.1.0
git push origin v0.1.0
```

The same workflow can be run manually from the Actions tab.
