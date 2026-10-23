<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="Branding/wordmark-dark.svg">
    <img src="Branding/wordmark-light.svg" alt="AirGlass" width="446" height="128">
  </picture>
</p>

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/screenshots/hero-dark.png">
    <img src="docs/screenshots/hero-light.png" alt="AirGlass: the menu bar popover and the live stream on an iPhone" width="560">
  </picture>
</p>

---

AirGlass is a small, open-source app that mirrors your computer's screen, or a single window, live to the browser of a phone on the same Wi-Fi. Nothing gets installed on the phone: scan the QR code and the picture opens in Safari.

**Platforms:** macOS 14 or later (menu bar app) and Windows 10 (2004) / Windows 11 (notification area app). Both share the same design, the same viewer page and the same security rules.

## Features

- **Entirely local.** Video goes straight from your computer to your phone over your local network. The app makes no requests to the internet: no analytics, no update checks, no STUN/TURN servers.
- **Encrypted video.** The stream uses WebRTC (DTLS-SRTP). The video codec is H.264, which your phone decodes in hardware.
- **Watch without an app.** Safari (or any modern browser) on your phone is all you need.
- **One-time QR code.** The key inside the QR code stops working after the first connection. A new QR code appears when the viewer disconnects or you press disconnect.
- **One viewer at a time.** While someone is watching, no other device can connect.
- **View only.** No touch, mouse or keyboard input goes from your phone to your computer.
- **Whole screen or a single window.** The window (on macOS, the screen too) is chosen with your operating system's own content picker.
- **Always visible.** The icon changes while someone is watching.
- **Five languages.** English, Turkish, Mandarin Chinese, Hindi and Spanish. The app starts in your system language (English if it isn't one of these); change it under **Settings… → Language**. The page on the phone follows the phone's language.

<a id="kurulum"></a>

## Installation

### macOS

1. Download `AirGlass-<version>.dmg` from the [Releases](https://github.com/lupusoftco/airglass/releases/latest) page.
2. Open the `.dmg` and drag **AirGlass** into **Applications**.
3. Open AirGlass. The app is not notarized by Apple, so macOS blocks the first launch. To open it:
   1. Click **Done** in the warning.
   2. Open **System Settings → Privacy & Security**.
   3. Scroll down to **Security** and click **Open Anyway** next to the line saying AirGlass was blocked.
   4. Enter your password and choose **Open Anyway** again.

   You only need to do this once.

   **Alternatively,** remove the quarantine flag in Terminal and macOS won't block the app:

   ```sh
   xattr -dr com.apple.quarantine /Applications/AirGlass.app
   ```
4. The AirGlass icon appears in the menu bar; there is no Dock icon.
5. The first time you pick a source, macOS asks for **Screen Recording** permission. Allow AirGlass under **System Settings → Privacy & Security → Screen & System Audio Recording**, then quit and reopen AirGlass.
6. macOS may also ask for **local network** access and, if the firewall is on, for **incoming connections**. Allow both; they are how the phone reaches the Mac.

<a id="windows-2"></a>

### Windows

1. Download `AirGlass-<version>.exe` from the [Releases](https://github.com/lupusoftco/airglass/releases/latest) page. There is no installer; it is a single file and needs no .NET installation.
2. Run it. The app is not signed, so Windows SmartScreen may say "Windows protected your PC": click **More info**, then **Run anyway**. You only need to do this once.
3. The AirGlass icon appears in the notification area at the right of the taskbar (if you don't see it, it is under the **^** arrow). Click it to open the popover; right-click for **About AirGlass**, **Settings…** and **Quit**.
4. On first launch **Windows Defender Firewall** asks about AirGlass. Keep **Private networks** checked and click **Allow access**; this is how the phone reaches the PC. If your Wi-Fi is set to **Public** in Windows, either make it **Private** (**Settings → Network & internet → Wi-Fi → your network's properties**) or also check **Public networks** in that dialog.

## How it works

**1. Pick a source and scan the QR code.** Click the AirGlass icon in the menu bar (on Windows, in the notification area). Choose **Whole screen** or **Pick a window**, then scan the QR code with your phone's camera and tap the link.

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/screenshots/popover-waiting-dark.png">
    <img src="docs/screenshots/popover-waiting-light.png" alt="Waiting popover: QR code, copy link, source picker" width="280">
  </picture>
</p>

**2. The phone connects.** Safari opens and connects to your computer; nothing is installed on the phone.

<p align="center">
  <img src="docs/screenshots/phone-connecting.png" alt="Connecting screen on the phone" width="180">
</p>

**3. Your screen, on the phone.** The picture arrives encrypted over your local network. Tap it to go fullscreen; rotating the phone works too.

<p align="center">
  <img src="docs/screenshots/phone-live.png" alt="Live stream on the phone, portrait and landscape" width="560">
</p>

**4. Press Disconnect to stop.** While someone is watching, the popover shows the connected device and the menu bar icon changes.

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/screenshots/popover-connected-dark.png">
    <img src="docs/screenshots/popover-connected-light.png" alt="Connected popover: device, shared window, Disconnect" width="280">
  </picture>
</p>

**5. A new QR code.** The phone shows "Disconnected". The QR code works only once, so the computer shows a fresh one right away.

<p align="center">
  <img src="docs/screenshots/phone-disconnected.png" alt="Disconnected screen on the phone" width="180">
</p>

<sub>Images are rendered from AirGlass's interface design. On Windows the popover opens above the notification area icon, with the same content. The screenshots show the Turkish interface.</sub>

## Building from source

### macOS

Requirements: macOS 14 (Sonoma) or later, Xcode 16 or later, [Homebrew](https://brew.sh).

```sh
brew install xcodegen
git clone https://github.com/lupusoftco/airglass.git
cd airglass
cp Config/Local.xcconfig.example Config/Local.xcconfig
```

Put your own Team ID in the `DEVELOPMENT_TEAM` line of `Config/Local.xcconfig`. The free "Personal Team" is enough; you can find the Team ID under **Xcode → Settings → Accounts**. This file is not committed. Without a Team ID the app is ad-hoc signed; it still runs, but macOS asks for Screen Recording permission again after every build.

```sh
make run    # generates the project, builds and launches AirGlass
make dmg    # creates dist/AirGlass-<version>.dmg
make clean  # removes build output
```

To work in Xcode, run `make project` and open the generated `AirGlass.xcodeproj`.

### Windows

Requirements: Windows 10 (2004) or later, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0); to build the exe, also [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows) (`pwsh`).

```powershell
git clone https://github.com/lupusoftco/airglass.git
cd airglass
dotnet run --project Windows/AirGlass        # builds and launches AirGlass
pwsh scripts/windows-publish.ps1             # creates and tests dist/AirGlass-<version>.exe
```

`windows-publish.ps1` builds the exe and tests it by running it briefly, so quit any running AirGlass first. The Windows app lives in `Windows/AirGlass` (WPF, C#). The viewer page on the phone is shared by both apps: the files in `Viewer/` are embedded in the exe.

## Known limitations

- **The computer and the phone must be on the same Wi-Fi.** Connections only work on the local network.
- **Guest networks and networks with client isolation don't work.** Hotel, café and corporate guest networks that stop devices from seeing each other keep the phone from reaching the computer.
- **Keeping the screen awake may pause music.** Safari does not offer the Wake Lock API on pages served over http, so AirGlass plays an invisible, silent video after the first tap. That can pause music or podcasts playing on the phone. It isn't needed in fullscreen.
- **No audio;** video only.
- **One viewer at a time.**
- **The page and the signaling travel over unencrypted HTTP on the local network;** the video itself is encrypted. The one-time key travels in the URL fragment (`#`), so it never appears in server logs or HTTP requests.
- **The app is not notarized (macOS) or signed (Windows),** so the first launch needs the "Open Anyway" step on macOS and "Run anyway" in SmartScreen on Windows.
- **On Windows, Pick a window**, and **Whole screen** with more than one monitor, open Windows' own capture picker; on Windows 10 a yellow border marks what is being captured.

## License

[MIT](LICENSE)
