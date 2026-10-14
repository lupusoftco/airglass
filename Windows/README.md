# AirGlass Windows Port

Wireless screen mirroring from Windows to iPhone over local network.

## Quick Start

### Prerequisites
- **OS**: Windows 10 (build 19041) or Windows 11
- **.NET SDK**: 8.0+ (download from dotnet.microsoft.com)
- **IDE**: Visual Studio 2024 (optional, or use VS Code)

### Build
```bash
cd Windows
dotnet restore
dotnet build
```

### Run
```bash
dotnet run --project AirGlass.Windows
```

## Project Structure

```
Windows/
├── AirGlass.Windows/         # Main application (entry point)
├── AirGlass.Capture/         # Screen capture (Windows.Graphics.Capture)
├── AirGlass.Encoding/        # H.264 encoder (MediaFoundation)
├── AirGlass.Streaming/       # WebRTC pipeline
└── AirGlass.Server/          # HTTP + WebSocket server
```

## Development Status

### Phase 1 - Screen Capture (Current)
- [ ] Windows.Graphics.Capture proof-of-concept
- [ ] Capture single frame to bitmap
- [ ] 60 FPS capture loop
- [ ] MediaFoundation H.264 encoding

### Phase 2 - WebRTC Integration
- [ ] libwebrtc C# binding
- [ ] Video track setup
- [ ] SDP offer/answer
- [ ] QR code generation

### Phase 3 - UI & Polish
- [ ] System tray application
- [ ] Window/display picker
- [ ] Status indicator
- [ ] Settings panel

## Architecture

**Capture** → **Encode** → **Stream** → **iPhone**

- **ScreenCapture**: Grabs frames from desktop using Windows.Graphics.Capture
- **H264Encoder**: Encodes raw frames to H.264 bitstream (MediaFoundation)
- **VideoPipeline**: Orchestrates capture → encode → WebRTC flow
- **PeerSession**: Manages WebRTC peer connection
- **LocalHttpServer**: Serves viewer HTML & signaling

## Testing

```bash
dotnet test
```

## Resources

- [WINDOWS_PORT_PLAN.md](../WINDOWS_PORT_PLAN.md) — Detailed 3-phase strategy
- [Windows API Docs](https://learn.microsoft.com/en-us/windows)
- [MediaFoundation](https://learn.microsoft.com/en-us/windows/win32/medfound/microsoft-media-foundation-sdk)
- [Windows.Graphics.Capture](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture)

---

**Status**: Early development  
**Target**: Q1 2026 release
