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
            VStack(spacing: 6) {
                Group {
                    if let url = appState.viewerURL {
                        QRCodeView(content: url)
                            .help(url)
                    } else {
                        QRPlaceholderView(message: appState.unavailableReason)
                    }
                }
                .frame(width: 200, height: 200)

                CopyLinkButton()
            }

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

/// Copies the viewer link (for opening it on a device without a camera).
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
                try? await Task.sleep(for: .seconds(1.5))
                guard !Task.isCancelled else { return }
                didCopy = false
            }
        } label: {
            if didCopy {
                Text("Kopyalandı ✓")
            } else {
                Label("Linki kopyala", systemImage: "doc.on.doc")
            }
        }
        .buttonStyle(.borderless)
        .font(.caption)
        .foregroundStyle(didCopy ? .secondary : .primary)
        .disabled(appState.viewerURL == nil)
        .animation(.easeInOut(duration: 0.15), value: didCopy)
    }
}

/// Stands in for the QR code while the server starts or no network is up.
private struct QRPlaceholderView: View {
    let message: String?

    var body: some View {
        RoundedRectangle(cornerRadius: 10, style: .continuous)
            .fill(.quaternary)
            .overlay {
                if let message {
                    Text(message)
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                        .padding()
                } else {
                    ProgressView()
                        .controlSize(.small)
                }
            }
    }
}

/// The selected screen or window, below the source picker.
private struct CaptureStatusView: View {
    @Environment(AppState.self) private var appState

    var body: some View {
        switch appState.captureState {
        case .idle:
            EmptyView()

        case .capturing(let source):
            HStack(spacing: 6) {
                Image(systemName: source.isWindow ? "macwindow" : "display")
                    .foregroundStyle(.secondary)
                Text(source.name)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .help(source.name)
                Spacer(minLength: 4)
                Button("Değiştir") { appState.chooseSource(appState.captureMode) }
                Button("Durdur") { appState.stopCapture() }
            }
            .font(.caption)
            .buttonStyle(.link)

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
