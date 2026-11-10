import AppKit
import SwiftUI

/// The Search YouTube… window: one per app, brought forward if open.
@MainActor
final class MusicVideoSearchWindowController: NSObject, NSWindowDelegate {
    static let shared = MusicVideoSearchWindowController()

    private lazy var model = MusicVideoSearchModel(source: MusicVideoController.shared)
    private var window: NSWindow?

    static let size = NSSize(width: 600, height: 640)

    /// Opens the picker for the playing song (closing the menu-bar panel
    /// first, so it doesn't sit over the window) and gives it the keyboard.
    func show() {
        LunalyrNavigation.dismissMenuBarPanel()
        model.prepare()
        if window == nil {
            let host = NSHostingController(rootView: MusicVideoSearchView(model: model))
            let window = NSWindow(contentViewController: host)
            window.title = "Search YouTube"
            window.styleMask = [.titled, .closable, .resizable]
            window.setContentSize(Self.size)
            window.contentMinSize = NSSize(width: 460, height: 360)
            window.isReleasedWhenClosed = false
            window.delegate = self
            window.center()
            self.window = window
            model.onClose = { [weak self] in self?.window?.close() }
        }
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }

    func windowWillClose(_ notification: Notification) {
        model.stop()
    }
}

/// The picker: a search field, then YouTube's results as YouTube lays them
/// out. Click a result (or choose it with ↑ ↓ and press Return) to use it
/// for the song. Nothing here plays: thumbnails are still images.
struct MusicVideoSearchView: View {
    @ObservedObject var model: MusicVideoSearchModel
    /// For a rendered picture (tests): plain stand-ins for the text field
    /// and buttons, a stack for the scroll view, no thumbnails loaded.
    var preview = false
    @FocusState private var focus: Focus?
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private enum Focus: Hashable { case field, results }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
                .padding(LunalyrDesign.Spacing.window)
            Divider()
            content
                .frame(maxWidth: .infinity, maxHeight: preview ? nil : .infinity, alignment: .top)
            Divider()
            footer
                .padding(.horizontal, LunalyrDesign.Spacing.window)
                .padding(.vertical, LunalyrDesign.Spacing.medium)
        }
        .frame(minWidth: 460, minHeight: preview ? nil : 360)
        .background(Color(nsColor: .windowBackgroundColor))
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion),
                   value: model.phase)
        .onAppear { if !preview { focus = .field } }
    }

    // MARK: Header

    private var header: some View {
        VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.small) {
            if let label = model.songLabel {
                Text("Pick the video for \(label).")
                    .lunalyrText(.caption)
                    .lineLimit(2)
            }
            HStack(spacing: LunalyrDesign.Spacing.small) {
                searchField
                if preview {
                    StandInButton(title: "Search")
                } else {
                    Button("Search") { model.search() }
                        .disabled(model.songKey == nil || model.query.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            }
            if let notice = model.notice {
                Text(notice)
                    .lunalyrText(.caption)
                    .transition(LunalyrDesign.Motion.reveal(reduceMotion: reduceMotion))
            }
        }
    }

    @ViewBuilder private var searchField: some View {
        if preview {
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                Text(model.query)
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 8)
            .frame(height: 24)
            .background(RoundedRectangle(cornerRadius: 6, style: .continuous).fill(Color(nsColor: .textBackgroundColor)))
            .overlay(RoundedRectangle(cornerRadius: 6, style: .continuous).strokeBorder(.quaternary))
        } else {
            TextField("Search YouTube", text: $model.query)
                .textFieldStyle(.roundedBorder)
                .focused($focus, equals: .field)
                .onSubmit { model.search() }
                .disabled(model.songKey == nil)
                .accessibilityLabel("Search YouTube for")
                .onKeyPress(.downArrow) {
                    guard !model.rows.isEmpty else { return .ignored }
                    if model.selection == nil { model.moveSelection(by: 1) }
                    focus = .results
                    return .handled
                }
        }
    }

    // MARK: Results

    @ViewBuilder private var content: some View {
        switch model.phase {
        case .results:
            results
        case .loading:
            SearchingNote(reduceMotion: reduceMotion)
        case .idle:
            message("Press Return to search.", detail: nil)
        case .empty:
            message("No videos found", detail: "Try other words.")
        case .rateLimited:
            message("Too many searches for now", detail: "Wait a minute, then press Return to try again.")
        case .failed:
            message("Couldn’t search YouTube", detail: "Check your connection, then press Return to try again.")
        case .noSong:
            message("Nothing is playing", detail: "Play a song to search for its video.")
        }
    }

    private func message(_ title: String, detail: String?) -> some View {
        VStack(spacing: LunalyrDesign.Spacing.xSmall) {
            Text(title).lunalyrText(.emphasis)
            if let detail { Text(detail).lunalyrText(.caption) }
        }
        .multilineTextAlignment(.center)
        .frame(maxWidth: .infinity)
        .padding(.vertical, LunalyrDesign.Spacing.section)
        .padding(.horizontal, LunalyrDesign.Spacing.window)
        .transition(.opacity)
    }

    @ViewBuilder private var results: some View {
        if preview {
            rowStack
        } else {
            ScrollViewReader { proxy in
                ScrollView {
                    rowStack
                }
                .focusable()
                .focused($focus, equals: .results)
                .focusEffectDisabled()
                .onKeyPress(.downArrow) { model.moveSelection(by: 1); return .handled }
                .onKeyPress(.upArrow) {
                    if model.selection == model.rows.first?.id { focus = .field } else { model.moveSelection(by: -1) }
                    return .handled
                }
                .onKeyPress(.return) { model.pickSelection(); return .handled }
                .onChange(of: model.selection) { _, id in
                    guard let id, focus == .results else { return }
                    withAnimation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion)) {
                        proxy.scrollTo(id)
                    }
                }
            }
            .transition(.opacity)
        }
    }

    private var rowStack: some View {
        LazyVStack(alignment: .leading, spacing: LunalyrDesign.Spacing.xSmall) {
            ForEach(model.rows) { row in
                MusicVideoSearchRowView(row: row, selected: model.selection == row.id,
                                        keyboard: focus == .results, preview: preview) {
                    model.selection = row.id
                    model.pick(row.id)
                }
                .id(row.id)
            }
        }
        .padding(LunalyrDesign.Spacing.small)
    }

    // MARK: Footer

    private var footer: some View {
        HStack(spacing: LunalyrDesign.Spacing.small) {
            if model.canGoBackToAutomatic {
                if preview {
                    StandInButton(title: "Back to automatic")
                } else {
                    Button("Back to automatic") { model.backToAutomatic() }
                        .help("Stop using your pick for this song and let Lunalyr find its video")
                }
            } else {
                Text("The video you pick is kept for this song.")
                    .lunalyrText(.footnote)
            }
            Spacer(minLength: LunalyrDesign.Spacing.small)
            if preview {
                StandInButton(title: "Close")
            } else {
                Button("Close") { model.close() }
                    .keyboardShortcut(.cancelAction)
            }
        }
        .controlSize(.regular)
    }
}

/// "Searching YouTube…", only once the wait passes `Hold.spinnerDelay`.
private struct SearchingNote: View {
    let reduceMotion: Bool
    @State private var shown = false

    var body: some View {
        HStack(spacing: LunalyrDesign.Spacing.small) {
            ProgressView().controlSize(.small)
            Text("Searching YouTube…").lunalyrText(.caption)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, LunalyrDesign.Spacing.section)
        .opacity(shown ? 1 : 0)
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion), value: shown)
        .task {
            try? await Task.sleep(for: .seconds(LunalyrDesign.Motion.Hold.spinnerDelay))
            if !Task.isCancelled { shown = true }
        }
        .accessibilityElement(children: .combine)
    }
}

/// One result, laid out as YouTube does: a 16:9 thumbnail with the length
/// in its corner, then the title (two lines at most), the channel, views
/// and age, and Lunalyr's hints.
struct MusicVideoSearchRowView: View {
    let row: MusicVideoSearchRow
    let selected: Bool
    /// The results have the keyboard, so the selection is drawn.
    var keyboard = false
    var preview = false
    let pick: () -> Void
    @State private var hovering = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    static let thumbnailWidth: CGFloat = 176

    var body: some View {
        Button(action: pick) {
            HStack(alignment: .top, spacing: LunalyrDesign.Spacing.medium) {
                MusicVideoSearchThumbnail(row: row, preview: preview)
                    .frame(width: Self.thumbnailWidth, height: Self.thumbnailWidth * 9 / 16)
                VStack(alignment: .leading, spacing: 3) {
                    Text(row.title)
                        .font(.body.weight(.semibold))
                        .lineLimit(2)
                        .multilineTextAlignment(.leading)
                        .fixedSize(horizontal: false, vertical: true)
                    if !row.channel.isEmpty {
                        HStack(spacing: 4) {
                            Text(row.channel).lineLimit(1)
                            if row.verifiedArtist {
                                Image(systemName: "music.note")
                                    .imageScale(.small)
                                    .help(MusicVideoSearchRow.verifiedLabel)
                            }
                        }
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                    }
                    if let details = row.details {
                        Text(details)
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }
                    if row.playingNow || row.sameLength {
                        HStack(spacing: LunalyrDesign.Spacing.xSmall) {
                            if row.playingNow { RowTag(text: MusicVideoSearchRow.playingNowLabel, symbol: "checkmark.circle.fill") }
                            if row.sameLength { RowTag(text: MusicVideoSearchRow.sameLengthHint, symbol: "timer") }
                        }
                        .padding(.top, 2)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .padding(LunalyrDesign.Spacing.small)
            .background(
                RoundedRectangle(cornerRadius: LunalyrDesign.Radius.artwork + 2, style: .continuous)
                    .fill(highlight)
            )
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion), value: hovering)
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion), value: selected)
        .help("Use this video for the song")
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(row.accessibilityLabel)
        .accessibilityHint("Uses this video for the song")
        .accessibilityAddTraits(.isButton)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }

    private var highlight: Color {
        if selected && keyboard { return Color.lunalyrAccent.opacity(0.18) }
        if hovering { return Color.primary.opacity(0.06) }
        return .clear
    }
}

private struct RowTag: View {
    let text: String
    let symbol: String

    var body: some View {
        Label(text, systemImage: symbol)
            .font(.caption.weight(.medium))
            .foregroundStyle(Color.lunalyrAccentText)
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .background(Capsule().fill(Color.lunalyrAccent.opacity(0.14)))
    }
}

/// A button-shaped label for rendered pictures, where AppKit-backed
/// controls draw blank.
private struct StandInButton: View {
    let title: String

    var body: some View {
        Text(title)
            .padding(.horizontal, 10)
            .frame(height: 22)
            .background(RoundedRectangle(cornerRadius: 5, style: .continuous).fill(Color(nsColor: .controlColor)))
            .overlay(RoundedRectangle(cornerRadius: 5, style: .continuous).strokeBorder(.quaternary))
    }
}

/// The result's still (YouTube's `mqdefault.jpg`), fetched as an image on
/// the music video's ephemeral session, with the length badge in the
/// corner. Never the video.
struct MusicVideoSearchThumbnail: View {
    let row: MusicVideoSearchRow
    var preview = false
    @State private var image: NSImage?
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    /// Stills already loaded this session, so a new search or reopening
    /// the window doesn't fetch them again.
    private static let cache: NSCache<NSString, NSImage> = {
        let cache = NSCache<NSString, NSImage>()
        cache.countLimit = 120
        return cache
    }()

    static func load(_ url: URL, http: MusicVideoHTTP = URLSessionMusicVideoHTTP()) async -> NSImage? {
        if let cached = cache.object(forKey: url.absoluteString as NSString) { return cached }
        guard let (data, response) = try? await http.get(url), response.statusCode == 200,
              let image = NSImage(data: data) else { return nil }
        cache.setObject(image, forKey: url.absoluteString as NSString)
        return image
    }

    var body: some View {
        ZStack(alignment: .bottomTrailing) {
            placeholder
            if let image {
                Image(nsImage: image)
                    .resizable()
                    .aspectRatio(contentMode: .fill)
                    .transition(.opacity)
            }
            if let duration = row.durationText {
                Text(duration)
                    .font(.caption2.weight(.semibold).monospacedDigit())
                    .foregroundStyle(.white)
                    .padding(.horizontal, 4)
                    .padding(.vertical, 1)
                    .background(RoundedRectangle(cornerRadius: 4, style: .continuous).fill(.black.opacity(0.8)))
                    .padding(4)
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: LunalyrDesign.Radius.artwork, style: .continuous))
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion),
                   value: image != nil)
        .task(id: row.videoID) {
            guard !preview, let url = row.thumbnailURL else { return }
            if let cached = Self.cache.object(forKey: url.absoluteString as NSString) { image = cached; return }
            image = nil
            let loaded = await Self.load(url)
            if !Task.isCancelled { image = loaded }
        }
        .accessibilityHidden(true)
    }

    /// A soft tint from the video's ID until the still arrives.
    private var placeholder: some View {
        let hue = Double(row.videoID.unicodeScalars.reduce(0) { ($0 &* 31 &+ Int($1.value)) & 0xFFFF } % 360) / 360
        return LinearGradient(colors: [Color(hue: hue, saturation: 0.35, brightness: 0.55),
                                       Color(hue: hue, saturation: 0.45, brightness: 0.3)],
                              startPoint: .topLeading, endPoint: .bottomTrailing)
            .overlay(Image(systemName: "film").font(.title2).foregroundStyle(.white.opacity(0.5)))
    }
}
