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

/// The player that lives on the desktop: a bar with the song, the
/// transport, a search box and the player to search in; above it, search
/// results, or a strip of covers for what plays next and the artist's albums.
struct DesktopPlayerView: View {
    static let width: CGFloat = 720
    static let barHeight: CGFloat = 68
    static let resultsHeight: CGFloat = 372
    static let stripHeight: CGFloat = 262

    @ObservedObject var model: DesktopPlayerModel
    let displayID: CGDirectDisplayID
    @ObservedObject private var hover = HoverState.shared
    @EnvironmentObject private var nowPlaying: NowPlayingMonitor
    @FocusState private var searchFocused: Bool

    private static let motion = Animation.smooth(duration: 0.34)

    var body: some View {
        let shown = hover.isActive(on: displayID) || model.isPinned
        // The bar sits in the middle of the desktop; what opens hangs below it.
        ZStack {
            bar
                .frame(width: Self.width, height: Self.barHeight)
                .glass(radius: 24, tint: model.player.tint)
            if model.isExpanded {
                drawer
                    .frame(width: Self.width, height: model.drawerHeight)
                    .offset(y: Self.barHeight / 2 + 10 + model.drawerHeight / 2)
                    .transition(.opacity.combined(with: .scale(scale: 0.96, anchor: .top)))
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .environment(\.colorScheme, .dark)
        .opacity(shown ? 1 : 0)
        .scaleEffect(shown ? 1 : 0.97)
        .animation(.easeOut(duration: 0.28), value: shown)
        .animation(Self.motion, value: model.isExpanded)
        .animation(Self.motion, value: model.isSearching)
        .onExitCommand {
            searchFocused = false
            model.query = ""
            model.isExpanded = false
        }
    }

    // MARK: Bar

    private var bar: some View {
        HStack(spacing: 12) {
            cover
            VStack(alignment: .leading, spacing: 1) {
                Text(nowPlaying.current?.title ?? "Nothing playing")
                    .font(.system(size: 13, weight: .semibold)).lineLimit(1)
                Text(nowPlaying.current?.artist ?? "Search to play something")
                    .font(.system(size: 11)).foregroundStyle(.white.opacity(0.62)).lineLimit(1)
            }
            .frame(width: 120, alignment: .leading)

            HStack(spacing: 2) {
                transportButton("backward.fill", "Previous", 13) { nowPlaying.previousTrack() }
                transportButton(nowPlaying.isPlaying ? "pause.fill" : "play.fill",
                                nowPlaying.isPlaying ? "Pause" : "Play", 17) { nowPlaying.playPause() }
                transportButton("forward.fill", "Next", 13) { nowPlaying.nextTrack() }
            }
            .disabled(!nowPlaying.canControlPlayback)

            searchField
            playerMenu
            Button {
                model.isExpanded.toggle()
            } label: {
                Image(systemName: "chevron.up")
                    .rotationEffect(.degrees(model.isExpanded ? 180 : 0))
                    .font(.system(size: 11, weight: .bold)).frame(width: 26, height: 26).contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help(model.isExpanded ? "Close" : "Open")
        }
        .padding(.horizontal, 14)
        .padding(.bottom, 4)
        .foregroundStyle(.white)
        .overlay(alignment: .bottom) { scrubber.padding(.horizontal, 22).padding(.bottom, 5) }
    }

    @ViewBuilder
    private var scrubber: some View {
        if let current = nowPlaying.current, !current.isStream, current.duration > 0 {
            TimelineView(.periodic(from: .now, by: 0.5)) { _ in
                BarScrubber(progress: min(nowPlaying.playbackTime() / current.duration, 1),
                            tint: model.player.tint) { fraction in nowPlaying.seek(to: fraction * current.duration) }
            }
        }
    }

    private var cover: some View {
        Group {
            if let artwork = nowPlaying.currentTrackArtwork {
                Image(nsImage: artwork).resizable().aspectRatio(contentMode: .fill)
            } else {
                Rectangle().fill(.white.opacity(0.1))
                    .overlay(Image(systemName: "music.note").foregroundStyle(.white.opacity(0.5)))
            }
        }
        .frame(width: 44, height: 44)
        .clipShape(RoundedRectangle(cornerRadius: 8))
        .shadow(color: .black.opacity(0.4), radius: 6, y: 2)
    }

    private func transportButton(_ symbol: String, _ label: String, _ size: CGFloat, _ action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol).font(.system(size: size, weight: .semibold))
                .frame(width: 30, height: 30).contentShape(Rectangle())
        }
        .buttonStyle(.plain).help(label).accessibilityLabel(label)
    }

    private var searchField: some View {
        HStack(spacing: 6) {
            Image(systemName: "magnifyingglass").font(.system(size: 11)).foregroundStyle(model.player.tint)
            TextField("Search \(model.player.shortName)", text: $model.query)
                .textFieldStyle(.plain).font(.system(size: 12))
                .focused($searchFocused)
                .onSubmit { if let first = model.results.first { model.play(first) } }
            if !model.query.isEmpty {
                Button { model.query = "" } label: {
                    Image(systemName: "xmark.circle.fill").foregroundStyle(.white.opacity(0.5))
                }.buttonStyle(.plain)
            }
        }
        .padding(.horizontal, 9).frame(height: 28)
        .background(.white.opacity(0.1), in: Capsule())
        .overlay(Capsule().strokeBorder(model.player.tint.opacity(0.55), lineWidth: 1))
        .onChange(of: model.query) { _, text in
            if !text.isEmpty { model.followNowPlaying(); model.isExpanded = true }
        }
    }

    /// Which player the search plays in: its name and colour, always shown.
    private var playerMenu: some View {
        Menu {
            ForEach(PlayerKind.allCases) { kind in
                Button {
                    model.player = kind
                } label: {
                    if kind == model.player { Label(kind.shortName, systemImage: "checkmark") } else { Text(kind.shortName) }
                }
            }
        } label: {
            HStack(spacing: 5) {
                Circle().fill(model.player.tint).frame(width: 7, height: 7)
                Text(model.player.shortName).font(.system(size: 11, weight: .semibold)).lineLimit(1)
                Image(systemName: "chevron.up.chevron.down").font(.system(size: 8, weight: .bold)).opacity(0.6)
            }
            .padding(.horizontal, 9).frame(height: 28)
            .background(model.player.tint.opacity(0.2), in: Capsule())
            .overlay(Capsule().strokeBorder(model.player.tint.opacity(0.5), lineWidth: 0.5))
        }
        .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
        .help("Search and play in \(model.player.shortName)")
    }

    // MARK: Drawer

    @ViewBuilder
    private var drawer: some View {
        if model.isSearching {
            resultsList
                .glass(radius: 26, tint: model.player.tint)
                .foregroundStyle(.white)
        } else {
            // No box behind the covers: they float over the desktop.
            VStack(spacing: 6) {
                Picker("", selection: $model.drawer) {
                    ForEach(DesktopPlayerModel.Drawer.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented).labelsHidden().frame(width: 200)
                .padding(3).background(.ultraThinMaterial, in: Capsule())
                strip
            }
            .foregroundStyle(.white)
        }
    }

    @ViewBuilder
    private var strip: some View {
        switch model.drawer {
        case .queue:
            if model.queue.isEmpty {
                stripMessage(queueMessage)
            } else {
                CoverStrip(items: model.queue.map { entry in
                    CoverStrip.Item(id: entry.id, title: entry.title, subtitle: entry.artist, url: nil,
                                    lookup: (entry.title, entry.artist), action: { [model] in model.play(entry) })
                })
            }
        case .albums:
            if model.albums.isEmpty {
                stripMessage(model.player == .appleMusic
                             ? "Play a song in Apple Music and the artist\u{2019}s albums appear here."
                             : "Albums are shown for Apple Music.")
            } else {
                CoverStrip(items: model.albums.map { entry in
                    CoverStrip.Item(id: entry.id, title: entry.title, subtitle: entry.year, url: entry.artworkURL,
                                    lookup: nil, action: { [model] in model.play(entry) })
                })
            }
        }
    }

    private var queueMessage: String {
        if case .message(let text) = model.status { return text }
        return model.player == .spotify ? "Spotify doesn\u{2019}t share what plays next." : "Nothing is queued after this song."
    }

    private func stripMessage(_ text: String) -> some View {
        Text(text).font(.system(size: 12)).foregroundStyle(.white.opacity(0.75)).multilineTextAlignment(.center)
            .padding(.horizontal, 16).padding(.vertical, 10)
            .background(.ultraThinMaterial, in: Capsule())
            .frame(maxHeight: .infinity)
    }

    private var resultsList: some View {
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

/// A row of covers that scrolls sideways with the trackpad or the wheel; the
/// ones at the edges tilt and shrink. Click a cover to play it.
private struct CoverStrip: View {
    struct Item: Identifiable {
        let id: String
        let title: String
        let subtitle: String
        let url: URL?
        let lookup: (String, String)?
        let action: () -> Void
    }

    let items: [Item]

    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            LazyHStack(spacing: 14) {
                ForEach(items) { item in
                    Button(action: item.action) {
                        VStack(spacing: 6) {
                            RemoteCover(url: item.url, symbol: "music.note", lookup: item.lookup)
                                .frame(width: 168, height: 168)
                                .clipShape(RoundedRectangle(cornerRadius: 12))
                                .shadow(color: .black.opacity(0.5), radius: 12, y: 6)
                            VStack(spacing: 1) {
                                Text(item.title).font(.system(size: 12, weight: .semibold)).lineLimit(1)
                                Text(item.subtitle).font(.system(size: 10)).foregroundStyle(.white.opacity(0.65)).lineLimit(1)
                            }
                            .frame(width: 168)
                            .shadow(color: .black.opacity(0.7), radius: 5)
                        }
                    }
                    .buttonStyle(.plain)
                    .scrollTransition(axis: .horizontal) { content, phase in
                        content
                            .scaleEffect(phase.isIdentity ? 1 : 0.84)
                            .opacity(phase.isIdentity ? 1 : 0.6)
                            .rotation3DEffect(.degrees(phase.value * -28), axis: (0, 1, 0), perspective: 0.6)
                    }
                }
            }
            .scrollTargetLayout()
            .padding(.horizontal, (DesktopPlayerView.width - 168) / 2)
        }
        .scrollTargetBehavior(.viewAligned)
        .frame(maxHeight: .infinity)
    }
}

/// A hairline progress bar on the bar's lower edge: click or drag to seek.
private struct BarScrubber: View {
    let progress: Double
    let tint: Color
    var onSeek: (Double) -> Void
    @State private var drag: Double?

    var body: some View {
        GeometryReader { geometry in
            let fraction = min(max(drag ?? progress, 0), 1)
            ZStack(alignment: .leading) {
                Capsule().fill(.white.opacity(0.18)).frame(height: 3)
                Capsule().fill(tint).frame(width: geometry.size.width * fraction, height: 3)
            }
            .frame(height: 12)
            .contentShape(Rectangle())
            .gesture(DragGesture(minimumDistance: 0)
                .onChanged { drag = min(max($0.location.x / geometry.size.width, 0), 1) }
                .onEnded { value in
                    let target = min(max(value.location.x / geometry.size.width, 0), 1)
                    drag = nil
                    onSeek(target)
                })
        }
        .frame(height: 12)
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
