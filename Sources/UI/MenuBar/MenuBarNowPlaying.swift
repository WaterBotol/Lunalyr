import SwiftUI

/// The top of the menu-bar panel (SPEC.md section 7, item 1-2): the cover,
/// title and artist with the music's transport at the right; the song's
/// progress; then the line being sung, large, with the next line beneath.
/// Nothing playing: a music note in the cover's place, "Nothing playing"
/// and the players followed, the transport dimmed, no progress, and a
/// sentence in place of the line, so the panel barely changes height.
struct MenuBarNowPlaying: View {
    @Model(\.nowPlaying) private var playing
    @Environment(\.lunalyrModels) private var models
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        let track = playing.current
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 10) {
                ArtworkView(image: playing.currentTrackArtwork, placeholderSymbol: TrackSummary<EmptyView>.placeholderSymbol(for: track),
                            size: 40, placeholderDelay: LunalyrDesign.Motion.Hold.placeholderDelay, cornerRadius: 5,
                            placeholderFill: Color.menuPanel(.fill))
                VStack(alignment: .leading, spacing: 1) {
                    Text(track?.title ?? "Nothing playing")
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(Color.studio(.ink))
                        .lineLimit(1)
                        .contentTransition(.opacity)
                    Text(subtitle(track))
                        .font(.system(size: 12))
                        .foregroundStyle(Color.menuPanel(.secondary))
                        .lineLimit(1)
                        .contentTransition(.opacity)
                }
                .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion),
                           value: track?.title)
                .accessibilityElement(children: .combine)
                Spacer(minLength: 8)
                TransportControls(size: .panel)
            }
            if let track, track.duration > 0 {
                MenuBarProgress(duration: track.duration)
            }
            MenuBarLyricLine()
                .padding(.top, 2)
        }
        .padding(.horizontal, MenuPanel.inset)
        .padding(.top, 8)
        .padding(.bottom, 6)
    }

    private func subtitle(_ track: NowPlayingMonitor.Track?) -> String {
        guard track != nil else {
            let installed = Set(models.resolved.installedPlayers)
            let followed = playing.followedPlayers.filter { installed.contains($0) }
            return Self.followingLine(followed.isEmpty ? playing.followedPlayers : followed)
        }
        return TrackSummary<EmptyView>.subtitle(for: track, idlePrompt: playing.idlePrompt)
    }

    /// "Following Music and Spotify": the players Lunalyr follows, browsers
    /// as "web players".
    static func followingLine(_ kinds: [PlayerKind]) -> String {
        var names = kinds.filter { !$0.isBrowser }.map(\.displayName)
        if kinds.contains(where: \.isBrowser) { names.append("web players") }
        guard let last = names.popLast() else { return "Following no players" }
        guard !names.isEmpty else { return "Following \(last)" }
        let head = names.joined(separator: ", ")
        return "Following \(head) and \(last)"
    }
}

/// Elapsed and total time either side of a 3-point bar, read once a second
/// while the music plays.
struct MenuBarProgress: View {
    let duration: Double
    @Model(\.nowPlaying) private var playing

    var body: some View {
        TimelineView(.periodic(from: .now, by: playing.isPlaying ? 1 : 30)) { _ in
            let elapsed = min(max(playing.playbackPosition(), 0), duration)
            HStack(spacing: 8) {
                Text(Self.time(elapsed))
                GeometryReader { proxy in
                    ZStack(alignment: .leading) {
                        Capsule().fill(Color.studio(.track).opacity(1.4))
                        Capsule().fill(Color.studio(.ink))
                            .frame(width: proxy.size.width * Self.fraction(elapsed, of: duration))
                    }
                }
                .frame(height: 3)
                Text(Self.time(duration))
            }
            .font(.system(size: 11).monospacedDigit())
            .foregroundStyle(Color.menuPanel(.secondary))
            .accessibilityElement(children: .ignore)
            .accessibilityLabel("Song position")
            .accessibilityValue("\(Self.time(elapsed)) of \(Self.time(duration))")
        }
    }

    /// "1:23", "12:05", "1:02:03".
    static func time(_ seconds: Double) -> String {
        let total = Int(max(seconds, 0).rounded(.down))
        let hours = total / 3600, minutes = (total % 3600) / 60, rest = total % 60
        if hours > 0 { return String(format: "%d:%02d:%02d", hours, minutes, rest) }
        return String(format: "%d:%02d", minutes, rest)
    }

    static func fraction(_ elapsed: Double, of duration: Double) -> CGFloat {
        guard duration > 0 else { return 0 }
        return CGFloat(min(max(elapsed / duration, 0), 1))
    }
}

/// The line being sung, from `LunalyrModels.lyricNow` like
/// `CurrentLyricLine`, in a block that keeps one height while a song is
/// current, whatever it shows: a line that wraps drops the next line
/// instead of growing, and a note between lines, "Looking for lyrics…" or
/// hidden lyrics sit in the same space. The panel's window sizes itself to
/// its content, so a block that grew and shrank with each line would move
/// every row below it, under the pointer, several times a minute. Idle, the
/// spec's sentence takes its place.
///
/// Hidden lyrics, by the person or for a mirrored display, never show here
/// (the menu-bar line says "Lyrics hidden" too), so presenting can't put them
/// on the shared screen.
struct MenuBarLyricLine: View {
    @Environment(\.lunalyrModels) private var models
    @Model(\.nowPlaying) private var playing
    /// Observed so new lyrics and their source show at once.
    @Model(\.lyrics) private var lyrics
    @Model(\.extras) private var extras

    enum Display: Equatable {
        /// Nothing playing: the spec's sentence.
        case prompt
        /// A song, its lyrics hidden.
        case hidden
        /// A song: the line being sung, or what stands in for it.
        case line
    }

    static func display(hasTrack: Bool, lyricsVisible: Bool) -> Display {
        guard hasTrack else { return .prompt }
        return lyricsVisible ? .line : .hidden
    }

    static let hiddenText = MenuBarLyricTicker.hiddenText
    static let idlePrompt = "Play a song and its lyrics show here and on your desktop."

    var body: some View {
        switch Self.display(hasTrack: playing.current != nil, lyricsVisible: extras.lyricsVisible) {
        case .prompt:
            Text(Self.idlePrompt)
                .font(.system(size: 13))
                .foregroundStyle(Color.menuPanel(.secondary))
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: .infinity, alignment: .leading)
                .accessibilityLabel("Current lyric")
                .accessibilityValue(Self.idlePrompt)
        case .hidden:
            // The source and precision still show on hover: they reveal no lyrics.
            let models = models.resolved
            MenuBarLyricBlock(source: MenuBarLyricQuote.source(of: models.lyricNow(models))) {
                Label(Self.hiddenText, systemImage: "eye.slash")
                    .font(.system(size: 13))
                    .foregroundStyle(Color.menuPanel(.secondary))
            }
            .accessibilityElement(children: .ignore)
            .accessibilityLabel("Current lyric")
            .accessibilityValue(Self.hiddenText)
        case .line:
            let models = models.resolved
            TimelineView(.periodic(from: .now, by: playing.isPlaying ? 0.25 : 30)) { _ in
                MenuBarLyricQuote(now: models.lyricNow(models))
            }
        }
    }
}

/// A `LyricNow` in the panel's fixed block: the line (two rows at most), the
/// next line beneath it when the line takes one row. Between lines a note
/// stands in, with the line to come beneath it. The words while looking and
/// without lyrics are `LyricQuoteView`'s own. Where the lyrics came from
/// ("LRCLIB · Word by word") is the block's tooltip and VoiceOver hint.
struct MenuBarLyricQuote: View {
    let now: LyricNow
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    /// What the block shows.
    enum Content: Equatable {
        case line(String, next: String?)
        /// An instrumental break or a pause in the voice, with the line to come.
        case between(next: String)
        /// "Looking for lyrics…", "No synced lyrics for this song.", "♪".
        case placeholder
    }

    static func content(of now: LyricNow) -> Content {
        if let line = now.line { return .line(line, next: now.next) }
        if now.source != nil, let next = now.next { return .between(next: next) }
        return .placeholder
    }

    var body: some View {
        let placeholder = LyricQuoteView(now: now).placeholder
        let content = Self.content(of: now)
        let source = Self.source(of: now)
        MenuBarLyricBlock(source: source) {
            // Old and new lines overlap while one gives way to the other.
            ZStack(alignment: .topLeading) {
                switch content {
                case .line(let line, let next):
                    ViewThatFits(in: .vertical) {
                        VStack(alignment: .leading, spacing: MenuBarLyricBlock<EmptyView>.rowSpacing) {
                            lineText(line)
                            if let next { nextText(next) }
                        }
                        lineText(line)
                    }
                    // By index, so a line sung twice in a row ("yeah / yeah")
                    // still rolls in as a new line.
                    .id(now.index ?? -1)
                    .transition(transition)
                case .between(let next):
                    VStack(alignment: .leading, spacing: MenuBarLyricBlock<EmptyView>.rowSpacing) {
                        Text(verbatim: "♪")
                            .font(MenuBarLyricBlock<EmptyView>.lineFont)
                            .foregroundStyle(Color.menuPanel(.secondary))
                        nextText(next)
                    }
                    .transition(transition)
                case .placeholder:
                    Text(placeholder)
                        .font(MenuBarLyricBlock<EmptyView>.nextFont)
                        .foregroundStyle(Color.menuPanel(.secondary))
                        .lineLimit(2)
                }
            }
        }
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion),
                   value: AnimationKey(content: content, index: now.index))
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Current lyric")
        .accessibilityValue(Self.spoken(content, placeholder: placeholder))
        .accessibilityHint(source.map { "From \($0)" } ?? "")
        .accessibilityAddTraits(.updatesFrequently)
    }

    /// What a change animates on: the content, and the line's index for a
    /// line repeated word for word.
    private struct AnimationKey: Equatable {
        let content: Content
        let index: Int?
    }

    /// Moves up into place; a fade under Reduce Motion.
    private var transition: AnyTransition {
        LyricQuoteView.lineTransition(reduceMotion: reduceMotion)
    }

    private func lineText(_ line: String) -> some View {
        Text(line)
            .font(MenuBarLyricBlock<EmptyView>.lineFont)
            .foregroundStyle(Color.studio(.ink))
            .lineLimit(2)
            .fixedSize(horizontal: false, vertical: true)
    }

    private func nextText(_ next: String) -> some View {
        Text(next)
            .font(MenuBarLyricBlock<EmptyView>.nextFont)
            .foregroundStyle(Color.menuPanel(.secondary))
            .lineLimit(1)
    }

    /// What VoiceOver reads as the value.
    static func spoken(_ content: Content, placeholder: String) -> String {
        switch content {
        case .line(let line, _): return line
        case .between(let next): return "Between lines. Next: \(next)"
        case .placeholder: return placeholder
        }
    }

    /// "LRCLIB · Word by word", once lyrics are showing.
    static func source(of now: LyricNow) -> String? {
        now.source.map { [$0, now.precision].compactMap { $0 }.joined(separator: " · ") }
    }
}

/// Two lyric rows, measured in the fonts they are set in (the line 16
/// semibold, the next line 13), so the block keeps one height; `lines`
/// fills it from the top.
struct MenuBarLyricBlock<Lines: View>: View {
    let source: String?
    let lines: Lines

    static var rowSpacing: CGFloat { 3 }
    static var lineFont: Font { .system(size: 16, weight: .semibold) }
    static var nextFont: Font { .system(size: 13) }

    init(source: String?, @ViewBuilder lines: () -> Lines) {
        self.source = source
        self.lines = lines()
    }

    var body: some View {
        template
            .hidden()
            .overlay(alignment: .topLeading) {
                lines
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            }
            .help(source.map { "Lyrics from \($0)" } ?? "")
    }

    /// The tallest the block's text gets: a line over two rows, or a line
    /// and the next one.
    private var template: some View {
        ZStack(alignment: .topLeading) {
            Text(verbatim: "Ag\nAg").font(Self.lineFont)
            VStack(alignment: .leading, spacing: Self.rowSpacing) {
                Text(verbatim: "Ag").font(Self.lineFont)
                Text(verbatim: "Ag").font(Self.nextFont)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityHidden(true)
    }
}
