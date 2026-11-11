import AppKit
import SwiftUI

// The pieces of the desktop player's "Sleeve" card: its glass, the record,
// the note over it, and the search and queue that take over its right-hand
// column. `DesktopPlayerView` lays them out.

// MARK: - Card

/// The card's glass: the system's frosted material with the album's colours
/// washed over it (its strongest colour at the top, a deep shade below), a
/// lit rim and a soft shadow. Solid under Reduce Transparency, with a firmer
/// edge under Increase Contrast. In the hit mask, one solid block: every
/// point of the card takes the click, never Finder.
struct SleeveCardBackground: View {
    let palette: ArtworkPalette
    let hasSong: Bool
    @Environment(\.playerLook) private var look
    @Environment(\.playerMaskRender) private var maskRender
    @Environment(\.playerPreview) private var preview

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: DesktopPlayerView.radius, style: .continuous)
        if maskRender {
            shape.fill(Color.black)
        } else if look.reduceTransparency {
            shape.fill(palette.deep.mixed(with: .init(0.06, 0.06, 0.07), 0.35).color)
                .overlay(shape.strokeBorder(.white.opacity(look.increaseContrast ? 0.6 : 0.2), lineWidth: 1))
        } else {
            ZStack {
                glass
                Color.black.opacity(0.24)
                LinearGradient(colors: [palette.accentColor.opacity(palette.fromArtwork ? 0.42 : 0.3),
                                        palette.deepColor.opacity(palette.fromArtwork ? 0.8 : 0.85)],
                               startPoint: .topLeading, endPoint: .bottomTrailing)
                LinearGradient(colors: [.white.opacity(0.10), .clear], startPoint: .top, endPoint: .center)
            }
            .clipShape(shape)
            .overlay(shape.strokeBorder(LinearGradient(colors: [.white.opacity(look.increaseContrast ? 0.7 : 0.45),
                                                                .white.opacity(look.increaseContrast ? 0.35 : 0.06),
                                                                .white.opacity(look.increaseContrast ? 0.5 : 0.18)],
                                                       startPoint: .topLeading, endPoint: .bottomTrailing),
                                        lineWidth: look.increaseContrast ? 1.5 : 1))
            .shadow(color: .black.opacity(0.35), radius: 24, y: 10)
        }
    }

    /// The system material; a preview, which can't draw it, shows the
    /// scene behind blurred instead.
    @ViewBuilder
    private var glass: some View {
        if let backdrop = preview?.backdrop {
            GeometryReader { proxy in
                let frame = proxy.frame(in: .named(PlayerPreview.space))
                backdrop
                    .offset(x: -frame.minX, y: -frame.minY)
                    .blur(radius: 30, opaque: true)
                    .saturation(1.4)
            }
        } else {
            Rectangle().fill(.ultraThinMaterial)
        }
    }
}

/// The record peeking out of the sleeve, its label in the album's colours.
struct SleeveRecord: View {
    let size: CGFloat
    let palette: ArtworkPalette

    var body: some View {
        ZStack {
            Circle().fill(RadialGradient(colors: [Color(white: 0.17), Color(white: 0.03)], center: .center,
                                         startRadius: 0, endRadius: size / 2))
            ForEach(Array(stride(from: size * 0.2, to: size * 0.49, by: 3.5)), id: \.self) { radius in
                Circle().stroke(Color.white.opacity(0.05), lineWidth: 0.6).frame(width: radius * 2, height: radius * 2)
            }
            Circle().fill(AngularGradient(colors: [.clear, .white.opacity(0.13), .clear, .white.opacity(0.09), .clear],
                                          center: .center))
            Circle().fill(LinearGradient(colors: [palette.accentColor, palette.deepColor], startPoint: .top, endPoint: .bottom))
                .frame(width: size * 0.33, height: size * 0.33)
            Circle().fill(Color(white: 0.05)).frame(width: 6, height: 6)
        }
        .frame(width: size, height: size)
        .shadow(color: .black.opacity(0.4), radius: 6, x: 3)
    }
}

/// A short note over the card about the song just picked: that it plays
/// alone and why, or that it could not be played. Long enough to read.
struct PlayerNoteToast: View {
    let note: String?
    /// Bumped when the note should show (once per note).
    let trigger: Int
    /// Previews: shown whenever there is a note.
    var alwaysShown = false
    @Environment(\.playerLook) private var look
    @State private var shown = false
    @State private var shownFor = 0

    /// Long enough to read the note: 2.5 s for a short one, up to 7 s.
    nonisolated static func noteSeconds(_ note: String?) -> Double {
        min(7, max(2.5, Double(note?.count ?? 0) / 14))
    }

    var body: some View {
        ZStack {
            if let note, shown || alwaysShown {
                Text(note).font(.system(size: 11, weight: .medium)).foregroundStyle(.white.opacity(0.92))
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 520)
                    .padding(.horizontal, 12).padding(.vertical, 5)
                    .playerCapsule(look)
                    .fixedSize(horizontal: false, vertical: true)
                    .transition(.opacity)
            }
        }
        .animation(.easeOut(duration: 0.3), value: shown)
        .allowsHitTesting(false)
        .onChange(of: trigger) { _, trigger in
            shown = true
            shownFor = trigger
            Task { @MainActor in
                try? await Task.sleep(nanoseconds: UInt64(Self.noteSeconds(note) * 1_000_000_000))
                // A newer note keeps its own time on screen.
                if shownFor == trigger { shown = false }
            }
        }
    }
}

/// The small × that closes the search or the queue (Esc does too).
struct PaneCloseButton: View {
    let label: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: "xmark").font(.system(size: 10, weight: .bold))
                .frame(width: 24, height: 24)
                .contentShape(Rectangle())
        }
        .buttonStyle(TransportButtonStyle())
        .foregroundStyle(.white.opacity(0.75))
        .help("\(label) (Esc)")
        .accessibilityLabel(label)
    }
}

// MARK: - Search

/// Which player the search talks to, as a segmented capsule. It only
/// steers the search: the transport and the queue follow what is playing.
struct PlayerPicker: View {
    let kinds: [PlayerKind]
    let selected: PlayerKind
    let onSelect: (PlayerKind) -> Void
    @Environment(\.playerLook) private var look

    var body: some View {
        HStack(spacing: 2) {
            ForEach(kinds) { kind in
                let on = kind == selected
                Button { onSelect(kind) } label: {
                    HStack(spacing: 5) {
                        Circle().fill(kind.tint).frame(width: 6, height: 6)
                        Text(kind.shortName).font(.system(size: 11, weight: .semibold)).lineLimit(1)
                    }
                    .foregroundStyle(on ? Color.black : .white.opacity(look.increaseContrast ? 0.9 : 0.72))
                    .padding(.horizontal, 10).padding(.vertical, 5)
                    .background(Capsule().fill(on ? Color.white : .clear))
                    .contentShape(Capsule())
                }
                .buttonStyle(.plain)
                .help(kind.isBrowser ? "Search \(kind.shortName) in \(kind.shortBrowserName)" : "Search and play in \(kind.shortName)")
                .accessibilityLabel("Search \(kind.shortName)")
                .accessibilityAddTraits(on ? .isSelected : [])
            }
        }
        .padding(3)
        .background(Capsule().fill(Color.black.opacity(0.25)))
        .overlay(Capsule().strokeBorder(.white.opacity(look.increaseContrast ? 0.4 : 0), lineWidth: 1))
        .fixedSize()
        .animation(.easeOut(duration: 0.2), value: selected)
    }
}

/// The search box: a magnifier, the text, a clear button; a white rim while
/// it has the keyboard. Return plays the selected result.
struct PlayerSearchField: View {
    @Binding var query: String
    var focused: FocusState<Bool>.Binding
    let player: PlayerKind
    /// Drawn as having the keyboard (a preview says so itself).
    let shownFocused: Bool
    let onSubmit: () -> Void
    @Environment(\.playerPreview) private var preview
    @Environment(\.playerLook) private var look

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: 10, style: .continuous)
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass").font(.system(size: 13)).foregroundStyle(.white.opacity(0.7))
            if preview != nil {
                HStack(spacing: 1) {
                    Text(query.isEmpty ? "Search \(player.shortName)" : query).font(.system(size: 15, weight: .medium))
                        .foregroundStyle(.white.opacity(query.isEmpty ? 0.45 : 1))
                    if shownFocused { Rectangle().fill(.white).frame(width: 1.5, height: 17) }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            } else {
                TextField("Search \(player.shortName)", text: $query)
                    .textFieldStyle(.plain).font(.system(size: 15, weight: .medium))
                    .focused(focused)
                    .onSubmit(onSubmit)
            }
            if !query.isEmpty {
                Button { query = "" } label: {
                    Image(systemName: "xmark.circle.fill").foregroundStyle(.white.opacity(0.5))
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Clear search")
            }
        }
        .padding(.horizontal, 12)
        .frame(height: 36)
        .background(shape.fill(Color.black.opacity(0.28)))
        .overlay(shape.strokeBorder(.white.opacity(shownFocused ? 0.85 : look.increaseContrast ? 0.6 : 0.25),
                                    lineWidth: shownFocused ? 2 : 1))
        .animation(.easeOut(duration: 0.15), value: shownFocused)
    }
}

/// The search results; the selected one (↑ ↓) is lit and plays on Return.
struct PlayerResultsList: View {
    let results: [PlayerSearchResult]
    let status: DesktopPlayerModel.Status
    let selection: Int
    let player: PlayerKind
    let onPlay: (PlayerSearchResult) -> Void
    @Environment(\.playerPreview) private var preview
    @Environment(\.playerMaskRender) private var maskRender
    @Environment(\.playerLook) private var look

    var body: some View {
        if maskRender {
            Color.clear
        } else if preview != nil {
            VStack(alignment: .leading, spacing: 2) { rows }
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
                .clipped()
        } else {
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 2) { rows }
                }
                .scrollIndicators(.never)
                .onChange(of: selection) { _, index in
                    guard results.indices.contains(index) else { return }
                    proxy.scrollTo(results[index].id)
                }
            }
        }
    }

    @ViewBuilder
    private var rows: some View {
        statusLine
        ForEach(Array(results.enumerated()), id: \.element.id) { index, result in
            row(result, selected: index == selection).id(result.id)
        }
    }

    @ViewBuilder
    private var statusLine: some View {
        switch status {
        case .searching: Text("Searching\u{2026}").font(.system(size: 12)).foregroundStyle(.white.opacity(0.65)).padding(8)
        case .message(let text): Text(text).font(.system(size: 12)).foregroundStyle(.white.opacity(0.75)).padding(8)
                .fixedSize(horizontal: false, vertical: true)
        case .idle: EmptyView()
        }
    }

    private func row(_ result: PlayerSearchResult, selected: Bool) -> some View {
        Button { onPlay(result) } label: {
            HStack(spacing: 10) {
                Group {
                    if let image = preview?.covers[result.id] {
                        Image(nsImage: image).resizable().aspectRatio(contentMode: .fill)
                    } else if preview != nil {
                        Color.white.opacity(0.1)
                    } else {
                        RemoteCover(url: result.artworkURL, symbol: result.kind == .album ? "square.stack" : "music.note",
                                    lookup: result.artworkURL == nil && player == .appleMusic ? (result.title, result.artist) : nil,
                                    persistentID: { if case .libraryTrack(let id) = result.target { return id } else { return nil } }())
                    }
                }
                .frame(width: 36, height: 36)
                .clipShape(RoundedRectangle(cornerRadius: 5, style: .continuous))
                VStack(alignment: .leading, spacing: 1) {
                    Text(result.title).font(.system(size: 13, weight: .semibold)).lineLimit(1)
                    Text(subtitle(result)).font(.system(size: 11))
                        .foregroundStyle(.white.opacity(look.increaseContrast ? 0.85 : 0.65)).lineLimit(1)
                }
                Spacer(minLength: 4)
                if let tag = tag(result) {
                    Text(tag).font(.system(size: 9, weight: .bold)).tracking(0.8)
                        .foregroundStyle(.white.opacity(look.increaseContrast ? 0.8 : 0.5))
                }
                if selected {
                    Image(systemName: "return").font(.system(size: 10, weight: .semibold))
                        .foregroundStyle(.white.opacity(0.8))
                        .frame(width: 22, height: 18)
                        .overlay(RoundedRectangle(cornerRadius: 4).strokeBorder(.white.opacity(0.45), lineWidth: 1))
                        .accessibilityHidden(true)
                }
            }
            .padding(6).contentShape(Rectangle())
            .background(selected ? Color.white.opacity(0.16) : Color.clear,
                        in: RoundedRectangle(cornerRadius: 10, style: .continuous))
        }
        .buttonStyle(RowButtonStyle())
        .accessibilityHint(selected ? "Selected. Return plays it" : "")
    }

    private func tag(_ result: PlayerSearchResult) -> String? {
        if result.inLibrary { return "LIBRARY" }
        return result.kind == .album ? "ALBUM" : nil
    }

    private func subtitle(_ result: PlayerSearchResult) -> String {
        if result.kind == .album || result.album.isEmpty || result.album == result.title { return result.artist }
        return "\(result.artist) \u{00B7} \(result.album)"
    }
}

// MARK: - Queue

/// The queue as a list: songs that played (faded), the one playing (lit),
/// and what comes next, each a click away. Opens on the song playing.
struct PlayerQueueList: View {
    let items: [PlayerQueueItem]
    /// The row a Next press moved to, shown as current until the player answers.
    var optimisticCurrent: String?
    let isPlaying: Bool
    /// The playing song's cover, for its row when the library has no id for it.
    var artwork: NSImage?
    /// Under the list: why there is nothing more (Spotify shares only the song playing).
    var footnote: String?
    let onTap: (PlayerQueueItem) -> Void
    @Environment(\.playerPreview) private var preview
    @Environment(\.playerMaskRender) private var maskRender
    @Environment(\.playerLook) private var look

    private var currentID: String? {
        if let optimisticCurrent, items.contains(where: { $0.id == optimisticCurrent }) { return optimisticCurrent }
        return items.first(where: \.isCurrent)?.id
    }

    var body: some View {
        if maskRender {
            Color.clear
        } else if preview != nil {
            VStack(alignment: .leading, spacing: 2) { rows }
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
                .clipped()
        } else {
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 2) { rows }
                }
                .scrollIndicators(.never)
                .task(id: currentID) {
                    // Laid out lazily: give it a moment before scrolling to the song playing.
                    try? await Task.sleep(nanoseconds: 60_000_000)
                    guard !Task.isCancelled, let currentID else { return }
                    withAnimation(look.reduceMotion ? nil : .smooth(duration: 0.3)) {
                        proxy.scrollTo(currentID, anchor: .center)
                    }
                }
            }
        }
    }

    @ViewBuilder
    private var rows: some View {
        let current = currentID.flatMap { id in items.firstIndex { $0.id == id } }
        ForEach(Array(items.enumerated()), id: \.element.id) { index, item in
            if index == 0, let current, current > 0 { section("Earlier") }
            if let current, index == current + 1 { section("Up next") }
            row(item, place: current.map { index < $0 ? .earlier : index == $0 ? .now : .next } ?? .next)
                .id(item.id)
        }
        if let footnote {
            Text(footnote).font(.system(size: 11)).foregroundStyle(.white.opacity(look.increaseContrast ? 0.85 : 0.6))
                .padding(.horizontal, 6).padding(.top, 6)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private enum Place { case earlier, now, next }

    private func section(_ title: String) -> some View {
        Text(title).font(.system(size: 10, weight: .semibold))
            .foregroundStyle(.white.opacity(look.increaseContrast ? 0.8 : 0.5))
            .padding(.top, 4).padding(.horizontal, 2)
            .accessibilityAddTraits(.isHeader)
    }

    private func row(_ item: PlayerQueueItem, place: Place) -> some View {
        let current = place == .now
        return Button { onTap(item) } label: {
            HStack(spacing: 10) {
                cover(item)
                    .frame(width: 30, height: 30)
                    .clipShape(RoundedRectangle(cornerRadius: 4, style: .continuous))
                VStack(alignment: .leading, spacing: 0) {
                    Text(item.title).font(.system(size: 12, weight: current ? .bold : .semibold)).lineLimit(1)
                    Text(item.artist).font(.system(size: 10))
                        .foregroundStyle(.white.opacity(look.increaseContrast ? 0.85 : 0.62)).lineLimit(1)
                }
                Spacer(minLength: 4)
                if current {
                    Image(systemName: isPlaying ? "waveform" : "pause.fill").font(.system(size: 11, weight: .bold))
                } else if item.index > 0 {
                    Text("\(item.index)").font(.system(size: 10).monospacedDigit()).foregroundStyle(.white.opacity(0.42))
                }
            }
            .padding(.horizontal, 6).padding(.vertical, 4)
            .contentShape(Rectangle())
            .background(current ? Color.white.opacity(0.16) : Color.clear,
                        in: RoundedRectangle(cornerRadius: 8, style: .continuous))
        }
        .buttonStyle(RowButtonStyle())
        .disabled(!item.isPlayable)
        .opacity(place == .earlier ? (look.increaseContrast ? 0.7 : 0.5) : item.isPlayable ? 1 : 0.6)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(item.artist.isEmpty ? item.title : "\(item.title) by \(item.artist)")
        .accessibilityHint(current ? "Now playing" : place == .earlier ? "Played earlier" : "Up next")
        .accessibilityAddTraits(.isButton)
        .accessibilityAction(named: current ? (isPlaying ? "Pause" : "Play") : "Play") { onTap(item) }
    }

    @ViewBuilder
    private func cover(_ item: PlayerQueueItem) -> some View {
        if let image = preview?.covers[item.id] {
            Image(nsImage: image).resizable().aspectRatio(contentMode: .fill)
        } else if item.id == currentID, item.persistentID == nil, let artwork {
            Image(nsImage: artwork).resizable().aspectRatio(contentMode: .fill)
        } else if preview != nil {
            Color.white.opacity(0.1)
        } else {
            RemoteCover(url: nil, symbol: "music.note", lookup: (item.title, item.artist), persistentID: item.persistentID)
        }
    }
}
