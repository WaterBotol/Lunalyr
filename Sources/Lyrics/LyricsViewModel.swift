import Combine
import Foundation

@MainActor
final class LyricsViewModel: ObservableObject {
    static let shared = LyricsViewModel()
    @Published var lines: [LyricsFetcher.Line] = [] {
        didSet {
            vocalWindows = LyricGapTiming.windows(for: lines)
            lineIndex = LyricLineIndex(lines: lines)
            if let first = lines.first, let last = lines.last, last.end - first.start > 1 {
                wordsPerSecond = Double(lines.reduce(0) { $0 + $1.words.count }) / (last.end - first.start)
            } else { wordsPerSecond = 0 }
            updateReadings()
        }
    }
    @Published private(set) var romanized: [Int: [LyricsFetcher.Word]] = [:]
    private var readingTask: Task<Void, Never>?
    private var readingGeneration = UUID()
    @Published var plainLines: [String] = []
    @Published var isLoading = false
    @Published var notFound = false
    private(set) var notFoundAt: Date?
    @Published var provenance: LyricsFetcher.Provenance?
    @Published var candidates: [LyricCandidate] = []
    @Published var sourceStates: [LyricSourceID:String] = [:]
    @Published var pinnedMatch = false
    @Published var cacheMessage: String?
    private(set) var vocalWindows: [VCVocalWindow] = []
    private var lineIndex = LyricLineIndex(lines: [])
    private(set) var wordsPerSecond: Double = 0
    private var cancellables: Set<AnyCancellable> = []
    private var lastKey: String?
    private var generation = UUID()
    private var lookupTask: Task<Void,Never>?
    private var originalResult: LyricsFetcher.Result?
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
                self?.localWatcher.watch(enabled.contains(.lyrimuse) ? folder : "") { [weak self] in
                    self?.refresh(force: true)
                }
            }.store(in: &cancellables)
        let readingOptions = RomanizationOptions.shared
        Publishers.CombineLatest4(readingOptions.$display, readingOptions.$language,
                                  readingOptions.$toneMarks, readingOptions.$preferSaved)
            .dropFirst().sink { [weak self] display, language, tones, saved in
                self?.updateReadings(options: .init(display: display, language: language, toneMarks: tones, preferSaved: saved))
            }.store(in: &cancellables)
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
    func activeLineIndex(at time:TimeInterval)->Int? {
        lineIndex.line(at: time)
    }
    func activeWordIndex(in line:LyricsFetcher.Line,at time:TimeInterval)->Int? {
        line.words.firstIndex {time >= $0.start && time < $0.end}
    }
    private func handleTrackChange(_ track:NowPlayingMonitor.Track?) {
        let next=track.map {LyricQuery(title:$0.title,artist:$0.artist,album:$0.album,duration:$0.duration).recordingKey}
        guard next != lastKey else {return}
        lastKey=next;lookupTask?.cancel();generation=UUID()
        originalResult=nil;lines=[];plainLines=[];provenance=nil;notFound=false;notFoundAt=nil
        candidates=[];sourceStates=[:];pinnedMatch=false;cacheMessage=nil;isLoading=false
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
        lookupTask?.cancel()
        let ticket=UUID();generation=ticket
        isLoading=true;notFound=false;cacheMessage=nil
        lookupTask=Task { [weak self] in
            let report=await LyricsFetcher.shared.lookup(query,force:force)
            guard let self,!Task.isCancelled,self.generation==ticket,
                  self.currentQuery?.recordingKey==recording else {return}
            self.isLoading=false;self.candidates=report.candidates;self.sourceStates=report.states
            self.pinnedMatch=report.pinned
            self.apply(report.result)
        }
    }
    private func apply(_ result:LyricsFetcher.Result?, repairQQGaps: Bool? = nil) {
        originalResult = result
        lines=[];plainLines=[];provenance=nil;notFound=false;notFoundAt=nil
        switch result {
        case .synced(let value,let source) where !value.isEmpty:
            let repair = repairQQGaps ?? LyricSourceOptions.shared.repairQQGaps
            lines = repair && source.source.lowercased().contains("qq") ? QQGapRepair.repair(value) : value
            provenance=source
        case .plain(let value,let source) where !value.isEmpty:plainLines=value;provenance=source
        default:notFound=true;notFoundAt=Date()
        }
    }
    func select(_ candidate:LyricCandidate) {
        guard let query=currentQuery else {return}
        lookupTask?.cancel();generation=UUID();isLoading=false
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
        lookupTask?.cancel(); generation = UUID(); isLoading = false
        apply(result)
        pinnedMatch = SavedLyrics.save(result, key: query.recordingKey, pin: true)
        return "Readings attached to \(count) lines.\(pinnedMatch ? " Saved for this recording." : " Couldn’t save the match.")"
    }
    @discardableResult
    func importLyrics(_ text:String)->Bool {
        guard let query=currentQuery else {return false}
        let parsed=LyricsFetcher.parseLRC(text,trackDuration:query.duration)
        guard !parsed.isEmpty else {cacheMessage="This file has no usable LRC timestamps.";return false}
        lookupTask?.cancel();generation=UUID();isLoading=false
        let result=LyricsFetcher.Result.synced(parsed,.init(precision:LyricsFetcher.precision(parsed),source:"Imported LRC"))
        apply(result)
        pinnedMatch=SavedLyrics.save(result,key:query.recordingKey,pin:true)
        cacheMessage=pinnedMatch ? "Imported lyrics saved for this recording." : "Lyrics imported; the match could not be saved."
        return true
    }
}
