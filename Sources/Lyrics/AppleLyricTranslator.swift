#if canImport(Translation)
import AppKit
import Foundation
import SwiftUI
import Translation

/// `LyricTranslating` with Apple's Translation framework, on this Mac only.
///
/// Only language pairs already installed are translated here, so no download
/// prompt ever appears unasked: a missing pair throws `needsDownload`, and
/// Settings asks the framework to download it when the listener says so.
/// macOS 26 opens a session directly; macOS 15 gets one from SwiftUI's
/// `translationTask` on a tiny transparent window (`TranslationHost`).
@available(macOS 15.0, *)
final class AppleLyricTranslator: LyricTranslating {
    func translate(_ texts: [String], from source: String, to target: String) async throws -> [String] {
        guard !texts.isEmpty else { return [] }
        let from = Locale.Language(identifier: source), to = Locale.Language(identifier: target)
        switch await LanguageAvailability().status(from: from, to: to) {
        case .installed: break
        case .supported: throw LyricTranslationError.needsDownload
        case .unsupported: throw LyricTranslationError.unsupported
        @unknown default: throw LyricTranslationError.unsupported
        }
        try Task.checkCancellation()
        let requests = texts.enumerated().map {
            TranslationSession.Request(sourceText: $0.element, clientIdentifier: String($0.offset))
        }
        let responses: [TranslationSession.Response]
        // `TranslationSession(installedSource:target:)` is in the macOS 26 SDK
        // only; CI and release builds use Xcode 16 (Swift 6.1, macOS 15 SDK),
        // where `#available` alone still fails to compile the call.
        #if compiler(>=6.2)
        if #available(macOS 26.0, *) {
            let session = TranslationSession(installedSource: from, target: to)
            responses = try await withTaskCancellationHandler(operation: {
                try await session.translations(from: requests)
            }, onCancel: { session.cancel() })
        } else {
            responses = try await TranslationHost.shared.translate(requests, source: from, target: to)
        }
        #else
        responses = try await TranslationHost.shared.translate(requests, source: from, target: to)
        #endif
        var answers = Array(repeating: "", count: texts.count)
        for response in responses {
            guard let id = response.clientIdentifier.flatMap(Int.init), answers.indices.contains(id) else { continue }
            answers[id] = response.targetText
        }
        return answers
    }
}

/// Runs batches through `translationTask` on a 1×1 transparent window that
/// takes no clicks and is shown only while a batch is in flight.
@available(macOS 15.0, *)
@MainActor
final class TranslationHost {
    static let shared = TranslationHost()

    final class Job {
        let id: UUID
        let requests: [TranslationSession.Request]
        private var continuation: CheckedContinuation<[TranslationSession.Response], Error>?
        init(id: UUID, _ requests: [TranslationSession.Request],
             _ continuation: CheckedContinuation<[TranslationSession.Response], Error>) {
            self.id = id
            self.requests = requests
            self.continuation = continuation
        }
        func finish(_ result: Result<[TranslationSession.Response], Error>) {
            continuation?.resume(with: result)
            continuation = nil
        }
    }

    final class Model: ObservableObject {
        @Published var configuration: TranslationSession.Configuration?
        var job: Job?
    }

    private let model = Model()
    private var window: NSPanel?

    func translate(_ requests: [TranslationSession.Request], source: Locale.Language,
                   target: Locale.Language) async throws -> [TranslationSession.Response] {
        let jobID = UUID()
        return try await withTaskCancellationHandler(operation: {
            try await withCheckedThrowingContinuation { continuation in
                // A newer song's batch replaces one still waiting.
                model.job?.finish(.failure(CancellationError()))
                let job = Job(id: jobID, requests, continuation)
                model.job = job
                show()
                if model.configuration?.source == source, model.configuration?.target == target {
                    model.configuration?.invalidate()
                } else {
                    model.configuration = .init(source: source, target: target)
                }
            }
        }, onCancel: {
            Task { @MainActor in self.cancel(jobID) }
        })
    }

    private func cancel(_ id: UUID) {
        guard let job = model.job, job.id == id else { return }
        model.job = nil
        job.finish(.failure(CancellationError()))
        hideIfIdle()
    }

    /// Called from the window's translation task.
    fileprivate func run(_ session: TranslationSession) async {
        guard let job = model.job else { return }
        let result: Result<[TranslationSession.Response], Error>
        do { result = .success(try await session.translations(from: job.requests)) } catch { result = .failure(error) }
        guard model.job === job else { return }
        model.job = nil
        job.finish(result)
        hideIfIdle()
    }

    private func show() {
        if window == nil {
            let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 1, height: 1),
                                styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: true)
            panel.isReleasedWhenClosed = false
            panel.alphaValue = 0
            panel.backgroundColor = .clear
            panel.isOpaque = false
            panel.hasShadow = false
            panel.ignoresMouseEvents = true
            panel.hidesOnDeactivate = false
            panel.isExcludedFromWindowsMenu = true
            panel.sharingType = .none
            panel.level = .init(rawValue: Int(CGWindowLevelForKey(.desktopWindow)) - 1)
            panel.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle, .transient]
            panel.contentView = NSHostingView(rootView: TranslationHostView(model: model, host: self))
            window = panel
        }
        window?.orderFrontRegardless()
    }

    private func hideIfIdle() {
        guard model.job == nil else { return }
        window?.orderOut(nil)
    }
}

@available(macOS 15.0, *)
private struct TranslationHostView: View {
    @ObservedObject var model: TranslationHost.Model
    let host: TranslationHost

    var body: some View {
        Color.clear
            .frame(width: 1, height: 1)
            .translationTask(model.configuration) { session in
                await host.run(session)
            }
    }
}

/// Asks the framework to download a language pair, showing its own prompt.
/// Attached to Settings, and run only when the listener turns translation
/// on or presses Download.
@available(macOS 15.0, *)
struct TranslationDownloadPrompt: ViewModifier {
    @Binding var request: LyricLanguagePair?
    let finished: () -> Void
    @State private var configuration: TranslationSession.Configuration?

    func body(content: Content) -> some View {
        content
            .translationTask(configuration) { session in
                try? await session.prepareTranslation()
                await MainActor.run {
                    configuration = nil
                    finished()
                }
            }
            .onChange(of: request) { _, pair in
                guard let pair else { return }
                configuration = .init(source: Locale.Language(identifier: pair.source),
                                      target: Locale.Language(identifier: pair.target))
                request = nil
            }
    }
}
#endif
