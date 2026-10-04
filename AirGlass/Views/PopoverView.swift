import SwiftUI

/// The menu bar popover, following the AirGlass design: 260 pt wide,
/// 10/8/6 pt padding, 10 pt between rows. The window's glass, corner radius
/// and shadow come from the system menu bar window.
struct PopoverView: View {
    @Environment(AppState.self) private var appState

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HeaderView()

            switch appState.popoverState {
            case .waiting:
                WaitingContent()
            case .connected(let deviceName, let address):
                ConnectedContent(deviceName: deviceName, address: address)
            case .permissionNeeded:
                PermissionContent()
            case .offline(let message):
                OfflineContent(message: message)
            }

            SeparatorLine()
            VStack(spacing: 0) {
                MenuRow("AirGlass Hakkında") {
                    AboutPanel.show()
                }
                MenuRow("Çıkış", shortcut: "⌘Q") {
                    NSApplication.shared.terminate(nil)
                }
                .keyboardShortcut("q")
            }
        }
        .padding(EdgeInsets(top: 10, leading: 8, bottom: 6, trailing: 8))
        .frame(width: 260)
    }
}

// MARK: - Header

private struct HeaderView: View {
    @Environment(AppState.self) private var appState

    var body: some View {
        HStack(spacing: 8) {
            Image("Logo")
                .resizable()
                .frame(width: 16.3, height: 15)
            Text("AirGlass")
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(Theme.text)
            Spacer(minLength: 0)
            StatusBadge(state: appState.popoverState)
        }
        .frame(height: 26)
        .padding(EdgeInsets(top: 2, leading: 4, bottom: 0, trailing: 4))
    }
}

private struct StatusBadge: View {
    let state: PopoverState

    var body: some View {
        HStack(spacing: 6) {
            switch state {
            case .waiting:
                hollowDot
                Text("Bekliyor")
            case .connected:
                Circle().fill(Theme.green).frame(width: 7, height: 7)
                Text("Yayında")
            case .permissionNeeded:
                Circle().fill(Theme.orange).frame(width: 7, height: 7)
                Text("İzin gerekli")
            case .offline:
                hollowDot
                Text("Çevrimdışı")
            }
        }
        .font(.system(size: 11, weight: .medium))
        .foregroundStyle(Theme.secondaryText)
    }

    private var hollowDot: some View {
        Circle()
            .strokeBorder(Theme.secondaryText, lineWidth: 1.5)
            .frame(width: 7, height: 7)
    }
}

// MARK: - 1 · Waiting for a viewer

private struct WaitingContent: View {
    @Environment(AppState.self) private var appState

    var body: some View {
        if let url = appState.viewerURL {
            VStack(spacing: 10) {
                QRCodeView(content: url)
                VStack(spacing: 2) {
                    Text("Telefonunun kamerasıyla okut")
                        .font(.system(size: 12, weight: .medium))
                        .foregroundStyle(Theme.text)
                    Text(appState.viewerHost ?? "")
                        .font(.system(size: 11, design: .monospaced))
                        .foregroundStyle(Theme.secondaryText)
                }
            }
            .frame(maxWidth: .infinity)
            .padding(EdgeInsets(top: 14, leading: 0, bottom: 12, trailing: 0))
            .background(Theme.card, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
            .padding(.horizontal, 4)
        }

        CopyLinkButton()
            .padding(.horizontal, 4)

        SeparatorLine()
        SectionLabel("Kaynak")
        SourcePicker()
            .padding(.horizontal, 4)

        if case .capturing(let source) = appState.captureState {
            SourceRow(source: source)
            HStack(spacing: 6) {
                ControlButton("Değiştir…", height: 24) {
                    appState.chooseSource(appState.captureMode)
                }
                ControlButton("Durdur", height: 24, foreground: Theme.red) {
                    appState.stopCapture()
                }
            }
            .padding(.horizontal, 4)
        } else if case .failed(let message) = appState.captureState {
            Text(message)
                .font(.system(size: 11))
                .foregroundStyle(Theme.secondaryText)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.horizontal, 4)
        }
    }
}

private struct CopyLinkButton: View {
    @Environment(AppState.self) private var appState
    @State private var didCopy = false
    @State private var resetTask: Task<Void, Never>?

    var body: some View {
        Button {
            appState.copyViewerURL()
            didCopy = true
            resetTask?.cancel()
            resetTask = Task {
                try? await Task.sleep(for: .seconds(1.6))
                guard !Task.isCancelled else { return }
                didCopy = false
            }
        } label: {
            HStack(spacing: 6) {
                if didCopy {
                    Image(systemName: "checkmark")
                        .font(.system(size: 12, weight: .bold))
                        .foregroundStyle(Theme.green)
                    Text("Kopyalandı")
                } else {
                    Image(systemName: "doc.on.doc")
                        .font(.system(size: 12))
                    Text("Linki kopyala")
                }
            }
            .controlLabel(height: 28)
        }
        .buttonStyle(ControlButtonStyle())
        .disabled(appState.viewerURL == nil)
        .accessibilityLabel(didCopy ? "Kopyalandı" : "Linki kopyala")
    }
}

/// "Tüm ekran / Pencere seç". Every click opens the system picker, also on
/// the already selected side.
private struct SourcePicker: View {
    @Environment(AppState.self) private var appState

    var body: some View {
        HStack(spacing: 2) {
            segment("Tüm ekran", mode: .fullScreen)
            segment("Pencere seç", mode: .window)
        }
        .padding(2)
        .background(Theme.segmentTrack, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Kaynak türü")
    }

    private func segment(_ title: String, mode: CaptureMode) -> some View {
        let isSelected = appState.captureMode == mode
        return Button {
            appState.chooseSource(mode)
        } label: {
            Text(title)
                .font(.system(size: 12, weight: isSelected ? .semibold : .medium))
                .foregroundStyle(Theme.text)
                .frame(maxWidth: .infinity)
                .frame(height: 24)
                .background {
                    if isSelected {
                        RoundedRectangle(cornerRadius: 6, style: .continuous)
                            .fill(Theme.segmentSelected)
                            .shadow(color: .black.opacity(0.12), radius: 1, y: 1)
                            .overlay(
                                RoundedRectangle(cornerRadius: 6, style: .continuous)
                                    .strokeBorder(Color.black.opacity(0.06), lineWidth: 0.5)
                            )
                    }
                }
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(isSelected ? .isSelected : [])
    }
}

// MARK: - 2 · Connected

private struct ConnectedContent: View {
    @Environment(AppState.self) private var appState
    let deviceName: String
    let address: String

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: "iphone")
                .font(.system(size: 18))
                .foregroundStyle(Theme.accent)
                .frame(width: 40, height: 40)
                .background(Theme.accentFill, in: Circle())
            VStack(alignment: .leading, spacing: 2) {
                Text(deviceName)
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(Theme.text)
                HStack(spacing: 5) {
                    Circle().fill(Theme.green).frame(width: 6, height: 6)
                    Text("Bağlı · \(address)")
                }
                .font(.system(size: 11))
                .foregroundStyle(Theme.secondaryText)
            }
            Spacer(minLength: 0)
        }
        .padding(EdgeInsets(top: 14, leading: 12, bottom: 14, trailing: 12))
        .background(Theme.card, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
        .padding(.horizontal, 4)

        SectionLabel("Yayınlanan")
        if case .capturing(let source) = appState.captureState {
            SourceRow(source: source)
        } else {
            SourcePicker()
                .padding(.horizontal, 4)
        }

        ControlButton("Bağlantıyı kes", height: 32, foreground: .white, background: Theme.red) {
            appState.disconnect()
        }
        .padding(EdgeInsets(top: 2, leading: 4, bottom: 0, trailing: 4))
    }
}

// MARK: - 3 · Screen Recording permission

private struct PermissionContent: View {
    @Environment(AppState.self) private var appState

    var body: some View {
        VStack(spacing: 10) {
            Image(systemName: "lock.display")
                .font(.system(size: 22))
                .foregroundStyle(Theme.orange)
                .frame(width: 48, height: 48)
                .background(Theme.orangeFill, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
            Text("Ekran Kaydı izni gerekli")
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(Theme.text)
            Text("AirGlass'ın ekranını yansıtabilmesi için Sistem Ayarları › Gizlilik ve Güvenlik › Ekran ve Sistem Sesi Kaydı bölümünden izin ver.")
                .font(.system(size: 12))
                .lineSpacing(2)
                .foregroundStyle(Theme.secondaryText)
                .fixedSize(horizontal: false, vertical: true)
        }
        .multilineTextAlignment(.center)
        .frame(maxWidth: .infinity)
        .padding(EdgeInsets(top: 6, leading: 8, bottom: 0, trailing: 8))

        ControlButton("Sistem Ayarları'nı aç", height: 32, foreground: .white, background: Theme.accent) {
            appState.openScreenRecordingSettings()
        }
        .padding(EdgeInsets(top: 2, leading: 4, bottom: 0, trailing: 4))

        Text("İzni verdikten sonra AirGlass yeniden başlatılabilir.")
            .font(.system(size: 11))
            .lineSpacing(1)
            .foregroundStyle(Theme.secondaryText)
            .multilineTextAlignment(.center)
            .fixedSize(horizontal: false, vertical: true)
            .frame(maxWidth: .infinity)
            .padding(.horizontal, 12)
    }
}

// MARK: - 4 · No network

private struct OfflineContent: View {
    let message: String

    var body: some View {
        VStack(spacing: 10) {
            VStack(spacing: 12) {
                Image(systemName: "wifi.slash")
                    .font(.system(size: 26))
                Text(message)
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(Theme.text)
                    .multilineTextAlignment(.center)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .foregroundStyle(Theme.secondaryText)
            .padding(12)
            .frame(width: 168, height: 168)
            .overlay(
                RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .strokeBorder(Theme.separator, style: StrokeStyle(lineWidth: 1.5, dash: [4, 3]))
            )

            Text("Mac ve telefon aynı ağa bağlandığında QR kod burada görünür.")
                .font(.system(size: 11))
                .lineSpacing(1)
                .foregroundStyle(Theme.secondaryText)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.horizontal, 16)
        }
        .frame(maxWidth: .infinity)
        .padding(EdgeInsets(top: 14, leading: 0, bottom: 12, trailing: 0))
        .background(Theme.card, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
        .padding(.horizontal, 4)
    }
}

// MARK: - Shared pieces

private struct SourceRow: View {
    let source: CaptureSource

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: source.isWindow ? "macwindow" : "display")
                .font(.system(size: 13))
                .foregroundStyle(Theme.text)
                .frame(width: 28, height: 28)
                .background(Theme.control, in: RoundedRectangle(cornerRadius: 7, style: .continuous))
            VStack(alignment: .leading, spacing: 0) {
                Text(source.name)
                    .font(.system(size: 13, weight: .medium))
                    .foregroundStyle(Theme.text)
                    .lineLimit(1)
                    .truncationMode(.tail)
                    .help(source.name)
                Text(source.isWindow ? "Pencere" : "Tüm ekran")
                    .font(.system(size: 11))
                    .foregroundStyle(Theme.secondaryText)
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 4)
    }
}

private struct SectionLabel: View {
    let title: String

    init(_ title: String) {
        self.title = title
    }

    var body: some View {
        Text(title)
            .font(.system(size: 11, weight: .semibold))
            .foregroundStyle(Theme.secondaryText)
            .padding(.horizontal, 4)
    }
}

private struct SeparatorLine: View {
    var body: some View {
        Rectangle()
            .fill(Theme.separator)
            .frame(height: 1)
            .padding(EdgeInsets(top: 2, leading: 4, bottom: 2, trailing: 4))
    }
}

/// Full-width rounded button: 13 pt medium on a tinted fill.
private struct ControlButton: View {
    let title: String
    let height: CGFloat
    let foreground: Color
    let background: Color
    let action: () -> Void

    init(_ title: String, height: CGFloat, foreground: Color = Theme.text,
         background: Color = Theme.control, action: @escaping () -> Void) {
        self.title = title
        self.height = height
        self.foreground = foreground
        self.background = background
        self.action = action
    }

    var body: some View {
        Button(action: action) {
            Text(title)
                .foregroundStyle(foreground)
                .controlLabel(height: height, background: background)
        }
        .buttonStyle(ControlButtonStyle())
    }
}

private struct ControlButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .opacity(configuration.isPressed ? 0.7 : 1)
    }
}

private extension View {
    func controlLabel(height: CGFloat, background: Color = Theme.control) -> some View {
        self
            .font(.system(size: 13, weight: .medium))
            .foregroundStyle(Theme.text)
            .frame(maxWidth: .infinity)
            .frame(height: height)
            .background(background, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            .contentShape(Rectangle())
    }
}

/// A full-width row that highlights on hover like a menu item
/// ("AirGlass Hakkında", "Çıkış ⌘Q").
private struct MenuRow: View {
    let title: String
    let shortcut: String?
    let action: () -> Void
    @State private var isHovered = false

    init(_ title: String, shortcut: String? = nil, action: @escaping () -> Void) {
        self.title = title
        self.shortcut = shortcut
        self.action = action
    }

    var body: some View {
        Button(action: action) {
            HStack {
                Text(title)
                    .font(.system(size: 13))
                    .foregroundStyle(Theme.text)
                Spacer()
                if let shortcut {
                    Text(shortcut)
                        .font(.system(size: 12))
                        .foregroundStyle(Theme.secondaryText)
                }
            }
            .padding(.horizontal, 8)
            .frame(height: 28)
            .background(
                RoundedRectangle(cornerRadius: 6, style: .continuous)
                    .fill(isHovered ? Theme.menuHover : .clear)
            )
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { isHovered = $0 }
    }
}
