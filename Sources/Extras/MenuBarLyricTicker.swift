import AppKit
import Combine
import QuartzCore

/// The line being sung, in the menu bar. Optional, beside Lunalyr's own
/// icon. While a song with timed lyrics plays and they are on screen it
/// checks four times a second, and touches the menu bar only when the line
/// changes. Paused, it checks every two seconds -- the pace at which the
/// player is re-read in case it was scrubbed. Otherwise nothing can change
/// by itself (no song, no timed lyrics, a stream, hidden lyrics, a paused
/// wallpaper, sleeping displays), so it waits for the track, playback, the
/// lyrics and their readings, the Extras or the displays to change instead
/// of waking the Mac. Each change rolls the new line up into place (a plain
/// cross-fade with Reduce Motion).
@MainActor
final class MenuBarLyricTicker: NSObject, NSMenuDelegate {
    /// Shown instead of the line, so the state lasts where it can be seen.
    static let pausedText = "⏸ Wallpaper paused"
    static let hiddenText = "Lyrics hidden"
    static let tickInterval: TimeInterval = 0.25
    /// While the song is paused.
    static let pausedTickInterval: TimeInterval = 2
    /// Lets macOS fold the ticks in with other wake-ups.
    static func tolerance(for interval: TimeInterval) -> TimeInterval { interval * 0.4 }

    private weak var extras: LunalyrExtras?
    private var item: NSStatusItem?
    private var timer: Timer?
    private var timerInterval: TimeInterval?
    private var shown: String?
    private var changes: Set<AnyCancellable> = []
    private var screensAsleep = false

    init(extras: LunalyrExtras) {
        self.extras = extras
    }

    func start() {
        if item == nil {
            let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
            item.button?.font = NSFont.menuBarFont(ofSize: 0)
            item.button?.wantsLayer = true
            item.button?.lineBreakMode = .byTruncatingTail
            item.button?.setAccessibilityLabel("Current lyric")
            // Built as it opens, so its titles always match what is on screen.
            let menu = NSMenu()
            menu.delegate = self
            item.menu = menu
            self.item = item
        }
        if changes.isEmpty { observe() }
        refresh(force: true)
    }

    func stop() {
        tick(nil)
        changes.removeAll()
        if let item { NSStatusBar.system.removeStatusItem(item) }
        item = nil
        shown = nil
    }

    /// Everything besides time that changes the line or whether it moves:
    /// romanised readings arrive after the lines, and replace them here
    /// when readings are shown in place of the lyrics.
    private func observe() {
        let monitor = NowPlayingMonitor.shared
        var sources: [AnyPublisher<Void, Never>] = [
            monitor.$current.map { _ in () }.eraseToAnyPublisher(),
            monitor.$isPlaying.map { _ in () }.eraseToAnyPublisher(),
            LyricsViewModel.shared.$lines.map { _ in () }.eraseToAnyPublisher(),
            LyricsViewModel.shared.$romanized.map { _ in () }.eraseToAnyPublisher(),
            RomanizationOptions.shared.objectWillChange.eraseToAnyPublisher(),
        ]
        if let extras { sources.append(extras.objectWillChange.eraseToAnyPublisher()) }
        // Published values arrive before they are stored: read them a turn later.
        Publishers.MergeMany(sources)
            .receive(on: DispatchQueue.main)
            .sink { [weak self] in self?.refresh() }
            .store(in: &changes)
        let workspace = NSWorkspace.shared.notificationCenter
        workspace.publisher(for: NSWorkspace.screensDidSleepNotification)
            .sink { [weak self] _ in
                self?.screensAsleep = true
                self?.refresh()
            }
            .store(in: &changes)
        workspace.publisher(for: NSWorkspace.screensDidWakeNotification)
            .sink { [weak self] _ in
                self?.screensAsleep = false
                self?.refresh(force: true)
            }
            .store(in: &changes)
    }

    func refresh(force: Bool = false) {
        guard let item, let extras else { return }
        let text = Self.currentText(limit: Int(extras.menuBarLength), extras: extras)
        tick(Self.tickInterval(hasTrack: NowPlayingMonitor.shared.current != nil,
                               playing: NowPlayingMonitor.shared.isPlaying,
                               hasTimedLines: !LyricsViewModel.shared.lines.isEmpty,
                               lyricsVisible: extras.lyricsVisible, paused: extras.wallpaperPaused,
                               screensAsleep: screensAsleep))
        guard force || text != shown else { return }
        let changed = text != shown
        shown = text
        item.isVisible = text != nil
        guard let button = item.button else { return }
        if changed, let layer = button.layer {
            let transition = CATransition()
            let calm = NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
            transition.type = calm ? .fade : .push
            transition.subtype = .fromBottom
            transition.duration = calm ? 0.35 : 0.42
            transition.timingFunction = CAMediaTimingFunction(controlPoints: 0.22, 1, 0.36, 1)
            layer.add(transition, forKey: "lyric")
        }
        button.title = text ?? ""
    }

    /// How often time alone can change the line, or nil when it cannot: a
    /// song with timed lyrics -- without them the item only ever shows ♪ --
    /// whose lyrics are on screen, a moving wallpaper and a display awake to
    /// show it. Paused, only a scrub in the player moves it, which is read
    /// every two seconds.
    static func tickInterval(hasTrack: Bool, playing: Bool, hasTimedLines: Bool, lyricsVisible: Bool,
                             paused: Bool, screensAsleep: Bool) -> TimeInterval? {
        guard hasTrack, hasTimedLines, lyricsVisible, !paused, !screensAsleep else { return nil }
        return playing ? tickInterval : pausedTickInterval
    }

    private func tick(_ interval: TimeInterval?) {
        guard interval != timerInterval || (interval != nil && timer == nil) else { return }
        timer?.invalidate()
        timer = nil
        timerInterval = interval
        guard let interval else { return }
        let timer = Timer(timeInterval: interval, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.refresh() }
        }
        timer.tolerance = Self.tolerance(for: interval)
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    /// What the item shows, before the line itself is read.
    enum Display: Equatable {
        /// No item: nothing is playing.
        case none
        case note(String)
        /// The line being sung.
        case line
    }

    /// A paused wallpaper says so even with nothing playing; hidden lyrics,
    /// whether by the user or by a mirrored display, never show here either.
    static func display(paused: Bool, hasTrack: Bool, lyricsVisible: Bool) -> Display {
        if paused { return .note(pausedText) }
        guard hasTrack else { return .none }
        return lyricsVisible ? .line : .note(hiddenText)
    }

    /// What the menu bar shows right now, or nil to hide the item.
    static func currentText(limit: Int, extras: LunalyrExtras) -> String? {
        switch display(paused: extras.wallpaperPaused, hasTrack: NowPlayingMonitor.shared.current != nil,
                       lyricsVisible: extras.lyricsVisible) {
        case .none: return nil
        case .note(let text): return text
        case .line: break
        }
        let model = LyricsViewModel.shared
        let time = NowPlayingMonitor.shared.playbackTime() + Settings.shared.lyricOffset
            + SongOffsets.shared.offset(for: model.currentQuery)
        guard let index = model.activeLineIndex(at: time), model.lines.indices.contains(index) else { return "♪" }
        let line = model.lines[index]
        let words = RomanizationOptions.shared.display == .replace ? model.romanized[index] : nil
        let text = words.map { $0.map(\.text).joined(separator: " ") } ?? line.text
        // Between lines, after the voice has stopped, a note reads better
        // than a stale line.
        let finished = time > line.end + 1.5 && index + 1 < model.lines.count
        return Self.text(line.isInstrumental || finished ? nil : text, limit: limit)
    }

    /// A line shortened to `limit` characters at a word boundary where one is
    /// close, by whole characters (never splitting a cluster or emoji).
    static func text(_ line: String?, limit: Int) -> String {
        guard let line else { return "♪" }
        let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return "♪" }
        let cap = max(4, limit)
        guard trimmed.count > cap else { return trimmed }
        let cut = trimmed.prefix(cap - 1)
        // Cut exactly at a space: the last word is whole, keep it.
        if trimmed[cut.endIndex].isWhitespace {
            return cut.trimmingCharacters(in: .whitespaces) + "…"
        }
        if let space = cut.lastIndex(of: " "), cut.distance(from: cut.startIndex, to: space) > cap / 2 {
            return String(cut[..<space]) + "…"
        }
        return String(cut) + "…"
    }

    // MARK: - Menu

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        for entry in Self.menuEntries(extras: extras, hotkeys: .shared) {
            guard let entry else { menu.addItem(.separator()); continue }
            let item = NSMenuItem(title: entry.title, action: entry.action, keyEquivalent: entry.key?.key ?? "")
            item.target = self
            if let key = entry.key { item.keyEquivalentModifierMask = key.modifiers }
            menu.addItem(item)
        }
    }

    struct MenuEntry {
        let title: String
        let action: Selector
        /// Shown beside the title while the global shortcut is registered.
        let key: (key: String, modifiers: NSEvent.ModifierFlags)?
    }

    /// The menu, top to bottom; nil is a separator. The names match the
    /// menu-bar window's.
    static func menuEntries(extras: LunalyrExtras?, hotkeys: GlobalHotkeys) -> [MenuEntry?] {
        let shortcuts = extras?.shortcutsEnabled == true
        func key(_ action: GlobalHotkeys.Action) -> (key: String, modifiers: NSEvent.ModifierFlags)? {
            shortcuts ? hotkeys.keyEquivalent(for: action) : nil
        }
        return [
            MenuEntry(title: extras?.lyricsActionTitle ?? LunalyrExtras.Titles.hideLyrics,
                      action: #selector(MenuBarLyricTicker.toggleLyrics), key: key(.toggleLyrics)),
            MenuEntry(title: extras?.pauseActionTitle ?? LunalyrExtras.Titles.pauseWallpaper,
                      action: #selector(MenuBarLyricTicker.togglePause), key: key(.pauseWallpaper)),
            MenuEntry(title: LunalyrExtras.Titles.copyCard, action: #selector(MenuBarLyricTicker.copyCard), key: key(.copyCard)),
            nil,
            MenuEntry(title: LunalyrExtras.Titles.settings, action: #selector(MenuBarLyricTicker.openSettings), key: nil),
            MenuEntry(title: "Hide This Line from the Menu Bar", action: #selector(MenuBarLyricTicker.hideTicker), key: nil),
        ]
    }

    @objc private func toggleLyrics() { extras?.toggleLyrics() }
    @objc private func togglePause() { extras?.toggleWallpaperPause() }
    @objc private func copyCard() { extras?.copyLyricCard() }
    @objc private func openSettings() { LunalyrNavigation.shared.openSettings() }
    @objc private func hideTicker() { extras?.showsMenuBarLine = false }
}
