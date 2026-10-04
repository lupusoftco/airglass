import SwiftUI

struct PopoverView: View {
    @Environment(AppState.self) private var appState

    var body: some View {
        VStack(spacing: 0) {
            Group {
                switch appState.connection {
                case .waiting:
                    WaitingView()
                case .connected(let deviceName):
                    ConnectedView(deviceName: deviceName)
                }
            }
            .padding(14)

            Divider()
                .padding(.horizontal, 10)

            MenuRowButton("Çıkış") {
                NSApplication.shared.terminate(nil)
            }
            .keyboardShortcut("q")
            .padding(5)
        }
        .frame(width: 260)
    }
}

private struct WaitingView: View {
    @Environment(AppState.self) private var appState

    var body: some View {
        // Every click on a segment opens the system picker for that mode.
        let mode = Binding(
            get: { appState.captureMode },
            set: { appState.chooseSource($0) }
        )

        VStack(spacing: 12) {
            QRCodeView(content: appState.viewerURL)
                .frame(width: 200, height: 200)

            Picker("Kaynak", selection: mode) {
                Text("Tüm ekran").tag(CaptureMode.fullScreen)
                Text("Pencere seç").tag(CaptureMode.window)
            }
            .pickerStyle(.segmented)
            .labelsHidden()

            CaptureStatusView()
        }
    }
}

/// Capture status below the source picker. The live preview is a
/// temporary test aid until frames reach the phone.
private struct CaptureStatusView: View {
    @Environment(AppState.self) private var appState

    var body: some View {
        switch appState.captureState {
        case .idle:
            EmptyView()

        case .capturing(let source):
            VStack(spacing: 6) {
                PreviewView(renderer: appState.preview)
                    .aspectRatio(source.size, contentMode: .fit)
                    .frame(maxHeight: 140)
                    .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))

                HStack {
                    Text("\(Int(source.size.width))×\(Int(source.size.height))")
                        .monospacedDigit()
                        .foregroundStyle(.secondary)
                    Spacer()
                    Button("Değiştir") { appState.chooseSource(appState.captureMode) }
                    Button("Durdur") { appState.stopCapture() }
                }
                .font(.caption)
                .buttonStyle(.link)
            }

        case .permissionDenied:
            VStack(spacing: 6) {
                Text("AirGlass'ın ekranı görebilmesi için Ekran Kaydı izni gerekiyor. İzni verdikten sonra uygulamayı yeniden açın.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .fixedSize(horizontal: false, vertical: true)
                Button("Sistem Ayarları'nı aç") { appState.openScreenRecordingSettings() }
                    .controlSize(.small)
            }

        case .failed(let message):
            Text(message)
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
        }
    }
}

private struct ConnectedView: View {
    @Environment(AppState.self) private var appState
    let deviceName: String

    var body: some View {
        VStack(spacing: 12) {
            HStack(spacing: 10) {
                Image(systemName: "iphone")
                    .font(.title2)
                    .foregroundStyle(.tint)
                VStack(alignment: .leading, spacing: 2) {
                    Text(deviceName)
                        .font(.headline)
                    Text("İzleniyor")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                }
                Spacer()
            }

            Button("Bağlantıyı kes", role: .destructive) {
                appState.disconnect()
            }
            .controlSize(.large)
            .frame(maxWidth: .infinity)
        }
    }
}

/// A full-width row that highlights on hover, like a native menu item.
private struct MenuRowButton: View {
    let title: LocalizedStringKey
    let action: () -> Void
    @State private var isHovered = false

    init(_ title: LocalizedStringKey, action: @escaping () -> Void) {
        self.title = title
        self.action = action
    }

    var body: some View {
        Button(action: action) {
            Text(title)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 9)
                .padding(.vertical, 4)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .background(
            RoundedRectangle(cornerRadius: 5, style: .continuous)
                .fill(isHovered ? Color.primary.opacity(0.1) : .clear)
        )
        .onHover { isHovered = $0 }
    }
}
