import AppKit
import Combine
import QuartzCore

/// The line being sung, in the menu bar. Optional, beside Lunalyr's own
/// icon. Its width is set once per song, from the widest line it will show,
/// so the status items to its left stay put while lines change; a line too
/// wide for it is shown in two halves, one after the other. While a song with
/// timed lyrics plays and they are on screen it wakes once per change -- the
/// next line, the second half of a long one, or the moment a finished line
/// gives way to ♪ -- and at least every two seconds to catch a scrub in the
/// player. Paused, it checks every two seconds. Otherwise nothing can change
/// by itself (no song, no timed lyrics, a stream, hidden lyrics, a paused
/// wallpaper, sleeping displays), so it waits for the track, playback, the
/// lyrics and their readings, the Extras or the displays to change instead
/// of waking the Mac. Each change rolls the new line up into place (a plain
/// cross-fade with Reduce Motion).
@MainActor
final class MenuBarLyricTicker: NSObject, NSMenuDelegate {
    /// Shown instead of the line, so the state lasts where it can be seen,
    /// beside the `pausedSymbol` image (an SF Symbol, never colour emoji).
    static let pausedText = "Wallpaper paused"
    static let pausedSymbol = "pause.rectangle.fill"
    static let hiddenText = "Lyrics hidden"
    /// The longest wait while a song plays: a scrub in the player is caught
    /// within this.
    static let maxWait: TimeInterval = 2
    /// The shortest wait, so a line start just missed is not spun on.
    static let minWait: TimeInterval = 0.05
    /// While the song is paused.
    static let pausedTickInterval: TimeInterval = 2
    /// How late a line start may show: barely noticeable, yet lets macOS
    /// fold the wake-up in with others.
    static let lineTolerance: TimeInterval = 0.03
    /// After a line, a note reads better than a stale line.
    static let finishedAfter: TimeInterval = 1.5
    /// Lines that change faster than this swap without the roll, so the
    /// roll never stutters.
    static let minTransitionGap: TimeInterval = 0.3
    /// With nothing playing the item waits this long before it leaves, so
    /// a skip (where players briefly report no track) never pops the menu bar.
    static let hideDelay: TimeInterval = 2
    /// Padding beside the widest line inside the item.
    static let widthPadding: CGFloat = 8

    private weak var extras: LunalyrExtras?
    private var item: NSStatusItem?
    private var timer: Timer?
    private var shown: Shown?
    private var changes: Set<AnyCancellable> = []
    private var screensAsleep = false
    private var layout: Layout?
    private var lastTransition = -Double.infinity
    private var hideWork: DispatchWorkItem?

    /// What is on the item now.
    private struct Shown: Equatable {
        var text: String
        var symbol: String?
        var dimmed: Bool
    }

    init(extras: LunalyrExtras) {
        self.extras = extras
    }

    func start() {
        if item == nil {
            let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
            item.button?.font = Self.font
            item.button?.wantsLayer = true
            item.button?.lineBreakMode = .byTruncatingTail
            item.button?.imagePosition = .imageLeading
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
        schedule(nil)
        hideWork?.cancel()
        hideWork = nil
        changes.removeAll()
        if let item { NSStatusBar.system.removeStatusItem(item) }
        item = nil
        shown = nil
        layout = nil
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
            LyricsViewModel.shared.$isLoading.removeDuplicates().map { _ in () }.eraseToAnyPublisher(),
            RomanizationOptions.shared.objectWillChange.eraseToAnyPublisher(),
        ]
        if let extras { sources.append(extras.objectWillChange.eraseToAnyPublisher()) }
        // Published values arrive before they are stored: read them a turn later.
        Publishers.MergeMany(sources)
            .receive(on: DispatchQueue.main)
            .sink { [weak self] in self?.refresh(relayout: true) }
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

    /// Re-reads what to show. `relayout` re-measures the song's lines (the
    /// lyrics, their readings or the length setting changed); `force`
    /// also redraws an unchanged line.
    func refresh(force: Bool = false, relayout: Bool = false) {
        guard let item, let extras else { return }
        if relayout || force { layout = nil }
        let monitor = NowPlayingMonitor.shared
        let model = LyricsViewModel.shared
        let playing = monitor.isPlaying
        let display = Self.display(paused: extras.wallpaperPaused, hasTrack: monitor.current != nil,
                                   lyricsVisible: extras.lyricsVisible)
        let maxWidth = Self.maxWidth(forCharacters: Int(extras.menuBarLength))
        var next: Shown
        var length: CGFloat
        var event: Double?
        switch display {
        case .none:
            // Between songs: hold a quiet ♪ for a moment before leaving.
            guard shown != nil, item.isVisible else {
                // Nothing was showing: stay away.
                item.isVisible = false
                schedule(nil)
                return
            }
            next = Shown(text: "♪", symbol: nil, dimmed: true)
            length = item.length > 0 ? item.length : Self.width(of: "♪") + Self.widthPadding
            if hideWork == nil {
                let work = DispatchWorkItem { [weak self] in
                    MainActor.assumeIsolated {
                        guard let self else { return }
                        self.hideWork = nil
                        self.item?.isVisible = false
                        self.shown = nil
                    }
                }
                hideWork = work
                DispatchQueue.main.asyncAfter(deadline: .now() + Self.hideDelay, execute: work)
            }
        case .note(let text):
            next = Shown(text: text, symbol: text == Self.pausedText ? Self.pausedSymbol : nil, dimmed: false)
            length = min(maxWidth, Self.width(of: text) + (next.symbol == nil ? 0 : 20)) + Self.widthPadding
        case .line:
            let layout = self.layout ?? Self.layout(lines: model.lines, readings: Self.readings(model: model),
                                                    maxWidth: maxWidth, measure: Self.width(of:))
            self.layout = layout
            let time = monitor.playbackTime() + Settings.shared.lyricOffset + SongOffsets.shared.offset(for: model.currentQuery)
            let state = Self.lineState(lines: model.lines, index: model.activeLineIndex(at: time),
                                       pageSwitches: layout.pageSwitches, time: time)
            event = state.nextEvent.map { $0 - time }
            let text = state.line.flatMap { layout.pages.indices.contains($0) ? layout.pages[$0][min(state.page, layout.pages[$0].count - 1)] : nil }
            // The music paused: the line stays, quieter, so it reads as stopped.
            next = Shown(text: text ?? "♪", symbol: nil, dimmed: !playing)
            // While the new song's lyrics are being found, keep the width:
            // it changes once, when they arrive (or turn out not to exist).
            let waiting = model.lines.isEmpty && model.isLoading && item.length > 0
            length = waiting ? item.length : layout.width
        }
        if display != .none {
            hideWork?.cancel()
            hideWork = nil
            item.isVisible = true
        }
        let interval = Self.tickInterval(hasTrack: monitor.current != nil, playing: playing,
                                         hasTimedLines: !model.lines.isEmpty, lyricsVisible: extras.lyricsVisible,
                                         paused: extras.wallpaperPaused, screensAsleep: screensAsleep)
        schedule(interval.map { ceiling in
            playing ? Self.wait(untilEventIn: event, ceiling: ceiling) : ceiling
        })
        if abs(item.length - length) > 0.5 { item.length = length }
        guard force || next != shown else { return }
        let textChanged = next.text != shown?.text || next.symbol != shown?.symbol
        shown = next
        guard let button = item.button else { return }
        let now = CACurrentMediaTime()
        if textChanged, now - lastTransition >= Self.minTransitionGap, let layer = button.layer {
            lastTransition = now
            layer.add(Self.transition(reduceMotion: NSWorkspace.shared.accessibilityDisplayShouldReduceMotion),
                      forKey: "lyric")
        }
        button.image = next.symbol.flatMap {
            NSImage(systemSymbolName: $0, accessibilityDescription: nil)?
                .withSymbolConfiguration(.init(pointSize: Self.font.pointSize, weight: .regular))
        }
        button.attributedTitle = NSAttributedString(string: next.text, attributes: [
            .font: Self.font,
            .foregroundColor: next.dimmed ? NSColor.secondaryLabelColor : NSColor.labelColor,
        ])
    }

    /// The roll that brings a new line in: a push up, or a short cross-fade
    /// with Reduce Motion.
    static func transition(reduceMotion: Bool) -> CATransition {
        let transition = CATransition()
        transition.type = reduceMotion ? .fade : .push
        transition.subtype = .fromBottom
        transition.duration = reduceMotion ? LunalyrDesign.Motion.CA.reduced : LunalyrDesign.Motion.CA.line
        transition.timingFunction = LunalyrDesign.Motion.CA.easeOut
        return transition
    }

    /// The longest wait between checks, or nil when time alone cannot change
    /// the line: a song with timed lyrics -- without them the item only ever
    /// shows ♪ -- whose lyrics are on screen, a moving wallpaper and a display
    /// awake to show it. Paused, only a scrub in the player moves it, which is
    /// read every two seconds.
    static func tickInterval(hasTrack: Bool, playing: Bool, hasTimedLines: Bool, lyricsVisible: Bool,
                             paused: Bool, screensAsleep: Bool) -> TimeInterval? {
        guard hasTrack, hasTimedLines, lyricsVisible, !paused, !screensAsleep else { return nil }
        return playing ? maxWait : pausedTickInterval
    }

    /// How long to sleep while playing: until the next change, kept between
    /// `minWait` and `ceiling`.
    static func wait(untilEventIn delay: TimeInterval?, ceiling: TimeInterval) -> TimeInterval {
        guard let delay, delay.isFinite else { return ceiling }
        return min(ceiling, max(minWait, delay + 0.01))
    }

    /// Line starts are kept close; a plain re-read may wait a little.
    static func tolerance(for wait: TimeInterval) -> TimeInterval {
        wait < maxWait ? lineTolerance : wait * 0.1
    }

    /// One-shot: each wake-up re-reads the line and aims the next one.
    private func schedule(_ wait: TimeInterval?) {
        timer?.invalidate()
        timer = nil
        guard let wait else { return }
        let timer = Timer(timeInterval: wait, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.timer = nil
                self?.refresh()
            }
        }
        timer.tolerance = Self.tolerance(for: wait)
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

    // MARK: - Lines, pages and timing

    /// Which line (nil: ♪) and which half of it shows at `time`, and when that
    /// next changes.
    struct LineState: Equatable {
        var line: Int?
        var page: Int
        /// Playback time of the next change, if any is coming.
        var nextEvent: Double?
    }

    static func lineState(lines: [LyricsFetcher.Line], index: Int?, pageSwitches: [Double?], time: Double) -> LineState {
        guard let index, lines.indices.contains(index) else {
            return LineState(line: nil, page: 0, nextEvent: lines.first(where: { $0.start > time })?.start)
        }
        let line = lines[index]
        let hasNext = index + 1 < lines.count
        var events: [Double] = []
        if hasNext { events.append(lines[index + 1].start) }
        let finishedAt = line.end + finishedAfter
        let finished = hasNext && time > finishedAt
        if hasNext, !finished { events.append(finishedAt) }
        var page = 0
        if pageSwitches.indices.contains(index), let flip = pageSwitches[index] {
            if time >= flip { page = 1 } else { events.append(flip) }
        }
        let next = events.filter { $0 > time }.min()
        if line.isInstrumental || finished { return LineState(line: nil, page: 0, nextEvent: next) }
        return LineState(line: index, page: page, nextEvent: next)
    }

    /// Each line's text as shown -- its reading when readings replace the
    /// lyrics -- with the words that time it.
    struct LineText {
        var text: String
        var words: [LyricsFetcher.Word]
    }

    static func readings(model: LyricsViewModel) -> [LineText] {
        let replace = RomanizationOptions.shared.display == .replace
        return model.lines.enumerated().map { index, line in
            if replace, let words = model.romanized[index] {
                return LineText(text: words.map(\.text).joined(separator: " "), words: words)
            }
            return LineText(text: line.text, words: line.words)
        }
    }

    /// A song's lines measured once: each line's one or two pages, when the
    /// second page takes over, and the item's width.
    struct Layout: Equatable {
        var pages: [[String]]
        var pageSwitches: [Double?]
        var width: CGFloat
    }

    static func layout(lines: [LyricsFetcher.Line], readings: [LineText], maxWidth: CGFloat,
                       measure: (String) -> CGFloat) -> Layout {
        var pages: [[String]] = []
        var switches: [Double?] = []
        var widest = measure("♪")
        for (index, line) in lines.enumerated() {
            let reading = readings.indices.contains(index) ? readings[index] : LineText(text: line.text, words: line.words)
            let split = Self.pages(reading.text, maxWidth: maxWidth, measure: measure)
            pages.append(split)
            if !line.isInstrumental { widest = max(widest, split.map(measure).max() ?? 0) }
            switches.append(split.count > 1 ? pageSwitch(line: line, words: reading.words, text: reading.text,
                                                         pages: split) : nil)
        }
        return Layout(pages: pages, pageSwitches: switches,
                      width: (min(maxWidth, widest) + widthPadding).rounded(.up))
    }

    /// The menu-bar font, and the room the length setting allows.
    static var font: NSFont { NSFont.menuBarFont(ofSize: 0) }

    static func width(of text: String) -> CGFloat {
        ceil(NSAttributedString(string: text, attributes: [.font: font]).size().width)
    }

    /// The "Longest line" setting counts characters; an average character
    /// of the menu-bar font turns it into points.
    static func maxWidth(forCharacters count: Int) -> CGFloat {
        let sample = "The quick brown fox jumps over the lazy dog"
        return (width(of: sample) / CGFloat(sample.count) * CGFloat(max(4, count))).rounded()
    }

    /// A line as one page when it fits, else two halves split at the space
    /// nearest its middle (or the middle itself, for writing without spaces),
    /// each shortened to fit if it still does not.
    static func pages(_ line: String, maxWidth: CGFloat, measure: (String) -> CGFloat) -> [String] {
        let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return ["♪"] }
        guard measure(trimmed) > maxWidth, trimmed.count > 1 else { return [trimmed] }
        let characters = Array(trimmed)
        let middle = characters.count / 2
        let spaces = characters.indices.filter { characters[$0].isWhitespace }
        let cut = spaces.min { abs($0 - middle) < abs($1 - middle) } ?? middle
        let first = String(characters[..<cut]).trimmingCharacters(in: .whitespaces)
        let second = String(characters[cut...]).trimmingCharacters(in: .whitespaces)
        guard !first.isEmpty, !second.isEmpty else { return [fitted(trimmed, maxWidth: maxWidth, measure: measure)] }
        return [fitted(first, maxWidth: maxWidth, measure: measure), fitted(second, maxWidth: maxWidth, measure: measure)]
    }

    /// When the second half of a paged line takes over: when its first word
    /// is sung, if the words are timed one by one, else a share of the line
    /// in proportion to its characters.
    static func pageSwitch(line: LyricsFetcher.Line, words: [LyricsFetcher.Word], text: String,
                           pages: [String]) -> Double {
        let tokens = text.split(whereSeparator: \.isWhitespace).count
        let firstCount = pages[0].split(whereSeparator: \.isWhitespace).count
        if words.count == tokens, words.indices.contains(firstCount), !pages[0].hasSuffix("…") {
            return words[firstCount].start
        }
        let total = pages.reduce(0) { $0 + $1.count }
        let share = total > 0 ? Double(pages[0].count) / Double(total) : 0.5
        // A line timed only at its start ends where the next begins, which
        // may be long after the singing; assume a brisk 11 characters a second.
        let sungEnd = words.last?.end
            ?? (line.endFromNextLine == true ? min(line.end, line.start + Double(total) / 11) : line.end)
        return line.start + max(0, sungEnd - line.start) * share
    }

    /// A text shortened to `maxWidth` with "…", at a word boundary where one
    /// is close, by whole characters (never splitting a cluster or emoji).
    static func fitted(_ text: String, maxWidth: CGFloat, measure: (String) -> CGFloat) -> String {
        guard measure(text) > maxWidth else { return text }
        let characters = Array(text)
        var low = 1, high = characters.count - 1, best = 1
        while low <= high {
            let middle = (low + high) / 2
            if measure(String(characters[..<middle]) + "…") <= maxWidth {
                best = middle
                low = middle + 1
            } else {
                high = middle - 1
            }
        }
        let cut = characters[..<best]
        // Cut exactly at a space: the last word is whole, keep it.
        if characters[best].isWhitespace { return String(cut).trimmingCharacters(in: .whitespaces) + "…" }
        if let space = cut.lastIndex(where: \.isWhitespace), space > best / 2 {
            return String(cut[..<space]).trimmingCharacters(in: .whitespaces) + "…"
        }
        return String(cut).trimmingCharacters(in: .whitespaces) + "…"
    }

    // MARK: - Menu

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        for entry in Self.menuEntries(extras: extras, hotkeys: .shared, tapSync: .shared) {
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
    static func menuEntries(extras: LunalyrExtras?, hotkeys: GlobalHotkeys,
                            tapSync: LyricTapSyncController? = nil) -> [MenuEntry?] {
        let shortcuts = extras?.shortcutsEnabled == true
        func key(_ action: GlobalHotkeys.Action) -> (key: String, modifiers: NSEvent.ModifierFlags)? {
            shortcuts ? hotkeys.keyEquivalent(for: action) : nil
        }
        var tapEntries: [MenuEntry?] = []
        if let tapSync {
            tapEntries.append(MenuEntry(title: tapSync.actionTitle, action: #selector(MenuBarLyricTicker.tapSync),
                                        key: key(.tapSyncLyrics)))
            if tapSync.canUndo {
                tapEntries.append(MenuEntry(title: LyricTapSyncController.undoTitle,
                                            action: #selector(MenuBarLyricTicker.undoTapSync), key: nil))
            }
        }
        return [
            MenuEntry(title: extras?.lyricsActionTitle ?? LunalyrExtras.Titles.hideLyrics,
                      action: #selector(MenuBarLyricTicker.toggleLyrics), key: key(.toggleLyrics)),
            MenuEntry(title: extras?.pauseActionTitle ?? LunalyrExtras.Titles.pauseWallpaper,
                      action: #selector(MenuBarLyricTicker.togglePause), key: key(.pauseWallpaper)),
            MenuEntry(title: LunalyrExtras.Titles.copyCard, action: #selector(MenuBarLyricTicker.copyCard), key: key(.copyCard)),
        ] + tapEntries + [
            nil,
            MenuEntry(title: LunalyrExtras.Titles.settings, action: #selector(MenuBarLyricTicker.openSettings), key: nil),
            MenuEntry(title: "Hide This Line from the Menu Bar", action: #selector(MenuBarLyricTicker.hideTicker), key: nil),
        ]
    }

    @objc private func toggleLyrics() { extras?.toggleLyrics() }
    @objc private func togglePause() { extras?.toggleWallpaperPause() }
    @objc private func copyCard() { extras?.copyLyricCard() }
    @objc private func tapSync() { LyricTapSyncController.shared.toggle() }
    @objc private func undoTapSync() { LyricTapSyncController.shared.undo() }
    @objc private func openSettings() { LunalyrNavigation.shared.openSettings() }
    @objc private func hideTicker() { extras?.showsMenuBarLine = false }
}
