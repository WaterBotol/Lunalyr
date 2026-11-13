import AppKit
import Combine
import Sparkle

/// Lunalyr's updates, through Sparkle. Once a day (`SUScheduledCheckInterval`)
/// Sparkle fetches the appcast in the background; it never downloads or
/// installs on its own. An update it finds that way is shown gently: the
/// menu-bar icon gets a badge and the panel a row (`UpdateBadgeState`),
/// rather than a window popping up uninvited. Check for Updates… shows
/// Sparkle's window, as it always has.
@MainActor
final class AppUpdates: ObservableObject {
    static let shared = AppUpdates()
    @Published private(set) var canCheck = false
    /// Whether Sparkle checks once a day by itself (Settings › General).
    @Published private(set) var automaticallyChecks = true
    /// The waiting update, the badge and the panel's row.
    @Published private(set) var badge: UpdateBadgeState
    private var controller: SPUStandardUpdaterController?
    private var delegate: SparkleDelegate?
    private var observation: AnyCancellable?
    private var attemptedStart = false
    private let defaults: UserDefaults
    let configured: Bool

    /// `defaults` is the one Sparkle keeps its settings in: the app's
    /// standard defaults. Only `startIfConfigured()` touches it.
    init(configured: Bool = UpdateConfiguration(info: Bundle.main.infoDictionary ?? [:]) != nil,
         defaults: UserDefaults = .standard) {
        self.configured = configured
        self.defaults = defaults
        badge = UpdateBadgeState(configured: configured)
    }

    var buttonDisabled: Bool { configured && !canCheck }
    var status: String {
        guard configured else { return "An update service hasn’t been set up for this build yet." }
        if badge.pendingVersion != nil { return "\(UpdateBadgeState.headline(for: badge.pendingVersion))." }
        return automaticallyChecks
            ? "Lunalyr checks for updates once a day and asks before installing one."
            : "Lunalyr checks for updates only when you ask."
    }

    func startIfConfigured() {
        guard configured, !attemptedStart else { return }
        attemptedStart = true
        Self.adoptDailyChecks(in: defaults)
        let delegate = SparkleDelegate(owner: self)
        self.delegate = delegate
        let manager = SPUStandardUpdaterController(startingUpdater: false,
            updaterDelegate: delegate, userDriverDelegate: delegate)
        controller = manager
        observation = manager.updater.publisher(for: \.canCheckForUpdates)
            .receive(on: RunLoop.main)
            .sink { [weak self] allowed in self?.canCheck = allowed }
        manager.startUpdater()
        automaticallyChecks = manager.updater.automaticallyChecksForUpdates
    }

    /// Check for Updates…, and Update… on a waiting update: Sparkle's
    /// window, which for an update already found shows that update.
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
        guard canCheck || badge.pendingVersion != nil else { return }
        // Lunalyr has no Dock icon: bring its windows forward, over the app
        // in front, and out from under the menu-bar panel.
        LunalyrNavigation.dismissMenuBarPanel()
        NSApp.activate(ignoringOtherApps: true)
        controller?.checkForUpdates(nil)
    }

    /// Later, in the menu-bar panel.
    func dismissBadge() { receive(.dismissed) }

    func setAutomaticallyChecks(_ on: Bool) {
        automaticallyChecks = on
        // Sparkle saves it, and reschedules its next check, itself.
        controller?.updater.automaticallyChecksForUpdates = on
    }

    func receive(_ event: UpdateBadgeState.Event) {
        var next = badge
        next.handle(event)
        if next != badge { badge = next }
    }

    // MARK: Existing installs

    /// 2.0.0 shipped with background checks off and no switch for them, so
    /// a value saved in its defaults was never the person's choice. Removed
    /// once, before Sparkle starts, so Info.plist's daily checks apply;
    /// after that the Settings switch decides.
    nonisolated static let adoptedDailyChecksKey = "custom.updates.adoptedDailyChecks"

    nonisolated static func adoptDailyChecks(in defaults: UserDefaults) {
        guard !defaults.bool(forKey: adoptedDailyChecksKey) else { return }
        defaults.removeObject(forKey: "SUEnableAutomaticChecks")
        defaults.set(true, forKey: adoptedDailyChecksKey)
    }
}

/// Sparkle's delegates, forwarding what matters to `AppUpdates`. Sparkle
/// calls them on the main thread.
private final class SparkleDelegate: NSObject, SPUUpdaterDelegate, SPUStandardUserDriverDelegate {
    private weak var owner: AppUpdates?

    init(owner: AppUpdates) { self.owner = owner }

    private func send(_ event: UpdateBadgeState.Event) {
        MainActor.assumeIsolated { owner?.receive(event) }
    }

    // Never "Check for updates automatically?" on a second launch: the
    // answer is in Info.plist and Settings › General.
    func updaterShouldPromptForPermissionToCheck(forUpdates updater: SPUUpdater) -> Bool { false }

    var supportsGentleScheduledUpdateReminders: Bool { true }

    // A scheduled check never opens a window by itself: the badge says it.
    func standardUserDriverShouldHandleShowingScheduledUpdate(_ update: SUAppcastItem,
                                                              andInImmediateFocus immediateFocus: Bool) -> Bool {
        false
    }

    func standardUserDriverWillHandleShowingUpdate(_ handleShowingUpdate: Bool, forUpdate update: SUAppcastItem,
                                                   state: SPUUserUpdateState) {
        send(.found(version: update.displayVersionString, userInitiated: state.userInitiated,
                    sparkleShowsIt: handleShowingUpdate))
    }

    func standardUserDriverDidReceiveUserAttention(forUpdate update: SUAppcastItem) {
        send(.userAttention)
    }

    func standardUserDriverWillFinishUpdateSession() {
        send(.sessionFinished)
    }
}
