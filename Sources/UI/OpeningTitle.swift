import AppKit
import SwiftUI

// MARK: - The song's opening title
//
// When a song starts, its title, artist and album are set on the wallpaper
// like a music video's title card: low on the left, clear of the lyric, in
// the lyric's own typeface. It arrives with the song, holds through the
// intro and has gone a second before the first line is sung. When the
// lookup finds no lyrics at all, it stays for the rest of the song, resting
// at a lower strength after half a minute (`staying`). Everything is
// a pure function of the song's time, so a seek or a pause shows the same
// frame for the same moment.

enum OpeningTitle {
    static let settingKey = "custom.openingTitle"

    static var isOn: Bool { UserDefaults.standard.object(forKey: settingKey) as? Bool ?? true }

    /// Keep the song title on screen: the card enters as usual (at the
    /// song's start, or when Lunalyr joins it part-way) and stays for the
    /// whole song, settling to `restOpacity` once it would have left, so
    /// there is always a name for what is playing. Low on the left beside
    /// the lyrics on the desktop; a compact row above them in the floating
    /// window. Testers asked for it (Nov 2026). The key predates the
    /// setting's present meaning ("show it in full mid-song too").
    static let alwaysKey = "custom.openingTitleAlways"

    static var keeps: Bool { UserDefaults.standard.bool(forKey: alwaysKey) }

    /// The album cover beside the title.
    static let coverKey = "custom.openingTitleCover"

    static var showsCover: Bool { UserDefaults.standard.object(forKey: coverKey) as? Bool ?? true }

    /// The cover to draw: the wallpaper's own artwork for the song, when the
    /// setting is on and there is one. Nil keeps the text where it is.
    static func cover(_ artwork: NSImage?, enabled: Bool) -> NSImage? {
        guard enabled, let artwork, artwork.size.width > 0, artwork.size.height > 0 else { return nil }
        return artwork
    }

    // MARK: Where it sits

    /// Where the card sits on the desktop and how large, as set by dragging
    /// it on the Settings picture: its lower-left corner as shares of the
    /// screen (so it lands in the same place on every display), and a
    /// scale. Nil `corner`: the default place, low on the left.
    struct Placement: Equatable {
        /// The corner's two shares, stored apart (`@AppStorage` reads them).
        static let leftKey = "custom.openingTitleLeft"
        static let liftKey = "custom.openingTitleLift"
        static let scaleKey = "custom.openingTitleScale"
        static let scales: ClosedRange<Double> = 0.6...1.6

        /// Left edge and distance up from the bottom, as shares of the screen.
        var corner: CGPoint?
        var scale = 1.0

        static let standard = Placement()
        var isStandard: Bool { corner == nil && abs(scale - 1) < 0.001 }

        static func stored(_ defaults: UserDefaults = .standard) -> Placement {
            var placement = Placement()
            if let left = defaults.object(forKey: leftKey) as? Double, let lift = defaults.object(forKey: liftKey) as? Double,
               left.isFinite, lift.isFinite {
                placement.corner = CGPoint(x: left, y: lift)
            }
            if let scale = defaults.object(forKey: scaleKey) as? Double, scale.isFinite {
                placement.scale = min(scales.upperBound, max(scales.lowerBound, scale))
            }
            return placement
        }

        func store(_ defaults: UserDefaults = .standard) {
            if let corner {
                defaults.set(Double(corner.x), forKey: Self.leftKey)
                defaults.set(Double(corner.y), forKey: Self.liftKey)
            } else {
                defaults.removeObject(forKey: Self.leftKey)
                defaults.removeObject(forKey: Self.liftKey)
            }
            if abs(scale - 1) < 0.001 { defaults.removeObject(forKey: Self.scaleKey) }
            else { defaults.set(min(Self.scales.upperBound, max(Self.scales.lowerBound, scale)), forKey: Self.scaleKey) }
        }

        static func reset(_ defaults: UserDefaults = .standard) {
            defaults.removeObject(forKey: leftKey)
            defaults.removeObject(forKey: liftKey)
            defaults.removeObject(forKey: scaleKey)
        }
    }

    /// Kept clear of the screen's edges (and the menu bar, at the top).
    static let edgeMargin: CGFloat = 16
    static let topMargin: CGFloat = 40

    /// The card's box on a desktop of `size` (origin top left): as wide as
    /// a title may run, and as tall as a full card with a cover, at its
    /// scale; its lower-left corner where the user put it, kept on screen.
    static func box(in size: CGSize, baseSize: CGFloat, placement: Placement) -> CGRect {
        let scale = CGFloat(min(Placement.scales.upperBound, max(Placement.scales.lowerBound, placement.scale)))
        let width = min(size.width * 0.46 * scale, max(1, size.width - 2 * edgeMargin))
        let height = min(baseSize * scale * 2.3, max(1, size.height - edgeMargin - topMargin))
        let leading = placement.corner.map { $0.x * size.width } ?? max(40, size.width * 0.058)
        let bottom = placement.corner.map { $0.y * size.height } ?? max(96, size.height * 0.13)
        let x = min(max(edgeMargin, leading), max(edgeMargin, size.width - width - edgeMargin))
        let lift = min(max(edgeMargin, bottom), max(edgeMargin, size.height - height - topMargin))
        return CGRect(x: x, y: size.height - lift - height, width: width, height: height)
    }

    /// The placement that puts the card's box at `box` on a desktop of `size`.
    static func placement(for box: CGRect, in size: CGSize, scale: Double) -> Placement {
        Placement(corner: CGPoint(x: box.minX / max(1, size.width), y: (size.height - box.maxY) / max(1, size.height)),
                  scale: min(Placement.scales.upperBound, max(Placement.scales.lowerBound, scale)))
    }

    /// The animated cover the card plays for `track`: the album's own,
    /// whatever the Animated covers switch says (testers asked for it, Nov
    /// 2026), unless the album is one the user left out of animated covers.
    @MainActor
    static func motionCover(_ motion: MotionArtworkProvider.Motion?, track: NowPlayingMonitor.Track?,
                            settings: Settings) -> URL? {
        guard let motion, isOn, showsCover, !settings.excludesMotion(artist: track?.artist, album: track?.album) else {
            return nil
        }
        return motion.square
    }

    /// Whether the card wants the album's animated cover looked up, apart
    /// from the wallpaper's own Animated covers.
    static var wantsMotionCover: Bool { isOn && showsCover }

    // Type sizes, as shares of the title's own size (before a long title is
    // set smaller), so the hierarchy holds at every size.
    static let artistShare: CGFloat = 0.32
    /// How strongly the card glows in the album's colour: fully over a dark
    /// picture, gently when the type is dark on a light one (a glow there
    /// would only muddy the words).
    static func glowStrength(dark: Bool) -> Double { dark ? 0.35 : 1 }
    static let readingShare: CGFloat = 0.32
    static let albumShare: CGFloat = 0.26
    /// The cover's side, as a share of the title's nominal size: the same
    /// for every title, so the card keeps its size whether the title takes
    /// one line or two.
    static func coverShare(compact: Bool) -> CGFloat { compact ? 1.65 : 2.4 }

    /// The title's sizes beside a cover, largest first: it takes the
    /// largest whose two lines fit beside the cover.
    static let titleSteps: [CGFloat] = [1, 0.84, 0.7, 0.58]
    /// The title face's ascent (its first baseline below the line's top),
    /// as a share of its size: with `capHeightShare`, where its capitals start.
    static let ascentShare: CGFloat = 0.95

    /// How long a card keeps an empty place for a cover still being
    /// fetched; after that, the words take the room.
    static let coverWait = 8.0
    static func reservesCover(age: Double) -> Bool { age < coverWait }

    /// The title face's cap height, as a share of its size, for lining the
    /// cover up with the title's capitals.
    static let capHeightShare: CGFloat = 0.7

    /// The card's spacing, one scale in steps of the title's nominal size:
    /// `line` between lines that belong together (the artist and album),
    /// `reading` under the title, `group` between the title and the
    /// details, and between the cover and the words.
    struct Spacing {
        let nominal: CGFloat
        var line: CGFloat { nominal * 0.08 }
        var reading: CGFloat { nominal * 0.12 }
        var group: CGFloat { nominal * 0.24 }
    }

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
        /// No lyrics were found: the card stays, resting after `restAfter`.
        /// Its leave and end are infinite until lyrics arrive (`leaving`).
        var stays = false
        /// Kept on screen for the whole song (`keeps`): it stays, and only
        /// a new song ends it.
        var kept = false
        /// A kept card: where the ordinary card would have left and been
        /// gone (`start` when there would have been none). It rests from
        /// `ordinaryLeave`, and what waits for the card (the credits)
        /// waits for `ordinaryEnd` instead of for ever.
        var ordinaryLeave: Double? = nil
        var ordinaryEnd: Double? = nil

        /// When what waits for the card to go may start.
        var clears: Double { ordinaryEnd ?? end }
        /// When a staying card starts to ease to rest.
        var restsFrom: Double { ordinaryLeave ?? settled + OpeningTitle.restAfter }
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
    /// A card that stays (no lyrics found) rests: held this long past
    /// settling, it eases over `restEase` to `restOpacity` and stays there.
    static let restAfter = 30.0
    static let restEase = 2.0
    static let restOpacity = 0.55

    /// When the first line may first be drawn: its own start, less the
    /// next-word preview lead and the line's attack.
    static func deadline(firstLine: Double, previewLead: Double) -> Double {
        firstLine - max(clearance, previewLead + 0.6)
    }

    /// The card's timing for a song first seen at `joined` seconds in, or
    /// nil when there is no room for it.
    static func schedule(words: Words, joined: Double = 0, previewLead: Double = 0.45,
                         keep: Bool = false) -> Schedule? {
        let partWay = joined > midSong
        let start = partWay ? joined + joinLead : lead
        if keep {
            // The full card, from the usual moment, for the whole song.
            let ordinary = schedule(words: words, joined: joined, previewLead: previewLead)
            let settled = start + fullEntrance
            return Schedule(start: start, settled: settled, leave: .infinity, end: .infinity, size: .full, stays: true,
                            kept: true, ordinaryLeave: max(settled, ordinary?.leave ?? settled),
                            ordinaryEnd: ordinary?.end ?? start)
        }
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

    /// The card for a song whose lookup found no lyrics, seen at song time
    /// `found`: it enters as usual (full, or compact for a song joined
    /// part-way) and stays for the rest of the song. When the lookup ran on
    /// after the card it held had begun to leave, it enters afresh.
    static func staying(joined: Double = 0, found: Double = 0) -> Schedule {
        let partWay = joined > midSong
        var start = partWay ? joined + joinLead : lead
        if let held = schedule(words: .none, joined: joined), found > held.leave { start = found + joinLead }
        let size: Size = partWay ? .compact : .full
        let entrance = size == .full ? fullEntrance : compactEntrance
        return Schedule(start: start, settled: start + entrance, leave: .infinity, end: .infinity, size: size, stays: true)
    }

    /// A staying card once lyrics arrive at song time `arrival`: it leaves
    /// from then, or once it has settled, with its ordinary exit.
    static func leaving(_ schedule: Schedule, at arrival: Double) -> Schedule {
        guard schedule.leave == .infinity, !schedule.kept else { return schedule }
        var card = schedule
        card.leave = max(arrival, schedule.settled)
        card.end = card.leave + (schedule.size == .full ? fullExit : compactExit)
        return card
    }

    /// A card that entered while the lyrics were being looked up, once the
    /// lookup ended at song time `arrival` and `next` is the card the song
    /// has now. The card on screen carries on instead of a second one
    /// crossfading in over it: it is `next` when the two share their start
    /// and size and `next` has not begun to leave. Otherwise it keeps its
    /// own entrance and size and leaves to be gone when `next` would be,
    /// never starting to leave before `arrival` (its exit quickened, down to
    /// the compact one's, to fit); with no card due (the first line is
    /// close) it leaves at once, as quickly as the loading scene used to
    /// dissolve. A card already on its way out keeps its own exit.
    static func carried(_ card: Schedule, into next: Schedule?, at arrival: Double) -> Schedule {
        if let next, next.start == card.start, next.size == card.size, arrival <= next.leave { return next }
        var carried = card
        carried.stays = false
        let exit = card.size == .full ? fullExit : compactExit
        let leave = min(card.leave, next.map { max(arrival, $0.end - exit) } ?? arrival)
        guard leave < card.leave else { return carried }
        carried.leave = leave
        carried.end = leave + (next.map { max(compactExit, min(exit, $0.end - leave)) } ?? compactExit)
        return carried
    }

    /// How far a staying card has eased to rest at `time`, 0...1.
    static func rest(_ schedule: Schedule, at time: Double) -> Double {
        guard schedule.stays else { return 0 }
        return Easing.smoothstep((time - schedule.restsFrom) / restEase)
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

        /// Moving: entering, easing to rest or leaving. Held, nothing on it changes.
        var moving: Bool
        /// 0 at full strength, 1 rested at `restOpacity` (a staying card).
        var rest: Double = 0
        /// Staying with no leave due: only an input (lyrics arriving) ends it.
        var stays = false
        /// Kept for the whole song (`Schedule.kept`).
        var kept = false

        /// The card's strength before its exit.
        var opacity: Double { 1 - (1 - OpeningTitle.restOpacity) * rest }
    }

    static func phase(_ schedule: Schedule?, at time: Double) -> Phase? {
        guard let schedule, time >= schedule.start, time < schedule.end else { return nil }
        let exit = time <= schedule.leave ? 0 : (time - schedule.leave) / (schedule.end - schedule.leave)
        let restStart = schedule.restsFrom
        let resting = schedule.stays && time >= restStart && time <= restStart + restEase
        return Phase(age: time - schedule.start, exit: min(1, max(0, exit)), size: schedule.size,
                     moving: time < schedule.settled || time > schedule.leave || resting,
                     rest: rest(schedule, at: time), stays: schedule.leave == .infinity, kept: schedule.kept)
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
    /// and never stopped while it is held, so its exit is drawn. A card that
    /// stays with no exit due is only looked at, for its ease to rest.
    static func pace(_ level: LyricPace.Level, phase: Phase?, playing: Bool) -> LyricPace.Level {
        guard let phase else { return level }
        if phase.moving { return playing ? .active : (level == .resting ? .waiting : level) }
        return level == .resting ? (playing && !phase.stays ? .still : .waiting) : level
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

    // The floating window's kept title: one row above the lyrics.
    /// Its margin from the window's top, the cover's side and its type, as
    /// shares of the lyric size.
    static let rowMargin: CGFloat = 0.18
    static let rowCover: CGFloat = 0.62
    static let rowTitle: CGFloat = 0.38
    static let rowArtist: CGFloat = 0.3
    /// The height kept clear for the row: its margin, the cover, and a gap
    /// before the lyrics.
    static func rowBand(baseSize: CGFloat, scale: Double = 1) -> CGFloat {
        baseSize * (rowMargin + (rowCover + 0.16) * CGFloat(RowPlacement.clamped(scale)))
    }

    /// Where the floating window's title row sits, as set by dragging it on
    /// the Settings picture (Floating over apps): across the window (its
    /// centre, as a share of the window's width), at the window's top or
    /// bottom edge (the lyrics make room on that side, so it never covers
    /// them), and a scale.
    struct RowPlacement: Equatable {
        static let centreKey = "custom.floatingTitleCentre"
        static let edgeKey = "custom.floatingTitleEdge"
        static let scaleKey = "custom.floatingTitleScale"
        static let scales: ClosedRange<Double> = 0.6...1.6

        var centre = 0.5
        var bottom = false
        var scale = 1.0

        static let standard = RowPlacement()
        var isStandard: Bool { abs(centre - 0.5) < 0.001 && !bottom && abs(scale - 1) < 0.001 }

        static func clamped(_ scale: Double) -> Double {
            scale.isFinite ? min(scales.upperBound, max(scales.lowerBound, scale)) : 1
        }

        static func stored(_ defaults: UserDefaults = .standard) -> RowPlacement {
            var placement = RowPlacement()
            if let centre = defaults.object(forKey: centreKey) as? Double, centre.isFinite {
                placement.centre = min(1, max(0, centre))
            }
            placement.bottom = defaults.string(forKey: edgeKey) == "bottom"
            if let scale = defaults.object(forKey: scaleKey) as? Double { placement.scale = clamped(scale) }
            return placement
        }

        func store(_ defaults: UserDefaults = .standard) {
            if abs(centre - 0.5) < 0.001 { defaults.removeObject(forKey: Self.centreKey) }
            else { defaults.set(min(1, max(0, centre)), forKey: Self.centreKey) }
            if bottom { defaults.set("bottom", forKey: Self.edgeKey) } else { defaults.removeObject(forKey: Self.edgeKey) }
            if abs(scale - 1) < 0.001 { defaults.removeObject(forKey: Self.scaleKey) }
            else { defaults.set(Self.clamped(scale), forKey: Self.scaleKey) }
        }

        static func reset(_ defaults: UserDefaults = .standard) {
            for key in [centreKey, edgeKey, scaleKey] { defaults.removeObject(forKey: key) }
        }
    }

    /// The title row's frame in a window of `window` (origin top left) for a
    /// row `size` across: centred where it was put, kept inside the window,
    /// `rowMargin` in from the top or bottom edge.
    static func rowFrame(in window: CGSize, size: CGSize, baseSize: CGFloat, placement: RowPlacement) -> CGRect {
        let edge = baseSize * 0.2
        let width = min(size.width, max(1, window.width - 2 * edge))
        let x = min(max(edge, CGFloat(placement.centre) * window.width - width / 2), max(edge, window.width - width - edge))
        let margin = baseSize * rowMargin
        let y = placement.bottom ? window.height - margin - size.height : margin
        return CGRect(x: x, y: y, width: width, height: size.height)
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
    private var stayTrack: Int?
    private var stayTrackSeen = Date.distantPast
    private var foundNone: Double?
    private var lyricsAt: Double?
    private var heldTrack: Int?
    private var held: OpeningTitle.Schedule?
    private var heldUntil: Double?

    /// The card `track` showed while its lyrics were being looked up, once
    /// the lookup has ended, and the song time it ended (`arrival`); nil
    /// when no card was up yet. `card` is the loading stage's card while
    /// `kind` is `.loading`. Kept past the scenes' cross-fade, so the card
    /// stays one card to its end (`OpeningTitle.carried`). A new song, or
    /// playback back before the card began (a replay), forgets it.
    func hold(track: Int, kind: LyricSceneKey.Kind, card: OpeningTitle.Schedule?, at time: Double)
        -> (card: OpeningTitle.Schedule, arrival: Double)? {
        if track != heldTrack {
            heldTrack = track
            held = nil
            heldUntil = nil
        }
        if let held, time < held.start {
            self.held = nil
            heldUntil = nil
        }
        switch kind {
        case .loading:
            if heldUntil == nil, let card, OpeningTitle.phase(card, at: time) != nil { held = card }
        case .synced, .plain, .missing:
            if held != nil, heldUntil == nil { heldUntil = time }
        case .idle:
            break
        }
        return currentHold(track: track)
    }

    /// What `hold` last worked out for `track`, without updating it.
    func currentHold(track: Int) -> (card: OpeningTitle.Schedule, arrival: Double)? {
        guard track == heldTrack, let held, let heldUntil else { return nil }
        return (held, heldUntil)
    }

    /// Whether `track`'s card stays because its lookup found no lyrics, and
    /// the song time lyrics arrived after that, if they did. `kind` is what
    /// the stage shows at song time `time`; `notFoundAt` is when the lookup
    /// reported nothing (one from the song before is ignored). A new song
    /// starts afresh; a seek back to before the lyrics arrived forgets them
    /// with the rest, so the card does not return over the lyrics.
    func stay(track: Int, kind: LyricSceneKey.Kind, notFoundAt: Date?, at time: Double)
        -> (found: Double, lyricsAt: Double?)? {
        if track != stayTrack {
            stayTrack = track
            stayTrackSeen = Date()
            foundNone = nil
            lyricsAt = nil
        }
        switch kind {
        case .missing:
            if foundNone == nil, let notFoundAt, notFoundAt > stayTrackSeen.addingTimeInterval(-0.25) { foundNone = time }
        case .synced, .plain:
            guard foundNone != nil else { break }
            if let arrived = lyricsAt, time < arrived - 1 {
                foundNone = nil
                lyricsAt = nil
            } else if lyricsAt == nil {
                lyricsAt = time
            }
        case .idle, .loading:
            break
        }
        return currentStay(track: track)
    }

    /// What `stay` last worked out for `track`, without updating it.
    func currentStay(track: Int) -> (found: Double, lyricsAt: Double?)? {
        guard track == stayTrack else { return nil }
        return foundNone.map { ($0, lyricsAt) }
    }

    /// The song time at which `track` was first seen: 0 for a song seen from
    /// its start, or once playback is back at its start. Playback sought
    /// back before that moment joins the song there instead, so a card
    /// (a kept one above all) is not missing until the old moment returns.
    func joined(track: Int, at time: Double) -> Double {
        if track != self.track {
            self.track = track
            joinedAt = time
        }
        if time < joinedAt { joinedAt = time }
        if time < OpeningTitle.midSong { joinedAt = 0 }
        return joinedAt
    }

    private var coverTrack: Int?
    private var coverArrived: Double?

    /// 0...1: how far `track`'s cover has faded in at wall time `wall`,
    /// given whether it is here (`has`). A cover already here when the
    /// song was first seen needs no fade of its own (the card's entrance
    /// brings it); one that arrives later fades in over `coverFadeIn`.
    func coverFade(track: Int, has: Bool, wall: Double) -> Double {
        if track != coverTrack {
            coverTrack = track
            coverArrived = has ? -.infinity : nil
        }
        guard has else {
            coverArrived = nil
            return 0
        }
        if coverArrived == nil { coverArrived = wall }
        return Easing.smoothstep((wall - (coverArrived ?? wall)) / Self.coverFadeIn)
    }

    static let coverFadeIn = 0.45

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
    /// The album's animated cover (`NowPlayingMonitor.motionArtwork`), played
    /// in the cover's place whatever the Animated covers switch says; the
    /// still shows while it loads. Nil for none.
    var motionCover: URL? = nil
    /// The stage is paused: so is the animated cover.
    var paused = false
    /// This song's cover is still being fetched: its place is kept, empty,
    /// so the words do not move when it arrives.
    var coverPending = false
    /// 0...1: how far a cover that arrived after the card has faded in.
    var coverFade = 1.0
    /// The lyric's base size on this screen.
    let baseSize: CGFloat
    let accent: Color
    let mood: TrackMood
    /// The floating lyrics: centred in the window, compact, no scrim of its own.
    var floating = false
    /// A kept title in the floating window: one compact row at the top,
    /// above the lyrics (`OpeningTitle.rowBand`), instead of the card.
    var row = false
    @ObservedObject private var settings = Settings.shared
    @Environment(\.lyricReduceMotion) private var reduceMotion
    @Environment(\.lyricInk) private var ink

    var body: some View {
        GeometryReader { geometry in
            let size = geometry.size
            let compact = floating || phase.size == .compact
            let reveal = OpeningTitle.Reveal.at(phase.age, size: compact ? .compact : .full, reduceMotion: reduceMotion)
            let leaving = Easing.smoothstep(phase.exit)
            // Where the user put it on the desktop, and how large
            // (Settings › Look, Text: `OpeningTitle.Placement`).
            let place = OpeningTitle.Placement.stored()
            let box = OpeningTitle.box(in: size, baseSize: baseSize, placement: place)
            let unit = floating ? baseSize * 0.95 : (compact ? baseSize * 0.8 : baseSize) * CGFloat(place.scale)
            let dark = Self.isDark(ink.base.rest)
            if row {
                let rowPlace = OpeningTitle.RowPlacement.stored()
                PlacedRow(baseSize: baseSize, placement: rowPlace) {
                    titleRow(reveal: reveal, leaving: leaving, dark: dark, width: size.width * 0.9,
                             scale: CGFloat(rowPlace.scale), bottom: rowPlace.bottom)
                }
                .frame(width: size.width, height: size.height)
            } else {
            let block = card(unit: unit, compact: compact, reveal: reveal, dark: dark,
                             width: floating ? size.width * 0.9 : box.width)
                .opacity((1 - leaving) * phase.opacity)
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
                                       center: UnitPoint(x: (box.minX + box.width * 0.3) / max(1, size.width),
                                                         y: (box.maxY - box.height * 0.4) / max(1, size.height)),
                                       startRadiusFraction: 0, endRadiusFraction: 0.5)
                        .opacity(max(reveal.title, reveal.rule) * (1 - leaving) * phase.opacity)
                    block
                        .padding(.leading, box.minX)
                        .padding(.bottom, size.height - box.maxY)
                }
                .frame(width: size.width, height: size.height, alignment: .bottomLeading)
            }
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
        // The card glows in the album's own colour: a tight halo and a wide,
        // soft bloom, arriving with the words and leaving with them.
        let glow = OpeningTitle.glowStrength(dark: dark)
        // One spacing scale (`OpeningTitle.Spacing`): the reading close
        // under its title, the artist a clear step down, the album tight
        // under the artist; the cover the same step away from the words.
        let step = OpeningTitle.Spacing(nominal: nominal)
        // Centred in the floating window without a cover; beside one, the
        // words line up against it.
        let centred = floating && cover == nil
        let rise = reduceMotion ? 0 : unit * 0.32
        // The cover's place: its picture once it is here, kept empty while
        // it is fetched (`coverPending`), none when the song has none.
        let slot = cover != nil || (coverPending && OpeningTitle.reservesCover(age: phase.age))
        let readingText = reading.flatMap { reading in showsReading ? reading : nil }.map { reading in
            Text(reading)
                .font(settings.uiFont(size: readingSize, mood: mood))
                .foregroundStyle(type.opacity(0.78))
                .lineLimit(1)
                .minimumScaleFactor(0.6)
                .padding(.top, step.reading)
                .opacity(reveal.reading)
                .offset(y: (1 - reveal.reading) * rise * 0.5)
        }
        // In the artist's own case, as the menu bar and player show it.
        let artistText = artist.isEmpty ? nil : Text(artist)
            .font(settings.uiFont(size: artistSize, mood: mood))
            .foregroundStyle(type.opacity(0.92))
            .lineLimit(1)
            .minimumScaleFactor(0.6)
            .opacity(reveal.artist)
        let albumText = compact ? nil : album.map { album in
            Text(album)
                .font(settings.uiFont(size: albumSize, mood: mood))
                .kerning(albumSize * 0.02)
                .foregroundStyle(type.opacity(0.7))
                .lineLimit(1)
                .minimumScaleFactor(0.6)
                .padding(.top, step.line)
                .opacity(reveal.album)
        }
        let typeShadow = OpeningTitleTypeShadow(dark: dark, accent: accent, glow: glow, unit: unit)
        if slot {
            // A cover of one size for every title (`coverShare`): the
            // title and its reading from the cover's top, stepping down a
            // size where two lines would not fit; the artist and album with
            // the last baseline on the cover's foot. A long title never
            // makes the card larger.
            CoverBesideWords(side: nominal * OpeningTitle.coverShare(compact: compact), gap: step.group,
                             capShare: OpeningTitle.capHeightShare / OpeningTitle.ascentShare, maximum: width) {
                ZStack {
                    if let cover {
                        TitleCover(still: cover, motion: animatedCover)
                            .clipShape(RoundedRectangle(cornerRadius: nominal * 0.1, style: .continuous))
                            .shadow(color: .black.opacity(0.45), radius: nominal * 0.12, y: nominal * 0.04)
                            .shadow(color: accent.opacity(0.55 * glow), radius: nominal * 0.32)
                            .scaleEffect(reduceMotion ? 1 : 0.9 + 0.1 * reveal.cover, anchor: .bottomLeading)
                            .opacity(reveal.cover * coverFade)
                    }
                }
                FirstThatFits {
                    ForEach(OpeningTitle.titleSteps, id: \.self) { share in
                        VStack(alignment: .leading, spacing: 0) {
                            titleText(titleSize * share, type: type, centred: centred, reveal: reveal, rise: rise)
                            readingText
                        }
                        .frame(minWidth: 0, minHeight: 0, alignment: .topLeading)
                        .clipped()
                    }
                    // Last: the smallest on one line, narrowed to fit.
                    VStack(alignment: .leading, spacing: 0) {
                        titleText(titleSize * (OpeningTitle.titleSteps.last ?? 0.58), type: type, centred: centred,
                                  reveal: reveal, rise: rise, lines: 1)
                        readingText
                    }
                    .frame(minWidth: 0, minHeight: 0, alignment: .topLeading)
                    .clipped()
                }
                .modifier(typeShadow)
                VStack(alignment: .leading, spacing: 0) {
                    artistText
                    albumText
                }
                .fixedSize(horizontal: false, vertical: true)
                .modifier(typeShadow)
            }
        } else {
            CappedWidth(maximum: width) {
                VStack(alignment: centred ? .center : .leading, spacing: 0) {
                    titleText(titleSize, type: type, centred: centred, reveal: reveal, rise: rise)
                    readingText
                    if let artistText { artistText.padding(.top, step.group) }
                    albumText
                }
                .modifier(typeShadow)
            }
        }
    }

    /// The title at `size`, on up to two lines, wiped on as it enters.
    private func titleText(_ size: CGFloat, type: Color, centred: Bool, reveal: OpeningTitle.Reveal,
                           rise: CGFloat, lines: Int = 2) -> some View {
        Text(title)
            .font(settings.lyricFont(size: size, hero: true, mood: mood))
            .foregroundStyle(type)
            .multilineTextAlignment(centred ? .center : .leading)
            .lineLimit(lines)
            .minimumScaleFactor(lines == 1 ? 0.6 : 0.85)
            .fixedSize(horizontal: false, vertical: true)
            .mask { wipe(reveal.wipe) }
            .opacity(min(1, reveal.title * 1.6))
            .offset(y: (1 - reveal.title) * rise)
    }

    /// The floating window's kept title: the cover, the title and the
    /// artist on one centred line, settling down from above as it enters
    /// and lifting away as it leaves.
    private func titleRow(reveal: OpeningTitle.Reveal, leaving: Double, dark: Bool, width: CGFloat,
                          scale: CGFloat, bottom: Bool) -> some View {
        let type: Color = dark ? Color(white: 0.08) : .white
        // The row's own size: the lyric size at the row's scale.
        let baseSize = self.baseSize * scale
        let side = baseSize * OpeningTitle.rowCover
        let glow = OpeningTitle.glowStrength(dark: dark)
        let arrival = reduceMotion ? reveal.title : Easing.smoothstep(reveal.title)
        return HStack(spacing: baseSize * 0.2) {
            if let cover {
                TitleCover(still: cover, motion: animatedCover)
                    .frame(width: side, height: side)
                    .clipShape(RoundedRectangle(cornerRadius: side * 0.14, style: .continuous))
                    .shadow(color: .black.opacity(0.4), radius: side * 0.08, y: side * 0.03)
                    .opacity(reveal.cover * coverFade)
            } else if coverPending && OpeningTitle.reservesCover(age: phase.age) {
                // Its place kept while it is fetched.
                Color.clear.frame(width: side, height: side)
            }
            HStack(alignment: .firstTextBaseline, spacing: baseSize * 0.16) {
                Text(title)
                    .font(settings.lyricFont(size: baseSize * OpeningTitle.rowTitle, hero: true, mood: mood))
                    .foregroundStyle(type)
                    .layoutPriority(1)
                if !artist.isEmpty {
                    Text(artist)
                        .font(settings.uiFont(size: baseSize * OpeningTitle.rowArtist, mood: mood))
                        .foregroundStyle(type.opacity(0.78))
                        .opacity(reveal.artist)
                }
            }
            .lineLimit(1)
            .truncationMode(.tail)
            .compositingGroup()
            .shadow(color: (dark ? Color.white : .black).opacity(0.5), radius: baseSize * 0.1, y: baseSize * 0.015)
            .shadow(color: accent.opacity(0.45 * glow), radius: baseSize * 0.3)
        }
        // Hugs its words (at most `width`), so it can be placed across the window.
        .modifier(CappedWidthModifier(maximum: width))
        .opacity(min(1, arrival * 1.4) * (1 - leaving) * phase.opacity)
        // Settles in from beyond its edge, and lifts back out that way.
        .offset(y: reduceMotion ? 0 : (bottom ? 1 : -1) * (1 - arrival + leaving) * baseSize * 0.18)
    }

    /// The animated cover to play: none under Reduce Motion, where the
    /// still stays (a looping picture is motion like any other).
    private var animatedCover: (url: URL, paused: Bool)? {
        guard let motionCover, !reduceMotion else { return nil }
        return (motionCover, paused)
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

/// The card's cover: the still, filling its square, and over it the
/// animated cover when there is one, looping silently on the shared player
/// the wallpaper uses (`MotionArtworkView`), which fades in once it has a
/// frame, so the still shows until then.
struct TitleCover: View {
    let still: NSImage
    var motion: (url: URL, paused: Bool)?

    var body: some View {
        Color.clear
            .overlay {
                Image(nsImage: still)
                    .resizable()
                    .interpolation(.high)
                    .aspectRatio(contentMode: .fill)
            }
            .overlay {
                if let motion {
                    MotionArtworkView(url: motion.url, placement: .fill, isPaused: motion.paused)
                }
            }
            .clipped()
    }
}

/// The first of its subviews (largest first) whose height at the offered
/// width fits the offered height, or the last when none does. The others
/// are given no room (each is clipped, so they draw nothing). Its first
/// text baseline is the chosen one's.
struct FirstThatFits: Layout {
    private func chosen(_ proposal: ProposedViewSize, subviews: Subviews) -> (index: Int, size: CGSize)? {
        guard !subviews.isEmpty else { return nil }
        let width = proposal.width
        for (index, subview) in subviews.enumerated() {
            let size = subview.sizeThatFits(ProposedViewSize(width: width, height: nil))
            if index == subviews.count - 1 || proposal.height.map({ size.height <= $0 + 0.5 }) ?? true {
                return (index, CGSize(width: min(size.width, width ?? size.width), height: size.height))
            }
        }
        return nil
    }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        chosen(proposal, subviews: subviews)?.size ?? .zero
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        let pick = chosen(proposal, subviews: subviews)
        for (index, subview) in subviews.enumerated() {
            if index == pick?.index, let size = pick?.size {
                subview.place(at: bounds.origin, proposal: ProposedViewSize(width: size.width, height: size.height))
            } else {
                subview.place(at: bounds.origin, proposal: .zero)
            }
        }
    }

    func explicitAlignment(of guide: VerticalAlignment, in bounds: CGRect, proposal: ProposedViewSize,
                           subviews: Subviews, cache: inout ()) -> CGFloat? {
        guard let pick = chosen(proposal, subviews: subviews) else { return nil }
        let dimensions = subviews[pick.index].dimensions(in: ProposedViewSize(width: pick.size.width,
                                                                              height: pick.size.height))
        return bounds.minY + dimensions[guide]
    }
}

/// `CappedWidth` as a modifier.
struct CappedWidthModifier: ViewModifier {
    let maximum: CGFloat
    func body(content: Content) -> some View { CappedWidth(maximum: maximum) { content } }
}

/// Places the floating window's title row (its one subview) where the
/// user put it (`OpeningTitle.rowFrame`): kept inside the window, at its
/// top or bottom edge.
struct PlacedRow: Layout {
    let baseSize: CGFloat
    let placement: OpeningTitle.RowPlacement

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        proposal.replacingUnspecifiedDimensions()
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        guard let row = subviews.first else { return }
        let size = row.sizeThatFits(ProposedViewSize(width: bounds.width * 0.9, height: nil))
        let frame = OpeningTitle.rowFrame(in: bounds.size, size: size, baseSize: baseSize, placement: placement)
        row.place(at: CGPoint(x: bounds.minX + frame.minX, y: bounds.minY + frame.minY),
                  proposal: ProposedViewSize(width: frame.width, height: frame.height))
    }
}

/// The card's type, as one: a soft shadow under it, as the lyrics have,
/// then a glow in the album's colour.
struct OpeningTitleTypeShadow: ViewModifier {
    let dark: Bool
    let accent: Color
    let glow: Double
    let unit: CGFloat

    func body(content: Content) -> some View {
        content
            .compositingGroup()
            .shadow(color: (dark ? Color.white : .black).opacity(0.45), radius: unit * 0.12, y: unit * 0.015)
            .shadow(color: accent.opacity(0.7 * glow), radius: unit * 0.18)
            .shadow(color: accent.opacity(0.45 * glow), radius: unit * 0.6)
    }
}

/// The title card's grid: a square cover of a fixed `side`, then a column
/// `gap` to its right, together at most `maximum` wide. Subviews: the
/// cover, the title (with its reading), the details (artist and album).
/// The cover's top is on the title's cap height (`capShare` of its first
/// baseline); the details sit with their last baseline on the cover's foot;
/// the title is offered the room between, `gap` clear of the details, and
/// steps down to fit it. Nothing in it depends on the title's length.
struct CoverBesideWords: Layout {
    let side: CGFloat
    let gap: CGFloat
    let capShare: CGFloat
    let maximum: CGFloat

    private struct Fit {
        var column: CGFloat
        var coverTop: CGFloat
        var titleRoom: CGFloat
        var title: CGSize
        var details: ViewDimensions?
    }

    private func fit(_ proposal: ProposedViewSize, subviews: Subviews) -> Fit {
        guard subviews.count == 3 else { return Fit(column: 0, coverTop: 0, titleRoom: 0, title: .zero, details: nil) }
        let width = min(proposal.width ?? maximum, maximum)
        let column = max(width * 0.4, width - side - gap)
        let details = subviews[2].dimensions(in: ProposedViewSize(width: column, height: nil))
        let detailsAbove = details[.lastTextBaseline]
        var top: CGFloat = 0
        var room = max(1, side - detailsAbove - gap)
        var title = CGSize.zero
        for _ in 0..<3 {
            room = max(1, top + side - detailsAbove - gap)
            let dimensions = subviews[1].dimensions(in: ProposedViewSize(width: column, height: room))
            title = CGSize(width: dimensions.width, height: dimensions.height)
            top = max(0, dimensions[.firstTextBaseline] * (1 - capShare))
        }
        return Fit(column: column, coverTop: top, titleRoom: room, title: title, details: details)
    }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let fit = fit(proposal, subviews: subviews)
        guard let details = fit.details else { return .zero }
        let bottom = fit.coverTop + side + (details.height - details[.lastTextBaseline])
        let wordsWidth = max(fit.title.width, details.width)
        return CGSize(width: side + gap + min(fit.column, wordsWidth), height: max(bottom, fit.title.height))
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        let fit = fit(proposal, subviews: subviews)
        guard let details = fit.details else { return }
        let x = bounds.minX + side + gap
        subviews[0].place(at: CGPoint(x: bounds.minX, y: bounds.minY + fit.coverTop),
                          proposal: ProposedViewSize(width: side, height: side))
        subviews[1].place(at: CGPoint(x: x, y: bounds.minY),
                          proposal: ProposedViewSize(width: fit.column, height: fit.titleRoom))
        subviews[2].place(at: CGPoint(x: x, y: bounds.minY + fit.coverTop + side - details[.lastTextBaseline]),
                          proposal: ProposedViewSize(width: fit.column, height: nil))
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
