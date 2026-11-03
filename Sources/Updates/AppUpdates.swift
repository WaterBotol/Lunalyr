import AppKit
import Combine
import Sparkle

@MainActor
final class AppUpdates: ObservableObject {
    static let shared = AppUpdates()
    @Published private(set) var canCheck = false
    private var controller: SPUStandardUpdaterController?
    private var observation: AnyCancellable?
    private var attemptedStart = false
    let configured = UpdateConfiguration(info: Bundle.main.infoDictionary ?? [:]) != nil

    var buttonDisabled: Bool { configured && !canCheck }
    var status: String {
        configured ? "Updates are checked and installed through this app’s update service."
            : "An update service hasn’t been set up for this build yet."
    }
    func startIfConfigured() {
        guard configured, !attemptedStart else { return }
        attemptedStart = true
        let manager = SPUStandardUpdaterController(startingUpdater: false,
            updaterDelegate: nil, userDriverDelegate: nil)
        controller = manager
        observation = manager.updater.publisher(for: \.canCheckForUpdates)
            .receive(on: RunLoop.main)
            .sink { [weak self] allowed in self?.canCheck = allowed }
        manager.startUpdater()
    }
    func check() {
        guard configured else {
            let alert = NSAlert()
            alert.messageText = "Updates aren’t available for this build yet"
            alert.informativeText = "The person distributing this copy needs to enable its update service. For now, install the newer app they share with you."
            alert.addButton(withTitle: "OK")
            NSApp.activate(ignoringOtherApps: true)
            alert.runModal()
            return
        }
        startIfConfigured()
        if canCheck { controller?.checkForUpdates(nil) }
    }
}
