import Foundation
import Yams

/// Custom fork: bounded parallel provider lookup, recording-specific caching,
/// manual selections and explicit timing provenance. Upstream licence retained.
final class LyricsFetcher {
    static let shared = LyricsFetcher()
    struct Word: Codable, Equatable { var text: String; var start: TimeInterval; var end: TimeInterval }
    /// Equatable over every field, so `sameResult` sees each one, including
    /// fields added later.
    struct Line: Codable, Equatable {
        var start: TimeInterval
        var end: TimeInterval
        /// True when the source timed only the line's start (LRC), so `end` is
        /// just where the next line or the track begins, not where singing
        /// stops. Nil when the source supplied the end, and in older caches.
        var endFromNextLine: Bool? = nil
        var text: String
        var words: [Word]
        var isInstrumental: Bool
        var wordTimed: Bool = false
        var romanization: String? = nil
        var translation: String? = nil
        /// True for a production credit ("Produced by: …"). Such a line is
        /// also `isInstrumental` with no words, so the lead layer skips it;
        /// its `text` is drawn apart by the credits layer instead.
        var credit: Bool? = nil
    }
    struct Provenance: Equatable, Codable {
        var precision: String
        var source: String
        var label: String { "\(precision) · \(source)" }
    }
    enum Result: Codable {
        case synced([Line], Provenance)
        case plain([String], Provenance)
    }
    private struct Outcome: Sendable {
        var id: LyricSourceID
        var candidate: LyricCandidate?
        var state: String
        var offline = false
        /// The source replied, with or without lyrics, rather than failing.
        var answered = false
    }
    private enum Event: Sendable {
        case outcome(Outcome)
        case grace
        case settle(Int)
        /// Music's own cache took too long; the services are asked anyway.
        case onlineDue
    }
    /// What `collect` gathered, and which sources it asked or stopped.
    private struct Collected {
        var report: LyricLookupReport
        /// Sources that replied, with or without lyrics.
        var answered: Set<LyricSourceID>
        /// Sources stopped, or never started, because their answer could not
        /// have changed what shows or what is cached.
        var stopped: Set<LyricSourceID>
        var asked: Set<LyricSourceID>
        /// The match on screen when the lookup ended.
        var shown: LyricCandidate?
    }
    typealias SourceFetch = @Sendable (LyricSourceID, LyricQuery, String) async throws -> LyricCandidate?
    /// How long a lookup holds back before showing what it has, and how long
    /// each source may take. Slow sources keep answering in the background
    /// while their answer could still matter.
    struct Timing: Sendable {
        /// The best match found by then is shown even if sources are pending.
        var grace: Double = 1.5
        /// A pending upgrade waits this long for something better still.
        var upgradeSettle: Double = 0.75
        /// Music can finish loading its own lyrics just after a track change.
        var appleRetry: Double = 4
        var lrclibLimit: Double = 20
        var sourceLimit: Double = 8
        /// An automatic lookup reads Music's own cache before asking any
        /// service, for at most this long, so a hit there needs no network.
        var musicFirst: Double = 0.7
        static let standard = Timing()
    }
    private let cacheFolder: URL
    private let timing: Timing
    private let fetchSource: SourceFetch
    private let isOnline: @Sendable () -> Bool
    private let misses: LyricMissCache
    init(cacheFolder: URL = SavedLyrics.defaultFolder, timing: Timing = .standard, misses: LyricMissCache = LyricMissCache(),
         isOnline: @escaping @Sendable () -> Bool = { LyricConnectivity.shared.isOnline }, fetch: SourceFetch? = nil) {
        self.cacheFolder = cacheFolder
        self.timing = timing
        self.misses = misses
        self.isOnline = isOnline
        fetchSource = fetch ?? { id, query, folder in try await LyricsFetcher.fetch(id, query: query, localFolder: folder) }
    }
    func lyrics(title: String, artist: String, album: String? = nil, duration: Double) async -> Result? {
        await lookup(LyricQuery(title:title,artist:artist,album:album,duration:duration)).result
    }
    /// Queries every enabled source at once and reports progress as they
    /// answer. A match is shown as soon as nothing pending could beat it, or
    /// when the grace period ends; after that only a match that ranks higher
    /// (`outranks`) replaces it, never a lower one. Saved explicit choices
    /// always win. An automatic lookup reads Music's cache first and stops
    /// asking sources whose answer could no longer change anything.
    func lookup(_ query: LyricQuery, force: Bool = false, options given: LyricSourceOptions.Snapshot? = nil,
                onUpdate: (@Sendable (LyricLookupReport) async -> Void)? = nil) async -> LyricLookupReport {
        let options: LyricSourceOptions.Snapshot
        if let given { options = given } else { options = await MainActor.run { LyricSourceOptions.shared.snapshot } }
        let key = query.recordingKey + "|" + options.fingerprint
        let pinned = SavedLyrics.read(query.recordingKey, pin: true, in: cacheFolder)
        if !force, let pinned { return LyricLookupReport(result: pinned, pinned: true, cached: true, revision: 1) }
        // Station names make junk matches, and a stream's clock is not the song's.
        if !force, query.duration <= 0 { return LyricLookupReport(result: nil, skippedStream: true) }
        let local = options.enabled.filter(\.isLocal)
        var online = options.enabled.filter { !$0.isLocal }
        var saved: LyricCandidate?
        var offered: [LyricCandidate] = []
        var states: [LyricSourceID: String] = [:]
        if !force, !online.isEmpty {
            if let candidate = SavedLyrics.readCandidate(key, in: cacheFolder), online.contains(candidate.source) {
                saved = candidate
                for id in online { states[id] = id == candidate.source ? "Saved match" : "Not checked: a saved match was used" }
                online = []
            } else {
                let recent = misses.answered(key)
                for id in online where recent.sources.contains(id) { states[id] = "No match a few minutes ago" }
                online.removeAll { recent.sources.contains($0) }
                // A close match they offered is listed again, with its reason.
                offered = recent.rejected
            }
        }
        let localPrint = local.contains(.lyrimuse) ? LocalLyricBridge.fingerprint(query, folder: options.localFolder) : nil
        let collected = await collect(query, options: options, local: local, online: online, seeded: (saved.map { [$0] } ?? []) + offered,
                                      states: states, pinned: pinned, automatic: !force, localPrint: localPrint, onUpdate: onUpdate)
        var report = collected.report
        let queried = !collected.asked.isDisjoint(with: online)
        report.cached = saved != nil
        report.offline = queried && report.offline
        guard !Task.isCancelled, queried else { return report }
        // Only the online choice is cached; local sources are read afresh
        // every time, so editing or adding a file needs no network lookup.
        if let best = Self.choose(report.candidates.filter { !$0.source.isLocal }, query: query, options: options) {
            // A choice that a source stopped early might have beaten is not kept.
            if Self.isSettled(best, pending: collected.stopped.filter { !$0.isLocal }, query: query, options: options) {
                SavedLyrics.save(best, key: key, in: cacheFolder)
                misses.remove(key)
            }
        } else if !force, !report.offline {
            // Only sources that replied are remembered, each with the close
            // match it offered, if any; ones that failed are asked again.
            let replied = collected.answered.intersection(online)
            misses.insert(key, sources: replied, rejected: report.rejected.map(\.candidate).filter { replied.contains($0.source) })
        }
        return report
    }
    /// Re-reads only the local lyric files after they change, keeping the
    /// online candidates already found. Nil when nothing relevant changed.
    func refreshLocal(_ query: LyricQuery, previous: LyricLookupReport,
                      options given: LyricSourceOptions.Snapshot? = nil) async -> LyricLookupReport? {
        let options: LyricSourceOptions.Snapshot
        if let given { options = given } else { options = await MainActor.run { LyricSourceOptions.shared.snapshot } }
        guard query.duration > 0, !previous.pinned, options.enabled.contains(.lyrimuse) else { return nil }
        let print = LocalLyricBridge.fingerprint(query, folder: options.localFolder)
        guard print != previous.localPrint else { return nil }
        let outcome = await Self.bounded(.lyrimuse, query: query, folder: options.localFolder, timing: timing, fetch: fetchSource)
        guard !Task.isCancelled else { return nil }
        let known = previous.candidates + previous.rejected.map(\.candidate)
        let before = known.first { $0.source == .lyrimuse }
        var report = previous
        report.localPrint = print
        guard !Self.sameResult(before?.result, outcome.candidate?.result) else { return report }
        var found = known.filter { $0.source != .lyrimuse }
        if let candidate = outcome.candidate { found.append(candidate) }
        let screened = Self.screen(found, query: query, options: options)
        let best = Self.choose(screened.accepted, query: query, options: options)
        // Only the local file changed: switch when it wins now or was what showed.
        let localWasShown = before.map { Self.sameResult($0.result, previous.result) } ?? false
        let shown = best?.source == .lyrimuse || localWasShown ? best : known.first { Self.sameResult($0.result, previous.result) }
        var states = previous.states
        states[.lyrimuse] = outcome.state
        report.candidates = screened.accepted
        report.rejected = screened.rejected
        report.states = Self.annotate(states, screened, shown: shown?.source, query: query)
        if !Self.sameResult(shown?.result, previous.result) {
            report.result = shown?.result
            report.revision = previous.revision + 1
        }
        return report
    }
    private func collect(_ query: LyricQuery, options: LyricSourceOptions.Snapshot, local: [LyricSourceID], online: [LyricSourceID],
                         seeded: [LyricCandidate], states initial: [LyricSourceID: String], pinned: Result?, automatic: Bool,
                         localPrint: String?, onUpdate: (@Sendable (LyricLookupReport) async -> Void)?) async -> Collected {
        let sources = local + online
        var found: [LyricSourceID: LyricCandidate] = [:]
        for candidate in seeded { found[candidate.source] = candidate }
        var states = initial
        for id in sources { states[id] = "Searching…" }
        var pending = Set(sources), answered: Set<LyricSourceID> = [], stopped: Set<LyricSourceID> = []
        var offline = false
        var publication = Publication(options: options, query: query, locked: pinned != nil)
        let fetch = fetchSource, timing = timing, folder = options.localFolder
        func report(_ screened: Screened, complete: Bool) -> LyricLookupReport {
            LyricLookupReport(result: publication.shown?.result ?? pinned, candidates: screened.accepted,
                              states: Self.annotate(states, screened, shown: publication.shown?.source, query: query), pinned: pinned != nil,
                              rejected: screened.rejected, revision: publication.revision, isComplete: complete, offline: offline,
                              localPrint: localPrint)
        }
        // Each source runs as its own task, so one can be stopped on its own.
        var running: [LyricSourceID: Task<Outcome, Never>] = [:]
        func stop(_ ids: Set<LyricSourceID>, because reason: String) {
            for id in ids {
                running[id]?.cancel()
                stopped.insert(id)
                pending.remove(id)
                states[id] = reason
            }
        }
        let retryApple = automatic && local.contains(.appleMusicCache)
        // An automatic lookup reads Music's own cache before asking any
        // service: a hit there needs no network, as before lookups were
        // progressive. The services are asked once Music has answered.
        let musicFirst = retryApple && !online.isEmpty
        await withTaskGroup(of: Event.self) { group in
            func start(_ ids: [LyricSourceID]) {
                // A cancelled lookup asks nothing more; pending sources it never
                // started simply end with the group.
                for id in ids where running[id] == nil && !stopped.contains(id) && !Task.isCancelled {
                    let task = Task { await Self.bounded(id, query: query, folder: folder, timing: timing, fetch: fetch) }
                    running[id] = task
                    group.addTask { await withTaskCancellationHandler { .outcome(await task.value) } onCancel: { task.cancel() } }
                }
            }
            start(musicFirst ? local : sources)
            var onlineStarted = !musicFirst, musicAnswered = false, onlineDue = false
            if musicFirst {
                group.addTask { try? await Task.sleep(nanoseconds: Self.nanoseconds(timing.musicFirst)); return .onlineDue }
            }
            if pinned == nil, !sources.isEmpty {
                group.addTask { try? await Task.sleep(nanoseconds: Self.nanoseconds(timing.grace)); return .grace }
            }
            var graceOver = false, settleOver = false, settleArmed = false, settleToken = 0
            // Music's second look is an upgrade opportunity; it never holds
            // back the first result, so it is not counted as pending.
            var appleRetried = false, appleRetrying = false
            while !pending.isEmpty || appleRetrying, let event = await group.next() {
                switch event {
                case .outcome(let outcome) where stopped.contains(outcome.id):
                    // Stopped: what it says can no longer change anything.
                    continue
                case .outcome(let outcome) where outcome.id == .appleMusicCache && outcome.candidate == nil
                        && retryApple && !appleRetried && !Task.isCancelled:
                    musicAnswered = true
                    appleRetried = true
                    appleRetrying = true
                    pending.remove(.appleMusicCache)
                    states[.appleMusicCache] = "Checking Music again shortly…"
                    group.addTask {
                        try? await Task.sleep(nanoseconds: Self.nanoseconds(timing.appleRetry))
                        guard !Task.isCancelled else { return .outcome(Outcome(id: .appleMusicCache, candidate: nil, state: "Cancelled")) }
                        var retry = await Self.bounded(.appleMusicCache, query: query, folder: folder, timing: timing, fetch: fetch)
                        if retry.candidate == nil, !Task.isCancelled {
                            retry.state = "No matching lyrics cached. Open Lyrics in Music to load them."
                        }
                        return .outcome(retry)
                    }
                case .outcome(let outcome):
                    if outcome.id == .appleMusicCache {
                        musicAnswered = true
                        if appleRetried { appleRetrying = false }
                    }
                    pending.remove(outcome.id)
                    states[outcome.id] = outcome.state
                    if let candidate = outcome.candidate { found[outcome.id] = candidate }
                    if outcome.answered { answered.insert(outcome.id) }
                    offline = offline || outcome.offline
                case .grace:
                    graceOver = true
                case .settle(let token):
                    settleOver = settleOver || token == settleToken
                case .onlineDue:
                    onlineDue = true
                }
                let screened = Self.screen(Array(found.values), query: query, options: options)
                let best = Self.choose(screened.accepted, query: query, options: options)
                let settled = Self.isSettled(best, pending: pending, query: query, options: options)
                switch publication.step(best: best, settled: settled, graceOver: graceOver, settleOver: settleOver) {
                case .publish:
                    settleArmed = false; settleOver = false; settleToken += 1
                case .holdForUpgrade where !settleArmed:
                    settleArmed = true; settleToken += 1
                    let token = settleToken
                    group.addTask { try? await Task.sleep(nanoseconds: Self.nanoseconds(timing.upgradeSettle)); return .settle(token) }
                default:
                    break
                }
                // Stop early once a source's answer could no longer change what
                // shows or what replays use: everything stops only once the best
                // possible match is in hand. A search asks every source, so all
                // the alternatives are listed.
                if automatic, publication.shown != nil, let best {
                    let onlineBest = Self.choose(screened.accepted.filter { !$0.source.isLocal }, query: query, options: options)
                    let idle = pending.filter {
                        !Self.matters($0, best: best, onlineBest: onlineBest, query: query, options: options)
                    }
                    if !idle.isEmpty {
                        stop(idle, because: Self.notNeeded + (best.source == .appleMusicCache
                             ? "Music’s own lyrics are showing" : "it couldn’t beat the match showing"))
                    }
                }
                if !onlineStarted, musicAnswered || onlineDue {
                    onlineStarted = true
                    start(online)
                }
                if !pending.isEmpty || appleRetrying, !Task.isCancelled, let onUpdate { await onUpdate(report(screened, complete: false)) }
            }
            group.cancelAll()
        }
        let screened = Self.screen(Array(found.values), query: query, options: options)
        // Every source has answered or been stopped, so whatever is best now is settled.
        _ = publication.step(best: Self.choose(screened.accepted, query: query, options: options), settled: true,
                             graceOver: true, settleOver: true)
        offline = offline || !isOnline()
        return Collected(report: report(screened, complete: true), answered: answered, stopped: stopped, asked: Set(running.keys),
                         shown: publication.shown)
    }
    /// Starts the state of a source that an automatic lookup stopped, or
    /// never asked, because its answer could not have changed anything.
    static let notNeeded = "Not needed: "
    /// Whether a pending source's answer could still change what shows, or
    /// the online choice that replays use. `best` is what shows once any
    /// upgrade waiting for a better match has been made.
    static func matters(_ source: LyricSourceID, best: LyricCandidate, onlineBest: LyricCandidate?,
                        query: LyricQuery, options: LyricSourceOptions.Snapshot) -> Bool {
        // Nothing outranks Music's own word-by-word or mixed timing, and a
        // replay reads Music's cache again, so no service is needed at all.
        if best.source == .appleMusicCache, rankedTiming(best.result) == 2 { return false }
        if couldBeat(best, from: source, query: query, options: options) { return true }
        guard !source.isLocal else { return false }
        return onlineBest.map { couldBeat($0, from: source, query: query, options: options) } ?? true
    }
    private static func nanoseconds(_ seconds: Double) -> UInt64 { UInt64(max(0, seconds) * 1_000_000_000) }

    /// When a progressive lookup shows lyrics and whether a later arrival may
    /// replace them. The first match appears once nothing pending could beat
    /// it, or when the grace period ends. After that a match that ranks
    /// higher (`outranks`) replaces it and a lower one never does, so the
    /// lookup ends on the best match found. While a pending source could
    /// still beat that upgrade too, it waits until `settleOver`, so one swap
    /// does instead of two.
    struct Publication {
        enum Step: Equatable { case wait, holdForUpgrade, publish }
        let options: LyricSourceOptions.Snapshot
        let query: LyricQuery
        /// An explicit saved choice is showing; automatic results never replace it.
        let locked: Bool
        private(set) var shown: LyricCandidate?
        private(set) var revision = 0
        init(options: LyricSourceOptions.Snapshot, query: LyricQuery, locked: Bool = false) {
            self.options = options
            self.query = query
            self.locked = locked
            revision = locked ? 1 : 0
        }
        /// `settled`: no pending source could change the choice.
        mutating func step(best: LyricCandidate?, settled: Bool, graceOver: Bool, settleOver: Bool) -> Step {
            guard !locked, let best else { return .wait }
            guard let current = shown else {
                guard settled || graceOver else { return .wait }
                show(best)
                return .publish
            }
            guard isUpgrade(best, over: current) else { return .wait }
            guard settled || settleOver else { return .holdForUpgrade }
            show(best)
            return .publish
        }
        func isUpgrade(_ next: LyricCandidate, over current: LyricCandidate) -> Bool {
            next.source != current.source && LyricsFetcher.outranks(next, current, query: query, options: options)
        }
        private mutating func show(_ candidate: LyricCandidate) {
            shown = candidate
            revision += 1
        }
    }

    typealias Screened = (accepted: [LyricCandidate], rejected: [LyricRejection])
    /// Accepted candidates in ranking order (`outranks`), and recording
    /// mismatches with the reason. Every source, Music's own cache included,
    /// must pass the recording check; catalogue size plays no role.
    static func screen(_ candidates: [LyricCandidate], query: LyricQuery, options: LyricSourceOptions.Snapshot) -> Screened {
        var accepted: [LyricCandidate] = [], rejected: [LyricRejection] = []
        for candidate in candidates where options.enabled.contains(candidate.source) {
            if let reason = rejection(candidate, query: query) { rejected.append(.init(candidate: candidate, reason: reason)) }
            else { accepted.append(candidate) }
        }
        // Lyrics in another language than the song's rank as another version (`LyricLanguage`).
        accepted = LyricLanguage.weigh(accepted, query: query)
        // Only matches for the playing version are compared: a live take has the same words but its own timing.
        let fitting = accepted.indices.filter { fitsVersion(accepted[$0], query) }
        for (index, weighed) in zip(fitting, weighAgreement(fitting.map { accepted[$0] })) { accepted[index] = weighed }
        accepted.sort { outranks($0, $1, query: query, options: options) }
        rejected.sort { order($0.candidate.source, in: options) < order($1.candidate.source, in: options) }
        return (accepted, rejected)
    }
    static func ranked(_ candidates: [LyricCandidate], query: LyricQuery, options: LyricSourceOptions.Snapshot) -> [LyricCandidate] {
        screen(candidates, query: query, options: options).accepted
    }
    /// Whether `a` ranks above `b`. Timed lyrics rank above plain text, and
    /// among timed lyrics a match for the playing version ranks above one
    /// for another version (Live, Remix, Acoustic…: `VersionTags`), whose
    /// timing will not fit. Then word-by-word and mixed timing rank
    /// together, first: Music's own lyrics, then LRCLIB's, then the other
    /// sources'. Line timing is next, Music's own first, then plain text.
    /// Beyond those leads, sources follow the configured order, which in
    /// Accuracy first comes after a closer title and artist match.
    static func outranks(_ a: LyricCandidate, _ b: LyricCandidate, query: LyricQuery,
                         options: LyricSourceOptions.Snapshot) -> Bool {
        let timing = rankedTiming(a.result), other = rankedTiming(b.result)
        if (timing > 0) != (other > 0) { return timing > 0 }
        // Timing that races through lines (`timingFault`) ranks below sound
        // timing, ahead of every source's lead: shown, it shoots to the end.
        let sound = !hasTimingFault(a.result), otherSound = !hasTimingFault(b.result)
        if sound != otherSound { return sound }
        let fits = fitsVersion(a, query), otherFits = fitsVersion(b, query)
        if fits != otherFits { return fits }
        if timing != other { return timing > other }
        let lead = Self.lead(a, timing: timing), otherLead = Self.lead(b, timing: timing)
        if lead != otherLead { return lead < otherLead }
        if options.mode == .smart {
            // Other services giving the same words: a tie-break, never above the leads.
            let peers = min(a.agreement.peers, 2), otherPeers = min(b.agreement.peers, 2)
            if peers != otherPeers { return peers > otherPeers }
            let closeness = confidence(a, query), otherCloseness = confidence(b, query)
            if closeness != otherCloseness { return closeness > otherCloseness }
        }
        return order(a.source, in: options) < order(b.source, in: options)
    }
    /// Whether timed lyrics stamp a run of lines faster than they could be
    /// sung (`LyricGapTiming.bunchedRun`).
    static func hasTimingFault(_ result: Result) -> Bool { timingFault(result) != nil }
    /// What is wrong with timed lyrics' timing, for the source list, or nil
    /// when it is sound: a run of lines stamped faster than they could be
    /// sung (`LyricGapTiming.bunchedRun`), or lines whose words are crammed
    /// into a split second (`LyricGapTiming.crushedLines`).
    static func timingFault(_ result: Result) -> String? {
        guard case .synced(let lines, _) = result else { return nil }
        return timingFault(lines)
    }
    static func timingFault(_ lines: [Line]) -> String? {
        if let run = LyricGapTiming.bunchedRun(in: lines) {
            return "\(run.count) lines bunched at \(LyricQuery.clock(run.start))"
        }
        let crushed = LyricGapTiming.crushedLines(in: lines)
        guard let first = crushed.first else { return nil }
        return crushed.count == 1 ? "a line crammed at \(LyricQuery.clock(first))"
            : "\(crushed.count) lines crammed from \(LyricQuery.clock(first))"
    }
    /// Whether the match names the same version as the playing song. A
    /// service that reported no checkable name repeats the search, so it fits.
    /// Lyrics in another language than the song's (`languageMismatch`, set
    /// by `screen`) are another version's: a language version's, whatever
    /// its name says.
    static func fitsVersion(_ candidate: LyricCandidate, _ query: LyricQuery) -> Bool {
        !candidate.languageMismatch && VersionTags.mismatch(query, title: candidate.title).isEmpty
    }
    /// The timing matches are ranked by: 2 for word-by-word or mixed timing,
    /// which rank together, 1 for line timing and 0 for plain text.
    static func rankedTiming(_ result: Result) -> Int { min(timingTier(result), 2) }
    /// Where a source stands ahead of the configured order at a ranked
    /// timing: Music's own cache leads timed lyrics, and LRCLIB follows it
    /// only with word-by-word or mixed timing. Every other source is 2.
    private static func lead(_ source: LyricSourceID, timing: Int) -> Int {
        switch source {
        case .appleMusicCache where timing >= 1: return 0
        case .lrclib where timing == 2: return 1
        default: return 2
        }
    }
    /// `lead` for a match: LRCLIB's word timing loses its lead when two other
    /// services agree on the timing and LRCLIB's disagrees with both
    /// (`LyricAgreement.timingOutlier`): its timing is made by listeners.
    private static func lead(_ candidate: LyricCandidate, timing: Int) -> Int {
        if candidate.source == .lrclib, candidate.agreement.timingOutlier { return 2 }
        return lead(candidate.source, timing: timing)
    }
    private static func order(_ source: LyricSourceID, in options: LyricSourceOptions.Snapshot) -> Int {
        options.enabled.firstIndex(of: source) ?? 999
    }
    /// The highest `confidence`: both names exact and, when the playing
    /// length is known, the same length.
    private static func closestMatch(_ query: LyricQuery) -> Double { query.duration > 0 ? 2.25 : 2 }
    private static func confidence(_ candidate: LyricCandidate, _ query: LyricQuery) -> Double {
        // How closely the name matches, and a little more for a recording whose
        // length is nearly the playing one's (never enough to outweigh a name).
        var value = LyricQuery.similarity(query.title, candidate.title) + LyricQuery.similarity(query.artist, candidate.artist)
        if let length = candidate.duration, length > 0, query.duration > 0 {
            value += 0.25 * max(0, 1 - abs(length - query.duration) / 10)
        }
        return value
    }
    /// Why a candidate is not the playing recording, or nil when it is.
    static func rejection(_ candidate: LyricCandidate, query: LyricQuery) -> String? {
        let duration = candidate.duration ?? 0
        let timed = duration.isFinite && duration > 0 && query.duration > 0
        // A local export whose artist matches is not rejected on its [length:],
        // as before: the files are written for the playing track. Without an
        // artist, the length is all that confirms the recording.
        let lengthDecides = !(candidate.source == .lyrimuse && query.hasArtist)
        let match = ll_recording_match(
            LyricQuery.similarity(query.title, candidate.title), query.artistScore(candidate.artist),
            query.hasArtist ? 1 : 0, query.hasArtist ? 0 : 1, lengthDecides ? 1 : 0,
            query.duration, duration
        )
        switch match {
        case Int32(LL_TITLE_MISMATCH.rawValue):
            return "Rejected: title “\(candidate.title)”"
        case Int32(LL_ARTIST_MISMATCH.rawValue):
            return candidate.artist.isEmpty ? "Rejected: no artist given" : "Rejected: artist “\(candidate.artist)”"
        case Int32(LL_MISSING_IDENTITY.rawValue):
            return "Rejected: no artist or song length to compare"
        case Int32(LL_DURATION_MISMATCH.rawValue):
            return "Rejected: duration \(LyricQuery.clock(duration)) vs \(LyricQuery.clock(query.duration))"
        default:
            break
        }
        // A service that reports no length is still caught serving a longer edit.
        if !timed, !candidate.source.isLocal, query.duration > 0, case .synced(let lines, _) = candidate.result,
           let last = lines.last(where: { !$0.isInstrumental }), last.start > query.duration + 3 {
            return "Rejected: lyrics run past the end of this recording"
        }
        return nil
    }
    /// Whether a search hit names the playing recording, as `rejection`
    /// judges a candidate, before its lyrics are fetched or chosen. A
    /// service offers one match, so a hit that fails the check must not be
    /// picked over one that passes it, however close its name.
    static func namesRecording(_ source: LyricSourceID, title: String, artist: String, duration: Double?,
                               query: LyricQuery) -> Bool {
        let probe = LyricCandidate(source: source, result: .plain([], Provenance(precision: "", source: "")),
                                   title: title, artist: artist, duration: duration, score: 0)
        return rejection(probe, query: query) == nil
    }
    /// The highest-scoring of one service's candidates, preferring those
    /// that pass the recording check.
    static func bestOffer(_ candidates: [LyricCandidate], query: LyricQuery) -> LyricCandidate? {
        candidates.map { (candidate: $0, passes: rejection($0, query: query) == nil) }
            .max { ($0.passes ? 1 : 0, $0.candidate.score) < ($1.passes ? 1 : 0, $1.candidate.score) }?.candidate
    }
    private static func annotate(_ base: [LyricSourceID: String], _ screened: Screened, shown: LyricSourceID?,
                                 query: LyricQuery) -> [LyricSourceID: String] {
        var states = base
        for candidate in screened.accepted {
            var found = base[candidate.source]?.hasPrefix("Saved match") == true ? "Saved match" : "Match found"
            let other = VersionTags.mismatch(query, title: candidate.title)
            if !other.isEmpty { found += " · other version (\(VersionTags.describe(other)))" }
            else if candidate.languageMismatch { found += " · lyrics in another language" }
            if candidate.viaOriginalTitle { found += " · found under the original title" }
            if let fault = LyricsFetcher.timingFault(candidate.result) { found += " · \(fault)" }
            if candidate.agreement.timingOutlier { found += " · timing differs from other sources" }
            else if candidate.agreement.peers > 0 {
                found += candidate.agreement.peers == 1 ? " · 1 other source agrees" : " · \(candidate.agreement.peers) other sources agree"
            }
            states[candidate.source] = candidate.source == shown ? found + " · showing" : found
        }
        for rejection in screened.rejected { states[rejection.candidate.source] = rejection.reason }
        return states
    }
    /// Whether no pending source could change what `choose` picks.
    static func isSettled(_ best: LyricCandidate?, pending: Set<LyricSourceID>, query: LyricQuery,
                          options: LyricSourceOptions.Snapshot) -> Bool {
        let open = pending.filter { options.enabled.contains($0) }
        guard !open.isEmpty else { return true }
        guard let best else { return false }
        return !open.contains { couldBeat(best, from: $0, query: query, options: options) }
    }
    /// Whether the best result a source could still return would rank above
    /// `best`. Nothing ranks above Music's own word-by-word or mixed timing.
    static func couldBeat(_ best: LyricCandidate, from source: LyricSourceID, query: LyricQuery,
                          options: LyricSourceOptions.Snapshot) -> Bool {
        guard source != best.source else { return false }
        let timing = rankedTiming(best.result), ceiling = min(source.timingCeiling, 2)
        if (ceiling > 0) != (timing > 0) { return ceiling > 0 }
        // Another version's timed lyrics: any timed source could still find the playing one.
        if timing > 0, !fitsVersion(best, query) { return true }
        if ceiling != timing { return ceiling > timing }
        let lead = Self.lead(source, timing: timing), bestLead = Self.lead(best, timing: timing)
        // Agreement is weighed only among the answers in hand: waiting for more would undo
        // stopping the other services once LRCLIB's word timing or Music's lyrics lead.
        if lead != bestLead { return lead < bestLead }
        // Accuracy first: a closer match could still arrive, the same names
        // with a length that fits (as `outranks` weighs it) included. A match
        // naming no length stopped such a source, so which one showed, was
        // cached and kept the song's offset depended on which answered first.
        if options.mode == .smart, confidence(best, query) < closestMatch(query) { return true }
        return order(source, in: options) < order(best.source, in: options)
    }
    /// Structural equality, so re-running a lookup that finds the same lyrics
    /// does not restart what is on screen. Every field of every line counts,
    /// translations included, so an edited local file, or a lookup that now
    /// has translations, still replaces what is shown.
    static func sameResult(_ a: Result?, _ b: Result?) -> Bool {
        switch (a, b) {
        case (nil, nil): return true
        case (.plain(let x, let p)?, .plain(let y, let q)?): return x == y && p == q
        case (.synced(let x, let p)?, .synced(let y, let q)?): return p == q && x == y
        default: return false
        }
    }
    static func timingTier(_ result: Result) -> Int {
        if hasWordTiming(result) { return 3 }
        if case .synced(let lines, _) = result {
            let vocal = lines.filter { !$0.isInstrumental }
            if vocal.contains(where: { validWordTiming($0) }), hasWordTimingEvidence(vocal) { return 2 }
            return vocal.isEmpty ? 0 : 1
        }
        return 0
    }
    /// The highest-ranked of `candidates` (`outranks`) from an enabled source.
    static func choose(_ candidates: [LyricCandidate], query: LyricQuery, options: LyricSourceOptions.Snapshot) -> LyricCandidate? {
        var chosen: LyricCandidate?
        for candidate in candidates where options.enabled.contains(candidate.source) {
            if chosen.map({ outranks(candidate, $0, query: query, options: options) }) ?? true { chosen = candidate }
        }
        return chosen
    }
    private static func bounded(_ id: LyricSourceID, query: LyricQuery, folder: String, timing: Timing,
                                fetch: @escaping SourceFetch) async -> Outcome {
        await withTaskGroup(of: Outcome.self) { group in
            group.addTask {
                do {
                    let candidate = try await fetch(id, query, folder)
                    return Outcome(id:id,candidate:candidate,state:candidate == nil ? "No usable match / unavailable" : "Match found",answered:true)
                } catch is CancellationError { return Outcome(id:id,candidate:nil,state:"Cancelled") }
                catch { return Outcome(id:id,candidate:nil,state:error.localizedDescription,offline:LyricHTTP.isOffline(error)) }
            }
            group.addTask {
                try? await Task.sleep(nanoseconds: Self.nanoseconds(id == .lrclib ? timing.lrclibLimit : timing.sourceLimit))
                return Outcome(id:id,candidate:nil,state:"Timed out")
            }
            let first = await group.next() ?? Outcome(id:id,candidate:nil,state:"Unavailable")
            group.cancelAll()
            return first
        }
    }
    private static func fetch(_ id: LyricSourceID, query: LyricQuery, localFolder: String) async throws -> LyricCandidate? {
        try Task.checkCancellation()
        switch id {
        case .appleMusicCache: return await AppleMusicCachedLyrics.lookup(query)
        case .lyricsPlus: return try await withOriginalTitle(id, query: query) { try await lyricsPlus($0) }
        // The Chinese catalogues often index only an artist's native name (`ArtistAliases`),
        // and a Japanese song's original title (`OriginalTitles`).
        case .netEase: return try await withOriginalTitle(id, query: query) { q in try await withAliases(id, query: q) { try await netEase($0) } }
        case .qqMusic:
            return try await withOriginalTitle(id, query: query) { q in try await withAliases(id, query: q) { try await AdditionalLyricsProviders.qqMusic($0) } }
        case .kugou:
            return try await withOriginalTitle(id, query: query) { q in try await withAliases(id, query: q) { try await AdditionalLyricsProviders.kugou($0) } }
        case .kuwo:
            return try await withOriginalTitle(id, query: query) { q in try await withAliases(id, query: q) { try await AdditionalLyricsProviders.kuwo($0) } }
        case .lyricsOVH: return try await AdditionalLyricsProviders.lyricsOVH(query)
        case .lrclib: return try await withOriginalTitle(id, query: query) { try await lrclib($0) }
        case .lyrimuse: return try LocalLyricBridge.candidate(query, folder: localFolder)
        case .amll: return try await withOriginalTitle(id, query: query) { try await AMLLLyricsProvider.lookup($0) }
        }
    }
    private static func lyricsPlus(_ query: LyricQuery) async throws -> LyricCandidate? {
        guard let hit = try await KPoeLyricsProvider.lookup(title:query.title,artist:query.artist,album:query.album,duration:query.duration) else {return nil}
        let provenance = Provenance(precision:precision(hit.lines),source:hit.source+" via Lyrics+")
        // Rank and label by the recording the node matched, when its names
        // can be checked. One in another script may be the same name
        // transliterated: the search used the library's names, so those
        // stand in and only the song's length is checked.
        let title = LyricQuery.checkable(hit.title, against: query.title)
        let artist = LyricQuery.checkable(hit.artist, against: query.artist)
        return LyricCandidate(source:.lyricsPlus,result:.synced(hit.lines,provenance),title:title ?? query.title,artist:artist ?? query.artist,
                              duration:nil,score:113,metadataReported:title != nil && artist != nil)
    }
    /// NetEase's lyrics for the edition its search matched, ranked and
    /// labelled by that edition. Throws when NetEase could not be reached or
    /// answered with an error, so an outage is not remembered as "no lyrics".
    static func netEase(_ query: LyricQuery, session: URLSession = .shared) async throws -> LyricCandidate? {
        let recorder = NetEaseLyricsProvider.MatchRecorder(session: session)
        let found = await NetEaseLyricsProvider.$recorder.withValue(recorder) {
            await NetEaseLyricsProvider.timedLines(title:query.title,artist:query.artist,duration:query.duration)
        }
        try Task.checkCancellation()
        guard let lines = found else {
            if let failure = recorder.failure { throw failure }
            return nil
        }
        let native = lines.contains { $0.wordTimed }, song = recorder.song
        return LyricCandidate(source:.netEase,result:.synced(lines,Provenance(precision:precision(lines),source:"NetEase")),
            title:song?.title ?? query.title,artist:song?.artist ?? query.artist,duration:song?.duration,
            score:native ? 110 : 96,metadataReported:song != nil)
    }
    static func validWordTiming(_ line: Line) -> Bool {
        guard line.wordTimed, !line.words.isEmpty, line.start.isFinite, line.end.isFinite, line.end > line.start else { return false }
        let covered = line.words.map(\.text).joined().filter { $0.isLetter || $0.isNumber }
        let letters = line.text.filter { $0.isLetter || $0.isNumber }
        guard covered == letters else { return false }
        // A whole line or a few long phrases timed as "words" (`isPhraseTimed`).
        guard !isPhraseTimed(line.text, letters: letters, tokens: line.words.count) else { return false }
        return line.words.enumerated().allSatisfy { i, word in
            word.start.isFinite && word.end.isFinite && word.start >= line.start - 0.05 && word.end > word.start && word.end <= line.end + 0.05 &&
                (i == 0 || word.start >= line.words[i - 1].start)
        }
    }
    static func hasWordTiming(_ result: Result) -> Bool {
        guard case .synced(let lines, _) = result else { return false }
        let vocal = lines.filter { !$0.isInstrumental }
        return !vocal.isEmpty && vocal.allSatisfy { validWordTiming($0) } && hasWordTimingEvidence(vocal)
    }
    static func precision(_ lines: [Line]) -> String {
        let vocal = lines.filter { !$0.isInstrumental }
        // Real word timing must be there to show (`hasWordTimingEvidence`).
        guard hasWordTimingEvidence(vocal) else {return "Line-synced"}
        if vocal.allSatisfy({validWordTiming($0)}) {return "Word-by-word"}
        if vocal.contains(where:{validWordTiming($0)}) {return "Mixed timing"}
        return "Line-synced"
    }
    private struct Response: Decodable {
        var trackName: String?
        var artistName: String?
        var albumName: String?
        var duration: Double?
        var syncedLyrics: String?
        var lyricsfile: String?
        var plainLyrics: String?
        var instrumental: Bool?
    }
    static func requestLRCLIB(_ path: String, parameters: [String:String], session: URLSession = .shared) async throws -> Data? {
        try Task.checkCancellation()
        var components = URLComponents(string:"https://lrclib.net/api/"+path)
        components?.queryItems = parameters.sorted {$0.key<$1.key}.map {URLQueryItem(name:$0.key,value:$0.value)}
        guard let url = components?.url else {return nil}
        for attempt in 0...1 {
            var request = URLRequest(url: url, timeoutInterval: 4)
            request.setValue("Lunalyr/0.4.6 (https://github.com/WaterBotol/Lunalyr)", forHTTPHeaderField: "User-Agent")
            let (data, response) = try await session.data(for: request)
            let http = response as? HTTPURLResponse
            let status = http?.statusCode ?? 0
            if status == 404 { return nil }
            if (status == 503 || status == 429), attempt == 0 {
                let delay = Double(http?.value(forHTTPHeaderField: "Retry-After") ?? "1") ?? 1
                // Long server backoffs are reported rather than keeping a lookup alive.
                guard delay.isFinite, delay <= 3 else { throw LyricHTTP.Failure(message: "LRCLIB is busy. Try again later (HTTP \(status)).") }
                try await Task.sleep(nanoseconds: UInt64(max(0, delay) * 1_000_000_000))
                continue
            }
            guard status == 200 else { throw LyricHTTP.Failure(message: "LRCLIB: HTTP \(status). The service may be temporarily unavailable.") }
            guard data.count <= 2_000_000 else { throw LyricHTTP.Failure(message: "LRCLIB response too large") }
            return data
        }
        return nil
    }

    static func lrclib(_ query: LyricQuery, session: URLSession = .shared) async throws -> LyricCandidate? {
        var parameters = ["track_name":query.title,"artist_name":query.artist]
        if let album = query.album, !album.isEmpty {parameters["album_name"]=album}
        if query.duration>0 {parameters["duration"]=String(Int(query.duration.rounded()))}
        var payloads: [Response] = []
        var exactError: Error?
        // The exact endpoint needs an artist; without one only search can match.
        if query.hasArtist {
            do {
                if let data = try await requestLRCLIB("get",parameters:parameters,session:session), let exact = try? JSONDecoder().decode(Response.self,from:data) { payloads.append(exact) }
            } catch is CancellationError { throw CancellationError() }
            catch { exactError = error }
        }
        // The exact recording is marked instrumental. A search would only
        // find a sung edition with the same name and length (an album's
        // instrumental version), so there are no lyrics to show.
        if payloads.first?.instrumental == true, payloads.first.map({ LyricQuery.similarity(query.title, $0.trackName ?? query.title) >= 0.95 }) == true {
            return nil
        }
        // An exact match whose timing races through lines (listener-made)
        // is not the last word: other uploads for the song are searched too.
        let exactTimed = payloads.contains { payload in
            if let file = payload.lyricsfile, !file.isEmpty, case let lines = parseLyricsFile(file), !lines.isEmpty {
                return timingFault(lines) == nil
            }
            if let lrc = payload.syncedLyrics, !lrc.isEmpty { return timingFault(parseLRC(lrc, trackDuration: query.duration)) == nil }
            return false
        }
        if !exactTimed {
            let search = query.hasArtist ? ["track_name":query.title,"artist_name":query.artist] : ["track_name":query.title]
            do {
                if let data = try await requestLRCLIB("search",parameters:search,session:session),
                   let found = try? JSONDecoder().decode([Response].self,from:data) { payloads.append(contentsOf:found.prefix(30)) }
            } catch is CancellationError { throw CancellationError() }
            catch { if payloads.isEmpty { throw error } }
        }
        if payloads.isEmpty, let exactError { throw exactError }
        let candidates = payloads.compactMap { payload -> LyricCandidate? in
            let title=payload.trackName ?? query.title,artist=payload.artistName ?? query.artist
            let delta=payload.duration.map {abs($0-query.duration)} ?? 0
            let score=vc_match_score(LyricQuery.similarity(query.title,title),query.artistScore(artist),
                delta,payload.duration != nil && query.duration>0 ? 1 : 0,0,1,
                LyricQuery.normalize(payload.albumName ?? "") == LyricQuery.normalize(query.album ?? "") ? 1 : 0)
            guard score>0 else {return nil}
            var lines: [Line] = []
            if let file=payload.lyricsfile,!file.isEmpty {lines=parseLyricsFile(file)}
            if lines.isEmpty,let lrc=payload.syncedLyrics,!lrc.isEmpty {lines=parseLRC(lrc,trackDuration:query.duration)}
            let result: Result
            if !lines.isEmpty {result = .synced(lines,Provenance(precision:precision(lines),source:"LRCLIB"))}
            else if let plain=payload.plainLyrics,!plain.isEmpty {
                result = .plain(plain.components(separatedBy:.newlines),Provenance(precision:"Not synced",source:"LRCLIB"))
            } else {return nil}
            // Below every upload with sound timing, above plain text.
            let bunched = !lines.isEmpty && timingFault(lines) != nil
            return LyricCandidate(source:.lrclib,result:result,title:title,artist:artist,duration:payload.duration,
                score:score-(lines.isEmpty ? 12 : bunched ? 6 : 0))
        }
        return bestOffer(candidates, query: query)
    }

    // MARK: - lyricsfile (YAML) parsing

    /// The schema is small and rigidly generated, so a line scanner beats
    /// pulling in a YAML dependency:
    ///   lines:
    ///   - text: I've been tryna call
    ///     start_ms: 27160
    ///     end_ms: 29960
    private struct Lyricsfile: Decodable {
        struct Metadata: Decodable { var offset_ms: Double? }
        struct Row: Decodable {
            struct Token: Decodable { var text: String; var start_ms: Double; var end_ms: Double? }
            var text: String
            var start_ms: Double
            var end_ms: Double?
            var words: [Token]?
        }
        var metadata: Metadata?
        var lines: [Row]?
    }
    static func parseLyricsFile(_ yaml: String) -> [Line] {
        guard yaml.utf8.count <= 1_000_000,
              let file = try? YAMLDecoder().decode(Lyricsfile.self, from: yaml) else { return [] }
        let offset = (file.metadata?.offset_ms ?? 0) / 1000
        guard offset.isFinite else { return [] }
        let rows = (file.lines ?? []).filter { $0.start_ms.isFinite && $0.start_ms >= 0 }.sorted { $0.start_ms < $1.start_ms }
        return rows.enumerated().compactMap { index, row in
            let start = max(0, row.start_ms / 1000 + offset)
            let end = row.end_ms.map { $0 / 1000 + offset } ?? (index + 1 < rows.count ? rows[index + 1].start_ms / 1000 + offset : start + 4)
            guard end.isFinite, end > start else { return nil }
            var line = makeLine(text: row.text, start: start, end: end)
            if row.end_ms == nil { line.endFromNextLine = true }
            let tokens = row.words ?? []
            let words = tokens.enumerated().map { i, token in
                Word(text: token.text.trimmingCharacters(in: .whitespacesAndNewlines),
                     start: token.start_ms / 1000 + offset,
                     end: token.end_ms.map { $0 / 1000 + offset } ?? (i + 1 < tokens.count ? tokens[i + 1].start_ms / 1000 + offset : end))
            }
            let covered = words.map(\.text).joined().filter { !$0.isWhitespace }
            // Rows timed by the line or by long phrases keep estimated words (`isPhraseTimed`).
            if !words.isEmpty, covered == row.text.filter({ !$0.isWhitespace }),
               !isPhraseTimed(row.text, letters: row.text.filter { $0.isLetter || $0.isNumber }, tokens: words.count), words.enumerated().allSatisfy({ i, word in
                word.start.isFinite && word.end.isFinite && word.start >= start && word.end > word.start && word.end <= end &&
                    (i == 0 || word.start >= words[i - 1].end)
            }) {
                line.words = words; line.wordTimed = true
            }
            return line
        }
    }

    // MARK: - LRC parsing (fallback)

    /// The longest LRC text read, in UTF-8 bytes.
    static let lrcInputLimit = 1_000_000
    /// A row is read once for each of its timestamps. The copies, counted by
    /// their text's length plus one each, may come to at most this, so a
    /// crafted file (thousands of stamps on a long row of word stamps) cannot
    /// exhaust memory; the rows after that point are not read.
    static let lrcExpansionLimit = 2 * lrcInputLimit

    static func parseLRC(_ text: String, trackDuration: Double) -> [Line] {
        guard text.utf8.count <= lrcInputLimit,
              let lineStamp = try? NSRegularExpression(pattern:#"\[(\d{1,3}):([0-5]\d)(?:[.:](\d{1,3}))?\]"#),
              let wordStamp = try? NSRegularExpression(pattern:#"<(\d{1,3}):([0-5]\d)(?:[.:](\d{1,3}))?>"#)
        else {return []}
        func time(_ match:NSTextCheckingResult,_ value:String)->Double {
            let ns=value as NSString
            let m=Double(ns.substring(with:match.range(at:1))) ?? 0
            let s=Double(ns.substring(with:match.range(at:2))) ?? 0
            let range=match.range(at:3)
            let fraction=range.location == NSNotFound ? "" : ns.substring(with:range)
            return m*60+s+(Double(fraction) ?? 0)/pow(10,Double(fraction.count))
        }
        // LRC convention: a positive [offset:] (milliseconds) shows lyrics
        // sooner, matching the app's own song offset.
        var offset=0.0
        if let regex=try? NSRegularExpression(pattern:#"(?im)^\s*\[offset:\s*([+-]?\d+(?:\.\d+)?)\s*\]"#),
           let match=regex.firstMatch(in:text,range:NSRange(text.startIndex...,in:text)),
           let range=Range(match.range(at:1),in:text) {let value=Double(text[range]) ?? 0;offset=value.isFinite ? value/1000 : 0}
        var rows:[LRCRow]=[],inCredits=true,budget=lrcExpansionLimit
        for raw in text.components(separatedBy:.newlines) {
            let row=raw.trimmingCharacters(in:.whitespaces)
            let ns=row as NSString
            let tags=lineStamp.matches(in:row,range:NSRange(location:0,length:ns.length))
            var leading:[NSTextCheckingResult]=[],end=0
            // Stamps written apart ("[00:12.00] [00:45.00]Chorus") all lead the row.
            for tag in tags {
                guard tag.range.location>=end,
                      ns.substring(with:NSRange(location:end,length:tag.range.location-end)).allSatisfy(\.isWhitespace) else {break}
                leading.append(tag);end=NSMaxRange(tag.range)
            }
            guard let first=leading.first else {continue}
            let body=ns.substring(from:end).trimmingCharacters(in:.whitespaces)
            let bodyNS=body as NSString
            let markers=wordStamp.matches(in:body,range:NSRange(location:0,length:bodyNS.length))
            let plain=wordStamp.stringByReplacingMatches(in:body,range:NSRange(location:0,length:bodyNS.length),withTemplate:"")
            // Credits are not lyrics; an everyday label ("鼓：…") counts only
            // before the first lyric or straight after another credit.
            if LyricCredits.isCredit(plain,inCredits:inCredits) {
                inCredits=true
                for tag in leading {
                    let stamp=time(tag,row)-offset
                    rows.append(LRCRow(stamp:stamp,start:max(0,stamp),text:plain.trimmingCharacters(in:.whitespaces),tokens:[],credit:true))
                }
                continue
            }
            if !isInstrumentalMark(plain) {inCredits=false}
            let cost=leading.count*(bodyNS.length+1)
            guard cost<=budget else {break}
            budget-=cost
            // The word stamps are read once; each copy of the row moves them
            // by its own timestamp.
            let prefix=markers.first.map {bodyNS.substring(to:$0.range.location).trimmingCharacters(in:.whitespaces)} ?? ""
            var syllables:[(text:String,time:Double,next:Double?,glued:Bool)]=[],spaced=true
            for (index,marker) in markers.enumerated() {
                let from=NSMaxRange(marker.range),to=index+1<markers.count ? markers[index+1].range.location : bodyNS.length
                let label=bodyNS.substring(with:NSRange(location:from,length:to-from))
                defer {spaced=label.last?.isWhitespace ?? spaced}
                let trimmed=label.trimmingCharacters(in:.whitespacesAndNewlines)
                if trimmed.isEmpty {continue}
                // Syllable stamps with no space between them belong to one word.
                syllables.append((trimmed,time(marker,body),index+1<markers.count ? time(markers[index+1],body) : nil,
                                  !spaced && !(label.first?.isWhitespace ?? true)))
            }
            let firstTime=time(first,row)
            for tag in leading {
                let stamp=time(tag,row)-offset,shift=time(tag,row)-firstTime-offset
                let begin=max(0,stamp)
                let tokens=syllables.map { syllable -> LyricScript.TimedToken in
                    let start=max(0,syllable.time+shift)
                    return .init(text:syllable.text,start:start,end:syllable.next.map {max(start,$0+shift)} ?? .greatestFiniteMagnitude,
                                 glued:syllable.glued)
                }
                let valid=prefix.isEmpty && tokens.enumerated().allSatisfy { index,token in
                    token.start>=begin && (index==0 || token.start>=tokens[index-1].start)
                }
                rows.append(LRCRow(stamp:stamp,start:begin,text:plain,tokens:valid ? tokens : []))
            }
        }
        let parsed=mergingSharedTimestamps(rows),lastEnd=trackDuration.isFinite ? trackDuration : 0
        return LyricCredits.markingTitleHeader(parsed.enumerated().compactMap { index,item in
            let end=index+1<parsed.count ? parsed[index+1].start : max(lastEnd,item.start+4)
            if item.credit {
                // Credits sit apart from the lyric: kept for the credits layer,
                // never drawn as a lead line, and never in the way of one.
                return Line(start:item.start,end:item.start+0.01,
                            endFromNextLine:true,text:item.text,words:[],isInstrumental:true,credit:true)
            }
            // A row an [offset:] pushed before the start is overtaken at 0.
            guard end>item.start else {return nil}
            var line=makeLine(text:item.text,start:item.start,end:end)
            line.endFromNextLine=true
            // Words are built once the end is known: the last syllable stamp
            // runs to it, so a final "tiful day" can still be divided.
            let words=LyricScript.words(from:item.tokens.map {var token=$0;token.end=min(token.end,end);return token})
            if !line.isInstrumental, !words.isEmpty, words.allSatisfy({$0.start < end}) {
                var timed = line
                timed.words = words
                timed.wordTimed = true
                if validWordTiming(timed) { line = timed }
            }
            if !line.isInstrumental {line.romanization=item.romanization;line.translation=item.translation}
            return line
        })
    }

    private struct LRCRow {
        /// The row's time after the offset, before clamping at zero.
        var stamp: Double
        var start: Double
        var text: String
        var tokens: [LyricScript.TimedToken]
        var romanization: String? = nil
        var translation: String? = nil
        var credit = false
    }

    /// Bilingual LRC repeats a timestamp for a reading or a translation, e.g.
    /// "[00:10.00]君の名は" then "[00:10.00]kimi no na wa". Those rows become one
    /// line: the first is the lyric, a Latin reading of a non-Latin lyric is its
    /// romanisation and any other version is its translation. Without this the
    /// first row lasted no time at all and only the second was ever shown. An
    /// empty or "♪" row gives way to a lyric at the same time, and a
    /// word-timed copy of the lyric lends it its word timing.
    private static func mergingSharedTimestamps(_ rows: [LRCRow]) -> [LRCRow] {
        // Stable: rows at one instant keep their order in the file. Rows that
        // meet at 0 only because an [offset:] moved them earlier stay apart.
        let ordered = rows.enumerated().sorted { ($0.element.stamp, $0.offset) < ($1.element.stamp, $1.offset) }.map(\.element)
        let readings = LyricScript.ReadingCache()
        var merged: [LRCRow] = []
        for row in ordered {
            guard var line = merged.last, abs(line.stamp - row.stamp) < 0.0005, !line.credit, !row.credit else { merged.append(row); continue }
            let text = row.text.trimmingCharacters(in: .whitespaces)
            let lyric = line.text.trimmingCharacters(in: .whitespaces)
            if isInstrumentalMark(text) { continue }
            if isInstrumentalMark(lyric) {
                line.text = row.text; line.tokens = row.tokens
            } else if text == lyric {
                guard line.tokens.isEmpty, !row.tokens.isEmpty else { continue }
                line.tokens = row.tokens
            } else if line.romanization == nil, LyricScript.isReading(text, of: lyric, cache: readings) {
                line.romanization = text
            } else if line.translation == nil {
                line.translation = text
            } else { continue }
            merged[merged.count - 1] = line
        }
        return merged
    }

    // MARK: - Shared line construction

    /// Empty text, or a lone note glyph: an instrumental break, not a lyric.
    static func isInstrumentalMark(_ text: String) -> Bool {
        text.allSatisfy { "♪♫•·-–—".contains($0) || $0.isWhitespace }
    }

    static func makeLine(text: String, start: Double, end: Double) -> Line {
        let cleaned = text.trimmingCharacters(in: .whitespaces)
        // A lone note glyph marks an instrumental break rather than a lyric.
        let instrumental = isInstrumentalMark(cleaned)
        return Line(
            start: start,
            end: end,
            text: instrumental ? "" : cleaned,
            words: instrumental ? [] : distributeWords(in: cleaned, from: start, to: end),
            isInstrumental: instrumental
        )
    }

    /// The share of a line's window that its estimated words take
    /// (`distributeWords`): a sung line normally finishes a little before
    /// the next one begins.
    static let estimatedWordShare = 0.88

    /// Where the line-level sweep of a lyric without word timing ends: the
    /// line's end, unless its estimated words were spread over less than the
    /// window (`LyricGapTiming.estimatingWords`: the last line running on
    /// through the outro, or a line before a long break), in which case the
    /// sweep ends with them. Never later than the line's end.
    static func estimatedSweepEnd(of line: Line) -> TimeInterval {
        guard !line.wordTimed, let sung = line.words.lazy.map(\.end).max(), sung > line.start else { return line.end }
        return min(line.end, line.start + (sung - line.start) / estimatedWordShare)
    }

    /// Splits a line's window across its words proportionally to their length,
    /// so longer words hold emphasis longer -- far more natural than an even
    /// split. The window is trimmed slightly because a sung line normally
    /// finishes before the next one begins (`estimatedWordShare`). A caller
    /// may pass a window shorter than the line's own
    /// (`LyricGapTiming.estimatingWords`); the line's times stay the caller's.
    static func distributeWords(in text: String, from start: TimeInterval, to end: TimeInterval) -> [Word] {
        var tokens: [String] = []
        text.enumerateSubstrings(in: text.startIndex..., options: .byWords) { _, _, enclosing, _ in
            let token=String(text[enclosing]).trimmingCharacters(in:.whitespacesAndNewlines)
            if !token.isEmpty {tokens.append(token)}
        }
        guard !tokens.isEmpty else { return [] }

        let window = max(end - start, 0.3)
        let usable = min(window, max(Double(tokens.count) * 0.42, window * estimatedWordShare))
        let weights = tokens.map { Double($0.count) + 1.5 }
        let totalWeight = weights.reduce(0, +)

        var words: [Word] = []
        var cursor = start
        for (token, weight) in zip(tokens, weights) {
            let slice = usable * (weight / totalWeight)
            words.append(Word(text: token, start: cursor, end: cursor + slice))
            cursor += slice
        }
        return words
    }
}
