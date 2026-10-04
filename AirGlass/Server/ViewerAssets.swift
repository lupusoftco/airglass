import Foundation

/// The phone-side viewer page, bundled under Resources/Viewer and kept in
/// memory. Only these exact paths are served.
struct ViewerAssets {
    struct Asset {
        var data: Data
        var contentType: String
    }

    private let assets: [String: Asset]

    init(bundle: Bundle = .main) {
        let files: [String: (name: String, type: String)] = [
            "/": ("index.html", "text/html; charset=utf-8"),
            "/viewer.js": ("viewer.js", "text/javascript; charset=utf-8"),
            "/viewer.css": ("viewer.css", "text/css; charset=utf-8"),
            // Silent clip that keeps the phone awake (see viewer.js).
            "/keepawake.mp4": ("keepawake.mp4", "video/mp4"),
        ]
        let directory = bundle.resourceURL?.appendingPathComponent("Viewer", isDirectory: true)

        assets = files.compactMapValues { file in
            guard let url = directory?.appendingPathComponent(file.name),
                  let data = try? Data(contentsOf: url)
            else { return nil }
            return Asset(data: data, contentType: file.type)
        }
    }

    subscript(path: String) -> Asset? {
        assets[path]
    }
}
