import Combine
import Foundation

@MainActor
final class LyricsViewModel: ObservableObject {
    static let shared = LyricsViewModel()
    @Published var lines: [LyricsFetcher.Line] = [] {
        didSet {
            updateVocalWindows()
            lineIndex = LyricLineIndex(lines: lines)
            if let first = lines.first, let last = lines.last, last.end - first.start > 1 {
                wordsPerSecond = Double(lines.reduce(0) { $0 + $1.words.count }) / (last.end - first.start)
            } else { wordsPerSecond = 0 }
            updateReadings()
            updateMachineTranslations()
        }
    }
    /// Lines translated on this Mac, by index, for songs whose lyric source
    /// has no translation (`LyricMachineTranslator`). A source's own
    /// translation always wins: these are only for lines without one.
    @Published private(set) var machineTranslations: [Int: String] = [:]
    let machineTranslator = LyricMachineTranslator.system()
    @Published private(set) var romanized: [Int: [LyricsFetcher.Word]] = [:]
    private var readingTask: Task<Void, Never>?
    private var readingGeneration = UUID()
    @Published var plainLines: [String] = []
    @Published var isLoading = false
    @Published var notFound = false
    private(set) var notFoundAt: Date?
    @Published var provenance: LyricsFetcher.Provenance? {
        // Song offsets are tuned to the timing of the source on screen.
        didSet { SongOffsets.shared.activeSource = provenance?.source }
    }
    @Published var candidates: [LyricCandidate] = []
    /// Matches that failed the recording check, with the reason. Each can
    /// still be used for the song as an explicit choice.
    @Published var nearMisses: [LyricRejection] = []
    @Published var sourceStates: [LyricSourceID:String] = [:]
    /// Lyrics are showing while slower sources are still answering.
    @Published var isRefining = false
    @Published var pinnedMatch = false
    @Published var cacheMessage: String?
    private(set) var vocalWindows: [VCVocalWindow] = []
    /// Whether long pauses in the line-only lyrics showing are inferred
    /// (`Presentation.infersPauses`). The vocal windows follow it and
    /// `lines`, whichever of the two is set first.
    private var infersPauses = false {
        didSet { if infersPauses != oldValue { updateVocalWindows() } }
    }
    private func updateVocalWindows() {
        vocalWindows = Presentation.vocalWindows(for: lines, inferringPauses: infersPauses)
    }
    private var lineIndex = LyricLineIndex(lines: [])
    private(set) var wordsPerSecond: Double = 0
    private var cancellables: Set<AnyCancellable> = []
    private var lastKey: String?
    private var generation = UUID()
    private var lookupTask: Task<Void,Never>?
    private var upgradeTask: Task<Void,Never>?
    private var localTask: Task<Void,Never>?
    private var originalResult: LyricsFetcher.Result?
    /// Latest progress of this track's lookup, and how it was started.
    private var report: LyricLookupReport?
    private var lastLookup: (query: LyricQuery, force: Bool)?
    /// This lookup repeats one for lyrics already on screen (a settings
    /// change, a returning connection): see `reception`.
    private var rerun = false
    private var appliedRevision = 0
    private var retryWhenOnline = false
    /// Retries this track has had because a lookup looked offline while the
    /// Mac had a connection.
    private var onlineRetries = 0
    /// Local lyric files changed while the lookup was still collecting; they
    /// are re-read once it completes, without asking the services again.
    private var localChangedDuringLookup = false
    private let localWatcher = LocalLyricsWatcher()

    var currentQuery: LyricQuery? {
        guard let track = NowPlayingMonitor.shared.current else { return nil }
        return LyricQuery(title:track.title,artist:track.artist,album:track.album,duration:track.duration)
    }
    private init() {
        LyricSourceOptions.shared.$repairQQGaps.dropFirst().sink { [weak self] enabled in
            guard let self, let result = self.originalResult else { return }
            self.apply(result, repairQQGaps: enabled)
        }.store(in: &cancellables)
        Publishers.CombineLatest(LocalLyricOptions.shared.$folder, LyricSourceOptions.shared.$enabled)
            .sink { [weak self] folder, enabled in
                // A change re-reads only the local source, never the network.
                self?.localWatcher.watch(enabled.contains(.lyrimuse) ? folder : "") { [weak self] in
                    self?.localLyricsChanged()
                }
            }.store(in: &cancellables)
        LyricSourceOptions.shared.objectWillChange
            .debounce(for: .milliseconds(200), scheduler: RunLoop.main)
            .sink { [weak self] in self?.refresh(force: false) }.store(in: &cancellables)
        let readingOptions = RomanizationOptions.shared
        Publishers.CombineLatest4(readingOptions.$display, readingOptions.$language,
                                  readingOptions.$toneMarks, readingOptions.$preferSaved)
            .dropFirst().sink { [weak self] display, language, tones, saved in
                self?.updateReadings(options: .init(display: display, language: language, toneMarks: tones, preferSaved: saved))
            }.store(in: &cancellables)
        let translation = MachineTranslationOptions.shared
        Publishers.CombineLatest(translation.$enabled, translation.$target)
            .dropFirst().sink { [weak self] enabled, target in
                self?.updateMachineTranslations(enabled: enabled, target: target)
            }.store(in: &cancellables)
        machineTranslator.$translations.assign(to: &$machineTranslations)
        LyricConnectivity.shared.restored
            .sink { [weak self] in self?.connectionRestored() }.store(in: &cancellables)
        NowPlayingMonitor.shared.$current.removeDuplicates()
            .sink { [weak self] track in self?.handleTrackChange(track) }.store(in:&cancellables)
        handleTrackChange(NowPlayingMonitor.shared.current)
    }
    private func updateReadings(options: RomanizationOptions.Snapshot? = nil) {
        readingTask?.cancel(); let ticket = UUID(); readingGeneration = ticket
        romanized = [:]
        let value = lines, chosen = options ?? RomanizationOptions.shared.snapshot
        guard !value.isEmpty, chosen.display != .off else { return }
        readingTask = Task { [weak self] in
            let worker = Task.detached(priority: .utility) { LyricRomanizer.readings(value, options: chosen) }
            let readings = await withTaskCancellationHandler(operation: { await worker.value }, onCancel: { worker.cancel() })
            guard let self, !Task.isCancelled, self.readingGeneration == ticket else { return }
            self.romanized = readings
        }
    }
    private func updateMachineTranslations(enabled: Bool? = nil, target: String? = nil, force: Bool = false) {
        let options = MachineTranslationOptions.shared
        let chosen = target ?? options.target
        machineTranslator.update(lines: lines, enabled: enabled ?? options.enabled,
                                 target: chosen.isEmpty ? MachineTranslationPolicy.systemLanguage() : chosen, force: force)
    }
    /// Tries the song on screen again, after language packs were downloaded.
    func retryMachineTranslation() { updateMachineTranslations(force: true) }
    func activeLineIndex(at time:TimeInterval)->Int? {
        lineIndex.line(at: time)
    }
    func activeWordIndex(in line:LyricsFetcher.Line,at time:TimeInterval)->Int? {
        line.words.firstIndex {time >= $0.start && time < $0.end}
    }
    private func handleTrackChange(_ track:NowPlayingMonitor.Track?) {
        let next=track.map {LyricQuery(title:$0.title,artist:$0.artist,album:$0.album,duration:$0.duration).recordingKey}
        guard next != lastKey else {return}
        lastKey=next;lookupTask?.cancel();upgradeTask?.cancel();localTask?.cancel();generation=UUID()
        originalResult=nil;lines=[];plainLines=[];provenance=nil;notFound=false;notFoundAt=nil
        candidates=[];nearMisses=[];sourceStates=[:];pinnedMatch=false;cacheMessage=nil;isLoading=false;isRefining=false
        report=nil;lastLookup=nil;appliedRevision=0;retryWhenOnline=false;onlineRetries=0;localChangedDuringLookup=false;rerun=false
        if let track {
            let query=LyricQuery(title:track.title,artist:track.artist,album:track.album,duration:track.duration)
            startLookup(query,recording:query.recordingKey,force:false)
        }
    }
    func refresh(force:Bool=true,title:String?=nil,artist:String?=nil) {
        guard let current=currentQuery else {return}
        var query=current
        if let title,!title.trimmingCharacters(in:.whitespacesAndNewlines).isEmpty {query.title=title}
        if let artist,!artist.trimmingCharacters(in:.whitespacesAndNewlines).isEmpty {query.artist=artist}
        startLookup(query,recording:current.recordingKey,force:force)
    }
    private func startLookup(_ query:LyricQuery,recording:String,force:Bool) {
        lookupTask?.cancel();upgradeTask?.cancel();localTask?.cancel()
        let ticket=UUID();generation=ticket
        // `notFound` stays as it is until the result arrives, so a repeat
        // lookup that also finds nothing does not show the notice again.
        isLoading=true;isRefining=false;cacheMessage=nil
        // The previous lookup's report must not be refreshed into this one.
        report=nil;appliedRevision=0;retryWhenOnline=false;localChangedDuringLookup=false;lastLookup=(query,force)
        rerun = !force && originalResult != nil
        let progress: @Sendable (LyricLookupReport) async -> Void = { [weak self] update in
            await self?.receive(update,ticket:ticket,recording:recording)
        }
        lookupTask=Task { [weak self] in
            let report=await LyricsFetcher.shared.lookup(query,force:force,onUpdate:progress)
            guard !Task.isCancelled else {return}
            self?.receive(report,ticket:ticket,recording:recording)
        }
    }
    /// Progress and the final report both arrive here. The first result is
    /// shown at once; a later upgrade waits for a line boundary.
    private func receive(_ update:LyricLookupReport,ticket:UUID,recording:String) {
        guard generation==ticket,currentQuery?.recordingKey==recording else {return}
        report=update
        if !Self.sameCandidates(candidates,update.candidates) {candidates=update.candidates}
        if nearMisses.map(\.id) != update.rejected.map(\.id) || nearMisses.map(\.reason) != update.rejected.map(\.reason) {
            nearMisses=update.rejected
        }
        if sourceStates != update.states {sourceStates=update.states}
        if pinnedMatch != update.pinned {pinnedMatch=update.pinned}
        let step=Self.reception(of:update,applied:appliedRevision,rerun:rerun)
        if step != .ignore {
            appliedRevision=max(update.revision,1)
            if step == .swapAtBoundary {
                upgrade(to:update.result,ticket:ticket)
            } else if !LyricsFetcher.sameResult(update.result,originalResult) || (originalResult==nil && !notFound) {
                // A repeat lookup that finds the same lyrics leaves the screen alone.
                apply(update.result)
            }
            isLoading=false
        }
        isRefining = !update.isComplete && appliedRevision>0
        guard update.isComplete else {return}
        isLoading=false
        // An advert has no lyrics to find; only a stream may hide a song.
        if update.skippedStream && NowPlayingMonitor.shared.current?.isAdvert != true {
            cacheMessage="This stream doesn’t report a song length, so its lyrics aren’t looked up automatically. Use Search to find a song."
        }
        if update.offline && lines.isEmpty {foundNothingOffline()}
        if localChangedDuringLookup {
            localChangedDuringLookup=false
            localLyricsChanged()
        }
    }
    /// What `receive` does with a report: `.show` puts its result on screen
    /// now, `.swapAtBoundary` once the line being sung ends (`upgrade`).
    /// A lookup's first result shows at once and each later one swaps at a
    /// boundary. A rerun for lyrics already on screen shows nothing until it
    /// completes: its early choice could be worse than what is showing
    /// (which the rerun may not have heard from yet), and showing it would
    /// swap the song's lyrics twice, mid-line.
    enum Reception: Equatable { case ignore, show, swapAtBoundary }
    nonisolated static func reception(of update: LyricLookupReport, applied: Int, rerun: Bool) -> Reception {
        if rerun && !update.isComplete { return .ignore }
        guard update.revision > applied || (update.isComplete && applied == 0) else { return .ignore }
        if applied > 0 || (rerun && update.result != nil) { return .swapAtBoundary }
        return .show
    }
    /// Nothing was found and the network looked unreachable. The lookup runs
    /// again when a connection returns. If the Mac is online already -- the
    /// link came back before the lookup finished, or one connection dropped
    /// on a working link -- it runs again shortly instead, once per track.
    private func foundNothingOffline() {
        retryWhenOnline=true
        guard LyricConnectivity.shared.isOnline else {
            cacheMessage="You’re offline. Lyrics will be looked up again when the connection returns."
            return
        }
        guard onlineRetries<1 else {return}
        onlineRetries+=1
        connectionRestored()
    }
    /// Swaps in a better result between lines, so a line is never re-timed
    /// while it is sung. The lines change in one atomic replacement, which
    /// also takes the place of a swap still waiting for a boundary.
    private func upgrade(to result:LyricsFetcher.Result?,ticket:UUID) {
        upgradeTask?.cancel()
        guard let result,!LyricsFetcher.sameResult(result,originalResult) else {return}
        guard let sung=lineBeingSung() else {apply(result);return}
        upgradeTask=Task { [weak self] in
            let deadline=Date().addingTimeInterval(12)
            while Date()<deadline {
                try? await Task.sleep(for:.milliseconds(150))
                guard let self,!Task.isCancelled,self.generation==ticket else {return}
                if self.lineBeingSung() != sung {break}
            }
            guard let self,!Task.isCancelled,self.generation==ticket else {return}
            self.apply(result)
        }
    }
    /// The index of the displayed line being sung now, if any.
    private func lineBeingSung() -> Int? {
        guard !lines.isEmpty else {return nil}
        let time=NowPlayingMonitor.shared.playbackTime()+Settings.shared.lyricOffset+SongOffsets.shared.offset(for:currentQuery)
        return Self.lineBeingSung(lines,index:lineIndex,at:time)
    }
    /// The line being sung at `time`. When this changes -- into a gap or on
    /// to the next line -- a boundary has passed and lines can be swapped.
    static func lineBeingSung(_ lines:[LyricsFetcher.Line],index:LyricLineIndex,at time:Double) -> Int? {
        guard let found=index.line(at:time),lines.indices.contains(found),
              time<lines[found].end,!lines[found].isInstrumental else {return nil}
        return found
    }
    private static func sameCandidates(_ a:[LyricCandidate],_ b:[LyricCandidate]) -> Bool {
        a.count==b.count && zip(a,b).allSatisfy { x,y in
            x.id==y.id && x.title==y.title && x.artist==y.artist && x.duration==y.duration && LyricsFetcher.sameResult(x.result,y.result)
        }
    }
    /// Re-reads only the local source when files in the chosen folder change.
    /// Files that cannot be this track's change nothing (`refreshLocal`
    /// compares the track's own files), and no service is asked again.
    private func localLyricsChanged() {
        guard let recording=lastKey,currentQuery?.recordingKey==recording,!pinnedMatch,let last=lastLookup else {return}
        guard let previous=report,previous.isComplete else {
            // The lookup still collecting re-reads them once it completes.
            localChangedDuringLookup=true
            return
        }
        localTask?.cancel()
        let ticket=generation
        localTask=Task { [weak self] in
            guard let update=await LyricsFetcher.shared.refreshLocal(last.query,previous:previous) else {return}
            guard let self,!Task.isCancelled,self.generation==ticket,self.currentQuery?.recordingKey==recording else {return}
            self.report=update
            if !Self.sameCandidates(self.candidates,update.candidates) {self.candidates=update.candidates}
            self.nearMisses=update.rejected
            if self.sourceStates != update.states {self.sourceStates=update.states}
            guard update.revision != previous.revision else {return}
            self.appliedRevision=update.revision
            // The edited file takes over between lines, in place of any
            // upgrade still waiting for a boundary; a removed one goes now.
            if update.result == nil {
                self.upgradeTask?.cancel()
                self.apply(nil)
            } else {
                self.upgrade(to:update.result,ticket:ticket)
            }
        }
    }
    /// A lookup that found nothing while offline runs again once a
    /// connection returns.
    private func connectionRestored() {
        guard retryWhenOnline,let recording=lastKey else {return}
        retryWhenOnline=false
        let ticket=generation
        Task { [weak self] in
            // Name resolution can lag behind the link coming up.
            try? await Task.sleep(for:.seconds(2))
            guard let self,self.generation==ticket,self.currentQuery?.recordingKey==recording,self.lines.isEmpty else {return}
            self.refresh(force:false)
        }
    }
    /// Shows `result`, and keeps it to show again when the gap setting changes.
    private func apply(_ result:LyricsFetcher.Result?, repairQQGaps: Bool? = nil) {
        originalResult = result
        let shown = Self.presentation(of: result, repairingGaps: repairQQGaps ?? LyricSourceOptions.shared.repairQQGaps,
                                    track: currentQuery.map { ($0.title, $0.artist) })
        infersPauses = shown.infersPauses
        plainLines = shown.plainLines
        lines = shown.lines
        provenance = shown.provenance
        notFound = !shown.found; notFoundAt = shown.found ? nil : Date()
    }
    /// What the model shows for a lookup result.
    struct Presentation {
        var lines: [LyricsFetcher.Line] = []
        var plainLines: [String] = []
        var provenance: LyricsFetcher.Provenance?
        /// Whether long pauses in `lines` are inferred (`LyricGapTiming`).
        var infersPauses = false
        var found: Bool { !lines.isEmpty || !plainLines.isEmpty }
        /// When singing is heard, for the instrumental-break cue: the model's
        /// `vocalWindows` while this shows.
        var vocalWindows: [VCVocalWindow] { Self.vocalWindows(for: lines, inferringPauses: infersPauses) }
        static func vocalWindows(for lines: [LyricsFetcher.Line], inferringPauses: Bool) -> [VCVocalWindow] {
            LyricGapTiming.windows(for: lines, inferringPauses: inferringPauses)
        }
    }
    /// What `apply` shows for `result`. One setting ("Treat long pauses as
    /// instrumental", `repairingGaps`) covers both estimates of when singing
    /// stops: QQ Music's lyrics are split by `QQGapRepair`, which judges
    /// every line end itself, and other line-only lyrics (LRCLIB, NetEase's
    /// LRC, Kugou, Kuwo, local files, imports) have the breaks after their
    /// lines inferred by `LyricGapTiming`. Word timing and supplied line
    /// ends are never shortened.
    static func presentation(of result: LyricsFetcher.Result?, repairingGaps repair: Bool,
                             track: (title: String, artist: String)? = nil) -> Presentation {
        let (title, artist) = track ?? ("", "")
        switch result {
        case .synced(let value, let source) where !value.isEmpty:
            let qq = source.source.lowercased().contains("qq")
            let flagged = repair && qq ? QQGapRepair.repair(LyricCredits.flagging(value, title: title, artist: artist))
                                       : LyricCredits.flagging(value, title: title, artist: artist)
            // Estimated word timing is worked out afresh for the song as a
            // whole, so only an abnormally long window is shortened.
            let validated = LyricGapTiming.estimatingWords(in: flagged, inferringPauses: repair).map { original in
                var line = original; line.wordTimed = LyricsFetcher.validWordTiming(original); return line
            }
            let lines = LyricLineIndex.removingMixedTimingCopies(validated)
            return Presentation(lines: lines, provenance: .init(precision: LyricsFetcher.precision(lines), source: source.source),
                                infersPauses: repair && !qq)
        case .plain(let value, let source) where !value.isEmpty:
            let plain = LyricCredits.droppingLeadingCredits(value, title: title, artist: artist)
            return Presentation(plainLines: plain.isEmpty ? value : plain, provenance: source)
        default:
            return Presentation()
        }
    }
    /// An explicit choice replaces whatever the lookup was doing, and later
    /// local file changes or a returning connection leave it alone.
    private func stopLookup() {
        lookupTask?.cancel();upgradeTask?.cancel();localTask?.cancel();generation=UUID();isLoading=false;isRefining=false
        report=nil;lastLookup=nil;localChangedDuringLookup=false;retryWhenOnline=false;rerun=false
    }
    func select(_ candidate:LyricCandidate) {
        guard let query=currentQuery else {return}
        stopLookup()
        apply(candidate.result)
        pinnedMatch=SavedLyrics.save(candidate.result,key:query.recordingKey,pin:true)
        cacheMessage=pinnedMatch ? "Match saved for this recording." : "Match applied; it could not be saved."
    }
    func forgetSelection() {
        guard let query=currentQuery else {return}
        SavedLyrics.unpin(query.recordingKey);pinnedMatch=false
        refresh(force:true)
    }
    func importRomanization(_ text: String) -> String {
        guard let query = currentQuery, !lines.isEmpty else { return "Play a song with timed lyrics first." }
        let unmodified: [LyricsFetcher.Line]
        if let originalResult, case .synced(let value, _) = originalResult { unmodified = value } else { unmodified = lines }
        let updated = LocalLyricBridge.attachingReadings(text, to: unmodified, duration: query.duration)
        let count = updated.filter { $0.romanization != nil }.count
        guard count > 0 else { return "No reading timestamps matched this song’s lyric lines." }
        let source = provenance ?? .init(precision: LyricsFetcher.precision(updated), source: "Imported lyrics")
        let result = LyricsFetcher.Result.synced(updated, source)
        stopLookup()
        apply(result)
        pinnedMatch = SavedLyrics.save(result, key: query.recordingKey, pin: true)
        return "Readings attached to \(count) lines.\(pinnedMatch ? " Saved for this recording." : " Couldn’t save the match.")"
    }
    @discardableResult
    func importLyrics(_ text:String)->Bool {
        guard let query=currentQuery else {return false}
        let parsed = text.trimmingCharacters(in: .whitespacesAndNewlines).hasPrefix("<")
            ? TTMLLyricParser.parse(text, duration: query.duration)
            : LyricsFetcher.parseLRC(text,trackDuration:query.duration)
        guard !parsed.isEmpty else {cacheMessage="This file has no usable LRC or TTML timestamps.";return false}
        stopLookup()
        let result=LyricsFetcher.Result.synced(parsed,.init(precision:LyricsFetcher.precision(parsed),source:"Imported lyrics"))
        apply(result)
        pinnedMatch=SavedLyrics.save(result,key:query.recordingKey,pin:true)
        cacheMessage=pinnedMatch ? "Imported lyrics saved for this recording." : "Lyrics imported; the match could not be saved."
        return true
    }
}
