# AirPlay Receiver for Windows — Architecture Blueprint

## Table of Contents
1. [Technology Stack Decision](#1-technology-stack-decision)
2. [Architecture Overview](#2-architecture-overview)
3. [Open-Source References](#3-open-source-references)
4. [Implemented Flows](#4-implemented-flows)
5. [Dependency Setup](#5-dependency-setup)
6. [Diagnostics](#6-diagnostics)

> **Scope.** The receiver supports four modes: screen mirroring, AirPlay Video
> from a fetchable `https://` URL, the FCUP/HLS path that makes the YouTube app
> work, and single-photo AirPlay. FairPlay-DRM services, YouTube live streams
> and photo slideshows do not work; §4 explains each. AirPlay from a browser tab
> is untested and not supported. The README carries the user-facing version of
> the same list.

---

## 1. Technology Stack Decision

### UI Framework — WinUI 3 (Windows App SDK)

**Chosen over WPF and Qt for these reasons:**

- WinUI 3 gives you a `SwapChainPanel` — a first-class DirectX 11/12 swap chain host that lives inside the XAML visual tree. This is the single most important rendering primitive: you get a native D3D11 surface to present decoded frames onto, while the rest of the window remains a normal XAML app.
- WPF's `D3DImage` works but requires an extra synchronization layer and a shared-handle dance between devices. It's measurably slower.
- Qt is viable for C++ teams and adds ~60 MB of DLLs, but the WinUI 3 stack is the correct choice when staying in C#/C++ on Windows.

### mDNS / Service Discovery — Makaretu.Dns (C# NuGet)

- `Makaretu.Dns.Multicast` is a pure-managed, MIT-licensed mDNS/DNS-SD implementation.
- The native Windows DNS Service Discovery API (`DnsServiceRegister`) works but is cumbersome from managed code and has documented quirks in its TXT record handling on Windows 10 22H2+.
- Makaretu gives you a 10-line advertisement and handles both IPv4 (224.0.0.251:5353) and IPv6 (ff02::fb:5353) multicast groups correctly.

### RTSP / AirPlay Protocol — Custom TCP Server (no third-party RTSP library)

- AirPlay's RTSP dialect diverges from RFC 2326 in ways that break most generic RTSP libraries (e.g., Apple uses `RECORD` rather than `PLAY`, and the body of `SETUP` carries custom binary TLV fields).
- A raw `TcpListener` with a hand-rolled HTTP/RTSP parser is ~300 lines and gives you full control over the pairing TLV encoding.
- Reference the UxPlay source for the exact TLV field IDs and SRP/Ed25519 handshake byte layout.

### Decoding — FFmpeg via FFmpeg.AutoGen + DXVA2/D3D11VA hardware acceleration

- `FFmpeg.AutoGen` (NuGet) provides P/Invoke bindings to the native ffmpeg DLLs. The 7.1.1 package binds **only** FFmpeg 7.x natives (`avcodec-61`, `avutil-59`, `swscale-8`, `swresample-5`); 6.x and 8.x will not resolve.
- `VideoDecoder` sets `AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA` on the codec context, and falls back to software decode (logged) when the device can't be created.
- Windows Media Foundation (MF) is an alternative but its H.265 decoder is codec-pack-dependent on Windows 10, and MF's async callback model adds complexity without latency benefit.

### Rendering — Direct3D 11 via `SwapChainPanel`

- One `VideoPresenter` owns the D3D11 device, the composition swap chain and an HLSL NV12→RGB pixel shader (BT.709 limited range), and is the **only** thing that draws. Every producer — mirroring, URL playback, photos — feeds it NV12 `AVFrame`s through the same `PresentFrame(IntPtr)` entry point, so letterboxing, display rotation and F11 fullscreen are implemented once.
- The path is **not** zero-copy, deliberately. D3D11VA decodes onto FFmpeg's own D3D11 device; `av_hwframe_transfer_data` copies the surface down to a CPU NV12 frame, and the presenter uploads it to *its* device with `UpdateSubresource`. Sharing a texture across two D3D11 devices needs keyed-mutex/shared-handle plumbing that buys nothing at mirroring resolutions, and the CPU frame is also what lets photos and URL playback reuse the identical sink.

---

## 2. Architecture Overview

```
┌───────────────────────────────────────────────────────────────────────────┐
│                              iPhone (iOS)                                 │
│   Control Center mirroring  ·  in-app AirPlay (video)  ·  Photos          │
└───────────────────────────────┬───────────────────────────────────────────┘
                                │  Wi-Fi (same subnet)
┌───────────────────────────────▼───────────────────────────────────────────┐
│                       AirPlay Receiver (Windows)                          │
│                                                                           │
│  ┌───────────────┐   ┌──────────────────────────────────────────────┐    │
│  │ Discovery     │   │ Protocol layer                               │    │
│  │ MdnsService   │──▶│ RtspServer ──▶ AirPlaySession (one per conn) │    │
│  │ _airplay._tcp │   │   ├ OPTIONS / GET_PARAMETER / GET /info      │    │
│  │ _raop._tcp    │   │   ├ POST /pair-setup, /pair-verify           │◀┐  │
│  │ port 7000     │   │   │     PairingHandler · DeviceIdentity      │ │  │
│  └───────────────┘   │   ├ POST /fp-setup  FairPlayHandshake        │ │  │
│                      │   │     └▶ Playfair (vendored playfair.dll)  │ │  │
│                      │   ├ SETUP / RECORD / TEARDOWN                │ │  │
│                      │   ├ POST /reverse  ──▶ ReverseChannel(PTTH)  │─┘  │
│                      │   ├ POST /play,/rate,/scrub,/stop,/action    │    │
│                      │   │     └▶ HlsCoordinator (FCUP)             │    │
│                      │   └ PUT  /photo    ──▶ PhotoHandler          │    │
│                      │        BinaryPlist · XmlPlist · RtspMessage  │    │
│                      └───┬───────────────┬──────────────┬───────────┘    │
│                          │ mirroring     │ URL playback │ photos         │
│  ┌───────────────────────▼──┐ ┌──────────▼─────────┐ ┌──▼─────────────┐  │
│  │ MirrorStreamReceiver     │ │ AirPlayVideoPlayer │ │ PhotoDecoder   │  │
│  │  MirrorStreamCrypto      │ │  avformat open URL │ │  mjpeg decode  │  │
│  │  (AES-CTR)               │ │  demux+decode+     │ │  swscale→NV12  │  │
│  │   └▶ VideoDecoder        │ │  present (1 thread)│ │  (full→limited │  │
│  │       D3D11VA → CPU NV12 │ │  swscale→NV12      │ │   range)       │  │
│  │  AudioStreamReceiver     │ │  ▲ HlsServer       │ │  PhotoCache    │  │
│  │   AAC-ELD → AudioOutput  │ │  │ HlsPlaylistStore│ │  (LRU, 412)    │  │
│  └───────────────────────┬──┘ └──┬─────────────────┘ └──┬─────────────┘  │
│                          │       │  NV12 AVFrame        │                │
│  ┌───────────────────────▼───────▼──────────────────────▼─────────────┐  │
│  │                       UI layer (WinUI 3)                           │  │
│  │   MainWindow  ──  VideoPresenter (SwapChainPanel, one surface)     │  │
│  │   ├ Aspect-ratio letterbox      ├ NV12→RGB HLSL shader             │  │
│  │   ├ F11 / double-click fullscr. ├ Display rotation remap           │  │
│  │   └ Idle overlay · HUD · tray   └ no-video watchdog (5 s)          │  │
│  └────────────────────────────────────────────────────────────────────┘  │
│                                                                           │
│  Log (file, %LOCALAPPDATA%\AirPlayReceiver\logs) — see §6                 │
└───────────────────────────────────────────────────────────────────────────┘
```

`AirPlayService` owns everything above and does the wiring. Note which objects
are **per-connection** and which are **per-app-run**: iOS spreads one logical
session over many short-lived TCP connections, so `AirPlaySession` is
per-connection but `AirPlayVideoPlayer`, `HlsCoordinator`, `PhotoHandler`,
`ReverseChannelRegistry` and `VideoPresenter` all live for the app run. A
`displayCached` photo and the `cacheOnly` that preloaded it routinely arrive on
different sockets; so do `/reverse`, `/play` and `/action`.

### Layer Interactions

**Discovery → Protocol**

`MdnsService` publishes two DNS-SD records on port 7000:
- `_airplay._tcp` — advertises the AirPlay 2 mirroring endpoint.
- `_raop._tcp` — required for the iPhone to show the device in Screen Mirroring (legacy RAOP advertisement, still expected by iOS 16+).

Both records carry TXT key-value pairs that declare feature flags. The critical flags are `features` (a 64-bit hex bitmask), `model`, and `deviceid` (MAC-style string). iOS will silently ignore the device if `features` doesn't set bit 5 (screen mirroring) and bit 7 (video).

When the iPhone user taps the device in Screen Mirroring, iOS opens a TCP connection to port 7000 and begins the RTSP exchange.

**Protocol → Media**

The mirroring handshake proceeds as follows. `SETUP` has two phases — one
carrying the encrypted stream key, one requesting the streams — which arrive as
two requests on the same connection, or occasionally as one carrying both.

```
iPhone → Receiver:  OPTIONS * RTSP/1.0
Receiver → iPhone:  200 OK  (Public: ANNOUNCE, SETUP, RECORD, PAUSE, FLUSH, TEARDOWN, OPTIONS, GET_PARAMETER, SET_PARAMETER, POST, GET)

iPhone → Receiver:  GET /info
Receiver → iPhone:  200 OK             (binary plist: displays[], audio formats, features)

iPhone → Receiver:  POST /pair-setup   (TLV8 body, SRP step 1)
Receiver → iPhone:  200 OK             (TLV8 body, SRP response)

iPhone → Receiver:  POST /pair-verify  (Curve25519 ephemeral key)
Receiver → iPhone:  200 OK             (signed verify)      → shared ECDH secret

iPhone → Receiver:  POST /fp-setup     (FairPlay, 2 phases: 16B then 164B request)
Receiver → iPhone:  200 OK             (the 164B phase-2 request is kept: playfair's input)

iPhone → Receiver:  SETUP  phase 1     (binary plist: ekey 72B, eiv)
Receiver → iPhone:  200 OK  eventPort/timingPort;  ekey ─▶ Playfair ─▶ 16-byte AES key

iPhone → Receiver:  SETUP  phase 2     (streams[]: type 110 video, 96 audio; streamConnectionID)
Receiver → iPhone:  200 OK             (dataPort = the ephemeral ports we bound)

iPhone → Receiver:  RECORD
Receiver → iPhone:  200 OK

                    ─── video frames arrive on the mirror TCP dataPort ───
```

`RtspServer` owns the sockets; `AirPlaySession` implements one method per verb.
The key material converges in `MirrorStreamCrypto`: the AES key from
`Playfair.DecryptKey` is combined with the pairing ECDH secret and the
`streamConnectionID` to derive the stream key and IV
(`SHA512("AirPlayStreamKey"+id ‖ eaeskey)`), and the whole session's type-0 video
frames form one continuous AES-CTR keystream. Getting any input wrong produces
frames that decrypt to noise — visible as `rejected ≈ rx` in the `[Pipeline]`
line (§6), which is why that counter exists.

Mirroring video is **TCP**, not RTP: `MirrorStreamReceiver` binds an ephemeral
port, reports it as `dataPort`, and reads length-prefixed frames. `RtpReceiver`
(UDP 7011) and `AudioStreamReceiver` (AAC-ELD → `AudioDecoder` → `AudioOutput`,
WASAPI) handle the legacy and audio routes.

**Media → UI**

Each producer converts to CPU NV12 and calls `VideoPresenter.PresentFrame`:

| Producer | Source | Conversion |
|----------|--------|------------|
| `VideoDecoder` (mirroring) | H.264/HEVC Annex-B from `MirrorStreamReceiver` | D3D11VA decode, then `av_hwframe_transfer_data` down to CPU NV12 |
| `AirPlayVideoPlayer` (URL playback) | whatever `avformat` opened | libswscale → NV12 |
| `PhotoDecoder` (photos) | one JPEG per `PUT /photo` | mjpeg decode, libswscale → NV12, downscaled to ≤3840×2160 |

`PresentFrame` uploads the two planes with `UpdateSubresource`, runs the
NV12→RGB shader, and calls `IDXGISwapChain1::Present(1, 0)` (vsync-locked) —
all inside one lock, so a frame is never half-drawn. The presenter has no idea
which producer sent a frame; that is what lets photos inherit letterboxing,
rotation reset and F11 fullscreen with no rendering code of their own.

---

## 3. Open-Source References

### UxPlay (C++, actively maintained)
**Repo:** https://github.com/FDH2/UxPlay  
**Why it matters:** The definitive reference for the AirPlay 2 mirroring protocol on non-Apple hardware. Key files to study:

| File | What to learn from it |
|------|----------------------|
| `lib/raop.c` | RTSP verb dispatch, `pair-setup`/`pair-verify` TLV8 encoding |
| `lib/raop_handler.c` | AES key derivation from the pairing session, RTP stream setup |
| `lib/stream.c` | RTP packet reassembly and NAL unit extraction |
| `lib/crypto.c` | AES-CBC (AirPlay 1) and ChaCha20-Poly1305 (AirPlay 2) decryption |
| `renderers/video_renderer_ffmpeg.c` | FFmpeg decode loop, hardware acceleration setup |
| `lib/fcup_request.h` | The FCUP request plist and its opaque `User-Agent` — the basis of `HlsCoordinator` |
| `lib/http_handlers.h` | `http_handler_play` / `_action` / `_hls`: the playlist round trip |
| `lib/httpd.c` | Why reverse-HTTP (PTTH) responses must never reach the request parser — see `ReverseChannel` |

### RPiPlay (C, Raspberry Pi target)
**Repo:** https://github.com/FD-/RPiPlay  
Forked from an earlier UxPlay; the `lib/` directory is nearly identical. Useful for cross-checking the mDNS TXT record key-value pairs against a known-working implementation.

### Shairplay (C, RAOP/AirPlay 1 only)
**Repo:** https://github.com/juhovh/shairplay  
Older but historically important. Only covers RAOP audio — not useful for screen mirroring directly, but its `dnssd.c` shows a portable mDNS registration approach if you want to avoid a managed library dependency.

### AirplayReceiver (C#, partial)
**Repo:** https://github.com/CodeWithKweku/AirplayReceiver  
A C# proof-of-concept. Not production-ready, but shows the RTSP parser pattern in managed code which maps cleanly to what this project needs.

### Key Protocol Documents
- **Apple's AirPlay specification** is not public, but the community-maintained reverse-engineering notes live at:  
  https://openairplay.github.io/airplay-spec/  
  `src/photos/photo_caching.md` there is the source for the `X-Apple-AssetAction` behaviours and the 412 contract in §4.4.
- **TLV8 encoding** used in `pair-setup` / `pair-verify` follows HomeKit Accessory Protocol (HAP) conventions. Apple's HAP specification (publicly available) defines the TLV8 tag space.

---

## 4. Implemented Flows

Four flows are implemented. They share the discovery layer, the RTSP server and
the presenter, and diverge in how pixels are obtained.

Common to all of them: `MdnsService` advertises `_airplay._tcp` and `_raop._tcp`
on port 7000 with `features = 0x5A7FFFF7,0xE` — the value a real Apple TV 3
advertises. The high word matters: a wrong one lets iOS *resolve* the device but
hides it from the Screen Mirroring list. `flags` in the TXT record is flipped
between idle and active as sessions start and stop, which is what a real
receiver does.

### 4.1 Screen mirroring

The §2 handshake, then: `MirrorStreamReceiver` accepts the phone's TCP
connection on the `dataPort` it reported, reads length-prefixed frames,
decrypts type-0 (video) payloads through `MirrorStreamCrypto`'s continuous
AES-CTR keystream, caches the SPS/PPS from the type-1 codec-config frame and
prepends it to each IDR, then hands Annex-B NALs to `VideoDecoder`. Audio
(type 96, AAC-ELD) takes the parallel `AudioStreamReceiver` → `AudioDecoder` →
`AudioResampler` → `AudioOutput` (WASAPI via NAudio) path.

The whole chain is instrumented: the 5-second `[Pipeline]` line reports
`rx / rejected / submitted / decoded / presented`, so a black window names its
own cause instead of needing a bisect (§6).

### 4.2 AirPlay Video — direct URL

When an app AirPlays a video it does not stream pixels; it hands over a URL and
expects the receiver to be the player. `POST /play` carries a plist with
`Content-Location` and a start position; `HlsCoordinator.Classify` routes a
plain `http(s)://` URL straight to `AirPlayVideoPlayer.Play`, which opens it
with `avformat`, decodes video *and* audio, converts frames to NV12 with
libswscale and presents them.

Transport then arrives on further short-lived connections — `/rate` (play/pause),
`/scrub` (seek), `/stop`, `/playback-info` (the phone's scrubber polls it),
`/setProperty`, `/getProperty` — which is why the player is one long-lived object
rather than session state.

Details that are load-bearing rather than incidental:

- **User-Agent.** FFmpeg's default `Lavf/61.x` gets a 403 from googlevideo, so
  requests present as `AppleCoreMedia/…(Apple TV…)`.
- **Rotation.** Portrait phone clips are stored landscape plus a container
  rotation flag; `VideoPresenter.SetRotation` remaps the shader's sampling
  coordinates rather than rotating pixels.
- **A/V sync.** With a playable audio stream the audio clock is master and video
  is scheduled against it; with no audio, a wall clock anchored on the first
  frame's PTS.
- **Failure is reported.** A URL that won't open (dead host, 403, DRM) sets
  `Failed` and shows a HUD notice — silence there used to leave the phone
  spinning on 0:00 forever.

> **DRM.** FairPlay-Streaming content (Netflix, Disney+, Prime Video, the Apple
> TV app) cannot be played. The license exchange requires keys that never leave
> Apple silicon, so this is a permanent limitation, not a to-do.

### 4.3 AirPlay Video — FCUP/HLS (the YouTube path)

App-sourced HLS never gives the receiver a fetchable URL: `Content-Location` is
`mlhls://localhost/master.m3u8`, a scheme only the phone resolves. The playlists
have to be pulled *from* the phone, which needs a channel pointing the wrong way
down an HTTP connection:

```
phone → receiver   POST /reverse  Upgrade: PTTH/1.0     ┐ ReverseChannel takes
receiver → phone   101 Switching Protocols              ┘ ownership of the socket

phone → receiver   POST /play     Content-Location: mlhls://localhost/master.m3u8
receiver → phone   200 OK  (immediately — playlists must not block the reply)
                   HlsServer.Start()  — its bound port is the rewrite target

receiver → phone   POST /event    FCUP request, XML plist   ┐ one request in
phone → receiver   POST /action   binary plist, playlist    ┘ flight at a time,
                   … repeated: master first, then every child it lists …

                   queue drains → AirPlayVideoPlayer.Play(
                                    http://127.0.0.1:<port>/…/master.m3u8)
```

A second `/play` for an item already in the store skips the round trips entirely
and resumes from where it was — which is what makes YouTube's ad interruptions
(`playlistRemove` then `playlistInsert`) tolerable.

The pieces:

- **`ReverseChannel` / `ReverseChannelRegistry`.** After the 101 the socket's
  roles invert: outbound are *requests* to the phone, inbound are *responses*.
  The inbound half must never reach the RTSP request parser, so a drain loop
  reads and discards it (and detects the far end closing). `RtspServer` hands
  socket ownership to the registry instead of disposing it, keyed by
  `X-Apple-Session-ID`.
- **`HlsCoordinator`.** Issues FCUP requests strictly one at a time — the next
  may only follow the phone's response to the previous — and tags each with a
  request id so a late response for a superseded item (what happens when an ad
  is inserted) is discarded rather than mixed in.
- **`HlsPlaylistStore` / `HlsVideo`.** Caches the playlists per item, rewrites
  `mlhls://localhost` to the loopback origin, and expands the condensed media
  playlists into absolute CDN URLs.
- **`HlsServer`.** Binds **127.0.0.1 only** and serves playlists to our own
  demuxer. It never proxies media: segments are absolute googlevideo URLs the
  player fetches directly. Binding it to the LAN would republish someone's
  YouTube session and add an unauthenticated listener for no gain. It answers
  `http://127.0.0.1:<port>` and not `localhost`, because on Windows `localhost`
  resolves to `::1` first and would fail against this v4-only listener.
- **`XmlPlist` / `BinaryPlist`.** FCUP requests go out as XML plists; the
  phone's `/action` responses come back as binary plists with the playlist in a
  `<data>` blob.

Known limitations of this path:

- **VOD only.** Each playlist is fetched once and cached. A live stream's media
  playlist is a sliding window that would have to be re-requested over the
  reverse channel on a timer; that is not implemented, so YouTube **live**
  streams do not play. Uploaded videos do.
- **Segment-boundary stalls.** `AirPlayVideoPlayer`'s loop does `av_read_frame`,
  decode *and* present on one thread, so a slow segment fetch over WAN blocks
  presentation for as long as the fetch takes — observed as ~2–3 s freezes at
  some boundaries. The fix is a dedicated demuxer thread feeding a packet queue;
  it is not done.

### 4.4 AirPlay Photos

Photos is a protocol of its own: no `/play`, no FCUP, no HLS, no RTP. A raw JPEG
arrives on `PUT /photo` (the body is *not* a plist) and has to appear on screen.

```
PUT /photo HTTP/1.1
X-Apple-AssetKey:    F92F9B91-954E-4D63-BB9A-EEC771ADE6E8
X-Apple-AssetAction: cacheOnly | displayCached      (optional)
X-Apple-Transition:  Dissolve | SlideLeft | None …  (optional)
Content-Length:      462848

<JPEG DATA>
```

`AirPlaySession` routes it to `PhotoHandler`, which implements three behaviours:

| `X-Apple-AssetAction` | Body | Behaviour |
|-----------------------|------|-----------|
| absent | JPEG | Display now (and cache it — iOS may follow up with `displayCached` for the same key). |
| `cacheOnly` | JPEG | Store under the asset key, display nothing. This is iOS preloading the next photos. |
| `displayCached` | **empty** | Display the JPEG already stored under that key. |

**412 Precondition Failed is the load-bearing part.** A `displayCached` for a key
that was evicted or never seen must answer 412; that is the sender's cue to
re-push the bytes. Answering 200 there strands the previous photo on screen for
the rest of the session, because iOS believes the new one went up. It is also
what makes bounding the cache safe: `PhotoCache` is an LRU capped at 48 entries
and 192 MB (full-resolution iPhone JPEGs run several MB each), and eviction is
harmless precisely because 412 triggers a re-send.

Display goes through `PhotoDecoder`: mjpeg decode, libswscale to NV12,
downscaled to at most 3840×2160 with the aspect ratio preserved and even
dimensions (NV12 chroma is half-resolution), then out through the *same* sinks
`AirPlayVideoPlayer` uses. That reuse is the design: photos get letterboxing,
rotation reset, fullscreen and the idle overlay with no D3D11 changes, and there
is only ever one thing on the surface.

One subtlety worth knowing before touching it: JPEG luma is full-range [0,255]
while the presenter's shader expands from limited-range [16,235]. The conversion
must be requested through libswscale's **pixel format** (the deprecated `YUVJ*`
formats), not through `sws_setColorspaceDetails` — an identical source and
destination size selects an unscaled plane-copy converter that ignores the range
flags, so a photo needing no downscale would otherwise come out different from
one that did. `PhotoDecoder.AsFullRangeFormat` carries the measurement table;
`PhotoHarness --diag` reproduces it.

> **Slideshows are not implemented.** They are a much larger protocol: the
> receiver *pulls* each image with `GET /slideshows/1/assets/N` over three
> parallel `X-Apple-Purpose: slideshow` reverse channels and reads a binary-plist
> response body. `ReverseChannel`'s drain loop deliberately discards inbound
> bodies, so that needs a request/response correlator built on top of it. The
> slideshow endpoints answer 200 with an empty body and log the request, which
> is what the receiver already did — single-photo pushes are what works.

### 4.5 Where the flows meet

`TEARDOWN` — not a control connection merely closing — ends a Photos session,
because iOS opens several short-lived connections per session and any of them
dropping would otherwise wipe the photo still being looked at. That is also
where the `photo/stopped` event is sent back over the reverse channel, while it
is still open.

The three producers all write to one `VideoPresenter`. Its render lock means a
frame is never half-drawn, but there is **no mutual exclusion between modes** —
nothing stops a mirror frame and a photo from alternating on the surface if two
senders are active at once. In practice one phone does one thing at a time, so
this has not been worth the coordination; it is a known gap rather than a
guarantee.

---

## 5. Dependency Setup

### NuGet Packages

```xml
<PackageReference Include="Microsoft.WindowsAppSDK" Version="1.5.240802000" />
<PackageReference Include="Makaretu.Dns.Multicast" Version="0.27.0" />
<PackageReference Include="NAudio" Version="2.2.1" />
<PackageReference Include="FFmpeg.AutoGen" Version="7.1.1" />
<PackageReference Include="SharpDX" Version="4.2.0" />
<PackageReference Include="SharpDX.Direct3D11" Version="4.2.0" />
<PackageReference Include="SharpDX.DXGI" Version="4.2.0" />
<PackageReference Include="SharpDX.D3DCompiler" Version="4.2.0" />
<PackageReference Include="BouncyCastle.Cryptography" Version="2.6.2" />
<PackageReference Include="Microsoft.Win32.Registry" Version="5.0.0" />
```

> **BouncyCastle** is needed for SRP-6a and Ed25519 — the .NET 8 `System.Security.Cryptography` namespace does not include SRP.
> **NAudio** provides the WASAPI output for both the mirroring AAC-ELD stream and URL playback.
> **D3DCompiler** compiles the NV12→RGB shader at runtime, so no build-time HLSL step is needed.

The project targets `net8.0-windows10.0.22621.0`, runs **unpackaged**
(`WindowsPackageType=None`) so it can bind ports 7000/7011 without AppContainer
loopback restrictions, and is **x64 only**.

### Native FFmpeg DLLs

Download an `ffmpeg-n7.1-...-win64-gpl-shared` build from https://github.com/BtbN/FFmpeg-Builds/releases (the major version must match `FFmpeg.AutoGen` — 7.x → `avcodec-61`; do **not** grab an 8.x build, whose `avcodec-62` DLLs won't bind).  
Copy the DLLs from the build's `bin/` folder to `native/x64/`:

```
avcodec-61.dll
avdevice-61.dll
avfilter-10.dll
avformat-61.dll
avutil-59.dll
postproc-58.dll
swresample-5.dll
swscale-8.dll
```

Add to `.csproj`:
```xml
<ItemGroup>
  <Content Include="native\x64\*.dll">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
</ItemGroup>
```

### Vendored playfair.dll

`vendor/playfair/playfair.dll` **is** committed, along with the sources it was
cross-compiled from (`vendor/playfair/src/`, taken from RPiPlay's
`lib/playfair`). It performs Apple's FairPlay key unwrap: given the 164-byte
`fp-setup` phase-2 request and the 72-byte `ekey` from `SETUP`, it returns the
16-byte AES key that — combined with the pairing ECDH secret — derives the mirror
stream key. `Protocol/Playfair.cs` is the P/Invoke wrapper and the csproj copies
the DLL next to the executable. Without it mirroring decrypts to noise and the
window is black; the startup banner (§6) reports it explicitly for that reason.

---

## 6. Diagnostics

Debug output vanishes in Release builds, so the receiver writes its own log.

**File log.** One per run at
`%LOCALAPPDATA%\AirPlayReceiver\logs\airplay-<yyyyMMdd-HHmmss>-<pid>.log`, lines
formatted `HH:mm:ss.fff [level] [tag] message`, `AutoFlush` on so a crash still
leaves the last line on disk, and the ten most recent runs retained. Logging
never throws: every IO failure is swallowed and writes fall back to
`Debug.WriteLine`.

**Startup banner.** The first lines record app/OS/.NET versions and then one
`PRESENT`/`MISSING` line per native dependency — `avcodec-61`, `avutil-59`,
`swscale-8`, `swresample-5`, `playfair.dll` — each with the consequence of it
being absent. A user's log therefore answers "is the build complete?" before
anyone reads the protocol trace.

**`[Pipeline]` heartbeat.** While a mirroring session is live,
`MirrorStreamReceiver` emits a summary every five seconds (and one final line at
teardown, so short sessions still leave evidence):

```
[Pipeline] rx=312 rejected=0 submitted=312 decoded=311 presented=311 decrypt=yes (5s)
```

Reading it: `rx=0` means the stream never arrived (firewall); `rejected ≈ rx`
means the decryption key is wrong (FairPlay unwrap failed — check
`playfair.dll`); `submitted>0` with `decoded=0` is a decoder problem;
`decoded>0` with `presented=0` is a renderer problem. The counters are
`Interlocked` because an unexpected second connection would otherwise lose
increments.

**No-video watchdog.** `RECORD` only means the verb arrived. `MainWindow` checks
five seconds after a session starts whether any frame reached the presenter, and
if not puts "Session active but no video frames" and the log path on the HUD.

**Harnesses.** `tools/PhotoHarness` (photos, incl. `--diag` for the libswscale
colour-range table) and `tests/HlsHarness` (the whole FCUP loop against a fake
phone) run the real sources over loopback and exit non-zero on failure. See
BUILD.md §7.
