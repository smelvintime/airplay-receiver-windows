# AirPlay Receiver for Windows

Turn a Windows 10/11 PC into an AirPlay screen-mirroring target, so an iPhone or
iPad can mirror to it from Control Center.

> **Status: preview.** Screen mirroring works — video and audio both play, and
> AirPlay Video (playing a URL from an app) is supported too. Expect rough
> edges: the app isn't code signed yet, and jitter/resend handling for audio is
> still basic.

## Download and run

Go to the [**Releases** page](../../releases), pick the newest release, and take
either download — they're the same app:

- **`AirPlayReceiverSetup.exe`** — the installer. **Recommended:** it adds a
  Start-menu entry and an uninstaller, and pre-approves the receiver in Windows
  Firewall so there's no prompt to get wrong. Needs admin rights.
- **`AirPlayReceiver-win-x64.zip`** — portable, no install. Right-click →
  **Extract All**, then run **`AirPlayReceiver.exe`** from the extracted folder
  (don't run it from inside the zip). No .NET install needed — it's
  self-contained.

Then:

1. Windows SmartScreen will warn that the app is unrecognized (it isn't code
   signed). Click **More info** → **Run anyway**.
2. **Zip only:** Windows Firewall prompts on first launch — **allow access on
   Private networks**. The receiver listens on UDP 5353 (discovery), TCP 7000
   (control), and UDP 7011 (video), plus a few per-session ports. It won't be
   discoverable if you decline. The installer sets these rules up for you.
3. On your iPhone: open Control Center → **Screen Mirroring** → pick your PC.
   Not sure which entry is yours? The receiver window shows the exact name it
   advertises ("Discoverable in Screen Mirroring as …").

Press **F11** or double-click the window to toggle fullscreen.

### If a release is missing a download

Every run of the build workflow leaves both files attached to it: open the
[**Actions** tab](../../actions/workflows/release.yml), click the most recent
successful run, and grab `AirPlayReceiver-win-x64` under **Artifacts**.
(Downloading Actions artifacts requires being signed in to GitHub.)

## Requirements

- Windows 10 version 1809 (build 17763) or newer, 64-bit
- The PC and the iPhone/iPad on the **same network and subnet** (see below)

## How devices connect

AirPlay runs over your local IP network — there is no Bluetooth involved, so
the PC needs no Bluetooth hardware and pairing happens automatically over the
network. Any of these setups work:

- **Wi-Fi** — PC and iPhone on the same Wi-Fi network (the usual setup).
- **Ethernet** — the PC can be wired to the same router the iPhone's Wi-Fi
  hangs off; wired + wireless on one subnet is fine.
- **Windows Mobile Hotspot** — no router? Turn on **Settings → Network &
  internet → Mobile hotspot** on the PC and join the iPhone to it. The
  receiver listens on the hotspot adapter too.

The receiver accepts connections over both **IPv4 and IPv6** on every network
adapter. Discovery uses mDNS/Bonjour (UDP 5353), control uses Apple's RTSP
dialect (TCP 7000), and the media streams follow on ports negotiated per
session. Apple's Bluetooth-assisted "peer-to-peer AirPlay" (mirroring without
a shared network, as an Apple TV can) relies on Apple-proprietary hardware
support and is not possible on a Windows PC.

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| PC doesn't appear under Screen Mirroring | Same network and subnet? Firewall allowed for Private networks? Some guest/corporate networks block mDNS between devices — the Mobile Hotspot setup above sidesteps that. |
| "Windows protected your PC" | SmartScreen — click **More info** → **Run anyway**. |
| App closes right away | If you used the zip, check that you extracted the whole thing rather than running the exe from inside it. |
| Connects, but no video | Make sure you're on the newest release — old previews couldn't decrypt the stream yet. |

## Building from source

See **[BUILD.md](BUILD.md)** for the Visual Studio setup, and
**[ARCHITECTURE.md](ARCHITECTURE.md)** for how the protocol and pipeline fit
together.

## Releasing

Pushing a version tag builds the app and publishes a release with both
`AirPlayReceiverSetup.exe` and `AirPlayReceiver-win-x64.zip` attached. Both
`v`-prefixed and bare tags work (existing releases use the bare form):

```
git tag 1.2.0
git push origin 1.2.0
```

The tag name becomes the installer's version in Programs & Features. If a
release for the tag already exists, the workflow adds the files to it instead of
creating a new one. The same workflow can also be run manually from the Actions
tab.

The installer is built with [Inno Setup](https://jrsoftware.org/isinfo.php) from
[`installer/AirPlayReceiver.iss`](installer/AirPlayReceiver.iss) — see
[BUILD.md](BUILD.md) to build it locally.
