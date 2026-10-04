import SwiftUI

@main
struct AirGlassApp: App {
    @State private var appState = AppState()

    var body: some Scene {
        MenuBarExtra {
            PopoverView()
                .environment(appState)
        } label: {
            // Like AirPlay: the icon changes while someone is watching.
            // Template images from the asset catalog, tinted by the menu bar.
            Image(appState.isStreaming ? "MenuBarActive" : "MenuBarIdle")
                .accessibilityLabel(appState.isStreaming ? "AirGlass – izleniyor" : "AirGlass")
        }
        .menuBarExtraStyle(.window)
    }
}
