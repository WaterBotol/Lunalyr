import Combine
import Foundation
import Network

/// Whether this Mac has a usable network path. A lookup that found nothing
/// while offline is not cached as a miss, and is repeated once a path returns.
final class LyricConnectivity: @unchecked Sendable {
    static let shared = LyricConnectivity()
    /// Fires on the main queue when a usable path returns after being lost.
    let restored = PassthroughSubject<Void, Never>()
    private let monitor = NWPathMonitor()
    private let lock = NSLock()
    private var online = true

    var isOnline: Bool { lock.withLock { online } }

    private init() {
        monitor.pathUpdateHandler = { [weak self] path in
            guard let self else { return }
            let usable = path.status == .satisfied
            let returned = self.lock.withLock { () -> Bool in
                defer { self.online = usable }
                return usable && !self.online
            }
            if returned { DispatchQueue.main.async { self.restored.send() } }
        }
        monitor.start(queue: DispatchQueue(label: "local.verso.custom.lyrics.connectivity", qos: .utility))
    }
}
