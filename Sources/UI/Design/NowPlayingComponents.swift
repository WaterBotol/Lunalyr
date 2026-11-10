import AppKit
import SwiftUI

// MARK: - Now playing
//
// The song, its cover, the music's transport and the line being sung, for
// the menu-bar panel, the Settings stage and onboarding:
//
//     TrackSummary()                       // cover, title, artist, from the models
//     TransportControls()                  // previous, play/pause, next
//     CurrentLyricLine()                   // the line being sung, kept current
//
// Each reads `NowPlayingMonitor` through `@Model(\.nowPlaying)`; the lower
// level views (`ArtworkView`, `LyricQuoteView`) take plain values, for
// previews and for drawing a fixed state.

/// An album cover, or a symbol where there is none. Decorative to
/// VoiceOver: the title beside it says what is playing. A new cover
/// cross-fades over the old one; with `placeholderDelay`, a cover that goes
/// missing (a song change, before the next cover is found) leaves only the
/// plain fill for that long before the symbol fades in, so a skip never
/// flashes the symbol between two covers.
struct ArtworkView: View {
    let image: NSImage?
    /// Shown without a cover: music.note, megaphone for an advert, radio
    /// waves for a stream (`TrackSummary.placeholderSymbol(for:)`).
    var placeholderSymbol = "music.note"
    var size: CGFloat = LunalyrDesign.Size.artworkLarge
    /// How long a cover that just went missing shows only the fill.
    var placeholderDelay: TimeInterval = 0
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var symbolVisible = true
    /// Bumped when the cover goes missing; starts the symbol's delay.
    @State private var missedAt = 0

    var body: some View {
        ZStack {
            Rectangle().fill(.quaternary)
            if image == nil, symbolVisible {
                Image(systemName: placeholderSymbol)
                    .font(.system(size: size * 0.36, weight: .medium))
                    .foregroundStyle(.secondary)
                    .transition(.opacity)
            }
            if let image {
                Image(nsImage: image).resizable().interpolation(.high).scaledToFill()
                    .id(ObjectIdentifier(image))
                    .transition(.opacity)
            }
        }
        .frame(width: size, height: size)
        .clipShape(RoundedRectangle(cornerRadius: radius, style: .continuous))
        // A light cover would otherwise melt into a light panel.
        .overlay(RoundedRectangle(cornerRadius: radius, style: .continuous)
            .strokeBorder(.primary.opacity(0.10), lineWidth: 0.5))
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion),
                   value: image.map(ObjectIdentifier.init))
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion),
                   value: symbolVisible)
        .onChange(of: image == nil) { _, missing in
            guard missing, placeholderDelay > 0 else { return }
            symbolVisible = false
            missedAt += 1
        }
        .task(id: missedAt) {
            guard missedAt > 0, !symbolVisible else { return }
            try? await Task.sleep(for: .seconds(placeholderDelay))
            guard !Task.isCancelled else { return }
            symbolVisible = true
        }
        .accessibilityHidden(true)
    }

    private var radius: CGFloat { size >= 48 ? LunalyrDesign.Radius.artwork : LunalyrDesign.Radius.artwork * 0.75 }
}

/// Cover, title and artist of the playing song, or Lunalyr's idle prompt.
/// One element to VoiceOver ("Golden Hour Static, Lunar Tides").
struct TrackSummary<Accessory: View>: View {
    var artworkSize: CGFloat = LunalyrDesign.Size.artworkLarge
    var titleLines = 2
    let accessory: Accessory
    @Model(\.nowPlaying) private var playing
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    init(artworkSize: CGFloat = LunalyrDesign.Size.artworkLarge, titleLines: Int = 2,
         @ViewBuilder accessory: () -> Accessory) {
        self.artworkSize = artworkSize
        self.titleLines = titleLines
        self.accessory = accessory()
    }

    var body: some View {
        let title = playing.current?.title ?? "Nothing playing"
        HStack(spacing: LunalyrDesign.Spacing.medium) {
            ArtworkView(image: playing.currentTrackArtwork, placeholderSymbol: Self.placeholderSymbol(for: playing.current),
                        size: artworkSize, placeholderDelay: LunalyrDesign.Motion.Hold.placeholderDelay)
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .lunalyrText(.emphasis)
                    .lineLimit(titleLines)
                    .fixedSize(horizontal: false, vertical: true)
                    .contentTransition(.opacity)
                Text(Self.subtitle(for: playing.current, idlePrompt: playing.idlePrompt))
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .contentTransition(.opacity)
            }
            // A song change cross-fades the words, as it does the cover.
            .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion),
                       value: title)
            .accessibilityElement(children: .combine)
            Spacer(minLength: 0)
            accessory
        }
    }

    /// The artist, or what kind of thing is playing, or the idle prompt.
    static func subtitle(for track: NowPlayingMonitor.Track?, idlePrompt: String) -> String {
        guard let track else { return idlePrompt }
        if track.isAdvert { return "Advertisement" }
        if !track.artist.isEmpty { return track.artist }
        return track.isStream ? "Live stream" : ""
    }

    /// Stands in for a missing cover.
    static func placeholderSymbol(for track: NowPlayingMonitor.Track?) -> String {
        guard let track else { return "music.note" }
        if track.isAdvert { return "megaphone" }
        return track.isStream ? "dot.radiowaves.left.and.right" : "music.note"
    }
}

extension TrackSummary where Accessory == EmptyView {
    init(artworkSize: CGFloat = LunalyrDesign.Size.artworkLarge, titleLines: Int = 2) {
        self.init(artworkSize: artworkSize, titleLines: titleLines) { EmptyView() }
    }
}

/// Previous, play or pause, next: the music's own transport, named for the
/// music so it can't be mistaken for pausing the wallpaper. Disabled, and
/// said so, while no player can be controlled.
struct TransportControls: View {
    enum Size {
        /// The menu-bar panel's and onboarding's.
        case regular
        /// Beside the Settings preview.
        case small
    }

    var size: Size
    @Model(\.nowPlaying) private var playing
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    /// Play or pause as pressed, shown at once while the player answers;
    /// cleared when it does, or after `pendingTimeout`.
    @State private var pending: Pending?

    struct Pending: Equatable {
        var playing: Bool
        var id: Int
    }

    init(size: Size = .regular) {
        self.size = size
    }

    /// How long a press shows its result before the player has confirmed it.
    static let pendingTimeout: TimeInterval = 1.5

    var body: some View {
        let waiting = playing.awaitsConsent
        let enabled = playing.canControlPlayback && !waiting
        let shownPlaying = Self.shownPlaying(actual: playing.isPlaying, pending: pending?.playing)
        HStack(spacing: size == .regular ? LunalyrDesign.Spacing.medium : LunalyrDesign.Spacing.xSmall) {
            button("backward.fill", title: "Previous track") { playing.previousTrack() }
            button(shownPlaying ? "pause.fill" : "play.fill", title: Self.title(playing: shownPlaying),
                   prominent: true) {
                pending = Pending(playing: !shownPlaying, id: (pending?.id ?? 0) + 1)
                playing.playPause()
            }
            button("forward.fill", title: "Next track") { playing.nextTrack() }
        }
        // Primary, not the accent; dimmed as AppKit dims a disabled control.
        .foregroundStyle(enabled ? AnyShapeStyle(.primary) : AnyShapeStyle(Color(nsColor: .disabledControlTextColor)))
        .disabled(!enabled)
        .help(waiting ? Self.consentHelp : "")
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion),
                   value: shownPlaying)
        .onChange(of: playing.isPlaying) { _, _ in pending = nil }
        .task(id: pending) {
            guard pending != nil else { return }
            try? await Task.sleep(for: .seconds(Self.pendingTimeout))
            guard !Task.isCancelled else { return }
            pending = nil
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Music playback")
    }

    /// "Play music" / "Pause music".
    static func title(playing: Bool) -> String { playing ? "Pause music" : "Play music" }

    /// Said while macOS's Automation prompt is up.
    static let consentHelp = "Waiting for you to answer macOS\u{2019}s permission prompt"

    /// What the play/pause button shows: a press at once, then the player.
    static func shownPlaying(actual: Bool, pending: Bool?) -> Bool { pending ?? actual }

    private func button(_ symbol: String, title: String, prominent: Bool = false,
                        action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(prominent ? (size == .regular ? .title2 : .title3) : (size == .regular ? .title3 : .body))
                .contentTransition(LunalyrDesign.Motion.symbolSwap(reduceMotion: reduceMotion))
                .frame(width: LunalyrDesign.Size.minimumHitTarget + (prominent ? 8 : 0),
                       height: LunalyrDesign.Size.minimumHitTarget)
                .contentShape(Rectangle())
        }
        .buttonStyle(.borderless)
        .help(title)
        .accessibilityLabel(title)
    }
}

/// The line being sung, with the next one faint beneath it, for a value
/// that is already known. `CurrentLyricLine` keeps one current.
struct LyricQuoteView: View {
    let now: LyricNow
    /// Whether anything is playing; without it the view says so.
    var hasTrack: Bool
    /// The source and timing ("LRCLIB · Word by word") under the lines.
    var showsSource: Bool
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    init(now: LyricNow, hasTrack: Bool = true, showsSource: Bool = true) {
        self.now = now
        self.hasTrack = hasTrack
        self.showsSource = showsSource
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Group {
                if let line = now.line {
                    Text(line)
                        .lunalyrText(.lyric)
                        .lineLimit(2)
                        .id(now.index)
                        .transition(Self.lineTransition(reduceMotion: reduceMotion))
                } else {
                    Text(placeholder)
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .lineLimit(2)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            if now.line != nil, let next = now.next {
                Text(next)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            if showsSource, let source = now.source {
                Text([source, now.precision].compactMap { $0 }.joined(separator: " · "))
                    .lunalyrText(.footnote)
                    .lineLimit(1)
            }
        }
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion),
                   value: now.index)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Current lyric")
        .accessibilityValue(now.line ?? placeholder)
        .accessibilityAddTraits(.updatesFrequently)
    }

    /// A new line rises 8 pt into place as it fades in; a fade under Reduce
    /// Motion. The panel's lyric block uses the same.
    static func lineTransition(reduceMotion: Bool) -> AnyTransition {
        reduceMotion ? .opacity : .asymmetric(insertion: .offset(y: 8).combined(with: .opacity), removal: .opacity)
    }

    /// What shows between lines and without lyrics.
    var placeholder: String {
        guard hasTrack else { return "Lyrics appear here while a song plays." }
        if now.isLoading { return "Looking for lyrics…" }
        if now.notFound { return "No synced lyrics for this song." }
        return now.source == nil ? "Waiting for lyrics…" : "♪"
    }
}

/// `LyricQuoteView` for the song playing now, refreshed a few times a second
/// while the music plays, from `LunalyrModels.lyricNow` (the app's lyrics,
/// global and song offsets, readings shown in place of the lyrics).
struct CurrentLyricLine: View {
    var showsSource: Bool
    @Environment(\.lunalyrModels) private var models
    @Model(\.nowPlaying) private var playing
    @Model(\.lyrics) private var lyrics

    init(showsSource: Bool = true) {
        self.showsSource = showsSource
    }

    var body: some View {
        let models = models.resolved
        TimelineView(.periodic(from: .now, by: playing.isPlaying ? 0.25 : 30)) { _ in
            LyricQuoteView(now: models.lyricNow(models), hasTrack: playing.current != nil, showsSource: showsSource)
        }
    }
}
