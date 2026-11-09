import Foundation

/// One WebSocket to the relay. Injected so tests never open a real one.
protocol ListenSocket: AnyObject, Sendable {
    func send(_ data: Data) async throws
    /// The next text message; throws once the socket closes.
    func receive() async throws -> String
    func close()
    /// The relay does not know the room (HTTP 404, or close code 4404).
    var isUnknownRoom: Bool { get }
}

protocol ListenSocketOpening: Sendable {
    func open(_ url: URL) -> any ListenSocket
}

struct LiveListenSockets: ListenSocketOpening {
    func open(_ url: URL) -> any ListenSocket { LiveListenSocket(url: url) }
}

/// URLSessionWebSocketTask, with messages sent as text frames.
final class LiveListenSocket: ListenSocket, @unchecked Sendable {
    private let task: URLSessionWebSocketTask?

    init(url: URL) {
        if AppRuntime.isUnitTesting {
            task = nil
        } else {
            let task = URLSession.shared.webSocketTask(with: url)
            task.maximumMessageSize = 1 << 20
            task.resume()
            self.task = task
        }
    }

    func send(_ data: Data) async throws {
        guard let task else { throw URLError(.notConnectedToInternet) }
        try await task.send(.string(String(decoding: data, as: UTF8.self)))
    }

    func receive() async throws -> String {
        guard let task else { throw URLError(.notConnectedToInternet) }
        switch try await task.receive() {
        case .string(let text): return text
        case .data(let data): return String(decoding: data, as: UTF8.self)
        @unknown default: return ""
        }
    }

    func close() {
        task?.cancel(with: .normalClosure, reason: nil)
    }

    var isUnknownRoom: Bool {
        guard let task else { return false }
        if (task.response as? HTTPURLResponse)?.statusCode == 404 { return true }
        return task.closeCode.rawValue == ListenProtocol.unknownRoomCloseCode
    }

    /// Sends one message and waits at most `timeout` for it to go: for the
    /// "end" a quitting app sends, when nothing can be awaited.
    func sendBlocking(_ data: Data, timeout: TimeInterval) {
        guard let task else { return }
        let done = DispatchSemaphore(value: 0)
        task.send(.string(String(decoding: data, as: UTF8.self))) { _ in done.signal() }
        _ = done.wait(timeout: .now() + timeout)
    }
}

/// Fetches a URL. Injected so tests never reach the network.
protocol ListenHTTP: Sendable {
    func get(_ url: URL) async throws -> (Data, Int)
    func post(_ url: URL) async throws -> (Data, Int)
}

struct LiveListenHTTP: ListenHTTP {
    func get(_ url: URL) async throws -> (Data, Int) { try await send(URLRequest(url: url)) }

    func post(_ url: URL) async throws -> (Data, Int) {
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        return try await send(request)
    }

    private func send(_ request: URLRequest) async throws -> (Data, Int) {
        guard !AppRuntime.isUnitTesting else { throw URLError(.notConnectedToInternet) }
        var request = request
        request.timeoutInterval = 8
        let (data, response) = try await URLSession.shared.data(for: request)
        return (data, (response as? HTTPURLResponse)?.statusCode ?? 0)
    }
}
