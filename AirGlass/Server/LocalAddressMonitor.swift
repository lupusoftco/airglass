import Darwin
import Foundation
import Network

/// Tracks the Mac's IPv4 address on the active Wi-Fi or Ethernet interface,
/// i.e. the address a phone on the same network can reach.
final class LocalAddressMonitor {
    /// Called on the main thread; `nil` when no suitable interface is up.
    var onChange: ((String?) -> Void)?

    private let monitor = NWPathMonitor()
    private let queue = DispatchQueue(label: "AirGlass.address-monitor", qos: .utility)

    func start() {
        monitor.pathUpdateHandler = { [weak self] path in
            let address = Self.lanAddress(for: path)
            DispatchQueue.main.async { self?.onChange?(address) }
        }
        monitor.start(queue: queue)
    }

    deinit {
        monitor.cancel()
    }

    private static func lanAddress(for path: NWPath) -> String? {
        guard path.status == .satisfied else { return nil }
        let addresses = ipv4AddressesByInterface()
        // availableInterfaces is ordered by system preference; VPN tunnels
        // and other virtual interfaces are skipped.
        return path.availableInterfaces
            .filter { $0.type == .wifi || $0.type == .wiredEthernet }
            .lazy
            .compactMap { addresses[$0.name] }
            .first
    }

    private static func ipv4AddressesByInterface() -> [String: String] {
        var head: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&head) == 0, let first = head else { return [:] }
        defer { freeifaddrs(head) }

        var result: [String: String] = [:]
        for pointer in sequence(first: first, next: { $0.pointee.ifa_next }) {
            let interface = pointer.pointee
            let flags = Int32(interface.ifa_flags)
            guard let address = interface.ifa_addr,
                  address.pointee.sa_family == UInt8(AF_INET),
                  flags & IFF_UP != 0,
                  flags & IFF_RUNNING != 0,
                  flags & IFF_LOOPBACK == 0
            else { continue }

            var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            guard getnameinfo(address, socklen_t(address.pointee.sa_len),
                              &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST) == 0
            else { continue }

            let ip = String(cString: host)
            let name = String(cString: interface.ifa_name)
            // Link-local addresses mean DHCP failed; nothing can reach them.
            if !ip.hasPrefix("169.254."), result[name] == nil {
                result[name] = ip
            }
        }
        return result
    }
}
