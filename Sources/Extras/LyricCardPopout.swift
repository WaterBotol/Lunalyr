import AppKit
import SwiftUI

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
    }

    @Published var rows: [Row] = []
    @Published var selection: Set<Int> = [] { didSet { schedulePreview() } }
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
    @Published var background: LyricCardBackground? { didSet { schedulePreview() } }

    private var previewTask: Task<Void, Never>?
    private var exportTask: Task<Void, Never>?

    init() {
        let defaults = UserDefaults.standard
        aspect = CardAspect(rawValue: defaults.string(forKey: Keys.aspect) ?? "") ?? .portrait
        let saved = (defaults.stringArray(forKey: Keys.formats) ?? []).compactMap(CardFormat.init(rawValue:))
        formats = saved.isEmpty ? [.mp4, .png] : Set(saved)
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
        var content = LyricCard.currentContent(before: 0, after: 0, composition: LunalyrExtras.shared.lyricCardComposition,
                                               rows: selection.sorted(), aspect: aspect)
        content?.background = background
        content?.backgroundStill = background?.first
        return content
    }

    private func schedulePreview() {
        previewTask?.cancel()
        previewTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 120_000_000)
            guard let self, !Task.isCancelled, var content = self.content() else { self?.preview = nil; return }
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
            let files = await LyricCard.export(content, formats: chosen, to: directory) { value in
                Task { @MainActor in self?.progress = value }
            }
            guard let self, !Task.isCancelled else { return }
            self.progress = nil
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
                    Task { @MainActor in model.setBackground(url) }
                }
                return true
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
            if let background = model.background, background.isAnimated {
                Text("Plays behind the lyrics and loops. PNG uses its first frame.").lunalyrText(.caption)
            } else if model.background == nil {
                Text("Pick or drop a picture, GIF or video.").lunalyrText(.caption)
            }

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
            Spacer(minLength: 0)
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
        }
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }
}
