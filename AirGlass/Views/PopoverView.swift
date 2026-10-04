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
        @Bindable var appState = appState

        VStack(spacing: 12) {
            QRCodeView(content: appState.viewerURL)
                .frame(width: 200, height: 200)

            Picker("Kaynak", selection: $appState.captureMode) {
                Text("Tüm ekran").tag(CaptureMode.fullScreen)
                Text("Pencere seç").tag(CaptureMode.window)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
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
