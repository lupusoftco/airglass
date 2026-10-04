import Foundation
import OSLog

/// Diagnostics, viewable in Console.app (filter: subsystem com.lupusoft.AirGlass)
/// or with `log stream --predicate 'subsystem == "com.lupusoft.AirGlass"'`.
/// Tokens are never logged.
enum Log {
    private static let subsystem = Bundle.main.bundleIdentifier ?? "AirGlass"

    static let server = Logger(subsystem: subsystem, category: "server")
    static let signaling = Logger(subsystem: subsystem, category: "signaling")
    static let webrtc = Logger(subsystem: subsystem, category: "webrtc")

    /// "host 192.168.1.5:50000 udp" from an SDP candidate line.
    static func describeCandidate(_ sdp: String) -> String {
        let fields = sdp.split(separator: " ").map(String.init)
        guard fields.count >= 8 else { return sdp }
        let type = fields.firstIndex(of: "typ").flatMap { fields.indices.contains($0 + 1) ? fields[$0 + 1] : nil } ?? "?"
        return "\(type) \(fields[4]):\(fields[5]) \(fields[2])"
    }
}
