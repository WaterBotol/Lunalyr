import AppKit
import SwiftUI

private typealias P = LunalyrDesign.Player

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
        let shape = RoundedRectangle(cornerRadius: P.Radius.card, style: .continuous)
        if maskRender {
            shape.fill(Color.black)
        } else if look.reduceTransparency {
            shape.fill(palette.deep.mixed(with: .init(0.06, 0.06, 0.07), 0.35).color)
                .overlay(shape.strokeBorder(P.Line.hairline(look), lineWidth: P.Line.width))
        } else {
            // The material, a veil so white type always reads, and the album's
            // colour as one flat tint (no gradient in chrome). One shadow; an
            // edge only under Increase Contrast.
            ZStack {
                glass
                Color.black.opacity(P.Fill.veil)
                wash.opacity(P.Fill.tint)
            }
            .clipShape(shape)
            .overlay(shape.strokeBorder(P.Line.hairline(look), lineWidth: P.Line.width))
            .shadow(color: P.Shadow.cardColor, radius: P.Shadow.cardRadius, y: P.Shadow.cardY)
        }
    }

    /// The card's tint: the deep shade with a little of the accent in it.
    private var wash: Color { palette.cardTint.color }

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
            Circle().fill(AngularGradient(colors: [.clear, .white.opacity(0.07), .clear, .white.opacity(0.05), .clear],
                                          center: .center))
            Circle().fill(palette.accentColor)
                .frame(width: size * 0.33, height: size * 0.33)
            Circle().fill(Color(white: 0.05)).frame(width: 6, height: 6)
        }
        .frame(width: size, height: size)
        .shadow(color: P.Shadow.coverColor, radius: 6, x: 3)
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
                Text(note).font(P.TypeScale.note).foregroundStyle(P.Ink.primary.color(look))
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 520)
                    .padding(.horizontal, 12).padding(.vertical, 6)
                    .playerCapsule(look)
                    .fixedSize(horizontal: false, vertical: true)
                    .transition(.opacity)
            }
        }
        .animation(shown ? P.Motion.feedback : P.Motion.feedbackOut, value: shown)
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
    @Environment(\.playerLook) private var look

    var body: some View {
        Button(action: action) {
            Image(systemName: "xmark").font(P.Glyph.icon)
                .frame(width: P.Glyph.button, height: P.Glyph.button)
                .contentShape(Rectangle())
        }
        .buttonStyle(TransportButtonStyle())
        .foregroundStyle(P.Ink.secondary.color(look))
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
                        Text(kind.shortName).font(P.TypeScale.label).lineLimit(1)
                    }
                    .foregroundStyle(on ? Color.black : P.Ink.secondary.color(look))
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
        .background(Capsule().fill(Color.black.opacity(P.Fill.well)))
        .overlay(Capsule().strokeBorder(P.Line.hairline(look), lineWidth: P.Line.width))
        .fixedSize()
        .animation(P.Motion.feedback, value: selected)
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
        let shape = RoundedRectangle(cornerRadius: P.Radius.control, style: .continuous)
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass").font(P.Glyph.icon).foregroundStyle(P.Ink.tertiary.color(look))
            if preview != nil {
                HStack(spacing: 1) {
                    Text(query.isEmpty ? "Search \(player.shortName)" : query).font(P.TypeScale.field)
                        .foregroundStyle((query.isEmpty ? P.Ink.tertiary : P.Ink.primary).color(look))
                    if shownFocused { Rectangle().fill(.white).frame(width: 1.5, height: 17) }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            } else {
                TextField("Search \(player.shortName)", text: $query)
                    .textFieldStyle(.plain).font(P.TypeScale.field)
                    .focused(focused)
                    .onSubmit(onSubmit)
            }
            if !query.isEmpty {
                Button { query = "" } label: {
                    Image(systemName: "xmark.circle.fill").foregroundStyle(P.Ink.tertiary.color(look))
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Clear search")
            }
        }
        .padding(.horizontal, 12)
        .frame(height: 36)
        .background(shape.fill(Color.black.opacity(P.Fill.well)))
        .overlay(shape.strokeBorder(shownFocused ? P.Line.focus(look) : P.Line.hairline(look),
                                    lineWidth: shownFocused ? P.Line.focusWidth : P.Line.width))
        .animation(P.Motion.feedback, value: shownFocused)
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
        case .searching: Text("Searching\u{2026}").font(P.TypeScale.meta).foregroundStyle(P.Ink.tertiary.color(look)).padding(8)
        case .message(let text): Text(text).font(P.TypeScale.meta).foregroundStyle(P.Ink.secondary.color(look)).padding(8)
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
                        Color.white.opacity(P.Fill.placeholder)
                    } else {
                        RemoteCover(url: result.artworkURL, symbol: result.kind == .album ? "square.stack" : "music.note",
                                    lookup: result.artworkURL == nil && player == .appleMusic ? (result.title, result.artist) : nil,
                                    persistentID: { if case .libraryTrack(let id) = result.target { return id } else { return nil } }())
                    }
                }
                .frame(width: 36, height: 36)
                .clipShape(RoundedRectangle(cornerRadius: P.Radius.thumb, style: .continuous))
                VStack(alignment: .leading, spacing: 1) {
                    Text(result.title).font(P.TypeScale.rowTitle).lineLimit(1)
                    Text(subtitle(result)).font(P.TypeScale.rowSubtitle)
                        .foregroundStyle(P.Ink.secondary.color(look)).lineLimit(1)
                }
                Spacer(minLength: 4)
                if let tag = tag(result) {
                    Text(tag).font(P.TypeScale.tag)
                        .foregroundStyle(P.Ink.tertiary.color(look))
                }
                if selected {
                    Image(systemName: "return").font(P.TypeScale.tag)
                        .foregroundStyle(P.Ink.secondary.color(look))
                        .frame(width: 22, height: 18)
                        .accessibilityHidden(true)
                }
            }
            .padding(6).contentShape(Rectangle())
            .background(selected ? Color.white.opacity(P.Fill.selected) : Color.clear,
                        in: RoundedRectangle(cornerRadius: P.Radius.row, style: .continuous))
        }
        .buttonStyle(RowButtonStyle())
        .accessibilityHint(selected ? "Selected. Return plays it" : "")
    }

    private func tag(_ result: PlayerSearchResult) -> String? {
        if result.inLibrary { return "Library" }
        return result.kind == .album ? "Album" : nil
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
                    withAnimation(look.reduceMotion ? nil : LunalyrDesign.Motion.standard) {
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
            Text(footnote).font(P.TypeScale.rowSubtitle).foregroundStyle(P.Ink.tertiary.color(look))
                .padding(.horizontal, 6).padding(.top, 6)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private enum Place { case earlier, now, next }

    private func section(_ title: String) -> some View {
        Text(title).font(P.TypeScale.tag)
            .foregroundStyle(P.Ink.tertiary.color(look))
            .padding(.top, 4).padding(.horizontal, 6)
            .accessibilityAddTraits(.isHeader)
    }

    private func row(_ item: PlayerQueueItem, place: Place) -> some View {
        let current = place == .now
        return Button { onTap(item) } label: {
            HStack(spacing: 10) {
                cover(item)
                    .frame(width: 30, height: 30)
                    .clipShape(RoundedRectangle(cornerRadius: P.Radius.thumb, style: .continuous))
                VStack(alignment: .leading, spacing: 0) {
                    Text(item.title).font(P.TypeScale.rowTitle).lineLimit(1)
                    Text(item.artist).font(P.TypeScale.rowSubtitle)
                        .foregroundStyle(P.Ink.secondary.color(look)).lineLimit(1)
                }
                Spacer(minLength: 4)
                if current {
                    Image(systemName: isPlaying ? "waveform" : "pause.fill").font(P.TypeScale.tag)
                } else if item.index > 0 {
                    Text("\(item.index)").font(P.TypeScale.time).foregroundStyle(P.Ink.tertiary.color(look))
                }
            }
            .padding(.horizontal, 6).padding(.vertical, 4)
            .contentShape(Rectangle())
            .background(current ? Color.white.opacity(P.Fill.selected) : Color.clear,
                        in: RoundedRectangle(cornerRadius: P.Radius.row, style: .continuous))
        }
        .buttonStyle(RowButtonStyle())
        .disabled(!item.isPlayable)
        .opacity(place == .earlier ? (look.increaseContrast ? 0.75 : 0.55) : item.isPlayable ? 1 : 0.6)
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
            Color.white.opacity(P.Fill.placeholder)
        } else {
            RemoteCover(url: nil, symbol: "music.note", lookup: (item.title, item.artist), persistentID: item.persistentID)
        }
    }
}
