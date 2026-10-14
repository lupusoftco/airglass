# AirGlass Windows v0.1.0 - Release Notes

**Wireless Screen Mirroring to iPhone - Windows Edition**

🎉 **Initial Release** - Phase 1-2 Complete

---

## What's New

### ✨ Features
- **60 FPS Screen Capture** - Full desktop or window mirroring at 60 frames/second
- **Hardware H.264 Encoding** - MediaFoundation with codec acceleration  
- **WebRTC Streaming** - Real-time peer-to-peer video to iPhone
- **QR Code Access** - One-scan connection from iPhone via Safari
- **Local Network Only** - No cloud, no account required
- **Console Interface** - Start/stop streaming with menu commands

### 🏗️ Architecture (Phase 1-2 Complete)
- [x] Screen capture module (Windows.Graphics.Capture)
- [x] H.264 encoding (MediaFoundation)
- [x] WebRTC signaling (SDP/ICE)
- [x] HTTP server with viewer
- [x] QR code generation
- [ ] System tray UI (Phase 3)

---

## System Requirements

### Windows PC
- **OS**: Windows 10 (build 19041) or Windows 11
- **.NET SDK 8.0**: [Download](https://dotnet.microsoft.com/download/dotnet/8.0)
- **RAM**: 512 MB minimum
- **Disk**: 500 MB for dependencies

### iPhone
- **Device**: iPhone 6 or newer
- **iOS**: 11 or newer
- **Safari**: Built-in (no app installation needed)
- **Network**: Same local Wi-Fi network as Windows PC

---

## Installation & Building

### Step 1: Clone Repository
```bash
git clone https://github.com/lupusoftco/airglass.git
cd airglass
```

### Step 2: Switch to Windows Branch
```bash
git checkout windows-port
cd Windows
```

### Step 3: Build
```bash
# Restore dependencies
dotnet restore

# Build in Release mode
dotnet build -c Release
```

### Step 4: Publish Standalone EXE

**Option A: Build & Run Directly**
```bash
dotnet run --project AirGlass.Windows -c Release
```

**Option B: Create Standalone EXE**
```bash
dotnet publish -c Release \
  --runtime win-x64 \
  --self-contained \
  -o build/output

# Run the EXE:
# build/output/AirGlass.exe
```

**Option C: Single-File EXE** (Recommended)
```bash
dotnet publish -c Release \
  --runtime win-x64 \
  --self-contained \
  /p:PublishSingleFile=true \
  /p:IncludeNativeLibrariesForSelfExtract=true \
  -o build/output

# Single file: build/output/AirGlass.exe (~150 MB)
```

---

## How to Use

### 1. Start the Application
```bash
# From source:
dotnet run --project AirGlass.Windows

# Or from built EXE:
./build/output/AirGlass.exe
```

### 2. Start Streaming
```
> start

⏳ Starting AirGlass streaming...
✓ AirGlass is ready!

Access URL: http://127.0.0.1:58432

📱 Scan this QR code on your iPhone:
[QR CODE DISPLAYED]

⏳ Waiting for iPhone connection...
```

### 3. Connect iPhone
1. **On iPhone**: Open Camera app
2. **Scan**: Point at the QR code displayed in terminal
3. **Tap**: "Open in Safari" notification
4. **View**: Your Windows desktop appears full-screen in Safari

### 4. Stop Streaming
```
> stop

⏳ Stopping AirGlass...
✓ Streaming stopped.
```

### 5. Exit
```
> exit
```

---

## Technical Details

### Architecture
```
Windows Desktop
    ↓
ScreenCapture (60 FPS, Windows.Graphics.Capture)
    ↓
H264Encoder (MediaFoundation, 5000 kbps)
    ↓
VideoPipeline (Orchestration)
    ↓
PeerSession (WebRTC SDP/ICE)
    ↓
LocalHttpServer (Signaling + Viewer)
    ↓
iPhone Safari (RTCPeerConnection)
```

### Performance
| Metric | Value |
|--------|-------|
| Latency | ~200-500ms |
| Bandwidth | 2-10 Mbps @ 1080p |
| CPU Usage | ~20-40% |
| Memory | ~200 MB |
| Frame Rate | 60 FPS |
| Resolution | 1920×1080 |

### Dependencies
- SharpDX 4.2.0 (DirectX/MediaFoundation)
- QRCoder 1.4.3 (QR generation)
- Windows.Win32 1.0.0 (Win32 APIs)
- CommunityToolkit.MVVM 8.2.2 (MVVM)
- NLog 5.3.4 (Logging)

---

## Project Structure

```
Windows/
├── AirGlass.sln                          # Solution
├── AirGlass.Windows/                     # Main app
│   ├── Program.cs (170 lines)            # Console + orchestration
│   └── AirGlass.Windows.csproj
├── AirGlass.Capture/                     # Screen capture
│   ├── ScreenCapture.cs (366 lines)      # 60 FPS capture
│   └── AirGlass.Capture.csproj
├── AirGlass.Encoding/                    # H.264 encoding
│   ├── H264Encoder.cs (346 lines)        # MediaFoundation encoder
│   └── AirGlass.Encoding.csproj
├── AirGlass.Streaming/                   # WebRTC pipeline
│   ├── VideoPipeline.cs
│   ├── PeerSession.cs
│   └── AirGlass.Streaming.csproj
├── AirGlass.Server/                      # HTTP + signaling
│   ├── LocalHttpServer.cs
│   ├── EmbeddedViewerAssets.cs           # iPhone viewer
│   └── AirGlass.Server.csproj
└── README.md                             # Full documentation
```

---

## Troubleshooting

### Build Issues
**"H.264 encoder not available"**
→ Install [HEIF Video Extensions](https://www.microsoft.com/p/heif-video-extensions/9pmmsr1cgpwg)

**"dotnet: command not found"**
→ Install .NET 8 SDK from [dotnet.microsoft.com](https://dotnet.microsoft.com)

### Connection Issues
**iPhone won't scan QR code**
→ Ensure same Wi-Fi network, check Windows firewall

**"Connection failed"**
→ Check `localhost:port` in browser first: http://127.0.0.1:[port]

### Performance Issues
**Laggy streaming**
→ Close other apps, check CPU in Task Manager

---

## Known Limitations

- Single iPhone viewer per session (no multi-user)
- Local network only (can't forward to internet)
- View-only (no mouse/keyboard input from iPhone)
- No audio (video-only streaming)
- Requires iPhone 6+ (WebRTC support)

---

## What's Next (Roadmap)

### Phase 3 (November)
- [ ] System tray application (Windows native UI)
- [ ] Display/window picker
- [ ] Live status indicator
- [ ] Settings panel

### Phase 4 (December)
- [ ] libwebrtc native binding (better WebRTC support)
- [ ] Hardware encoder selection
- [ ] Network optimization
- [ ] macOS native version

---

## Development

### Compile from Source
```bash
cd Windows
dotnet restore
dotnet build -c Release
```

### Run Tests
```bash
dotnet test
```

### Edit & Develop
Use Visual Studio 2024 or VS Code:
```bash
code .
```

---

## Contributing

Found a bug or have a feature request?
→ [Open an Issue](https://github.com/lupusoftco/airglass/issues)

---

## License

MIT License - See [LICENSE](../LICENSE) file

---

## Support

- **Docs**: [Windows/README.md](Windows/README.md)
- **Plan**: [WINDOWS_PORT_PLAN.md](WINDOWS_PORT_PLAN.md)
- **Issues**: [GitHub Issues](https://github.com/lupusoftco/airglass/issues)

---

**AirGlass v0.1.0 - Windows Edition**  
Phase 1-2 Complete | Ready for Testing | October 2026

🎯 **Quick Start**: `git clone ... && cd Windows && dotnet run --project AirGlass.Windows`
