import AppKit
import Combine
import SwiftUI

/// Whether one surface can be seen, so its frame clock and videos can stop
/// when it cannot. A wallpaper normally belongs to an inactive app, so this
/// follows the surface rather than app activation.
///
/// Desktop wallpapers take their visibility from `DesktopCoverageMonitor`,
/// which reads the window list: their own occlusion state is unreliable below
/// Finder's icon window and only triggers a fresh check. The Settings preview
/// lives in an ordinary window, whose occlusion and minimised state are exact.
///
/// Every attached activity is listed in `RenderSurfaces`, which System audio
/// reads to know whether anything that could show a visualiser is drawing.
@MainActor
final class WindowRenderActivity: ObservableObject {
    /// What a surface's frame clock is doing.
    enum State: Equatable {
        case drawing
        /// Covered, minimised or closed, or the screens are asleep or
        /// locked: it may be seen again at any moment.
        case hidden
        /// A desktop wallpaper that Pause wallpaper froze, until the user
        /// resumes it.
        case frozen
    }

    @Published private(set) var isVisible = true
    /// Displays asleep, screen locked, screen saver or the user switched out.
    @Published private(set) var screensAsleep = false
    private weak var window: NSWindow?
    private var followsOrdinaryWindow = false
    private let windowObservers = ObserverBag()
    private var cancellables: Set<AnyCancellable> = []
    private let surfaces: RenderSurfaces

    /// `surfaces` is where this activity is listed; tests pass their own.
    init(surfaces: RenderSurfaces? = nil) {
        self.surfaces = surfaces ?? .shared
    }

    /// A desktop wallpaper window on `displayID`.
    func start(window: NSWindow, displayID: CGDirectDisplayID) {
        stop()
        self.window = window
        followsOrdinaryWindow = false
        let monitor = DesktopCoverageMonitor.shared
        windowObservers.tokens.append(NotificationCenter.default.addObserver(
            forName: NSWindow.didChangeOcclusionStateNotification, object: window, queue: .main
        ) { _ in
            Task { @MainActor in DesktopCoverageMonitor.shared.setNeedsUpdate() }
        })
        monitor.$hiddenDisplays
            .map { !$0.contains(displayID) }
            .removeDuplicates()
            .sink { [weak self] visible in self?.setVisible(visible) }
            .store(in: &cancellables)
        followSystemState()
        surfaces.attach(self)
        monitor.setNeedsUpdate()
    }

    /// An ordinary window such as Settings: paused while minimised, hidden,
    /// fully covered or closed, whatever the wallpaper preference says.
    func follow(window: NSWindow?) {
        guard window !== self.window || !followsOrdinaryWindow else { return }
        stop()
        followsOrdinaryWindow = true
        self.window = window
        guard let window else { setVisible(false); return }
        for name in [NSWindow.didChangeOcclusionStateNotification, NSWindow.didMiniaturizeNotification,
                     NSWindow.didDeminiaturizeNotification, NSWindow.willCloseNotification] {
            windowObservers.tokens.append(NotificationCenter.default.addObserver(
                forName: name, object: window, queue: .main
            ) { [weak self] notification in
                let closing = notification.name == NSWindow.willCloseNotification
                Task { @MainActor [weak self] in self?.refreshOrdinaryWindow(closing: closing) }
            })
        }
        followSystemState()
        refreshOrdinaryWindow(closing: false)
        surfaces.attach(self)
    }

    /// What this surface's frame clock does with these choices: "Pause fully
    /// covered wallpapers" and Pause wallpaper.
    func state(pauseWhenCovered: Bool, wallpaperPaused: Bool) -> State {
        Self.state(visible: isVisible, screensAsleep: screensAsleep, followsWindow: followsOrdinaryWindow,
                   pauseWhenCovered: pauseWhenCovered, wallpaperPaused: wallpaperPaused)
    }

    /// Whether this surface's frame clock runs. Pause wallpaper
    /// (`wallpaperPaused`) only ever stops a desktop wallpaper.
    func shouldRender(pauseWhenCovered: Bool, wallpaperPaused: Bool = false) -> Bool {
        state(pauseWhenCovered: pauseWhenCovered, wallpaperPaused: wallpaperPaused) == .drawing
    }

    /// The rule every surface's frame clock follows, and System audio with
    /// it. Pause wallpaper freezes the desktop wallpapers, but not an
    /// ordinary window (`followsWindow`: the Settings preview, where changes
    /// are judged), which stops only when it cannot be seen. A wallpaper
    /// stops while covered only when "Pause fully covered wallpapers" is on.
    /// Nothing renders while the screens sleep, lock or run the screen saver.
    nonisolated static func state(visible: Bool, screensAsleep: Bool, followsWindow: Bool,
                                  pauseWhenCovered: Bool, wallpaperPaused: Bool) -> State {
        if wallpaperPaused && !followsWindow { return .frozen }
        guard !screensAsleep else { return .hidden }
        return (followsWindow || pauseWhenCovered) && !visible ? .hidden : .drawing
    }

    func stop() {
        windowObservers.removeAll()
        cancellables.removeAll()
        window = nil
        surfaces.detach(self)
    }

    private func followSystemState() {
        DesktopCoverageMonitor.shared.$systemHidden
            .removeDuplicates()
            .sink { [weak self] hidden in
                guard let self, self.screensAsleep != hidden else { return }
                self.screensAsleep = hidden
                self.surfaces.changed()
            }
            .store(in: &cancellables)
    }

    private func refreshOrdinaryWindow(closing: Bool) {
        guard let window, !closing else { setVisible(false); return }
        setVisible(window.isVisible && !window.isMiniaturized && window.occlusionState.contains(.visible))
    }

    private func setVisible(_ visible: Bool) {
        guard visible != isVisible else { return }
        isVisible = visible
        surfaces.changed()
    }
}

/// The surfaces whose frame clocks are attached: each display's wallpaper,
/// and the Settings preview while it is shown. System audio asks this
/// whether anything that could show a visualiser is drawing, by the same
/// rule each surface's frame clock follows, instead of reading window
/// occlusion -- which is unreliable for the wallpapers and never covered
/// the preview, so the preview and its meters went silent whenever every
/// wallpaper was covered. Pause wallpaper is part of that rule: it freezes
/// the wallpapers, while a preview that is still moving keeps drawing.
@MainActor
final class RenderSurfaces {
    static let shared = RenderSurfaces()

    private struct Member { weak var activity: WindowRenderActivity? }
    private var members: [ObjectIdentifier: Member] = [:]
    private let subject = PassthroughSubject<Void, Never>()
    private var notificationPending = false

    /// Fires on the main queue, once for a burst of changes, when a surface
    /// is attached or detached or is seen or hidden. Pause wallpaper is a
    /// setting, which its listeners follow themselves.
    var changes: AnyPublisher<Void, Never> { subject.eraseToAnyPublisher() }

    /// What each attached surface is doing with these choices; empty while
    /// there are none.
    func states(pauseWhenCovered: Bool, wallpaperPaused: Bool) -> [WindowRenderActivity.State] {
        members = members.filter { $0.value.activity != nil }
        return members.values.compactMap {
            $0.activity?.state(pauseWhenCovered: pauseWhenCovered, wallpaperPaused: wallpaperPaused)
        }
    }

    fileprivate func attach(_ activity: WindowRenderActivity) {
        members[ObjectIdentifier(activity)] = Member(activity: activity)
        changed()
    }

    fileprivate func detach(_ activity: WindowRenderActivity) {
        guard members.removeValue(forKey: ObjectIdentifier(activity)) != nil else { return }
        changed()
    }

    /// Several displays change together, so listeners hear about it once,
    /// after every surface has taken its new state.
    fileprivate func changed() {
        guard !notificationPending else { return }
        notificationPending = true
        DispatchQueue.main.async { [weak self] in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.notificationPending = false
                self.subject.send()
            }
        }
    }
}

/// Notification tokens removed when their owner goes away. A Settings
/// preview's activity is released with the window.
private final class ObserverBag {
    var tokens: [NSObjectProtocol] = []
    func removeAll() {
        tokens.forEach { NotificationCenter.default.removeObserver($0) }
        tokens.removeAll()
    }
    deinit { tokens.forEach { NotificationCenter.default.removeObserver($0) } }
}

/// Reports the window hosting a SwiftUI view, so the view's activity can
/// follow it.
struct HostWindowReader: NSViewRepresentable {
    let onChange: @MainActor (NSWindow?) -> Void

    func makeNSView(context: Context) -> ReaderView {
        let view = ReaderView()
        view.onChange = onChange
        return view
    }

    func updateNSView(_ view: ReaderView, context: Context) { view.onChange = onChange }

    final class ReaderView: NSView {
        var onChange: (@MainActor (NSWindow?) -> Void)?
        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            let window = self.window
            // Report after the move settles; SwiftUI may still be mid-update.
            DispatchQueue.main.async { [weak self] in
                guard let self, self.window === window else { return }
                MainActor.assumeIsolated { self.onChange?(window) }
            }
        }
        override func hitTest(_ point: NSPoint) -> NSView? { nil }
    }
}
