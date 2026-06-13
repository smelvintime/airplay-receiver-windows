# Building & Running in Visual Studio

A step-by-step guide to compiling and launching the AirPlay receiver for the
first time. For the design and protocol details, see [ARCHITECTURE.md](ARCHITECTURE.md).

## 1. Prerequisites

Install **Visual Studio 2022 (17.8 or newer)** with these workloads/components:

- **.NET Desktop Development** workload
- **Windows App SDK C# Templates** (under the workload's optional components, or
  install the [Windows App SDK](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads))
- **.NET 8 SDK** (bundled with recent VS 2022)
- Windows 10 1809 (build 17763) or newer

## 2. (Optional for first launch) Add the FFmpeg DLLs

Video decoding needs the native FFmpeg 6.x DLLs. They are intentionally **not**
committed (see `.gitignore`). Without them the app still launches and is
discoverable — it just can't decode video and shows a notice in the HUD.

To enable decoding:

1. Download `ffmpeg-n6.x-win64-gpl-shared` from
   <https://github.com/BtbN/FFmpeg-Builds/releases>.
2. Copy these DLLs into `src/AirPlayReceiver/native/x64/`:
   `avcodec-61.dll`, `avdevice-61.dll`, `avfilter-10.dll`, `avformat-61.dll`,
   `avutil-59.dll`, `postproc-58.dll`, `swresample-5.dll`, `swscale-8.dll`.

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

## 5. What to expect

- A dark window titled **AirPlay Receiver** with the idle overlay:
  *"Open Control Center on your iPhone and tap Screen Mirroring."*
- Press **F11** or double-click to toggle borderless fullscreen.
- If the FFmpeg DLLs are missing, a HUD line notes that decoding is unavailable.

> **Current limitations:** pairing crypto, NV12→RGB rendering, and RTP
> decryption are still stubs (see the PR description and `ARCHITECTURE.md §4`),
> so a real mirroring session won't display video yet. This first run verifies
> the app builds, launches, and advertises itself on the network.

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| Build error: platform `Any CPU` not supported | Switch the platform dropdown to **x64**. |
| `DllNotFoundException` for `avcodec-61` at runtime | Add the FFmpeg DLLs (step 2); they must sit in `native/x64/` next to the build output. |
| Device doesn't appear in iPhone Screen Mirroring | Confirm both devices are on the same subnet and the firewall prompt was allowed for Private networks. |
| App closes immediately | Run with the debugger (F5) and check the Output window for the startup exception. |
