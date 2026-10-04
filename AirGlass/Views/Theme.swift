import AppKit
import SwiftUI

/// Colors from the AirGlass design (light / dark).
enum Theme {
    static let text = Color(light: 0x1D1D1F, dark: 0xF5F5F7)
    static let secondaryText = Color(light: 0x6E6E73, dark: 0xA1A1A6)
    static let accent = Color(light: 0x5B6EF5, dark: 0x7383FF)
    static let accentFill = Color(light: (0x5B6EF5, 0.12), dark: (0x7383FF, 0.20))
    static let green = Color(light: 0x28A745, dark: 0x30D158)
    static let orange = Color(light: 0xC96A00, dark: 0xFF9F0A)
    static let orangeFill = Color(light: (0xFF9500, 0.14), dark: (0xFF9F0A, 0.18))
    static let red = Color(light: 0xE5352B, dark: 0xFF453A)

    /// Inset panel behind the QR code and the device card.
    static let card = Color(light: (0x000000, 0.04), dark: (0xFFFFFF, 0.06))
    /// Buttons and icon tiles.
    static let control = Color(light: (0x000000, 0.06), dark: (0xFFFFFF, 0.10))
    static let segmentTrack = Color(light: (0x000000, 0.07), dark: (0xFFFFFF, 0.08))
    static let segmentSelected = Color(light: (0xFFFFFF, 1), dark: (0xFFFFFF, 0.22))
    static let separator = Color(light: (0x3C3C43, 0.16), dark: (0xFFFFFF, 0.11))
    static let menuHover = Color(light: (0x000000, 0.06), dark: (0xFFFFFF, 0.10))
}

extension Color {
    init(light: UInt32, dark: UInt32) {
        self.init(light: (light, 1), dark: (dark, 1))
    }

    init(light: (UInt32, CGFloat), dark: (UInt32, CGFloat)) {
        self.init(nsColor: NSColor(name: nil) { appearance in
            let isDark = appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
            let (hex, alpha) = isDark ? dark : light
            return NSColor(
                srgbRed: CGFloat((hex >> 16) & 0xFF) / 255,
                green: CGFloat((hex >> 8) & 0xFF) / 255,
                blue: CGFloat(hex & 0xFF) / 255,
                alpha: alpha
            )
        })
    }
}
