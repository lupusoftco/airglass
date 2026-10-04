import Foundation
import Security

/// Holds the secret that the QR code carries. Read from the server queue
/// and written from the main thread, hence the lock.
final class TokenStore: @unchecked Sendable {
    /// 192 bits of entropy, encoded as 32 URL-safe characters.
    private static let byteCount = 24

    private let lock = NSLock()
    private var token: String

    init() {
        token = Self.generate()
    }

    var current: String {
        lock.withLock { token }
    }

    @discardableResult
    func rotate() -> String {
        let newToken = Self.generate()
        lock.withLock { token = newToken }
        return newToken
    }

    /// Constant-time comparison so response timing reveals nothing.
    func isValid(_ candidate: String) -> Bool {
        let expected = Array(current.utf8)
        let given = Array(candidate.utf8)
        guard expected.count == given.count else { return false }
        var difference: UInt8 = 0
        for index in expected.indices {
            difference |= expected[index] ^ given[index]
        }
        return difference == 0
    }

    /// A fresh 192-bit random value, URL-safe base64.
    static func generate() -> String {
        var bytes = [UInt8](repeating: 0, count: byteCount)
        let status = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        precondition(status == errSecSuccess, "Secure random generator unavailable")
        return Data(bytes).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
}
