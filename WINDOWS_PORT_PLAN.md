# AirGlass Windows Port - Plan

## Proje Özeti
AirGlass: macOS menu bar uygulaması - Mac ekranını iPhone'a lokal Wi-Fi üzerinden WebRTC ile aktarır.

**Hedef**: Windows sürümü yazılması (Windows 10/11, system tray app)

---

## Mevcut Mimari (macOS)

### Bileşenler
1. **ScreenCapturer** (Capture/) - ScreenCaptureKit kullanarak ekran yakalanması
2. **VideoPipeline** (Streaming/) - H.264 encoding + WebRTC stream
3. **LocalServer** (Server/) - HTTP + WebSocket server, viewer assets sunma
4. **PeerSession** (Streaming/) - WebRTC peer connection yönetimi
5. **UI** - SwiftUI menu bar app + web viewer (Safari)

### Teknoloji Stack
- **UI**: SwiftUI (macOS-only)
- **Capture**: ScreenCaptureKit (macOS 13+)
- **Encoding**: Video Toolbox (macOS)
- **Networking**: URLSession, WebRTC (cross-platform)
- **Server**: URLSession (basic HTTP)

---

## Windows Port Stratejisi

### Phase 1: Altyapı & Prototip (2-3 hafta)
**Hedef**: Ekran yakala → H.264 encode → WebRTC stream
- [ ] Project setup (C#/.NET 8 veya C++ with WinRT)
- [ ] DXGI desktop duplication API (screen capture)
- [ ] MediaFoundation H.264 encoder integration
- [ ] Windows system tray app shell
- [ ] Basic HTTP/WebSocket server

### Phase 2: WebRTC Integration (1-2 hafta)
**Hedef**: Capture → Encode → WebRTC → QR
- [ ] WebRTC library (libwebrtc via C# binding)
- [ ] Video track setup
- [ ] SDP negotiation & ICE
- [ ] QR code generation & display

### Phase 3: UI & Polish (1 hafta)
**Hedef**: Üretim-ready UI
- [ ] System tray icon (active/idle toggle)
- [ ] Window/display picker (Windows API)
- [ ] Status UI (streaming, connections)
- [ ] Settings panel

---

## Teknik Kararlar

### 1. Hangi Dil & Framework?
**Seçenek A**: C# + .NET 8 + WinUI 3
- ✅ Hızlı geliştirme
- ✅ System tray kolay
- ✅ Windows native APIs (WinRT)
- ❌ .NET runtime gerekli (çoğu Windows 11'de var)
- **Önerilen**: Başla bununla

**Seçenek B**: C++ + WinRT + DirectX
- ✅ Native, single exe
- ✅ Performans
- ❌ Kompleks, daha uzun
- **İkinci tercih**: Phase 2 sonra optimization için

### 2. Screen Capture API
**Windows.Graphics.Capture (WinRT)**
- Simpler API, Phase 1 için ideal
- Recommended for fast prototyping

**DXGI** (Direct3D Desktop Duplication)
- Phase 2'de optimize için

### 3. WebRTC Library
**libwebrtc** + C# binding
- Cross-platform, Linux port için yol açar

### 4. Encoder
**MediaFoundation H.264**
- Free, Windows native, HW accel capable

---

## Dosya Yapısı (Önerilen)

```
Windows/
├── AirGlass.Windows/          # Main WinUI app
│   ├── AirGlass.Windows.csproj
│   └── Program.cs
├── AirGlass.Capture/          # Screen capture
│   └── ScreenCapture.cs
├── AirGlass.Encoding/         # H.264 encoding
│   └── H264Encoder.cs
├── AirGlass.Streaming/        # WebRTC
│   ├── VideoPipeline.cs
│   └── PeerSession.cs
└── AirGlass.Server/           # HTTP server
    └── LocalHttpServer.cs
```

---

## Next Step
Mac'de çalıştır: `dotnet build` → Phase 1 prototip

---

**Status**: Phase 1 başlangıcı  
**Hedef**: Q1 2026 çıkış
