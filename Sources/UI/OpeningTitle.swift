import AppKit
import SwiftUI

// MARK: - The song's opening title
//
// When a song starts, its title, artist and album are set on the wallpaper
// like a music video's title card: low on the left, clear of the lyric, in
// the lyric's own typeface. It arrives with the song, holds through the
// intro and has gone a second before the first line is sung. Everything is
// a pure function of the song's time, so a seek or a pause shows the same
// frame for the same moment.

enum OpeningTitle {
    static let settingKey = "custom.openingTitle"

    static var isOn: Bool { UserDefaults.standard.object(forKey: settingKey) as? Bool ?? true }

    /// The album cover beside the title.
    static let coverKey = "custom.openingTitleCover"

    static var showsCover: Bool { UserDefaults.standard.object(forKey: coverKey) as? Bool ?? true }

    /// The cover to draw: the wallpaper's own artwork for the song, when the
    /// setting is on and there is one. Nil keeps the text where it is.
    static func cover(_ artwork: NSImage?, enabled: Bool) -> NSImage? {
        guard enabled, let artwork, artwork.size.width > 0, artwork.size.height > 0 else { return nil }
        return artwork
    }

    // Type sizes, as shares of the title's own size (before a long title is
    // set smaller), so the hierarchy holds at every size.
    static let artistShare: CGFloat = 0.32
    static let readingShare: CGFloat = 0.32
    static let albumShare: CGFloat = 0.26

    /// How much of the card is drawn.
    enum Size: Equatable {
        /// Rule, title, its reading, artist and album.
        case full
        /// A smaller title and the artist: a short intro, or a song joined
        /// part-way.
        case compact
    }

    /// What the stage knows about the song's words.
    enum Words: Equatable {
        /// Timed lyrics; the start of the first sung line, nil if none is sung.
        case timed(firstLine: Double?)
        /// Nothing to sing along to (yet): looking lyrics up, none found, an
        /// instrumental. The card holds longer.
        case none
        /// Words already on screen from the start (unsynced lyrics): only a
        /// brief, compact card.
        case showing
    }

    /// When the card enters, settles, starts leaving and is gone, in song time.
    struct Schedule: Equatable {
        var start: Double
        var settled: Double
        var leave: Double
        var end: Double
        var size: Size
    }

    // Timing (seconds of song time).

    /// The beat after the song starts before the card does.
    static let lead = 0.5
    /// A song first seen later than this was joined part-way.
    static let midSong = 2.5
    /// Joined part-way: the card starts this long after.
    static let joinLead = 0.3
    /// Entrances: the full card's reveal is staged over this long.
    static let fullEntrance = 1.6
    static let compactEntrance = 0.8
    /// Exits are quicker than entrances.
    static let fullExit = 0.7
    static let compactExit = 0.45
    /// The shortest hold worth arriving for.
    static let fullMinimumHold = 1.2
    static let compactMinimumHold = 0.7
    /// The longest the card holds through a long intro, and with no words.
    static let lyricHold = 8.0
    static let wordlessHold = 11.0
    /// A song joined part-way, or with words already showing.
    static let briefHold = 2.6
    /// Gone at least this long before the first line starts to appear.
    static let clearance = 1.0

    /// When the first line may first be drawn: its own start, less the
    /// next-word preview lead and the line's attack.
    static func deadline(firstLine: Double, previewLead: Double) -> Double {
        firstLine - max(clearance, previewLead + 0.6)
    }

    /// The card's timing for a song first seen at `joined` seconds in, or
    /// nil when there is no room for it.
    static func schedule(words: Words, joined: Double = 0, previewLead: Double = 0.45) -> Schedule? {
        let partWay = joined > midSong
        let start = partWay ? joined + joinLead : lead
        func card(_ size: Size, leave: Double) -> Schedule {
            let entrance = size == .full ? fullEntrance : compactEntrance
            let exit = size == .full ? fullExit : compactExit
            return Schedule(start: start, settled: start + entrance, leave: leave, end: leave + exit, size: size)
        }
        let brief = card(.compact, leave: start + compactEntrance + briefHold)
        switch words {
        case .showing:
            return brief
        case .none, .timed(firstLine: nil):
            return partWay ? brief : card(.full, leave: start + fullEntrance + wordlessHold)
        case .timed(firstLine: let first?):
            let gone = deadline(firstLine: first, previewLead: previewLead)
            let room = gone - start
            if room >= fullEntrance + fullMinimumHold + fullExit {
                return card(.full, leave: min(gone - fullExit, start + fullEntrance + lyricHold))
            }
            if room >= compactEntrance + compactMinimumHold + compactExit {
                return card(.compact, leave: gone - compactExit)
            }
            // The words are already under way: joined part-way, a moment's
            // reminder; at the very start, nothing.
            return partWay && first < start ? brief : nil
        }
    }

    /// The start of the first line that is sung (credits and breaks are not).
    static func firstSung(_ lines: [LyricsFetcher.Line]) -> Double? {
        lines.first { !$0.isInstrumental && $0.credit != true }?.start
    }

    /// Where the card is at `time`: nil before it starts and once it has gone.
    struct Phase: Equatable {
        /// Seconds since the card started entering.
        var age: Double
        /// 0 while held, rising to 1 as it leaves.
        var exit: Double
        var size: Size

        /// Moving: entering or leaving. Held, nothing on it changes.
        var moving: Bool
    }

    static func phase(_ schedule: Schedule?, at time: Double) -> Phase? {
        guard let schedule, time >= schedule.start, time < schedule.end else { return nil }
        let exit = time <= schedule.leave ? 0 : (time - schedule.leave) / (schedule.end - schedule.leave)
        return Phase(age: time - schedule.start, exit: min(1, max(0, exit)), size: schedule.size,
                     moving: time < schedule.settled || time > schedule.leave)
    }

    /// 0...1: how much of something else naming the song (the loading
    /// notice) may show: none while the card is up, fading back after.
    static func yields(to schedule: Schedule?, at time: Double) -> Double {
        guard let schedule else { return 1 }
        return Easing.smoothstep((time - schedule.end) / 0.5)
    }

    /// The card's phase for the stage's clock: as `phase`, but a card due
    /// within the second counts as moving, so a resting clock is awake for it.
    static func upcoming(_ schedule: Schedule?, at time: Double) -> Phase? {
        guard let schedule, time < schedule.start else { return phase(schedule, at: time) }
        guard time >= schedule.start - 1 else { return nil }
        return Phase(age: 0, exit: 0, size: schedule.size, moving: true)
    }

    /// The stage's clock while the card is up: every frame while it moves,
    /// and never stopped while it is held, so its exit is drawn.
    static func pace(_ level: LyricPace.Level, phase: Phase?, playing: Bool) -> LyricPace.Level {
        guard let phase else { return level }
        if phase.moving { return playing ? .active : (level == .resting ? .waiting : level) }
        return level == .resting ? (playing ? .still : .waiting) : level
    }

    // MARK: Motion

    /// How far each part of the card has arrived, 0...1, `age` seconds in.
    /// Reduce Motion fades the whole card in at once.
    struct Reveal: Equatable {
        var rule: Double
        /// The cover: arrives a beat before the title's wipe.
        var cover: Double
        var title: Double
        /// How far the soft wipe has uncovered the title, left to right.
        var wipe: Double
        var reading: Double
        var artist: Double
        var album: Double

        static func at(_ age: Double, size: Size, reduceMotion: Bool) -> Reveal {
            if reduceMotion {
                let fade = Easing.smoothstep(age / 0.6)
                return Reveal(rule: fade, cover: fade, title: fade, wipe: 1, reading: fade, artist: fade, album: fade)
            }
            // Staged: the rule draws in, the title is wiped on beside it,
            // then the reading, the artist and the album follow.
            let scale = size == .full ? 1.0 : compactEntrance / fullEntrance
            func part(_ from: Double, _ span: Double) -> Double {
                Easing.settle(age - from * scale, span * scale)
            }
            // The wipe travels at an even, eased pace; everything else settles.
            let wipe = Easing.smoothstep((age - 0.1 * scale) / (1.2 * scale))
            return Reveal(rule: part(0, 0.9), cover: part(0, 0.55), title: part(0.12, 1.15), wipe: wipe, reading: part(0.5, 0.9),
                          artist: part(0.45, 1.1), album: part(0.75, 0.85))
        }
    }

    /// 1 for a title of up to 24 characters, down to 0.7 for a long one.
    static func lengthScale(_ title: String) -> CGFloat {
        CGFloat(min(1, max(0.7, 24 / Double(max(1, title.count)))))
    }

    /// The title's line on its own: the album, unless it only repeats the
    /// title ("Song - Single").
    static func albumLine(album: String, title: String) -> String? {
        var name = album.trimmingCharacters(in: .whitespacesAndNewlines)
        for suffix in [" - Single", " - EP", " – Single", " – EP"] where name.hasSuffix(suffix) {
            name = String(name.dropLast(suffix.count))
        }
        guard !name.isEmpty, LyricQuery.normalize(name) != LyricQuery.normalize(title) else { return nil }
        return album.trimmingCharacters(in: .whitespacesAndNewlines)
    }
}

/// Per song: when Lunalyr first saw it (so a song joined part-way gets only
/// a brief card), and the title's reading, worked out once. Never publishes.
@MainActor
final class OpeningTitleMemory: ObservableObject {
    private var track: Int?
    private var joinedAt = 0.0
    private var readingKey: String?
    private var reading: String?

    /// The song time at which `track` was first seen: 0 for a song seen from
    /// its start, or once playback is back at its start.
    func joined(track: Int, at time: Double) -> Double {
        if track != self.track {
            self.track = track
            joinedAt = time
        }
        if time < OpeningTitle.midSong { joinedAt = 0 }
        return joinedAt
    }

    /// The title's romanisation when readings are shown and it needs one.
    func reading(of title: String, options: RomanizationOptions.Snapshot) -> String? {
        guard options.display != .off, LyricRomanizer.containsNonLatin(title) else { return nil }
        let key = "\(title)\u{1}\(options.language.rawValue)\u{1}\(options.toneMarks)"
        if key != readingKey {
            readingKey = key
            let text = LyricRomanizer.romanize(title, language: options.language, toneMarks: options.toneMarks)
            reading = text == title || text.isEmpty ? nil : OpeningTitleMemory.capitalised(text)
        }
        return reading
    }

    /// "yoru ni kakeru" as a title: "Yoru ni Kakeru". Short particles stay lower case.
    static func capitalised(_ text: String) -> String {
        let small: Set<String> = ["ni", "no", "wo", "o", "wa", "ga", "to", "de", "e", "he", "mo", "ka", "ya", "yo", "ne"]
        return text.split(separator: " ", omittingEmptySubsequences: false).enumerated().map { index, word in
            let lower = word.lowercased()
            if index > 0, small.contains(lower) { return lower }
            return lower.prefix(1).uppercased() + lower.dropFirst()
        }.joined(separator: " ")
    }
}

/// The card itself.
struct OpeningTitleCard: View {
    let phase: OpeningTitle.Phase
    let title: String
    let artist: String
    let album: String?
    let reading: String?
    /// The album cover, or nil for none.
    var cover: NSImage? = nil
    /// The lyric's base size on this screen.
    let baseSize: CGFloat
    let accent: Color
    let mood: TrackMood
    /// The floating lyrics: centred in the window, compact, no scrim of its own.
    var floating = false
    @ObservedObject private var settings = Settings.shared
    @Environment(\.lyricReduceMotion) private var reduceMotion
    @Environment(\.lyricInk) private var ink

    var body: some View {
        GeometryReader { geometry in
            let size = geometry.size
            let compact = floating || phase.size == .compact
            let reveal = OpeningTitle.Reveal.at(phase.age, size: compact ? .compact : .full, reduceMotion: reduceMotion)
            let leaving = Easing.smoothstep(phase.exit)
            let unit = floating ? baseSize * 0.95 : (compact ? baseSize * 0.8 : baseSize)
            let dark = Self.isDark(ink.base.rest)
            let block = card(unit: unit, compact: compact, reveal: reveal, dark: dark,
                             width: floating ? size.width * 0.9 : size.width * 0.46)
                .opacity(1 - leaving)
                .blur(radius: reduceMotion ? 0 : leaving * unit * 0.06)
                .offset(y: reduceMotion ? 0 : -leaving * unit * 0.14)
            if floating {
                block.frame(width: size.width, height: size.height)
            } else {
                ZStack(alignment: .bottomLeading) {
                    // Legible over a bright or busy video: the picture
                    // darkens softly towards the corner the card is in, like
                    // a film's grade, rather than a patch behind the words.
                    let shade = dark ? Color.white : .black
                    EllipticalGradient(stops: [.init(color: shade.opacity(0.4), location: 0),
                                               .init(color: shade.opacity(0.28), location: 0.3),
                                               .init(color: shade.opacity(0.1), location: 0.65),
                                               .init(color: shade.opacity(0), location: 1)],
                                       center: UnitPoint(x: 0.2, y: 0.8), startRadiusFraction: 0, endRadiusFraction: 0.5)
                        .opacity(max(reveal.title, reveal.rule) * (1 - leaving))
                    block
                        .padding(.leading, max(40, size.width * 0.058))
                        .padding(.bottom, max(96, size.height * 0.13))
                }
                .frame(width: size.width, height: size.height, alignment: .bottomLeading)
            }
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }

    /// Dark type, for a lyric ink chosen against a light backdrop.
    static func isDark(_ colour: Color) -> Bool {
        guard let rgb = NSColor(colour).usingColorSpace(.sRGB) else { return false }
        let luminance = 0.2126 * rgb.redComponent + 0.7152 * rgb.greenComponent + 0.0722 * rgb.blueComponent
        return luminance < 0.4
    }

    @ViewBuilder
    private func card(unit: CGFloat, compact: Bool, reveal: OpeningTitle.Reveal, dark: Bool, width: CGFloat) -> some View {
        let type: Color = dark ? Color(white: 0.08) : .white
        // The title's size; the lines under it take shares of it. A long
        // title is set smaller, but they are not.
        let nominal = unit * (compact ? 0.72 : 0.92)
        let titleSize = nominal * OpeningTitle.lengthScale(title)
        let artistSize = nominal * OpeningTitle.artistShare
        let readingSize = nominal * OpeningTitle.readingShare
        let albumSize = nominal * OpeningTitle.albumShare
        let showsReading = reading != nil && !compact
        let ruleHeight = max(2, unit * 0.04), ruleGap = unit * (compact ? 0.18 : 0.22)
        let readingTop = nominal * 0.1, artistTop = nominal * (compact ? 0.2 : 0.26)
        // The cover is as tall as the title block: the title, its reading
        // and the artist.
        let titleLines: CGFloat = OpeningTitle.lengthScale(title) < 1 ? 2 : 1
        let side = titleLines * titleSize * 1.2 + (showsReading ? readingTop + readingSize * 1.25 : 0)
            + artistTop + artistSize * 1.2
        let gap = side * 0.16
        let columnWidth = cover == nil ? width : max(width * 0.4, width - side - gap)
        // Centred in the floating window without a cover; beside one, the
        // words line up against it.
        let centred = floating && cover == nil
        let rise = reduceMotion ? 0 : unit * 0.32
        HStack(alignment: .top, spacing: gap) {
            if let cover {
                Image(nsImage: cover)
                    .resizable()
                    .interpolation(.high)
                    .aspectRatio(contentMode: .fill)
                    .frame(width: side, height: side)
                    .clipShape(RoundedRectangle(cornerRadius: side * 0.07, style: .continuous))
                    .shadow(color: .black.opacity(0.45), radius: side * 0.08, y: side * 0.03)
                    .scaleEffect(reduceMotion ? 1 : 0.9 + 0.1 * reveal.cover, anchor: .bottomLeading)
                    .opacity(reveal.cover)
                    .padding(.top, ruleHeight + ruleGap)
            }
            CappedWidth(maximum: columnWidth) {
            VStack(alignment: centred ? .center : .leading, spacing: 0) {
                Capsule()
                    .fill(accent)
                    .frame(width: unit * (compact ? 0.7 : 1.0), height: ruleHeight)
                    .scaleEffect(x: reduceMotion ? 1 : reveal.rule, y: 1, anchor: centred ? .center : .leading)
                    .opacity(reveal.rule)
                    .padding(.bottom, ruleGap)
                Group {
                    Text(title)
                        .font(settings.lyricFont(size: titleSize, hero: true, mood: mood))
                        .foregroundStyle(type)
                        .multilineTextAlignment(centred ? .center : .leading)
                        .lineLimit(2)
                        .minimumScaleFactor(0.55)
                        .fixedSize(horizontal: false, vertical: true)
                        .mask { wipe(reveal.wipe) }
                        .opacity(min(1, reveal.title * 1.6))
                        .offset(y: (1 - reveal.title) * rise)
                    if let reading, showsReading {
                        Text(reading)
                            .font(settings.uiFont(size: readingSize, mood: mood))
                            .foregroundStyle(type.opacity(0.78))
                            .lineLimit(1)
                            .minimumScaleFactor(0.6)
                                .padding(.top, readingTop)
                            .opacity(reveal.reading)
                            .offset(y: (1 - reveal.reading) * rise * 0.5)
                    }
                    if !artist.isEmpty {
                        // In the artist's own case, as the menu bar and player show it.
                        Text(artist)
                            .font(settings.uiFont(size: artistSize, mood: mood))
                            .foregroundStyle(type.opacity(0.92))
                            .lineLimit(1)
                            .minimumScaleFactor(0.6)
                                .padding(.top, artistTop)
                            .opacity(reveal.artist)
                    }
                    if let album, !compact {
                        Text(album)
                            .font(settings.uiFont(size: albumSize, mood: mood))
                            .kerning(albumSize * 0.02)
                            .foregroundStyle(type.opacity(0.7))
                            .lineLimit(1)
                            .minimumScaleFactor(0.6)
                                .padding(.top, nominal * 0.1)
                            .opacity(reveal.album)
                    }
                }
                .compositingGroup()
                // A soft shadow under the type, as the lyrics have.
                .shadow(color: (dark ? Color.white : .black).opacity(0.45), radius: unit * 0.12, y: unit * 0.015)
            }
            }
        }
    }

    /// A soft left-to-right wipe: `progress` 0 hides everything, 1 shows it all.
    private func wipe(_ progress: Double) -> some View {
        if reduceMotion { return LinearGradient(colors: [.black], startPoint: .leading, endPoint: .trailing) }
        let soft = 0.25
        let edge = -soft + (1 + soft) * progress
        return LinearGradient(stops: [.init(color: .black, location: max(0, edge)),
                                      .init(color: .clear, location: min(1, max(0.0001, edge + soft)))],
                              startPoint: .leading, endPoint: .trailing)
    }
}

/// Offers its content at most `maximum` points of width and takes the size
/// the content uses, so a short title's card hugs its words (and centres
/// with its cover in the floating window) while a long one still wraps.
struct CappedWidth: Layout {
    let maximum: CGFloat

    private func proposal(_ proposal: ProposedViewSize) -> ProposedViewSize {
        ProposedViewSize(width: min(proposal.width ?? maximum, maximum), height: proposal.height)
    }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        subviews.first?.sizeThatFits(self.proposal(proposal)) ?? .zero
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        subviews.first?.place(at: bounds.origin, proposal: self.proposal(proposal))
    }
}
