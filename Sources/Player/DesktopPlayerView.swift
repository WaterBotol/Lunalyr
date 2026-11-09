import SwiftUI

extension PlayerKind {
    /// The colour each player is known by, so the switch reads at a glance.
    var tint: Color {
        switch self {
        case .appleMusic: return Color(red: 0.98, green: 0.26, blue: 0.40)
        case .spotify: return Color(red: 0.12, green: 0.84, blue: 0.38)
        }
    }

    var shortName: String { self == .appleMusic ? "Apple Music" : "Spotify" }
}

/// Set while the hit mask is drawn: scroll views come out blank in an image,
/// so the mask is told to paint plain blocks where they are.
private struct MaskRenderKey: EnvironmentKey { static let defaultValue = false }
extension EnvironmentValues {
    var playerMaskRender: Bool {
        get { self[MaskRenderKey.self] }
        set { self[MaskRenderKey.self] = newValue }
    }
}

/// The player that lives on the desktop: a strip of covers with what played,
/// what plays now (in the middle) and what plays next, and one small line
/// under it to pick the player and search. Searching swaps the strip for the
/// results.
struct DesktopPlayerView: View {
    static let width: CGFloat = 720
    static let stripHeight: CGFloat = 214
    static let resultsHeight: CGFloat = 372
    static let cover: CGFloat = 150

    @ObservedObject var model: DesktopPlayerModel
    let displayID: CGDirectDisplayID
    @ObservedObject private var hover = HoverState.shared
    @EnvironmentObject private var nowPlaying: NowPlayingMonitor
    @Environment(\.playerMaskRender) private var maskRender
    @FocusState private var searchFocused: Bool

    var body: some View {
        let shown = hover.isActive(on: displayID) || model.isPinned
        VStack(spacing: 10) {
            Group {
                if model.isSearching {
                    resultsList
                        .frame(width: Self.width, height: Self.resultsHeight)
                        .glass(radius: 26, tint: model.player.tint)
                } else {
                    strip.frame(width: Self.width, height: Self.stripHeight)
                }
            }
            controls
        }
        .foregroundStyle(.white)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .environment(\.colorScheme, .dark)
        .opacity(shown ? 1 : 0)
        .scaleEffect(shown ? 1 : 0.97)
        .animation(.easeOut(duration: 0.28), value: shown)
        .animation(.smooth(duration: 0.34), value: model.isSearching)
        .onExitCommand {
            searchFocused = false
            model.query = ""
        }
        .onChange(of: shown) { _, now in if now { model.refreshQueue() } }
    }

    // MARK: Strip

    @ViewBuilder
    private var strip: some View {
        if maskRender {
            Color.black.opacity(0.3)
        } else if model.queue.isEmpty, let now = nowPlaying.current {
            // A player that shares no queue (Spotify): just what is playing.
            CoverStrip(items: [PlayerQueueItem(id: "now", title: now.title, artist: now.artist, index: 0, isCurrent: true)],
                       tint: model.playingKind.tint, artwork: nowPlaying.currentTrackArtwork) { _ in nowPlaying.playPause() }
        } else if model.queue.isEmpty {
            Text(queueMessage).font(.system(size: 12)).foregroundStyle(.white.opacity(0.8))
                .shadow(color: .black.opacity(0.6), radius: 4)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else {
            CoverStrip(items: model.queue, tint: model.playingKind.tint, artwork: nowPlaying.currentTrackArtwork) { entry in
                if entry.isCurrent { nowPlaying.playPause() } else { model.play(entry) }
            }
        }
    }

    private var queueMessage: String {
        return "Nothing is playing."
    }

    // MARK: Controls

    private var controls: some View {
        HStack(spacing: 16) {
            playerSwitch
            Spacer(minLength: 0)
            HStack(spacing: 0) {
                transportButton("backward.fill", "Previous", 12) { nowPlaying.previousTrack() }
                transportButton(nowPlaying.isPlaying ? "pause.fill" : "play.fill",
                                nowPlaying.isPlaying ? "Pause" : "Play", 15) { nowPlaying.playPause() }
                transportButton("forward.fill", "Next", 12) { nowPlaying.nextTrack() }
            }
            .disabled(!nowPlaying.canControlPlayback)
            Spacer(minLength: 0)
            searchField
        }
        .shadow(color: .black.opacity(0.5), radius: 4)
        .frame(width: Self.width - 40, height: 30)
    }

    private func transportButton(_ symbol: String, _ label: String, _ size: CGFloat, _ action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol).font(.system(size: size, weight: .semibold))
                .frame(width: 32, height: 30).contentShape(Rectangle())
        }
        .buttonStyle(.plain).help(label).accessibilityLabel(label)
    }

    /// Both players side by side; the one in use is lit in its own colour.
    private var playerSwitch: some View {
        HStack(spacing: 14) {
            ForEach(PlayerKind.allCases) { kind in
                let on = kind == model.player
                Button { model.player = kind } label: {
                    VStack(spacing: 3) {
                        Text(kind.shortName).font(.system(size: 11, weight: on ? .bold : .medium))
                            .foregroundStyle(on ? kind.tint : .white.opacity(0.55))
                        Capsule().fill(on ? kind.tint : .clear).frame(height: 2)
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .help("Search and play in \(kind.shortName)")
            }
        }
        .animation(.easeOut(duration: 0.2), value: model.player)
    }

    /// A small field with a hairline under it, not a pill.
    private var searchField: some View {
        VStack(spacing: 3) {
            HStack(spacing: 5) {
                Image(systemName: "magnifyingglass").font(.system(size: 11)).foregroundStyle(.white.opacity(0.7))
                TextField("Search", text: $model.query)
                    .textFieldStyle(.plain).font(.system(size: 12))
                    .focused($searchFocused)
                    .onSubmit { if let first = model.results.first { model.play(first) } }
                if !model.query.isEmpty {
                    Button { model.query = "" } label: {
                        Image(systemName: "xmark.circle.fill").foregroundStyle(.white.opacity(0.5))
                    }.buttonStyle(.plain)
                }
            }
            Rectangle().fill(searchFocused ? model.player.tint : .white.opacity(0.3)).frame(height: 1)
        }
        .frame(width: 170)
    }

    // MARK: Results

    @ViewBuilder
    private var resultsList: some View {
        if maskRender { Color.black.opacity(0.3) } else { resultsScroll }
    }

    private var resultsScroll: some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 2) {
                statusLine
                ForEach(model.results) { resultRow($0) }
            }
            .padding(10)
        }
    }

    @ViewBuilder
    private var statusLine: some View {
        switch model.status {
        case .searching: Text("Searching\u{2026}").font(.system(size: 12)).foregroundStyle(.white.opacity(0.6)).padding(8)
        case .message(let text): Text(text).font(.system(size: 12)).foregroundStyle(.white.opacity(0.7)).padding(8)
        case .idle: EmptyView()
        }
    }

    private func resultRow(_ result: PlayerSearchResult) -> some View {
        Button { model.play(result) } label: {
            HStack(spacing: 10) {
                RemoteCover(url: result.artworkURL, symbol: result.kind == .album ? "square.stack" : "music.note",
                            lookup: result.artworkURL == nil && model.player == .appleMusic ? (result.title, result.artist) : nil)
                    .frame(width: 40, height: 40)
                VStack(alignment: .leading, spacing: 1) {
                    Text(result.title).font(.system(size: 13, weight: .medium)).lineLimit(1)
                    Text(subtitle(result)).font(.system(size: 11)).foregroundStyle(.white.opacity(0.6)).lineLimit(1)
                }
                Spacer()
                if result.inLibrary {
                    Text("Library").font(.system(size: 10, weight: .semibold)).foregroundStyle(.white.opacity(0.5))
                } else if result.kind == .album {
                    Text("Album").font(.system(size: 10, weight: .semibold)).foregroundStyle(.white.opacity(0.5))
                }
            }
            .padding(6).contentShape(Rectangle())
        }
        .buttonStyle(RowButtonStyle())
    }

    private func subtitle(_ result: PlayerSearchResult) -> String {
        if result.kind == .album || result.album.isEmpty || result.album == result.title { return result.artist }
        return "\(result.artist) \u{00B7} \(result.album)"
    }
}

private struct StripOffsetKey: PreferenceKey {
    static let defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) { value = nextValue() }
}

/// Turns the mouse wheel's up and down into sideways scrolling over the
/// panel's strip, which only listens to sideways movement on its own.
private final class WheelBridge {
    private var monitor: Any?

    func start() {
        guard monitor == nil else { return }
        monitor = NSEvent.addLocalMonitorForEvents(matching: .scrollWheel) { event in
            guard let window = event.window, window.title == ControlPanelController.windowTitle,
                  event.scrollingDeltaX == 0, event.scrollingDeltaY != 0, !event.hasPreciseScrollingDeltas,
                  var view = window.contentView?.hitTest(event.locationInWindow) else { return event }
            while !(view is NSScrollView) {
                guard let up = view.superview else { return event }
                view = up
            }
            guard let scroll = view as? NSScrollView, let document = scroll.documentView else { return event }
            let clip = scroll.contentView
            let widest = max(0, document.frame.width - clip.bounds.width)
            let x = min(max(clip.bounds.origin.x - event.scrollingDeltaY * 24, 0), widest)
            clip.setBoundsOrigin(NSPoint(x: x, y: clip.bounds.origin.y))
            scroll.reflectScrolledClipView(clip)
            return nil
        }
    }

    func stop() {
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
    }

    deinit { stop() }
}

/// A row of covers that scrolls sideways with the trackpad or the wheel,
/// opening on the song that is playing. Click a cover to play it. The faster
/// it scrolls, the more the covers bend, blur and brighten, like light
/// passing through thick glass.
private struct CoverStrip: View {
    let items: [PlayerQueueItem]
    let tint: Color
    var artwork: NSImage?
    let onTap: (PlayerQueueItem) -> Void
    @State private var focus: String?
    @State private var velocity: CGFloat = 0
    @State private var lastOffset: CGFloat?
    @State private var lastTime: TimeInterval = 0
    @State private var decay: Task<Void, Never>?
    @State private var wheel = WheelBridge()

    private var currentID: String? { items.first(where: \.isCurrent)?.id ?? items.first?.id }

    var body: some View {
        let bend = max(-1, min(1, velocity / 1800))
        let strength = abs(bend)
        ScrollView(.horizontal, showsIndicators: false) {
            LazyHStack(spacing: 14) {
                ForEach(items) { item in
                    Button { onTap(item) } label: {
                        VStack(spacing: 6) {
                            Group {
                                if item.isCurrent, let artwork {
                                    Image(nsImage: artwork).resizable().aspectRatio(contentMode: .fill)
                                } else {
                                    RemoteCover(url: nil, symbol: "music.note", lookup: (item.title, item.artist))
                                }
                            }
                                .frame(width: DesktopPlayerView.cover, height: DesktopPlayerView.cover)
                                .clipShape(RoundedRectangle(cornerRadius: 12))
                                .overlay(RoundedRectangle(cornerRadius: 12)
                                    .strokeBorder(tint, lineWidth: item.isCurrent ? 2 : 0))
                                .shadow(color: .black.opacity(0.5), radius: 12, y: 6)
                            VStack(spacing: 1) {
                                Text(item.title).font(.system(size: 12, weight: .semibold)).lineLimit(1)
                                Text(item.artist).font(.system(size: 10)).foregroundStyle(.white.opacity(0.65)).lineLimit(1)
                            }
                            .frame(width: DesktopPlayerView.cover)
                            .shadow(color: .black.opacity(0.7), radius: 5)
                        }
                    }
                    .buttonStyle(.plain)
                    .id(item.id)
                    .scrollTransition(axis: .horizontal) { content, phase in
                        let edge = abs(phase.value)
                        return content
                            .scaleEffect((phase.isIdentity ? 1 : 0.84) - 0.1 * strength * edge)
                            .opacity(phase.isIdentity ? 1 : 0.6)
                            .rotation3DEffect(.degrees(phase.value * (-28 - 34 * strength)), axis: (0, 1, 0), perspective: 0.6)
                            // Squeezed towards the middle, like a lens, and
                            // blurred and lit by how fast it moves.
                            .offset(x: -phase.value * strength * 34)
                            .blur(radius: strength * (2 + 5 * edge))
                            .brightness(strength * 0.12 * (1 - edge))
                            .saturation(1 + strength * 0.25)
                    }
                }
            }
            .scrollTargetLayout()
            .padding(.horizontal, (DesktopPlayerView.width - DesktopPlayerView.cover) / 2)
            .background(GeometryReader { proxy in
                Color.clear.preference(key: StripOffsetKey.self, value: proxy.frame(in: .named("strip")).minX)
            })
        }
        .coordinateSpace(name: "strip")
        .onPreferenceChange(StripOffsetKey.self) { measured(offset: $0) }
        .scrollPosition(id: $focus, anchor: .center)
        .scrollTargetBehavior(.viewAligned)
        .task(id: "\(currentID ?? "")|\(items.count)") {
            // The row is laid out lazily; give it a moment before centring.
            try? await Task.sleep(nanoseconds: 80_000_000)
            guard !Task.isCancelled else { return }
            withAnimation(.smooth(duration: 0.4)) { focus = currentID }
        }
        .onAppear { wheel.start() }
        .onDisappear { wheel.stop() }
    }

    private func measured(offset: CGFloat) {
        let now = ProcessInfo.processInfo.systemUptime
        defer { lastOffset = offset; lastTime = now }
        guard let last = lastOffset, now > lastTime else { return }
        let instant = (offset - last) / CGFloat(now - lastTime)
        guard instant.isFinite else { return }
        withAnimation(.easeOut(duration: 0.12)) { velocity = velocity * 0.6 + instant * 0.4 }
        decay?.cancel()
        decay = Task { @MainActor in
            while abs(velocity) > 12 {
                try? await Task.sleep(nanoseconds: 33_000_000)
                guard !Task.isCancelled else { return }
                withAnimation(.easeOut(duration: 0.1)) { velocity *= 0.82 }
            }
            velocity = 0
        }
    }
}

/// Covers found by name, once each, for songs that carry no cover address.
/// Only the song's own title and artist are sent, and only for Apple Music.
enum CoverLookup {
    private static let cache = NSCache<NSString, NSURL>()

    static func url(title: String, artist: String) async -> URL? {
        let key = "\(title)|\(artist)".lowercased() as NSString
        if let hit = cache.object(forKey: key) { return hit as URL }
        guard let url = AppleMusicSearch.searchURL("\(title) \(artist)", entity: "song", limit: 1),
              let (data, _) = try? await URLSession.shared.data(from: url),
              let first = AppleMusicSearch.parseCatalogue(data).first, let art = first.artworkURL else { return nil }
        let large = URL(string: art.absoluteString.replacingOccurrences(of: "200x200", with: "400x400")) ?? art
        cache.setObject(large as NSURL, forKey: key)
        return large
    }
}

/// A cover that downloads itself, with a plain tile while it does.
struct RemoteCover: View {
    let url: URL?
    let symbol: String
    var lookup: (String, String)?
    @State private var image: NSImage?

    var body: some View {
        ZStack {
            Rectangle().fill(.white.opacity(0.1))
            if let image {
                Image(nsImage: image).resizable().aspectRatio(contentMode: .fill).transition(.opacity)
            } else {
                Image(systemName: symbol).foregroundStyle(.white.opacity(0.4))
            }
        }
        .animation(.easeOut(duration: 0.25), value: image)
        .clipShape(RoundedRectangle(cornerRadius: 6))
        .task(id: "\(url?.absoluteString ?? "")|\(lookup?.0 ?? "")|\(lookup?.1 ?? "")") {
            image = nil
            var address = url
            if address == nil, let lookup { address = await CoverLookup.url(title: lookup.0, artist: lookup.1) }
            guard let address, let (data, _) = try? await URLSession.shared.data(from: address) else { return }
            image = NSImage(data: data)
        }
    }
}

private struct RowButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .background(configuration.isPressed ? Color.white.opacity(0.14) : Color.clear, in: RoundedRectangle(cornerRadius: 8))
    }
}

private extension View {
    /// Frosted dark glass with a hairline edge.
    func glass(radius: CGFloat, tint: Color) -> some View {
        background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: radius, style: .continuous))
            .background(Color.black.opacity(0.28), in: RoundedRectangle(cornerRadius: radius, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: radius, style: .continuous).strokeBorder(.white.opacity(0.14), lineWidth: 0.5))
            .shadow(color: .black.opacity(0.35), radius: 24, y: 8)
    }
}
