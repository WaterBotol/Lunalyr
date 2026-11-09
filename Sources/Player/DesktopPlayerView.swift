import SwiftUI

/// The glass player that lives on the desktop: a bar with the song, the
/// transport, a search box and the player to search in; above it, search
/// results, the queue or a cover-flow of the artist's albums.
struct DesktopPlayerView: View {
    static let width: CGFloat = 640
    static let barHeight: CGFloat = 68
    static let drawerHeight: CGFloat = 372

    @ObservedObject var model: DesktopPlayerModel
    @EnvironmentObject private var nowPlaying: NowPlayingMonitor
    @FocusState private var searchFocused: Bool

    var body: some View {
        VStack(spacing: 8) {
            if model.isExpanded {
                drawer
                    .frame(height: Self.drawerHeight)
                    .glass(radius: 26)
                    .transition(.move(edge: .bottom).combined(with: .opacity))
            }
            bar
                .frame(height: Self.barHeight)
                .glass(radius: 24)
        }
        .frame(width: Self.width)
        .frame(maxHeight: .infinity, alignment: .bottom)
        .environment(\.colorScheme, .dark)
        .animation(.spring(response: 0.34, dampingFraction: 0.86), value: model.isExpanded)
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
            .frame(width: 130, alignment: .leading)

            HStack(spacing: 4) {
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
                Image(systemName: model.isExpanded ? "chevron.down" : "chevron.up")
                    .font(.system(size: 11, weight: .bold)).frame(width: 26, height: 26).contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help(model.isExpanded ? "Close" : "Open")
        }
        .padding(.horizontal, 14)
        .foregroundStyle(.white)
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
            Image(systemName: "magnifyingglass").font(.system(size: 11)).foregroundStyle(.white.opacity(0.6))
            TextField("Search \(model.player.displayName)", text: $model.query)
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
        .onChange(of: searchFocused) { _, focused in
            if focused { model.followNowPlaying(); model.isExpanded = true }
        }
    }

    /// Which player the search plays in.
    private var playerMenu: some View {
        Menu {
            ForEach(PlayerKind.allCases) { kind in
                Button {
                    model.player = kind
                } label: {
                    if kind == model.player { Label(kind.displayName, systemImage: "checkmark") } else { Text(kind.displayName) }
                }
            }
        } label: {
            Image(systemName: model.player == .appleMusic ? "music.note" : "dot.radiowaves.left.and.right")
                .font(.system(size: 12, weight: .semibold)).frame(width: 26, height: 26)
        }
        .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
        .help("Play in \(model.player.displayName)")
    }

    // MARK: Drawer

    @ViewBuilder
    private var drawer: some View {
        VStack(spacing: 0) {
            if model.isSearching {
                resultsList
            } else {
                Picker("", selection: $model.drawer) {
                    ForEach(DesktopPlayerModel.Drawer.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented).labelsHidden().frame(width: 190).padding(.top, 12)
                switch model.drawer {
                case .albums: albumsView
                case .queue: queueList
                }
            }
        }
        .foregroundStyle(.white)
    }

    private var resultsList: some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 2) {
                statusLine
                ForEach(model.results) { result in
                    resultRow(result)
                }
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
                RemoteCover(url: result.artworkURL, symbol: result.kind == .album ? "square.stack" : "music.note")
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

    private var queueList: some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 2) {
                statusLine
                ForEach(model.queue) { item in
                    Button { model.play(item) } label: {
                        HStack(spacing: 10) {
                            Text("\(item.index)").font(.system(size: 11).monospacedDigit())
                                .foregroundStyle(.white.opacity(0.4)).frame(width: 26)
                            VStack(alignment: .leading, spacing: 1) {
                                Text(item.title).font(.system(size: 13, weight: .medium)).lineLimit(1)
                                Text(item.artist).font(.system(size: 11)).foregroundStyle(.white.opacity(0.6)).lineLimit(1)
                            }
                            Spacer()
                        }
                        .padding(6).contentShape(Rectangle())
                    }
                    .buttonStyle(RowButtonStyle())
                }
            }
            .padding(10)
        }
    }

    // MARK: Cover-flow

    @ViewBuilder
    private var albumsView: some View {
        if model.albums.isEmpty {
            Spacer()
            Text(model.player == .appleMusic
                 ? "Play a song in Apple Music and the artist\u{2019}s albums appear here."
                 : "Albums are shown for Apple Music.")
                .font(.system(size: 12)).foregroundStyle(.white.opacity(0.6)).multilineTextAlignment(.center).padding(30)
            Spacer()
        } else {
            CoverFlow(albums: model.albums, selected: $model.selectedAlbum) { model.play($0) }
        }
    }
}

/// Albums in a row: the middle one faces you, the rest turn away.
private struct CoverFlow: View {
    let albums: [PlayerAlbum]
    @Binding var selected: Int
    var onPlay: (PlayerAlbum) -> Void

    var body: some View {
        VStack(spacing: 10) {
            ZStack {
                ForEach(visible, id: \.self) { index in
                    let offset = index - selected
                    RemoteCover(url: albums[index].artworkURL, symbol: "square.stack")
                        .frame(width: 176, height: 176)
                        .clipShape(RoundedRectangle(cornerRadius: 10))
                        .shadow(color: .black.opacity(0.5), radius: offset == 0 ? 16 : 6, y: 6)
                        .scaleEffect(offset == 0 ? 1 : 0.72)
                        .rotation3DEffect(.degrees(Double(max(-1, min(1, offset))) * -52), axis: (0, 1, 0), perspective: 0.5)
                        .offset(x: CGFloat(offset) * (offset == 0 ? 0 : 92) + (offset == 0 ? 0 : (offset > 0 ? 70 : -70)))
                        .zIndex(Double(10 - abs(offset)))
                        .onTapGesture {
                            if offset == 0 { onPlay(albums[index]) } else { withAnimation(.spring(response: 0.3)) { selected = index } }
                        }
                }
            }
            .frame(height: 200)
            .contentShape(Rectangle())
            .gesture(DragGesture(minimumDistance: 20).onEnded { value in
                step(value.translation.width < 0 ? 1 : -1)
            })
            .onScrollWheel { delta in step(delta > 0 ? -1 : 1) }

            VStack(spacing: 2) {
                Text(albums[safe: selected]?.title ?? "").font(.system(size: 14, weight: .semibold)).lineLimit(1)
                Text(albums[safe: selected]?.year ?? "").font(.system(size: 11)).foregroundStyle(.white.opacity(0.6))
            }
            Text("Click the front album to play it").font(.system(size: 10)).foregroundStyle(.white.opacity(0.4))
        }
        .padding(.top, 8)
    }

    private var visible: [Int] {
        guard !albums.isEmpty else { return [] }
        return Array(max(0, selected - 3)...min(albums.count - 1, selected + 3))
    }

    private func step(_ direction: Int) {
        withAnimation(.spring(response: 0.3)) { selected = min(max(selected + direction, 0), albums.count - 1) }
    }
}

private extension Array {
    subscript(safe index: Int) -> Element? { indices.contains(index) ? self[index] : nil }
}

/// A cover that downloads itself, with a plain tile while it does.
struct RemoteCover: View {
    let url: URL?
    let symbol: String
    @State private var image: NSImage?

    var body: some View {
        ZStack {
            Rectangle().fill(.white.opacity(0.1))
            if let image {
                Image(nsImage: image).resizable().aspectRatio(contentMode: .fill)
            } else {
                Image(systemName: symbol).foregroundStyle(.white.opacity(0.4))
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: 6))
        .task(id: url) {
            image = nil
            guard let url, let (data, _) = try? await URLSession.shared.data(from: url) else { return }
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
    func glass(radius: CGFloat) -> some View {
        background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: radius, style: .continuous))
            .background(Color.black.opacity(0.28), in: RoundedRectangle(cornerRadius: radius, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: radius, style: .continuous).strokeBorder(.white.opacity(0.14), lineWidth: 0.5))
            .shadow(color: .black.opacity(0.35), radius: 24, y: 8)
    }

    /// Horizontal wheel or trackpad scrolling, as -1/+1 steps.
    func onScrollWheel(_ handler: @escaping (CGFloat) -> Void) -> some View {
        background(ScrollWheelCatcher(handler: handler))
    }
}

private struct ScrollWheelCatcher: NSViewRepresentable {
    let handler: (CGFloat) -> Void

    func makeNSView(context: Context) -> CatcherView { CatcherView(handler: handler) }
    func updateNSView(_ view: CatcherView, context: Context) { view.handler = handler }

    final class CatcherView: NSView {
        var handler: (CGFloat) -> Void
        private var accumulated: CGFloat = 0
        init(handler: @escaping (CGFloat) -> Void) { self.handler = handler; super.init(frame: .zero) }
        required init?(coder: NSCoder) { nil }

        override func scrollWheel(with event: NSEvent) {
            let dx = abs(event.scrollingDeltaX) > abs(event.scrollingDeltaY) ? event.scrollingDeltaX : -event.scrollingDeltaY
            accumulated += dx
            if abs(accumulated) > 24 { handler(accumulated); accumulated = 0 }
            if event.phase == .ended { accumulated = 0 }
        }
    }
}
