import AppKit
import SwiftUI
import UniformTypeIdentifiers

// MARK: - The lyric card window
//
// Copy Lyric Card (the menu-bar tile, the shortcut and Settings) opens this
// window for the song that is playing: its whole lyrics to pick lines from
// (click, shift-click for a range, ⌘-click to add), the card's shape, and
// which of MP4, GIF and PNG to make. Copy puts the chosen formats on the
// clipboard as attachments; Save… writes them to a folder.

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
        static let sound = "custom.lyricCardBackgroundSound"
        static let textScale = "custom.lyricCardTextScale"
    }

    @Published var rows: [Row] = []
    @Published var selection: Set<Int> = [] { didSet { schedulePreview() } }
    /// The card's layout; nil follows the wallpaper. Shared with Settings → Share → Card layout.
    @Published var layout: LyricComposition? {
        didSet { LunalyrExtras.shared.lyricCardComposition = layout; schedulePreview() }
    }
    @Published var aspect: CardAspect {
        didSet { UserDefaults.standard.set(aspect.rawValue, forKey: Keys.aspect); schedulePreview() }
    }
    @Published var formats: Set<CardFormat> {
        didSet { UserDefaults.standard.set(formats.map(\.rawValue).sorted(), forKey: Keys.formats) }
    }
    @Published var preview: NSImage?
    @Published var progress: Double?
    @Published var status = ""
    @Published var title = ""
    @Published var artist = ""
    /// A picture, GIF or video behind the lyrics; nil keeps the album look.
    @Published var background: LyricCardBackground? {
        didSet {
            // The lyrics' colours and transitions follow the background's own colours, not the album's.
            backgroundPalette = background.map { LyricCard.palette(for: NSImage(cgImage: $0.first, size: .zero)) }
            schedulePreview()
        }
    }
    private var backgroundPalette: AlbumWallpaperPalette?
    /// Sound chosen to go under the MP4 (any audio file, or a video's own sound).
    @Published var soundFile: URL?
    enum SoundChoice: String, CaseIterable, Identifiable {
        case none, song, video, file
        var id: String { rawValue }
        var title: String {
            switch self {
            case .none: return "None"
            case .song: return "Song"
            case .video: return "Video"
            case .file: return "File"
            }
        }
    }
    @Published var soundChoice: SoundChoice {
        didSet {
            UserDefaults.standard.set(soundChoice.rawValue, forKey: Keys.sound)
            // The video's own sound is the sound with Video; under a song or file it starts off.
            if soundChoice != oldValue { backgroundVolume = soundChoice == .video ? 1 : 0 }
        }
    }
    /// How big the lyrics are on the card, 50 to 110% of the room they have.
    @Published var textScale: Double {
        didSet { UserDefaults.standard.set(textScale, forKey: Keys.textScale); schedulePreview() }
    }
    /// How loud the song or chosen file is in the MP4 (0 to 1.5).
    @Published var soundVolume = 1.0
    /// How loud the background video's own sound is.
    @Published var backgroundVolume = 1.0
    /// The song's own sound for the selected lines, once fetched or recorded.
    @Published var songClip: SongAudioClip?
    /// The stretch of the song the selected lines need.
    @Published var songRange: ClosedRange<Double>?
    @Published var songStatus = ""
    @Published var songBusy = false
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

    init() {
        let defaults = UserDefaults.standard
        aspect = CardAspect(rawValue: defaults.string(forKey: Keys.aspect) ?? "") ?? .portrait
        layout = LunalyrExtras.shared.lyricCardComposition
        let saved = (defaults.stringArray(forKey: Keys.formats) ?? []).compactMap(CardFormat.init(rawValue:))
        formats = saved.isEmpty ? [.mp4, .png] : Set(saved)
        soundChoice = SoundChoice(rawValue: defaults.string(forKey: Keys.sound) ?? "") ?? .video
        textScale = defaults.object(forKey: Keys.textScale) as? Double ?? 0.85
        if let path = defaults.string(forKey: Keys.background), FileManager.default.fileExists(atPath: path) {
            Task { [weak self] in
                let loaded = await LyricCardBackground.load(URL(fileURLWithPath: path))
                self?.background = loaded
            }
        }
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
        guard !songBusy, let range = songRange else { return }
        songBusy = true
        songStatus = "Looking for the song’s file…"
        songTask = Task { [weak self] in
            defer { Task { @MainActor in self?.songBusy = false; self?.onFinished?() } }
            if let file = await SongAudioSource.localFile() {
                self?.songClip = SongAudioClip(url: file, songStart: 0, songEnd: .infinity)
                self?.songStatus = "Using the song’s own file."
                return
            }
            do {
                let clip = try await SongAudioSource.record(range: range) { text in
                    Task { @MainActor in self?.songStatus = text }
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
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        panel.message = "Choose a sound for the MP4: an audio file, or a video whose sound to use"
        panel.prompt = "Use This Sound"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        soundFile = url
        soundChoice = .file
    }

    /// Dropped or chosen files: audio goes under the card, anything else behind it.
    func accept(_ url: URL) {
        if let type = UTType(filenameExtension: url.pathExtension.lowercased()), type.conforms(to: .audio) {
            soundFile = url
            soundChoice = .file
        } else {
            setBackground(url)
        }
    }

    func setBackground(_ url: URL) {
        status = "Loading background…"
        Task { [weak self] in
            let loaded = await LyricCardBackground.load(url)
            guard let self else { return }
            if let loaded {
                self.background = loaded
                UserDefaults.standard.set(url.path, forKey: Keys.background)
                self.status = ""
            } else {
                self.status = "Couldn’t open that file as a background"
            }
        }
    }

    func clearBackground() {
        background = nil
        UserDefaults.standard.removeObject(forKey: Keys.background)
    }

    var hasSong: Bool { NowPlayingMonitor.shared.current != nil }

    /// Reads the playing song's lyrics and selects the line being sung.
    func reload() {
        exportTask?.cancel()
        progress = nil
        status = ""
        if layout != LunalyrExtras.shared.lyricCardComposition { layout = LunalyrExtras.shared.lyricCardComposition }
        let playing = NowPlayingMonitor.shared
        title = playing.current?.title ?? ""
        artist = playing.current?.artist ?? ""
        let model = LyricsViewModel.shared
        if model.lines.isEmpty {
            // The same filtered list `LyricCard.currentContent` indexes.
            rows = model.plainLines.filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
                .enumerated().map { Row(id: $0.offset, text: $0.element) }
        } else {
            rows = model.lines.indices
                .filter { !model.lines[$0].isInstrumental && !model.lines[$0].text.isEmpty }
                .map { Row(id: $0, text: model.lines[$0].text) }
        }
        let time = playing.playbackTime() + Settings.shared.lyricOffset + SongOffsets.shared.offset(for: model.currentQuery)
        let active = model.activeLineIndex(at: time)
        if let active, rows.contains(where: { $0.id == active }) {
            selection = [active]
        } else if let first = rows.first {
            selection = [first.id]
        } else {
            selection = []
        }
    }

    func selectAll() { selection = Set(rows.map(\.id)) }

    private func content() -> LyricCard.Content? {
        guard !selection.isEmpty else { return nil }
        var content = LyricCard.currentContent(before: 0, after: 0, composition: layout,
                                               rows: selection.sorted(), aspect: aspect)
        content?.background = background
        if let backgroundPalette { content?.palette = backgroundPalette }
        content?.backgroundStill = background?.first
        content?.keepBackgroundSound = soundChoice != .none
        content?.soundVolume = soundVolume
        content?.textScale = textScale
        content?.backgroundVolume = backgroundVolume
        content?.soundFile = soundChoice == .file ? soundFile : nil
        content?.songAudio = soundChoice == .song && !songNeedsFetching ? songClip : nil
        return content
    }

    private func schedulePreview() {
        previewTask?.cancel()
        previewTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 120_000_000)
            guard let self, !Task.isCancelled, var content = self.content() else { self?.preview = nil; return }
            self.songRange = LyricCard.songRange(content)
            content.pages = [content.stillPage]
            self.preview = LyricCard.image(content, scale: 0.4)
        }
    }

    var canMake: Bool { !selection.isEmpty && !formats.isEmpty && progress == nil }

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
        progress = 0
        status = "Making your card…"
        let directory = folder ?? LyricCard.exportDirectory
        exportTask = Task { [weak self] in
            var warnings: [String] = []
            let files = await LyricCard.export(content, formats: chosen, to: directory, notes: { warnings.append($0) }) { value in
                Task { @MainActor in self?.progress = value }
            }
            guard let self, !Task.isCancelled else { return }
            self.progress = nil
            defer {
                if !warnings.isEmpty { self.status += "\n" + warnings.joined(separator: "\n") }
                if folder == nil { self.onFinished?() }
            }
            guard !files.isEmpty else { self.status = "Couldn’t make the card"; return }
            let names = files.map { $0.0.title }.joined(separator: ", ")
            if folder == nil {
                if LyricCard.publish(files, to: .general) {
                    LyricCard.pruneExpiredExports(in: directory.deletingLastPathComponent(), keeping: directory)
                    self.status = "Copied \(names)"
                } else {
                    self.status = "Couldn’t copy the card"
                }
            } else {
                self.status = "Saved \(names)"
                NSWorkspace.shared.activateFileViewerSelecting(files.map(\.1))
            }
        }
    }

    func cancel() {
        exportTask?.cancel()
        progress = nil
        status = "Cancelled"
    }
}

struct LyricCardPopoutView: View {
    private func levelRow(_ title: String, value: Binding<Double>, range: ClosedRange<Double> = 0...1.5) -> some View {
        HStack(spacing: 8) {
            Text(title).frame(width: 110, alignment: .leading)
            Slider(value: value, in: range)
            Text("\(Int((value.wrappedValue * 100).rounded()))%")
                .monospacedDigit().foregroundStyle(.secondary).frame(width: 44, alignment: .trailing)
        }
        .controlSize(.small)
    }

    @ObservedObject var model: LyricCardPopoutModel
    @ObservedObject private var style = SettingsStyleStore.shared

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
        }
    }

    private var options: some View {
        VStack(alignment: .leading, spacing: 12) {
            ScrollView {
            VStack(alignment: .leading, spacing: 14) {
            ZStack {
                RoundedRectangle(cornerRadius: 14, style: .continuous).fill(.quinary)
                if let image = model.preview {
                    Image(nsImage: image).resizable().scaledToFit().clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
                        .padding(8)
                        .accessibilityLabel("Preview of the card")
                } else {
                    Text("Pick lines to preview").lunalyrText(.caption)
                }
            }
            .frame(height: 210)
            .onDrop(of: [.fileURL], isTargeted: nil) { providers in
                guard let provider = providers.first else { return false }
                _ = provider.loadObject(ofClass: URL.self) { url, _ in
                    guard let url else { return }
                    Task { @MainActor in model.accept(url) }
                }
                return true
            }

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
                if model.background != nil { Button("Remove") { model.clearBackground() }.controlSize(.small) }
                Button("Choose…") { model.chooseBackground() }.controlSize(.small)
            }
            Text("Sound (MP4 only)").lunalyrText(.groupTitle)
            Picker("", selection: $model.soundChoice) {
                ForEach(LyricCardPopoutModel.SoundChoice.allCases) { Text($0.title).tag($0) }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            switch model.soundChoice {
            case .none:
                Text("The MP4 will be silent.").lunalyrText(.caption)
            case .song:
                HStack(spacing: 8) {
                    Text(model.songStatus.isEmpty
                         ? (model.songNeedsFetching ? "Not fetched for these lines yet" : "Ready for these lines")
                         : model.songStatus)
                        .lineLimit(2).foregroundStyle(.secondary)
                    Spacer(minLength: 0)
                    if model.songBusy {
                        Button("Cancel") { model.cancelSong() }.controlSize(.small)
                    } else {
                        Button(model.songNeedsFetching ? "Get the song’s sound" : "Redo") { model.fetchSong() }
                            .controlSize(.small).disabled(model.songRange == nil)
                    }
                }
                Text("Uses the song’s own file when it has one. Otherwise it plays the selected lines once and records them, which takes as long as they last. You’ll hear it, and macOS may ask to allow audio recording.")
                    .lunalyrText(.caption)
            case .video:
                Text(model.background?.hasAudio == true ? "The background video’s own sound plays under the card."
                     : "Choose a video with sound as the background.").lunalyrText(.caption)
            case .file:
                HStack(spacing: 8) {
                    Text(model.soundFile?.lastPathComponent ?? "No file chosen")
                        .lineLimit(1).truncationMode(.middle)
                        .foregroundStyle(model.soundFile == nil ? .secondary : .primary)
                    Spacer(minLength: 0)
                    Button("Choose…") { model.chooseSound() }.controlSize(.small)
                }
                Text("Any audio file, or a video to take the sound from. You can drop one on the preview.").lunalyrText(.caption)
            }
            if model.soundChoice == .song || model.soundChoice == .file {
                levelRow("Song level", value: $model.soundVolume)
            }
            if model.soundChoice != .none, model.background?.hasAudio == true {
                levelRow("Background level", value: $model.backgroundVolume)
            }
            Text("A GIF can’t carry sound, so make an MP4 when you want it.").lunalyrText(.caption)
            if let background = model.background, background.isAnimated {
                Text("Plays behind the lyrics and loops. PNG uses its first frame.").lunalyrText(.caption)
            } else if model.background == nil {
                Text("Pick or drop a picture, GIF or video.").lunalyrText(.caption)
            }

            Text("Text size").lunalyrText(.groupTitle)
            levelRow("Lyrics", value: $model.textScale, range: 0.5...1.1)
            Text("Applies to every layout.").lunalyrText(.caption)

            Text("Shape").lunalyrText(.groupTitle)
            HStack(spacing: 8) {
                ForEach(CardAspect.allCases) { aspect in
                    Button { model.aspect = aspect } label: {
                        VStack(spacing: 1) {
                            Text(aspect.title).font(.body.weight(.semibold))
                            Text(aspect.detail).font(.caption)
                        }
                        .foregroundStyle(model.aspect == aspect ? Color.white : Color.primary)
                        .frame(maxWidth: .infinity, minHeight: 44)
                        .styledSurface(radius: 10, selected: model.aspect == aspect)
                    }
                    .buttonStyle(.plain)
                    .accessibilityAddTraits(model.aspect == aspect ? .isSelected : [])
                }
            }

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
                        .styledSurface(radius: 10, selected: on)
                    }
                    .buttonStyle(.plain)
                    .accessibilityAddTraits(on ? .isSelected : [])
                }
            }

            if model.formats.contains(.png) && model.selection.count > LyricCard.Content.stillLineLimit {
                Text("The PNG shows the first \(LyricCard.Content.stillLineLimit) lines. MP4 and GIF play them all.")
                    .lunalyrText(.caption)
            }
            }
            .padding(.trailing, 6)
            }
            .scrollIndicators(.automatic)
            if let progress = model.progress {
                ProgressView(value: progress)
                Button("Cancel") { model.cancel() }
            } else if !model.status.isEmpty {
                Text(model.status).lunalyrText(.caption)
            }
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
}

@MainActor
final class LyricCardPopoutController {
    static let shared = LyricCardPopoutController()

    private let model = LyricCardPopoutModel()
    private var window: NSWindow?

    func show() {
        model.reload()
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
