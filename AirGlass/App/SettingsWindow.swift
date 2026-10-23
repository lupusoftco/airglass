import AppKit
import SwiftUI

/// The Settings window, opened from the popover. One at a time; its title
/// and contents follow the chosen language right away.
@MainActor
enum SettingsWindow {
    private static var window: NSWindow?

    /// The title is plain AppKit, so it is refreshed by hand.
    fileprivate static func updateTitle() {
        window?.title = L("settings.title")
    }

    static func show() {
        // A menu bar app is never frontmost on its own (see AboutPanel).
        NSApp.activate()
        if let window {
            window.makeKeyAndOrderFront(nil)
            return
        }
        let window = NSWindow(contentViewController: NSHostingController(rootView: SettingsView()))
        window.styleMask = [.titled, .closable]
        window.isReleasedWhenClosed = false
        window.title = L("settings.title")
        window.center()
        window.makeKeyAndOrderFront(nil)
        self.window = window
    }
}

private struct SettingsView: View {
    @Bindable private var localizer = Localizer.shared

    var body: some View {
        Form {
            Picker(L("settings.language"), selection: $localizer.language) {
                ForEach(localizer.languages) { language in
                    Text(language.name).tag(language.code)
                }
            }
            .pickerStyle(.menu)
        }
        .padding(20)
        .frame(width: 320)
        .onChange(of: localizer.language) { SettingsWindow.updateTitle() }
    }
}
