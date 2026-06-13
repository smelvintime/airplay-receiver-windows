# AirPlay Receiver for Windows — Architecture Blueprint

## Table of Contents
1. [Technology Stack Decision](#1-technology-stack-decision)
2. [Architecture Overview](#2-architecture-overview)
3. [Open-Source References](#3-open-source-references)
4. [Step-by-Step Implementation Plan](#4-step-by-step-implementation-plan)
5. [Dependency Setup](#5-dependency-setup)

---

## 1. Technology Stack Decision

### UI Framework — WinUI 3 (Windows App SDK)

**Chosen over WPF and Qt for these reasons:**

- WinUI 3 gives you a `SwapChainPanel` — a first-class DirectX 11/12 swap chain host that lives inside the XAML visual tree. This is the single most important rendering primitive: you get a native D3D11 surface with zero copy from the decoder, while the rest of the window remains a normal XAML app.
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

- `FFmpeg.AutoGen` (NuGet) provides P/Invoke bindings to the native ffmpeg DLLs.
- Set `AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA` on the codec context for zero-copy GPU decode straight into a `ID3D11Texture2D`.
- Fallback: `AV_HWDEVICE_TYPE_DXVA2` for Windows 10 1607 compatibility.
- Windows Media Foundation (MF) is an alternative but its H.265 decoder is codec-pack-dependent on Windows 10, and MF's async callback model adds complexity without latency benefit.

### Rendering — Direct3D 11 via `SwapChainPanel`

- The decoded `ID3D11Texture2D` (NV12 or P010 pixel format) is converted to RGBA via a trivial HLSL pixel shader (YUV→RGB) and presented to a `IDXGISwapChain1` bound to the `SwapChainPanel`.
- This path has **zero CPU-side copies** for the video plane: decode → GPU texture → shader → present.

---

## 2. Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                        iPhone (iOS)                             │
│   Screen Mirroring picker  →  mDNS browse  →  RTSP connect     │
└──────────────────────────┬──────────────────────────────────────┘
                           │  Wi-Fi (same subnet)
┌──────────────────────────▼──────────────────────────────────────┐
│                  AirPlay Receiver (Windows)                     │
│                                                                 │
│  ┌─────────────────┐   ┌──────────────────┐  ┌──────────────┐  │
│  │  Discovery Layer│   │  Protocol Layer  │  │  Media Layer │  │
│  │                 │   │                  │  │              │  │
│  │  MdnsService    │──▶│  RtspServer      │  │  RtpReceiver │  │
│  │  _airplay._tcp  │   │  ├ OPTIONS       │  │  ├ UDP 7011  │  │
│  │  _raop._tcp     │   │  ├ GET_PARAMETER │  │  └ H.264/    │  │
│  │  port 7000      │   │  ├ POST /pair-*  │  │    HEVC NALUs│  │
│  └─────────────────┘   │  ├ SETUP        │  └──────┬───────┘  │
│                        │  ├ RECORD       │         │          │
│                        │  └ TEARDOWN     │  ┌──────▼───────┐  │
│                        └──────────────────  │  VideoDecoder│  │
│                                             │  FFmpeg+D3D11│  │
│  ┌─────────────────────────────────────────▼──────────────┐   │
│  │                   UI Layer (WinUI 3)                    │   │
│  │   MainWindow  ──  VideoPresenter (SwapChainPanel)       │   │
│  │   ├ Drag/Resize/Maximize                                │   │
│  │   ├ Aspect-ratio letterbox                              │   │
│  │   └ F11 / double-click → borderless fullscreen          │   │
│  └─────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
```

### Layer Interactions

**Discovery → Protocol**

`MdnsService` publishes two DNS-SD records on port 7000:
- `_airplay._tcp` — advertises the AirPlay 2 mirroring endpoint.
- `_raop._tcp` — required for the iPhone to show the device in Screen Mirroring (legacy RAOP advertisement, still expected by iOS 16+).

Both records carry TXT key-value pairs that declare feature flags. The critical flags are `features` (a 64-bit hex bitmask), `model`, and `deviceid` (MAC-style string). iOS will silently ignore the device if `features` doesn't set bit 5 (screen mirroring) and bit 7 (video).

When the iPhone user taps the device in Screen Mirroring, iOS opens a TCP connection to port 7000 and begins the RTSP exchange.

**Protocol → Media**

The RTSP handshake proceeds as follows:

```
iPhone → Receiver:  OPTIONS * RTSP/1.0
Receiver → iPhone:  200 OK  (Public: ANNOUNCE, SETUP, RECORD, PAUSE, FLUSH, TEARDOWN, OPTIONS, GET_PARAMETER, SET_PARAMETER, POST, GET)

iPhone → Receiver:  POST /pair-setup   (TLV8 body, SRP step 1)
Receiver → iPhone:  200 OK             (TLV8 body, SRP response)

iPhone → Receiver:  POST /pair-verify  (Curve25519 ephemeral key)
Receiver → iPhone:  200 OK             (signed verify)

iPhone → Receiver:  SETUP              (stream type, control port, timing port)
Receiver → iPhone:  200 OK             (server_port=7011, ...)

iPhone → Receiver:  RECORD
Receiver → iPhone:  200 OK

                    ─── RTP stream begins on UDP port 7011 ───
```

`RtspServer` parses incoming lines, dispatches to handler methods per RTSP verb, and passes the negotiated AES key + IV to `RtpReceiver` for packet decryption (AirPlay 1 uses AES-CBC; AirPlay 2 uses ChaCha20-Poly1305 — see UxPlay `raop_handler.c` for exact key derivation).

**Media → UI**

`RtpReceiver` reassembles RTP packets into complete NAL units, passes them to `VideoDecoder` which feeds them into FFmpeg's `avcodec_send_packet`. The decoder returns a `AVFrame` backed by a `ID3D11Texture2D` (hardware frame). `VideoPresenter` consumes these frames on the render thread, running a HLSL NV12→RGBA shader, then calls `IDXGISwapChain1::Present(1, 0)` (vsync-locked, 1 interval) to minimise tearing without excessive latency.

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
- **TLV8 encoding** used in `pair-setup` / `pair-verify` follows HomeKit Accessory Protocol (HAP) conventions. Apple's HAP specification (publicly available) defines the TLV8 tag space.

---

## 4. Step-by-Step Implementation Plan

### Phase 0 — Project Scaffolding (Day 1)
- Create WinUI 3 / Windows App SDK project (`AirPlayReceiver.csproj`).
- Add NuGet packages: `Makaretu.Dns.Multicast`, `FFmpeg.AutoGen`, `SharpDX.Direct3D11`, `SharpDX.DXGI`.
- Add native FFmpeg DLLs as content (`avcodec-61.dll`, `avutil-59.dll`, `swscale-8.dll`).
- Verify the app builds and shows a blank window.

### Phase 1 — Discovery (Days 2–3)
- Implement `MdnsService`: advertise `_airplay._tcp` and `_raop._tcp` on port 7000.
- Set `features` bitmask to `0x5A7FFFF7,0x1E` (UxPlay's known-good value). The high word `0x1E` carries the unified pair-setup / MFi capability bits that modern iOS requires before it will show the device in the Screen Mirroring picker — a high word of `0x1` lets iOS resolve the device but hides it from the list.
- Set `deviceid` to the machine's MAC address (any adapter).
- **Test:** open iPhone → Control Center → Screen Mirroring. Your device name should appear.

### Phase 2 — RTSP Handshake / Pairing (Days 4–8)
- Implement `RtspServer`: `TcpListener` on port 7000, read lines until `\r\n\r\n`, parse headers.
- Handle `OPTIONS` — return the supported method list.
- Handle `GET_PARAMETER` — return empty body (iOS polls this to keep the connection alive).
- Handle `POST /pair-setup` — implement SRP-6a with the pairing PIN (or no-PIN for screen mirroring in most iOS versions). Reference UxPlay `raop_handler.c` lines 150–300.
- Handle `POST /pair-verify` — Curve25519 ECDH, sign with Ed25519. Reference UxPlay `crypto.c`.
- Handle `SETUP` — parse the requested stream type (`96` = H.264, `110` = HEVC), open UDP port 7011 for RTP, respond with negotiated ports.
- Handle `RECORD` — signal the media pipeline to start.
- **Test:** Wireshark capture on the Wi-Fi adapter (`tcp.port == 7000`) — verify the full OPTIONS→pair-setup→pair-verify→SETUP→RECORD exchange completes with 200 OK at each step.

### Phase 3 — RTP Ingestion & Decryption (Days 9–11)
- Implement `RtpReceiver`: `UdpClient` on port 7011.
- Parse the 12-byte RTP fixed header: `V=2, PT, Sequence, Timestamp, SSRC`.
- Decrypt payload using AES-CBC key+IV derived in Phase 2 (AirPlay 1) or ChaCha20-Poly1305 (AirPlay 2 — key in the `SETUP` response extension header).
- Reassemble RTP fragments into complete H.264 Annex-B NAL units (handle FU-A fragmentation per RFC 6184).
- **Test:** Write raw NAL units to a `.h264` file and play with `ffplay raw_video.h264 -f h264`.

### Phase 4 — Decode & Render (Days 12–16)
- Implement `VideoDecoder`: initialize FFmpeg with `AV_HWDEVICE_TYPE_D3D11VA`.
- Feed NAL units via `av_packet_from_data` → `avcodec_send_packet` → `avcodec_receive_frame`.
- Implement `VideoPresenter`: bind a `SwapChainPanel` to a `IDXGISwapChain1`.
- Write HLSL pixel shader to convert NV12 → RGBA.
- On each decoded frame: copy `ID3D11Texture2D` to render target, dispatch shader, present.
- **Test:** Live mirroring should appear. Measure latency with a stopwatch display on the iPhone.

### Phase 5 — UI Polish (Days 17–20)
- Aspect ratio letterboxing: on `SizeChanged`, compute the largest rectangle fitting the stream's `width:height` ratio and update `SwapChainPanel` margin/size.
- Fullscreen toggle: F11 and double-click → `AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen)` then hide title bar.
- Window drag on custom title bar: `SetTitleBar(titleBarGrid)`.
- Status overlay: receiver name, resolution, codec (H.264/HEVC), and frame rate — drawn as a XAML `TextBlock` layered over the `SwapChainPanel`.

### Phase 6 — Hardening (Days 21–25)
- Graceful TEARDOWN handling — stop RTP, release decoder, show idle screen.
- Reconnection loop — re-register mDNS and re-listen after disconnect.
- Multi-display awareness — remember which monitor was fullscreen.
- Latency tuning — experiment with `Present(0, 0)` (no vsync) vs `Present(1, 0)` and a dedicated decode thread with lock-free ring buffer.

---

## 5. Dependency Setup

### NuGet Packages

```xml
<PackageReference Include="Makaretu.Dns.Multicast" Version="0.27.0" />
<PackageReference Include="FFmpeg.AutoGen" Version="6.1.0" />
<PackageReference Include="SharpDX" Version="4.2.0" />
<PackageReference Include="SharpDX.Direct3D11" Version="4.2.0" />
<PackageReference Include="SharpDX.DXGI" Version="4.2.0" />
<PackageReference Include="BouncyCastle.Cryptography" Version="2.3.0" />
```

> **BouncyCastle** is needed for SRP-6a and Ed25519 — the .NET 8 `System.Security.Cryptography` namespace does not include SRP.

### Native FFmpeg DLLs

Download the latest `ffmpeg-n6.x-win64-gpl-shared` build from https://github.com/BtbN/FFmpeg-Builds/releases.  
Copy to `native/x64/`:

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
