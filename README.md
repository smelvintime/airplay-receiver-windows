# AirPlay Receiver for Windows

Turn a Windows 10/11 PC into an AirPlay screen-mirroring target, so an iPhone or
iPad can mirror to it from Control Center.

> **Status: work in progress.** The app builds, launches, and advertises itself
> on your network, so your iPhone will find it under Screen Mirroring. Parts of
> the pairing crypto and RTP decryption are still stubs, so a real session may
> not display video yet. Treat this as a preview, not a finished product.

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

No installer and no .NET install are needed — the zip is self-contained. Press
**F11** or double-click the window to toggle fullscreen.

### If there are no releases yet

Every run of the build workflow also leaves a downloadable zip attached to it:
open the [**Actions** tab](../../actions/workflows/release.yml), click the most
recent successful run, and grab `AirPlayReceiver-win-x64` under **Artifacts**.
(Downloading Actions artifacts requires being signed in to GitHub.)

## Requirements

- Windows 10 version 1809 (build 17763) or newer, 64-bit
- The PC and the iPhone/iPad on the **same Wi-Fi network and subnet**

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| PC doesn't appear under Screen Mirroring | Same Wi-Fi network? Firewall allowed for Private networks? Some guest/corporate networks block mDNS between devices. |
| "Windows protected your PC" | SmartScreen — click **More info** → **Run anyway**. |
| App closes right away | Check that you extracted the whole zip rather than running the exe from inside it. |
| Connects, but no video | Expected for now — see the status note above. |

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
