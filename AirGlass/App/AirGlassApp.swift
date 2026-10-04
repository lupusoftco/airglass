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
            Image(systemName: appState.isStreaming ? "airplayvideo.circle.fill" : "airplayvideo")
        }
        .menuBarExtraStyle(.window)
    }
}
