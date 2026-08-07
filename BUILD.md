# Building & Running in Visual Studio

A step-by-step guide to compiling and launching the AirPlay receiver for the
first time. For the design and protocol details, see [ARCHITECTURE.md](ARCHITECTURE.md).

## 1. Prerequisites

Install **Visual Studio 2022 (17.8 or newer)** with these workloads/components:

- **.NET Desktop Development** workload
- **Windows App SDK C# Templates** (under the workload's optional components, or
  install the [Windows App SDK](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads))
- **.NET 8 SDK** (bundled with recent VS 2022) — the project targets
  `net8.0-windows10.0.22621.0`
- Windows 10 1809 (build 17763) or newer
- **x64 only.** `<Platforms>x64</Platforms>` is the only configured platform;
  `Any CPU` and `ARM64` will not build.

## 2. (Optional for first launch) Add the FFmpeg DLLs

Every pixel path — mirroring, URL playback and photos — needs the native FFmpeg
**7.x** DLLs. They are intentionally **not** committed (see `.gitignore`).
Without them the app still launches and is discoverable; it just can't decode
anything and shows a notice in the HUD.

To enable decoding:

1. Download an `ffmpeg-n7.1.x-...-win64-gpl-shared-7.1` zip from
   <https://github.com/BtbN/FFmpeg-Builds/releases> (any release whose asset
   name starts with `ffmpeg-n7.1`).
2. Copy these DLLs from the zip's `bin/` folder into
   `src/AirPlayReceiver/native/x64/`:
   `avcodec-61.dll`, `avdevice-61.dll`, `avfilter-10.dll`, `avformat-61.dll`,
   `avutil-59.dll`, `postproc-58.dll`, `swresample-5.dll`, `swscale-8.dll`.

> **Version matters.** The project pins `FFmpeg.AutoGen 7.1.1`, which binds
> **only** FFmpeg 7.x natives. FFmpeg **8.x will NOT work** — its DLLs are
> named `avcodec-62.dll` etc. and the bindings won't resolve them (no video,
> no build error to warn you). FFmpeg **6.x will NOT work** either
> (`avcodec-60`). It must be a 7.x shared build.
>
> At build time the DLLs are copied into the output preserving their relative
> path: they land in `native\x64\` **next to** `AirPlayReceiver.exe`, and the
> app loads them from there.

`playfair.dll`, which performs the FairPlay key unwrap that mirroring depends on,
**is** committed (`src/AirPlayReceiver/vendor/playfair/`) and is copied next to
the exe automatically. Nothing to do for it — but if it goes missing, mirror
video decrypts to garbage and the window stays black.

## 3. Open and build

1. Open **`AirPlayReceiver.sln`** in Visual Studio.
2. Set the configuration to **Debug** and the platform to **x64** (this is the
   only supported platform — the toolbar dropdown should already show `x64`).
3. Build with **Ctrl+Shift+B**. NuGet restore runs automatically on first build.

## 4. Run

Press **F5** (or Ctrl+F5 to run without the debugger).

On first launch Windows Firewall will prompt — **allow access on Private
networks**, since the receiver needs:

- UDP **5353** (mDNS / Bonjour discovery)
- TCP **7000** (RTSP control)
- UDP **7011** (RTP video)
- plus **ephemeral** TCP/UDP ports, negotiated per session: the mirror video
  stream and the mirror audio stream each bind a port the OS assigns and report
  it back to the phone in the `SETUP` response.

AirPlay Video also makes **outbound** connections — the receiver fetches the
media URL itself. The `HlsServer` behind the YouTube path binds loopback only
and is never exposed to the network.

## 5. What to expect

- A dark window titled **AirPlay Receiver** with the idle overlay:
  *"Open Control Center on your iPhone and tap Screen Mirroring."*
- Press **F11** or double-click to toggle borderless fullscreen.
- If the FFmpeg DLLs are missing, a HUD line notes that decoding is unavailable.
- Mirroring, AirPlay Video from a plain `https://` URL, the YouTube (FCUP/HLS)
  path, and a single AirPlay photo all work. FairPlay-DRM services, YouTube live
  streams, browser-tab AirPlay and photo *slideshows* do not — see the README's
  "What does not work" for why.

## 6. Diagnostics

Every run writes a log to
`%LOCALAPPDATA%\AirPlayReceiver\logs\airplay-<date>-<pid>.log` (ten runs are
kept). Read it before anything else: the banner at the top reports
`PRESENT`/`MISSING` for `avcodec-61`, `avutil-59`, `swscale-8`, `swresample-5`
and `playfair.dll`, so a missing native is visible immediately rather than as a
black window. During a mirroring session a `[Pipeline]` line every five seconds
reports `rx / rejected / submitted / decoded / presented`, which localises a
black screen to the network, the key unwrap, the decoder or the renderer. The
README's troubleshooting section has the full reading key.

## 7. Test harnesses

There is no unit-test project; two console harnesses stand in for the phone and
exit non-zero on failure. Neither is part of `AirPlayReceiver.sln` — both
link-compile the receiver's real sources so a console app doesn't drag in WinUI.

```
dotnet run --project tools\PhotoHarness -c Release      # AirPlay Photos, 38 checks
dotnet run --project tests\HlsHarness   -c Release      # FCUP/HLS loop end to end
```

`PhotoHarness` drives the real `PhotoHandler` and `PhotoDecoder` over a loopback
socket with raw `PUT /photo` bytes: the three `X-Apple-AssetAction` behaviours,
the 412-on-cache-miss contract, LRU eviction, downscaling, the JPEG full-range →
limited-range NV12 conversion, and malformed input. It really runs FFmpeg, so it
needs the DLLs from step 2 — `--ffmpeg <dir>` points it at another directory.
`--diag` re-runs the libswscale colour-range measurements behind the range
conversion; worth doing after any FFmpeg upgrade.

`HlsHarness` runs a fake phone that performs the `/reverse` PTTH upgrade, posts
`/play` with an `mlhls://` location, answers each FCUP request with a canned
playlist, and fetches the rewritten playlists back out of the loopback
`HlsServer`. It has no FFmpeg or native dependency at all and runs anywhere.

CI (`.github/workflows/ci.yml`) only compiles — the harnesses are run locally.

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| Build error: platform `Any CPU` not supported | Switch the platform dropdown to **x64**. |
| `DllNotFoundException` for `avcodec-61` at runtime | Add the FFmpeg DLLs (step 2); they must end up in `native\x64\` next to the exe in the build output. Also check you grabbed a 7.x build — 8.x DLLs (`avcodec-62`) and 6.x DLLs (`avcodec-60`) won't resolve. |
| Device doesn't appear in iPhone Screen Mirroring | Confirm both devices are on the same subnet and the firewall prompt was allowed for Private networks. |
| App closes immediately | Run with the debugger (F5) and check the Output window for the startup exception. |
| Session connects but the window is black | Open this run's log and read the `[Pipeline]` line (§6). |
| `MSB3030` / path-too-long when building from a deeply nested directory | The output paths exceed `MAX_PATH`. Build from a shorter path, or point a junction at the repo (`mklink /J %TEMP%\d1 <repo>`) and build from there. |
