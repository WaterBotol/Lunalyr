import SwiftUI

/// The top of the menu-bar panel: the song (or the idle prompt), the music's
/// own transport centred beneath it, the line being sung with its source and
/// precision, and -- right under the line it moves -- this song's lyric
/// timing. Idle, the prompt, a dimmed transport, where the lyrics will
/// appear and the (disabled) timing all stay in place, at the height they
/// have with a song, so nothing jumps when a song starts or stops.
struct MenuBarNowPlaying: View {
    var body: some View {
        VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.small) {
            TrackSummary()
            TransportControls()
                .frame(maxWidth: .infinity)
            MenuBarLyricLine()
            MenuBarLyricTiming()
                .padding(.top, LunalyrDesign.Spacing.hairline)
        }
    }
}

/// This song's lyrics earlier or later, a tenth of a second a click: the
/// per-song, per-source offset the shortcuts and Timing & sources change too.
struct MenuBarLyricTiming: View {
    @ObservedObject private var tapSync = LyricTapSyncController.shared
    @Model(\.offsets) private var offsets
    @Model(\.nowPlaying) private var playing

    var body: some View {
        VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.xSmall) {
            PanelSectionHeader("Lyric timing") {
                SongTimingNudge()
            }
            // Tap to sync: a key pressed as each line starts sets the timing.
            HStack(spacing: 6) {
                Spacer(minLength: 0)
                Button(tapSync.isListening ? "Stop tap to sync" : "Tap to sync…") { tapSync.toggle() }
                    .help("Press any key each time a lyric line starts; Lunalyr sets this song’s timing from the taps")
                if tapSync.canUndo {
                    Button("Undo tap sync") { tapSync.undo() }
                        .help("Back to the timing from before the last tap to sync")
                }
            }
            .controlSize(.small)
            // Belongs to a song: shown, but disabled, with nothing playing.
            .disabled(playing.current == nil && !tapSync.isListening)
        }
    }
}

/// The line being sung, with its source and precision ("LRCLIB · Word by
/// word"), from `LunalyrModels.lyricNow` like `CurrentLyricLine`, but in a
/// block that keeps one height while a song is current, whatever it shows: a
/// line that wraps drops the next line instead of growing, and a note between
/// lines, "Looking for lyrics…" or hidden lyrics sit in the same space. The
/// panel's window sizes itself to its content, so a block that grew and
/// shrank with each line would move every control below it, under the
/// pointer, several times a minute -- and Hide lyrics would jump away from the
/// click that pressed it. Idle, the prompt sits in the same block.
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
        /// Nothing playing: "Lyrics appear here while a song plays."
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

    var body: some View {
        switch Self.display(hasTrack: playing.current != nil, lyricsVisible: extras.lyricsVisible) {
        case .prompt:
            let prompt = LyricQuoteView(now: .none, hasTrack: false).placeholder
            MenuBarLyricBlock(source: nil, centred: true) {
                Text(prompt)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
            }
            .accessibilityElement(children: .ignore)
            .accessibilityLabel("Current lyric")
            .accessibilityValue(prompt)
        case .hidden:
            // The source and precision still show: they reveal no lyrics.
            let models = models.resolved
            MenuBarLyricBlock(source: MenuBarLyricQuote.source(of: models.lyricNow(models)), centred: true) {
                Label(Self.hiddenText, systemImage: "eye.slash")
                    .font(.callout)
                    .foregroundStyle(.secondary)
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
/// next line beneath it when the line takes one row, the source at the foot.
/// Between lines a note stands in, with the line to come beneath it. The
/// words while looking and without lyrics are `LyricQuoteView`'s own.
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
        MenuBarLyricBlock(source: Self.source(of: now), centred: content == .placeholder) {
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
                            .lunalyrText(.lyric)
                            .foregroundStyle(.secondary)
                        nextText(next)
                    }
                    .transition(transition)
                case .placeholder:
                    Text(placeholder)
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .lineLimit(2)
                }
            }
        }
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion),
                   value: AnimationKey(content: content, index: now.index))
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Current lyric")
        .accessibilityValue(Self.spoken(content, placeholder: placeholder))
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
            .lunalyrText(.lyric)
            .lineLimit(2)
            .fixedSize(horizontal: false, vertical: true)
    }

    private func nextText(_ next: String) -> some View {
        Text(next)
            .font(.callout)
            .foregroundStyle(.secondary)
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

/// Two lyric rows and a source row, measured in the fonts they are set in so
/// the block follows the text size; `lines` fills what the source leaves.
/// Stand-ins (`centred`) sit in the middle of the space they have.
struct MenuBarLyricBlock<Lines: View>: View {
    let source: String?
    let centred: Bool
    let lines: Lines

    static var rowSpacing: CGFloat { LunalyrDesign.Spacing.xSmall }

    init(source: String?, centred: Bool, @ViewBuilder lines: () -> Lines) {
        self.source = source
        self.centred = centred
        self.lines = lines()
    }

    var body: some View {
        template
            .hidden()
            .overlay(alignment: .topLeading) {
                VStack(alignment: .leading, spacing: Self.rowSpacing) {
                    lines
                        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: centred ? .leading : .topLeading)
                    if let source {
                        Text(source)
                            .lunalyrText(.footnote)
                            .lineLimit(1)
                    }
                }
            }
    }

    /// The tallest the block's text gets: a line over two rows, or a line
    /// and the next one, then the source.
    private var template: some View {
        VStack(alignment: .leading, spacing: Self.rowSpacing) {
            ZStack(alignment: .topLeading) {
                Text(verbatim: "Ag\nAg").lunalyrText(.lyric)
                VStack(alignment: .leading, spacing: Self.rowSpacing) {
                    Text(verbatim: "Ag").lunalyrText(.lyric)
                    Text(verbatim: "Ag").font(.callout)
                }
            }
            Text(verbatim: "Ag").lunalyrText(.footnote)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityHidden(true)
    }
}
