import AppKit

/// The standard macOS About panel. Icon, name and version come from the
/// bundle (Info.plist), the copyright line from NSHumanReadableCopyright.
enum AboutPanel {
    static let websiteURL = URL(string: "https://lupusoft.com")!
    static let repositoryURL = URL(string: "https://github.com/lupusoftco/airglass")!
    static let licenseURL = URL(string: "https://github.com/lupusoftco/airglass/blob/main/LICENSE")!

    @MainActor
    static func show() {
        // A menu bar app is never frontmost on its own; without this the
        // panel would open behind the active app's windows.
        NSApp.activate()
        NSApp.orderFrontStandardAboutPanel(options: [.credits: credits])
    }

    /// Centered credit lines, each a clickable link.
    private static var credits: NSAttributedString {
        let paragraph = NSMutableParagraphStyle()
        paragraph.alignment = .center
        paragraph.paragraphSpacing = 2

        let base: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: NSFont.smallSystemFontSize),
            .foregroundColor: NSColor.labelColor,
            .paragraphStyle: paragraph,
        ]
        let lines: [(String, URL)] = [
            ("Developed by Lupusoft", websiteURL),
            ("GitHub", repositoryURL),
            ("MIT License", licenseURL),
        ]

        let text = NSMutableAttributedString()
        for (index, (title, url)) in lines.enumerated() {
            if index > 0 {
                text.append(NSAttributedString(string: "\n", attributes: base))
            }
            var attributes = base
            attributes[.link] = url
            text.append(NSAttributedString(string: title, attributes: attributes))
        }
        return text
    }
}
