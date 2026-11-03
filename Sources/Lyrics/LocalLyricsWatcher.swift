import Darwin
import Foundation

/// Watches only the folder explicitly chosen in settings. Export bursts are
/// debounced so an LRC + TTML + reading update causes a single lookup.
@MainActor
final class LocalLyricsWatcher {
    private var path = ""
    private var source: DispatchSourceFileSystemObject?
    private var pending: Task<Void, Never>?
    func watch(_ path: String, onChange: @escaping () -> Void) {
        guard path != self.path else { return }
        pending?.cancel(); source?.cancel(); source = nil; self.path = path
        guard !path.isEmpty else { return }
        let fd = open(path, O_EVTONLY)
        guard fd >= 0 else { return }
        let watcher = DispatchSource.makeFileSystemObjectSource(fileDescriptor: fd, eventMask: [.write], queue: .main)
        watcher.setEventHandler { [weak self] in
            Task { @MainActor in
                guard let self else { return }
                self.pending?.cancel()
                self.pending = Task {
                    do { try await Task.sleep(nanoseconds: 700_000_000) } catch { return }
                    guard !Task.isCancelled else { return }
                    onChange()
                }
            }
        }
        watcher.setCancelHandler { close(fd) }
        source = watcher; watcher.resume()
    }
    deinit { pending?.cancel(); source?.cancel() }
}
