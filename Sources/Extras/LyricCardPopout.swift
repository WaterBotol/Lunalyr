import AppKit
import Combine
import SwiftUI
import UniformTypeIdentifiers

// MARK: - The lyric card window
//
// Copy Lyric Card (the menu-bar tile, the shortcut and Settings) opens this
// window for the song that is playing: its whole lyrics to pick lines from
// (click, shift-click for a range, ⌘-click to add), the card's shape, and
// which of MP4, GIF and PNG to make. Copy puts the chosen formats on the
// clipboard as attachments; Save… writes them to a folder.

/// What the card window reads about the song, and where it makes and copies
/// cards. The app's is `.live`; tests pass their own, so they never touch the
/// player, the clipboard or real preferences.
struct LyricCardSource {
    typealias Export = @MainActor (_ content: LyricCard.Content, _ formats: Set<CardFormat>, _ directory: URL,
                                   _ notes: @escaping (String) -> Void,
                                   _ phase: @escaping (LyricCard.ExportPhase) -> Void,
                                   _ progress: @escaping (Double) -> Void) async -> [(CardFormat, URL)]

    /// Tells songs apart: another key is another song.
    var trackKey: @MainActor () -> String
    var title: @MainActor () -> String
    var artist: @MainActor () -> String
    var hasSong: @MainActor () -> Bool
    /// The lines to pick from.
    var rows: @MainActor () -> [LyricCardPopoutModel.Row]
    /// The row being sung now, if any.
    var activeRow: @MainActor () -> Int?
    var content: @MainActor (_ rows: [Int], _ layout: LyricComposition?, _ aspect: CardAspect) -> LyricCard.Content?
    /// The card layout shared with Settings → Share → Card layout.
    var composition: @MainActor () -> LyricComposition?
    var setComposition: @MainActor (LyricComposition?) -> Void
    var export: Export
    var pasteboard: NSPasteboard
    var defaults: UserDefaults
    /// Refreshes the lines when the song or its lyrics change (the app only).
    var followsTheSong: Bool

    @MainActor static var live: Self {
        Self(trackKey: {
                 let current = NowPlayingMonitor.shared.current
                 return "\(current?.title ?? "")|\(current?.artist ?? "")"
             },
             title: { NowPlayingMonitor.shared.current?.title ?? "" },
             artist: { NowPlayingMonitor.shared.current?.artist ?? "" },
             hasSong: { NowPlayingMonitor.shared.current != nil },
             rows: {
                 let model = LyricsViewModel.shared
                 if model.lines.isEmpty {
                     // The same filtered list `LyricCard.currentContent` indexes.
                     return model.plainLines.filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
                         .enumerated().map { LyricCardPopoutModel.Row(id: $0.offset, text: $0.element) }
                 }
                 return model.lines.indices
                     .filter { !model.lines[$0].isInstrumental && !model.lines[$0].text.isEmpty }
                     .map { LyricCardPopoutModel.Row(id: $0, text: model.lines[$0].text) }
             },
             activeRow: {
                 let model = LyricsViewModel.shared
                 let time = NowPlayingMonitor.shared.playbackTime() + Settings.shared.lyricOffset
                     + SongOffsets.shared.offset(for: model.currentQuery)
                 return model.activeLineIndex(at: time)
             },
             content: { rows, layout, aspect in
                 LyricCard.currentContent(before: 0, after: 0, composition: layout, rows: rows, aspect: aspect)
             },
             composition: { LunalyrExtras.shared.lyricCardComposition },
             setComposition: { LunalyrExtras.shared.lyricCardComposition = $0 },
             export: { content, formats, directory, notes, phase, progress in
                 await LyricCard.export(content, formats: formats, to: directory, notes: notes, phase: phase, progress: progress)
             },
             pasteboard: .general,
             defaults: .standard,
             followsTheSong: true)
    }
}

@MainActor
final class LyricCardPopoutModel: ObservableObject {
    struct Row: Identifiable, Equatable {
        let id: Int
        let text: String
    }

    private enum Keys {
        static let aspect = "custom.lyricCardAspect"
        static let formats = "custom.lyricCardFormats"
        static let background = "custom.lyricCardBackgroundPath"
        static let useSong = "custom.lyricCardUseSong"
        static let textScale = "custom.lyricCardTextScale"
    }

    @Published var rows: [Row] = []
    @Published var selection: Set<Int> = [] { didSet { optionChanged() } }
    /// The card's layout; nil follows the wallpaper. Shared with Settings → Share → Card layout.
    @Published var layout: LyricComposition? {
        didSet { source.setComposition(layout); optionChanged() }
    }
    @Published var aspect: CardAspect {
        didSet { source.defaults.set(aspect.rawValue, forKey: Keys.aspect); optionChanged() }
    }
    @Published var formats: Set<CardFormat> {
        didSet { source.defaults.set(formats.map(\.rawValue).sorted(), forKey: Keys.formats); clearResult() }
    }
    @Published var preview: NSImage?
    /// Counts the previews drawn, so each new one cross-fades in.
    @Published private(set) var previewGeneration = 0
    /// The preview shown is out of date and a new one hasn't come yet.
    @Published private(set) var previewStale = false
    @Published var progress: Double?
    @Published var status = ""

    /// How the status line reads: a plain note, a card made, or a failure.
    enum StatusTone: Equatable { case plain, success, failure }
    @Published var statusTone: StatusTone = .plain
    /// What the last export noted (a recording that didn't cover every line,
    /// a sound it couldn't read), shown under Details.
    @Published var warnings: [String] = []
    /// The files the last export made, for Show in Finder.
    @Published private(set) var madeFiles: [URL] = []
    /// Counts the cards made, for the one-shot check mark.
    @Published private(set) var cardsMade = 0
    @Published var title = ""
    @Published var artist = ""
    /// A picture, GIF or video behind the lyrics; nil keeps the album look.
    @Published var background: LyricCardBackground? {
        didSet {
            // The lyrics' colours and transitions follow the background's own colours, not the album's.
            backgroundPalette = background.map { LyricCard.palette(for: NSImage(cgImage: $0.first, size: .zero)) }
            optionChanged()
        }
    }
    private var backgroundPalette: AlbumWallpaperPalette?
    /// An audio file mixed under the MP4, and how loud.
    struct SoundFile: Identifiable, Equatable {
        let id = UUID()
        var url: URL
        var volume = 1.0
    }
    /// Any number of audio files to go under the MP4, mixed together.
    @Published var soundFiles: [SoundFile] = []
    /// Whether the song's own sound goes under the MP4.
    @Published var useSong = false { didSet { source.defaults.set(useSong, forKey: Keys.useSong); optionChanged() } }
    /// How big the lyrics are on the card, 50 to 110% of the room they have.
    @Published var textScale: Double {
        didSet { source.defaults.set(textScale, forKey: Keys.textScale); optionChanged() }
    }
    /// How loud the song is in the MP4: a slider position from 0 to 1.5, shown in dB.
    @Published var soundVolume = 1.0
    /// How loud the background video's own sound is.
    @Published var backgroundVolume = 1.0
    /// Lowers the song while the background video's own sound plays.
    @Published var duckSong = false
    /// What decides the MP4's length, when the background is a video over two seconds.
    @Published var clipLength: ClipLength = .matchBackground { didSet { optionChanged() } }
    /// Keeps the MP4 to 60 seconds.
    @Published var capsLength = false { didSet { optionChanged() } }
    /// The MP4's length for the current choices.
    @Published var clipPlan: LyricCard.ClipPlan?
    /// The song's own sound for the selected lines, once fetched or recorded.
    @Published var songClip: SongAudioClip? {
        // A replaced recording is deleted (the song's own file never is).
        didSet { if let oldValue, oldValue.url != songClip?.url { SongAudioSource.discard(oldValue) } }
    }
    /// The stretch of the song the selected lines need.
    @Published var songRange: ClosedRange<Double>?
    @Published var songStatus = ""
    @Published var songBusy = false
    /// How the recording is getting on, while it runs.
    @Published var recording: SongAudioSource.RecordingUpdate?
    /// Brings the card window back to the front: recording the song or
    /// copying can leave another app in front.
    var onFinished: (() -> Void)?
    private var songTask: Task<Void, Never>?
    var songNeedsFetching: Bool {
        guard let range = songRange else { return true }
        return songClip.map { !$0.covers(range) } ?? true
    }

    private var previewTask: Task<Void, Never>?
    private var exportTask: Task<Void, Never>?
    /// The export running now. Progress from any other (one cancelled, or
    /// finished) is stale and ignored.
    private var exportID: UUID?
    /// The song whose lines were last loaded.
    private(set) var loadedTrackKey: String?
    let source: LyricCardSource

    init(source: LyricCardSource? = nil) {
        let source = source ?? .live
        self.source = source
        let defaults = source.defaults
        aspect = CardAspect(rawValue: defaults.string(forKey: Keys.aspect) ?? "") ?? .portrait
        layout = source.composition()
        let saved = (defaults.stringArray(forKey: Keys.formats) ?? []).compactMap(CardFormat.init(rawValue:))
        formats = saved.isEmpty ? [.mp4, .png] : Set(saved)
        useSong = defaults.bool(forKey: Keys.useSong)
        // The app's own window only: tests share the temporary folder with the real app.
        if source.followsTheSong { DispatchQueue.global(qos: .utility).async { SongAudioSource.discardLeftoverRecordings() } }
        textScale = defaults.object(forKey: Keys.textScale) as? Double ?? 0.85
        if let path = defaults.string(forKey: Keys.background), FileManager.default.fileExists(atPath: path) {
            Task { [weak self] in
                let loaded = await LyricCardBackground.load(URL(fileURLWithPath: path))
                self?.background = loaded
            }
        }
        if source.followsTheSong { followTheSong() }
    }

    func chooseBackground() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = LyricCardBackground.contentTypes
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        panel.message = "Choose a picture, GIF or video to put behind the lyrics"
        panel.prompt = "Use as Background"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        setBackground(url)
    }

    /// Gets the song's own sound for the selected lines: from its file if the
    /// player has a plain one, otherwise by playing those lines once and recording.
    func fetchSong() {
        // Not mid-export: the card being made may be reading the current recording.
        guard !songBusy, progress == nil, let range = songRange else { return }
        songBusy = true
        songStatus = "Looking for the song’s file…"
        songTask = Task { [weak self] in
            defer {
                self?.recording = nil
                self?.songBusy = false
                self?.onFinished?()
            }
            if let file = await SongAudioSource.localFile() {
                self?.songClip = SongAudioClip(url: file, songStart: 0, songEnd: .infinity)
                self?.songStatus = "Using the song’s own file."
                return
            }
            do {
                let clip = try await SongAudioSource.record(range: range) { update in
                    self?.recording = update
                    switch update {
                    case .starting: self?.songStatus = "Starting the song…"
                    case .recording: self?.songStatus = "Recording the song…"
                    }
                }
                self?.songClip = clip
                self?.songStatus = "Recorded the song."
            } catch is CancellationError {
                self?.songStatus = "Cancelled."
            } catch {
                self?.songStatus = error.localizedDescription
            }
        }
    }

    func cancelSong() { songTask?.cancel() }

    func chooseSound() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.audio, .movie]
        panel.allowsMultipleSelection = true
        panel.canChooseDirectories = false
        panel.message = "Choose sounds for the MP4: audio files, or videos whose sound to use"
        panel.prompt = "Add Sounds"
        guard panel.runModal() == .OK else { return }
        panel.urls.forEach(addSound)
    }

    func addSound(_ url: URL) {
        guard !soundFiles.contains(where: { $0.url == url }) else { return }
        soundFiles.append(SoundFile(url: url))
    }

    func removeSound(_ id: UUID) { soundFiles.removeAll { $0.id == id } }

    /// Dropped or chosen files: audio goes under the card, anything else behind it.
    func accept(_ url: URL) {
        if let type = UTType(filenameExtension: url.pathExtension.lowercased()), type.conforms(to: .audio) {
            addSound(url)
        } else {
            setBackground(url)
        }
    }

    func setBackground(_ url: URL) {
        show("Loading background…")
        Task { [weak self] in
            let loaded = await LyricCardBackground.load(url)
            guard let self else { return }
            if let loaded {
                self.background = loaded
                source.defaults.set(url.path, forKey: Keys.background)
                self.show("")
            } else {
                self.show("Couldn’t open that file as a background", tone: .failure)
            }
        }
    }

    func clearBackground() {
        background = nil
        source.defaults.removeObject(forKey: Keys.background)
    }

    var hasSong: Bool { source.hasSong() }

    /// Making a card, or getting the song's sound: what the card is made
    /// from can't change underneath either.
    var isBusy: Bool { progress != nil || songBusy }

    /// The window is opening again. A new song starts afresh; the same song
    /// keeps the selection and the recording, only refreshing lines that
    /// changed meanwhile. Nothing changes while a card or recording is
    /// being made.
    func prepareToShow() {
        guard !isBusy else { return }
        reload(trackChanged: loadedTrackKey != source.trackKey())
    }

    /// Reads the playing song's lyrics. A new track selects the line being
    /// sung and drops the old song's recording; new lyrics for the same
    /// track keep the lines picked where they are still there.
    func reload(trackChanged: Bool = true) {
        if trackChanged {
            exportTask?.cancel()
            exportID = nil
            progress = nil
            show("")
            songTask?.cancel(); songClip = nil; songStatus = ""; songRange = nil
        }
        if layout != source.composition() { layout = source.composition() }
        loadedTrackKey = source.trackKey()
        title = source.title()
        artist = source.artist()
        let fresh = source.rows()
        if !trackChanged {
            guard fresh != rows else { return }
            let kept = Self.carrySelection(selection, from: rows, to: fresh)
            let hadSelection = !selection.isEmpty
            rows = fresh
            if !kept.isEmpty {
                selection = kept
                if hadSelection { show("Lyrics updated, selection kept") }
                return
            }
        }
        rows = fresh
        if let active = source.activeRow(), rows.contains(where: { $0.id == active }) {
            selection = [active]
        } else if let first = rows.first {
            selection = [first.id]
        } else {
            selection = []
        }
    }

    /// The picked lines in `new`, matched by their words: the second of two
    /// identical lines stays the second. Lines no longer there are dropped.
    nonisolated static func carrySelection(_ selection: Set<Int>, from old: [Row], to new: [Row]) -> Set<Int> {
        var occurrence: [String: Int] = [:]
        var wanted: Set<String> = []
        for row in old {
            let seen = occurrence[row.text, default: 0]
            occurrence[row.text] = seen + 1
            if selection.contains(row.id) { wanted.insert("\(seen)|\(row.text)") }
        }
        occurrence = [:]
        var kept: Set<Int> = []
        for row in new {
            let seen = occurrence[row.text, default: 0]
            occurrence[row.text] = seen + 1
            if wanted.contains("\(seen)|\(row.text)") { kept.insert(row.id) }
        }
        return kept
    }

    /// Keeps the lines to pick from in step with the song: a new track, or its
    /// lyrics arriving or changing, refreshes them. Never mid-export.
    var isOpen: () -> Bool = { true }
    private var watchers: [AnyCancellable] = []
    private func followTheSong() {
        let track = NowPlayingMonitor.shared.$current
            .map { "\($0?.title ?? "")|\($0?.artist ?? "")" }
            .removeDuplicates().dropFirst()
        watchers.append(track.debounce(for: .milliseconds(250), scheduler: DispatchQueue.main).sink { [weak self] _ in
            guard let self, self.isOpen(), self.progress == nil else { return }
            self.reload(trackChanged: true)
        })
        let lyrics = LyricsViewModel.shared.$lines.map(\.count).removeDuplicates().dropFirst()
        watchers.append(lyrics.debounce(for: .milliseconds(400), scheduler: DispatchQueue.main).sink { [weak self] _ in
            guard let self, self.isOpen(), self.progress == nil else { return }
            self.reload(trackChanged: false)
        })
    }

    func selectAll() { selection = Set(rows.map(\.id)) }

    private func content() -> LyricCard.Content? {
        guard !selection.isEmpty else { return nil }
        var content = source.content(selection.sorted(), layout, aspect)
        content?.background = background
        if let backgroundPalette { content?.palette = backgroundPalette }
        content?.backgroundStill = background?.first
        content?.keepBackgroundSound = true
        content?.soundVolume = soundVolume
        content?.textScale = textScale
        content?.backgroundVolume = backgroundVolume
        content?.extraSounds = soundFiles.map { LyricCard.SoundTrack(url: $0.url, volume: $0.volume) }
        // A recording that covers only some of the lines leaves the rest silent (the export says so).
        content?.songAudio = useSong ? songClip : nil
        content?.duckSong = duckSong
        content?.clipLength = clipLength
        content?.capsLength = capsLength
        return content
    }

    /// Something the card is made from changed: the last result no longer
    /// describes it, and the preview is redrawn.
    private func optionChanged() {
        clearResult()
        schedulePreview()
    }

    /// Clears the last card's result (never a card being made).
    private func clearResult() {
        guard progress == nil, statusTone != .plain || !warnings.isEmpty || !madeFiles.isEmpty else { return }
        show("")
    }

    /// Sets the status line, dropping the last card's details.
    func show(_ text: String, tone: StatusTone = .plain) {
        status = text
        statusTone = tone
        warnings = []
        madeFiles = []
    }

    private var staleTask: Task<Void, Never>?

    private func schedulePreview() {
        previewTask?.cancel()
        // A preview still not redrawn after a moment dims, with a spinner.
        if staleTask == nil {
            staleTask = Task { [weak self] in
                try? await Task.sleep(nanoseconds: 250_000_000)
                guard !Task.isCancelled else { return }
                self?.previewStale = true
            }
        }
        previewTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 120_000_000)
            // Superseded by a newer change: that one draws the preview. Clearing
            // it here would flash the empty placeholder on every slider tick.
            guard !Task.isCancelled, let self else { return }
            defer { self.previewDrawn() }
            guard var content = self.content() else { self.preview = nil; return }
            self.songRange = LyricCard.songRange(content)
            self.clipPlan = LyricCard.clipPlan(content)
            content.pages = [content.stillPage]
            self.preview = LyricCard.image(content, scale: 0.4)
        }
    }

    private func previewDrawn() {
        staleTask?.cancel()
        staleTask = nil
        previewStale = false
        previewGeneration += 1
    }

    var canMake: Bool { !selection.isEmpty && !formats.isEmpty && !isBusy }

    func toggle(_ format: CardFormat) {
        if formats.contains(format) { formats.remove(format) } else { formats.insert(format) }
    }

    func copy() { make(into: nil) }

    func save() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.prompt = "Save Here"
        panel.message = "Choose where to save the lyric card"
        guard panel.runModal() == .OK, let folder = panel.url else { return }
        make(into: folder)
    }

    private func make(into folder: URL?) {
        guard let content = content(), canMake else { return }
        let chosen = formats
        let id = UUID()
        exportID = id
        progress = 0
        show("Making your card…")
        let directory = folder ?? LyricCard.exportDirectory
        let export = source.export
        exportTask = Task { [weak self] in
            var warnings: [String] = []
            let files = await export(content, chosen, directory, { warnings.append($0) },
                                     { self?.reportPhase($0, for: id) }) { value in
                self?.reportProgress(value, for: id)
            }
            guard !Task.isCancelled, let self, self.exportID == id else {
                // Cancelled: nothing half-made is left behind (an MP4 may have lost its sound).
                LyricCard.discardExport(files, in: directory, ownsDirectory: folder == nil)
                return
            }
            self.exportID = nil
            self.progress = nil
            if files.isEmpty, folder == nil { try? FileManager.default.removeItem(at: directory) }
            defer {
                self.warnings = warnings
                if folder == nil { self.onFinished?() }
            }
            guard !files.isEmpty else { self.show("Couldn’t make the card", tone: .failure); return }
            let names = files.map { $0.0.title }.joined(separator: ", ")
            if folder == nil {
                if LyricCard.publish(files, to: self.source.pasteboard) {
                    LyricCard.pruneExpiredExports(in: directory.deletingLastPathComponent(), keeping: directory)
                    self.succeeded("Copied \(names)", files: files.map(\.1))
                } else {
                    self.show("Couldn’t copy the card", tone: .failure)
                }
            } else {
                self.succeeded("Saved \(names)", files: files.map(\.1))
                NSWorkspace.shared.activateFileViewerSelecting(files.map(\.1))
            }
        }
    }

    /// How far the export `id` has got. Only the running export moves the
    /// bar, and only forward: a report arriving after Cancel or the finish
    /// would otherwise bring back a bar with nothing behind it.
    func reportProgress(_ value: Double, for id: UUID) {
        guard id == exportID, let current = progress, value.isFinite else { return }
        progress = max(current, min(1, value))
    }

    private func succeeded(_ text: String, files: [URL]) {
        show(text, tone: .success)
        madeFiles = files
        cardsMade += 1
    }

    func showMadeFiles() {
        guard !madeFiles.isEmpty else { return }
        NSWorkspace.shared.activateFileViewerSelecting(madeFiles)
    }

    /// What the export `id` is doing now, shown under the bar.
    func reportPhase(_ phase: LyricCard.ExportPhase, for id: UUID) {
        guard id == exportID, progress != nil else { return }
        status = phase.title
    }

    func cancel() {
        exportTask?.cancel()
        exportID = nil
        progress = nil
        show("Cancelled")
    }

    /// Waits for the export in progress, if any (tests).
    func waitForExport() async { await exportTask?.value }
}

struct LyricCardPopoutView: View {
    private typealias Motion = LunalyrDesign.Motion

    private func levelRow(_ title: String, value: Binding<Double>, range: ClosedRange<Double> = 0...1.5) -> some View {
        HStack(spacing: 8) {
            Text(title).frame(width: 110, alignment: .leading)
            Slider(value: value, in: range)
            Text("\(Int((value.wrappedValue * 100).rounded()))%")
                .monospacedDigit().foregroundStyle(.secondary).frame(width: 44, alignment: .trailing)
        }
        .controlSize(.small)
    }

    /// A sound's level: the slider runs 0 to 150% and shows the gain in dB
    /// (0 dB is level with the other sounds; 150% is +12 dB).
    private func soundLevelRow<Title: View>(value: Binding<Double>, @ViewBuilder title: () -> Title) -> some View {
        HStack(spacing: 8) {
            title().frame(width: 110, alignment: .leading)
            Slider(value: value, in: 0...LyricCardMix.maximumLevel)
            Text(LyricCardMix.label(forLevel: value.wrappedValue))
                .monospacedDigit().foregroundStyle(.secondary).frame(width: 52, alignment: .trailing)
                .help("\(Int((value.wrappedValue * 100).rounded()))%")
        }
        .controlSize(.small)
    }

    @ObservedObject var model: LyricCardPopoutModel
    @ObservedObject private var style = SettingsStyleStore.shared
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var dropTargeted = false
    @State private var showsDetails = false

    private func animation(_ animation: Animation) -> Animation {
        Motion.animation(animation, reduceMotion: reduceMotion)
    }

    /// The Booleans that show or hide option rows: their rows fade in and out
    /// when one of these changes, and only then (never with the preview).
    private struct Rows: Equatable {
        var useSong: Bool
        var clipChoice: Bool
        var hasPlan: Bool
        var backgroundSound: Bool
        var sounds: Int
        var background: Bool
        var animatedBackground: Bool
        var stillLimit: Bool
    }

    private var rowKey: Rows {
        Rows(useSong: model.useSong, clipChoice: model.clipPlan?.offersChoice == true, hasPlan: model.clipPlan != nil,
             backgroundSound: model.background?.hasAudio == true, sounds: model.soundFiles.count,
             background: model.background != nil, animatedBackground: model.background?.isAnimated == true,
             stillLimit: model.formats.contains(.png) && model.selection.count > LyricCard.Content.stillLineLimit)
    }

    private var reveal: AnyTransition { Motion.reveal(reduceMotion: reduceMotion) }

    var body: some View {
        HStack(spacing: 0) {
            lyricsList
                .frame(minWidth: 300, idealWidth: 340)
            Divider()
            options
                .frame(width: 330)
        }
        .frame(minHeight: 600)
        .styledCanvas()
        .lunalyrTint()
    }

    private var lyricsList: some View {
        VStack(alignment: .leading, spacing: 8) {
            VStack(alignment: .leading, spacing: 2) {
                Text(model.title.isEmpty ? "Nothing playing" : model.title).font(.headline).lineLimit(1)
                if !model.artist.isEmpty { Text(model.artist).foregroundStyle(.secondary).lineLimit(1) }
            }
            .padding([.horizontal, .top], 16)
            if model.rows.isEmpty {
                Text(model.hasSong ? "No lyrics found for this song." : "Play a song to make a lyric card.")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                Group {
                    List(model.rows, selection: $model.selection) { row in
                        Text(row.text).lineLimit(2).tag(row.id)
                    }
                    .scrollContentBackground(.hidden)
                    HStack {
                        Text("\(model.selection.count) of \(model.rows.count) lines")
                            .lunalyrText(.caption)
                        Spacer()
                        Button("Whole song") { model.selectAll() }.controlSize(.small)
                    }
                    .padding([.horizontal, .bottom], 16)
                }
                // The lines can't change under a card or recording being made.
                .disabled(model.isBusy)
                .opacity(model.isBusy ? 0.55 : 1)
                .animation(animation(Motion.quick), value: model.isBusy)
            }
        }
    }

    private var options: some View {
        VStack(alignment: .leading, spacing: 12) {
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    previewTile
                    settings
                }
                .padding(.trailing, 6)
                .animation(animation(Motion.standard), value: rowKey)
            }
            .scrollIndicators(.automatic)
            footer
            HStack {
                Button("Save…") { model.save() }.disabled(!model.canMake)
                Spacer()
                Button("Copy") { model.copy() }
                    .buttonStyle(.lunalyrProminent)
                    .keyboardShortcut(.defaultAction)
                    .disabled(!model.canMake)
            }
        }
        .padding(16)
    }

    private var previewTile: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 14, style: .continuous).fill(.quinary)
            if let image = model.preview {
                Image(nsImage: image).resizable().scaledToFit().clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
                    .padding(8)
                    .opacity(model.previewStale ? 0.6 : 1)
                    .id(model.previewGeneration)
                    .transition(.opacity)
                    .accessibilityLabel("Preview of the card")
            } else {
                Text("Pick lines to preview").lunalyrText(.caption)
                    .transition(.opacity)
            }
            if model.previewStale, model.preview != nil {
                ProgressView().controlSize(.small)
                    .transition(.opacity)
                    .accessibilityLabel("Updating the preview")
            }
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .strokeBorder(Color.lunalyrAccent, lineWidth: 2)
                .opacity(dropTargeted ? 1 : 0)
                .allowsHitTesting(false)
        }
        .frame(height: 210)
        .animation(animation(Motion.quick), value: model.previewGeneration)
        .animation(animation(Motion.quick), value: model.previewStale)
        .animation(animation(Motion.quick), value: dropTargeted)
        .onDrop(of: [.fileURL], isTargeted: $dropTargeted) { providers in
            guard !model.isBusy, let provider = providers.first else { return false }
            _ = provider.loadObject(ofClass: URL.self) { url, _ in
                guard let url else { return }
                Task { @MainActor in model.accept(url) }
            }
            return true
        }
    }

    @ViewBuilder private var settings: some View {
        let locked = model.isBusy
        Group {
            ChoiceMenu("Layout", selection: $model.layout, selectedTitle: { $0?.title ?? "Follow wallpaper" }) {
                Text("Follow wallpaper").tag(Optional<LyricComposition>.none)
                LyricCompositionOptions()
            }

            Text("Background").lunalyrText(.groupTitle)
            HStack(spacing: 8) {
                Text(model.background?.name ?? "Album colours")
                    .lineLimit(1).truncationMode(.middle)
                    .foregroundStyle(model.background == nil ? .secondary : .primary)
                Spacer(minLength: 0)
                if model.background != nil {
                    Button("Remove") { model.clearBackground() }.controlSize(.small).transition(.opacity)
                }
                Button("Choose…") { model.chooseBackground() }.controlSize(.small)
            }
            Text("Clip length (MP4)").lunalyrText(.groupTitle)
            if model.clipPlan?.offersChoice == true {
                Picker("Clip length", selection: $model.clipLength) {
                    ForEach(ClipLength.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented).labelsHidden().controlSize(.small)
                .transition(reveal)
            }
            Toggle("Max 60 s", isOn: $model.capsLength).toggleStyle(.checkbox)
            if let plan = model.clipPlan {
                Text(plan.summary).lunalyrText(.caption).transition(reveal)
            }
            Text("Sound (MP4 only)").lunalyrText(.groupTitle)
            Text("Mix as many as you like: the song, a background video’s sound, and audio files.").lunalyrText(.caption)
            Toggle("The song", isOn: $model.useSong).toggleStyle(.checkbox)
        }
        .disabled(locked)
        if model.useSong {
            songRow.transition(reveal)
            Group {
                soundLevelRow(value: $model.soundVolume) { Text("Song level") }
                if model.background?.hasAudio == true {
                    Toggle("Duck the song under the video’s sound", isOn: $model.duckSong).toggleStyle(.checkbox)
                }
                Text("Uses the song’s own file when it has one. Otherwise it plays the selected lines once and records them, which takes as long as they last. You’ll hear it, and macOS may ask to allow audio recording.")
                    .lunalyrText(.caption)
            }
            .disabled(locked)
            .transition(reveal)
        }
        Group {
            if model.background?.hasAudio == true {
                soundLevelRow(value: $model.backgroundVolume) { Text("Background video") }.transition(reveal)
            }
            ForEach($model.soundFiles) { $file in
                HStack(spacing: 8) {
                    soundLevelRow(value: $file.volume) {
                        Text(file.url.lastPathComponent).lineLimit(1).truncationMode(.middle)
                    }
                    Button { model.removeSound(file.id) } label: { Image(systemName: "minus.circle") }
                        .buttonStyle(.borderless).accessibilityLabel("Remove \(file.url.lastPathComponent)")
                }
                .controlSize(.small)
                .transition(reveal)
            }
            HStack {
                Button("Add audio or video sound…") { model.chooseSound() }.controlSize(.small)
                Spacer(minLength: 0)
            }
            Text("You can also drop audio files on the preview.").lunalyrText(.caption)
            Text("A GIF can’t carry sound, so make an MP4 when you want it.").lunalyrText(.caption)
            if let background = model.background, background.isAnimated {
                Text("Plays behind the lyrics and loops. PNG uses its first frame.").lunalyrText(.caption).transition(reveal)
            } else if model.background == nil {
                Text("Pick or drop a picture, GIF or video.").lunalyrText(.caption).transition(reveal)
            }

            Text("Text size").lunalyrText(.groupTitle)
            levelRow("Lyrics", value: $model.textScale, range: 0.5...1.1)
            Text("Applies to every layout.").lunalyrText(.caption)

            Text("Shape").lunalyrText(.groupTitle)
            HStack(spacing: 8) {
                ForEach(CardAspect.allCases) { aspect in
                    let on = model.aspect == aspect
                    Button { model.aspect = aspect } label: {
                        VStack(spacing: 1) {
                            Text(aspect.title).font(.body.weight(.semibold))
                            Text(aspect.detail).font(.caption)
                        }
                        .foregroundStyle(on ? Color.white : Color.primary)
                        .frame(maxWidth: .infinity, minHeight: 44)
                    }
                    .buttonStyle(CardTileStyle(selected: on))
                    .accessibilityAddTraits(on ? .isSelected : [])
                }
            }
            .animation(animation(Motion.quick), value: model.aspect)

            Text("Make").lunalyrText(.groupTitle)
            HStack(spacing: 8) {
                ForEach(CardFormat.allCases) { format in
                    let on = model.formats.contains(format)
                    Button { model.toggle(format) } label: {
                        VStack(spacing: 1) {
                            Text(format.title).font(.body.weight(.semibold))
                            Text(format.detail).font(.caption).multilineTextAlignment(.center)
                        }
                        .foregroundStyle(on ? Color.white : Color.primary)
                        .frame(maxWidth: .infinity, minHeight: 52)
                    }
                    .buttonStyle(CardTileStyle(selected: on))
                    .accessibilityAddTraits(on ? .isSelected : [])
                }
            }
            .animation(animation(Motion.quick), value: model.formats)

            if model.formats.contains(.png) && model.selection.count > LyricCard.Content.stillLineLimit {
                Text("The PNG shows the first \(LyricCard.Content.stillLineLimit) lines. MP4 and GIF play them all.")
                    .lunalyrText(.caption)
                    .transition(reveal)
            }
        }
        .disabled(locked)
    }

    /// Getting the song's sound: a spinner while the song starts, then a bar
    /// and a countdown while it records. Cancel stays usable while the rest
    /// of the window is locked.
    private var songRow: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 8) {
                if model.songBusy, model.recording == nil || model.recording == .starting {
                    ProgressView().controlSize(.small).transition(.opacity)
                }
                Text(model.songStatus.isEmpty
                     ? (model.songNeedsFetching ? "Not fetched for these lines yet" : "Ready for these lines")
                     : model.songStatus)
                    .lineLimit(2).foregroundStyle(.secondary)
                Spacer(minLength: 0)
                Group {
                    if model.songBusy {
                        Button("Cancel") { model.cancelSong() }
                    } else {
                        Button(model.songNeedsFetching ? "Get the song’s sound" : "Redo") { model.fetchSong() }
                            .disabled(model.songRange == nil || model.progress != nil)
                    }
                }
                .controlSize(.small)
                .frame(minWidth: 132, alignment: .trailing)
            }
            if case .recording(let elapsed, let needed, let left) = model.recording {
                HStack(spacing: 8) {
                    ProgressView(value: elapsed, total: needed)
                    Text("\(left) s left")
                        .monospacedDigit()
                        .contentTransition(.numericText(countsDown: true))
                        .foregroundStyle(.secondary)
                        .frame(minWidth: 52, alignment: .trailing)
                }
                .controlSize(.small)
                .animation(animation(Motion.quick), value: left)
                .transition(.opacity)
                .accessibilityElement(children: .combine)
                .accessibilityLabel("Recording, \(left) seconds left")
            }
        }
        .animation(animation(Motion.quick), value: model.recording == nil)
    }

    /// One fixed-height slot under the options: the bar with Cancel while a
    /// card is made, otherwise the last result. Nothing below it moves.
    private var footer: some View {
        ZStack(alignment: .leading) {
            if let progress = model.progress {
                HStack(spacing: 10) {
                    VStack(alignment: .leading, spacing: 4) {
                        HStack {
                            Text(model.status).lunalyrText(.caption).lineLimit(1)
                            Spacer(minLength: 4)
                            Text("\(Int((progress * 100).rounded(.down)))%")
                                .lunalyrText(.caption)
                                .monospacedDigit()
                                .contentTransition(.numericText(value: progress))
                        }
                        ProgressView(value: progress)
                            .accessibilityLabel(model.status)
                    }
                    Button("Cancel") { model.cancel() }
                        .controlSize(.small)
                        .keyboardShortcut(.cancelAction)
                }
                .transition(.opacity)
            } else if !model.status.isEmpty {
                resultLine.transition(.opacity)
            }
        }
        .frame(height: 34)
        .animation(animation(Motion.quick), value: model.progress == nil)
        .animation(animation(Motion.quick), value: model.status)
    }

    private var resultLine: some View {
        HStack(alignment: .center, spacing: 6) {
            switch model.statusTone {
            case .success:
                Image(systemName: LunalyrStatus.success.symbol)
                    .foregroundStyle(LunalyrStatus.success.text)
                    .symbolEffect(.bounce, options: .nonRepeating, value: reduceMotion ? 0 : model.cardsMade)
                    .accessibilityHidden(true)
            case .failure:
                Image(systemName: LunalyrStatus.warning.symbol)
                    .foregroundStyle(LunalyrStatus.warning.text)
                    .accessibilityHidden(true)
            case .plain:
                EmptyView()
            }
            Text(model.status)
                .lunalyrText(.caption)
                .foregroundStyle(model.statusTone == .plain ? Color.secondary : Color.primary)
                .lineLimit(2)
            Spacer(minLength: 4)
            if !model.warnings.isEmpty {
                Button("Details") { showsDetails = true }
                    .controlSize(.small)
                    .popover(isPresented: $showsDetails, arrowEdge: .top) {
                        VStack(alignment: .leading, spacing: 6) {
                            ForEach(model.warnings, id: \.self) { Text($0).fixedSize(horizontal: false, vertical: true) }
                        }
                        .font(.callout)
                        .padding(12)
                        .frame(width: 280, alignment: .leading)
                    }
            }
            if !model.madeFiles.isEmpty {
                Button("Show in Finder") { model.showMadeFiles() }.controlSize(.small)
            }
        }
        .accessibilityElement(children: .contain)
    }
}

/// A Shape or Make tile: lights up on hover, darkens when pressed, and
/// answers a click at once.
private struct CardTileStyle: ButtonStyle {
    let selected: Bool

    func makeBody(configuration: Configuration) -> some View {
        CardTile(configuration: configuration, selected: selected)
    }

    private struct CardTile: View {
        let configuration: ButtonStyleConfiguration
        let selected: Bool
        @Environment(\.isEnabled) private var enabled
        @Environment(\.accessibilityReduceMotion) private var reduceMotion
        @State private var hovering = false

        var body: some View {
            let state: ThemeState = !enabled ? .disabled : configuration.isPressed ? .pressed : hovering ? .hover : .normal
            configuration.label
                .themedControl(radius: 10, selected: selected, state: state)
                .contentShape(Rectangle())
                .onHover { hovering = $0 }
                .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion), value: hovering)
        }
    }
}

@MainActor
final class LyricCardPopoutController {
    static let shared = LyricCardPopoutController()

    private let model = LyricCardPopoutModel()
    private var window: NSWindow?

    init() { model.isOpen = { [weak self] in self?.window?.isVisible == true } }

    func show() {
        // Already open: just bring it forward, keeping the lines picked, the
        // song's recording and any card being made.
        if let window, window.isVisible {
            NSApp.activate(ignoringOtherApps: true)
            window.makeKeyAndOrderFront(nil)
            return
        }
        model.prepareToShow()
        if window == nil {
            let host = NSHostingController(rootView: LyricCardPopoutView(model: model))
            let window = NSWindow(contentViewController: host)
            window.title = "Lyric Card"
            window.styleMask = [.titled, .closable, .resizable]
            window.setContentSize(NSSize(width: 700, height: 680))
            window.isReleasedWhenClosed = false
            window.center()
            self.window = window
            model.onFinished = { [weak self] in
                NSApp.activate(ignoringOtherApps: true)
                self?.window?.makeKeyAndOrderFront(nil)
            }
        }
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }
}
