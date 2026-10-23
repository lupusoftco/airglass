import Foundation
import Observation

/// The app's interface language, chosen in Settings. Texts come from
/// Localization/Strings.json, the table the Windows app uses too. Views read
/// `L(...)` and update as soon as the language changes.
@Observable
final class Localizer: @unchecked Sendable {
    struct Language: Identifiable, Hashable {
        let code: String
        /// In its own language: "Türkçe", "English", …
        let name: String
        var id: String { code }
    }

    static let shared = Localizer()
    private static let defaultsKey = "language"

    let languages: [Language]

    /// One of `languages`' codes. Saved across launches.
    var language: String {
        didSet {
            UserDefaults.standard.set(language, forKey: Self.defaultsKey)
            lock.withLock { current = language }
        }
    }

    @ObservationIgnored private let strings: [String: [String: String]]
    /// `language` for readers off the main thread (the server, capture).
    @ObservationIgnored private let lock = NSLock()
    @ObservationIgnored private var current: String

    private init() {
        var languages: [Language] = []
        var strings: [String: [String: String]] = [:]
        if let url = Bundle.main.url(forResource: "Strings", withExtension: "json"),
           let data = try? Data(contentsOf: url),
           let table = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
            languages = (table["languages"] as? [[String: String]] ?? []).compactMap { entry in
                guard let code = entry["code"], let name = entry["name"] else { return nil }
                return Language(code: code, name: name)
            }
            strings = table["strings"] as? [String: [String: String]] ?? [:]
        }
        self.languages = languages
        self.strings = strings

        let codes = languages.map(\.code)
        let saved = UserDefaults.standard.string(forKey: Self.defaultsKey)
        let initial = saved.flatMap { codes.contains($0) ? $0 : nil } ?? Self.systemLanguage(among: codes)
        language = initial
        current = initial
    }

    /// The text for `key` in the current language; `{0}` becomes `argument`.
    func text(_ key: String, _ argument: String? = nil) -> String {
        let code = Thread.isMainThread ? language : lock.withLock { current }
        let text = strings[key]?[code] ?? strings[key]?["en"] ?? key
        guard let argument else { return text }
        return text.replacingOccurrences(of: "{0}", with: argument)
    }

    /// The first of the user's preferred languages that AirGlass has, else English.
    private static func systemLanguage(among codes: [String]) -> String {
        for preferred in Locale.preferredLanguages {
            let base = preferred.split(separator: "-").first.map(String.init) ?? preferred
            if let match = codes.first(where: { $0 == preferred || $0.split(separator: "-").first.map(String.init) == base }) {
                return match
            }
        }
        return "en"
    }
}

/// Shorthand for `Localizer.shared.text(_:_:)`.
func L(_ key: String, _ argument: String? = nil) -> String {
    Localizer.shared.text(key, argument)
}
