import AppKit
import SwiftUI
import UniformTypeIdentifiers

/// Timing & sources, split by scope (SPEC §8): on the left, on the canvas
/// grey, what applies to this song (its timing, where its lyrics came from,
/// finding others); on the right, on white, what applies to every song
/// (without synced lyrics, the sources and their order, local exports,
/// advanced timing).
///
/// "Is a song playing, which one" comes from the models (`currentQuery`):
/// the lyric view model and the source options are the app's own.
@MainActor
struct TimingPage: View {
    @Environment(\.lunalyrModels) private var models
    @Environment(\.undoManager) private var undoManager
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Model(\.nowPlaying) private var playing
    @Model(\.offsets) private var offsets
    @Model(\.settings) private var settings
    @Model(\.sources) private var options
    @Model(\.lyrics) private var model
    @Model(\.localLyrics) private var local
    @ObservedObject private var tapSync = LyricTapSyncController.shared
    /// `ArtistAliases.enabled`.
    @AppStorage("custom.artistAliases") private var artistAliases = true
    @State private var title = ""
    @State private var artist = ""
    /// The recording the search fields were last filled for.
    @State private var filledFor: String?
    @State private var message: String?
    @State private var defaultFolderUnavailable = false

    private var query: LyricQuery? { models.resolved.currentQuery }

    var body: some View {
        ThemedSplitForm {
            ThemedPageTitle("This song").padding(.bottom, -14)
            songHeader
            lyricTiming
            if model.provenance != nil || model.isRefining || model.pinnedMatch { lyricsSource }
            findLyrics
        } trailing: {
            ThemedPageTitle("Every song").padding(.bottom, -12)
            ThemedSection("Without synced lyrics") {
                ChoiceMenu("When none are found", selection: $settings.noLyrics, selectedTitle: \.title) {
                    ForEach(Settings.NoLyricsBehaviour.allCases) { Text($0.title).tag($0) }
                }
            }
            sources
            localExports
            ThemedSection("Advanced timing") {
                ValueSlider("Global offset", value: $settings.lyricOffset, in: -3...3, step: 0.05,
                            minimumLabel: "Later", maximumLabel: "Earlier",
                            format: TimingNudge.format, spoken: TimingNudge.spoken)
                InspectorCaption("Moves every song’s lyrics; the song offset adds to it.")
                CheckboxList {
                    Toggle(isOn: $options.repairQQGaps) {
                        CheckboxLabel("Treat long pauses as instrumental",
                                      caption: "A long gap after a line shows as a break, not a held line.", more: pauseCaption)
                    }
                    .accessibilityLabel("Treat long pauses as instrumental")
                }
            }
        }
        .lunalyrReveal(message)
        .lunalyrReveal(model.cacheMessage)
        .lunalyrReveal(tapSync.canUndo)
        .onAppear { fillTrack() }
        .onChange(of: query?.recordingKey) { _, _ in fillTrack() }
    }

    // MARK: This song

    @ViewBuilder private var songHeader: some View {
        if let track = playing.current, query != nil {
            HStack(spacing: 10) {
                ArtworkView(image: playing.currentTrackArtwork, placeholderSymbol: TrackSummary<EmptyView>.placeholderSymbol(for: track),
                            size: 44, placeholderDelay: LunalyrDesign.Motion.Hold.placeholderDelay)
                VStack(alignment: .leading, spacing: 1) {
                    Text(track.title)
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(Color.studio(.ink))
                        .lineLimit(2)
                    Text(Self.byline(track))
                        .font(.system(size: 12))
                        .foregroundStyle(Color.studio(.ink2))
                        .lineLimit(1)
                }
                .accessibilityElement(children: .combine)
            }
        } else {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Image(systemName: "music.note").accessibilityHidden(true)
                Text("Play a song to adjust its timing or find lyrics.")
            }
            .font(.system(size: 12.5))
            .foregroundStyle(Color.studio(.ink2))
        }
    }

    /// "Artist · Album", or as much of it as is known.
    static func byline(_ track: NowPlayingMonitor.Track) -> String {
        var parts: [String] = []
        if !track.artist.isEmpty { parts.append(track.artist) }
        if let album = track.album, !album.isEmpty { parts.append(album) }
        return parts.joined(separator: " · ")
    }

    private var lyricTiming: some View {
        ThemedSection("Lyric timing") {
            // Tap to sync beside the nudge while both fit, under it when not.
            ViewThatFits(in: .horizontal) {
                HStack(spacing: 6) {
                    SongTimingNudge()
                    Spacer(minLength: 8)
                    tapSyncButton
                }
                VStack(alignment: .leading, spacing: 8) {
                    SongTimingNudge()
                    tapSyncButton
                }
            }
            if tapSync.canUndo {
                // Tap to sync's own Undo, as in the menu-bar panel.
                Button("Undo Tap to Sync") { tapSync.undo() }
                    .controlSize(.small)
                    .help("Back to the timing from before the last tap to sync")
                    .revealRow()
            }
            if let query {
                let offset = offsets.offset(for: query)
                ValueSlider("Song offset", value: Binding(get: { offsets.offset(for: query) },
                                                          set: { offsets.set($0, for: query) }),
                            in: Self.offsetRange(for: offset), step: 0.05, minimumLabel: "Later", maximumLabel: "Earlier",
                            format: TimingNudge.format, spoken: TimingNudge.spoken)
            } else {
                ValueSlider("Song offset", value: .constant(0), in: -10...10, minimumLabel: "Later", maximumLabel: "Earlier",
                            format: TimingNudge.format, spoken: TimingNudge.spoken)
                    .disabled(true)
            }
            InspectorCaption("Positive offsets bring lyrics in earlier.", more: "Saved for this recording and the lyric source showing. Positive offsets bring lyrics in earlier.")
        }
    }

    private var tapSyncButton: some View {
        Button(tapSync.isListening ? LyricTapSyncController.stopTitle : LyricTapSyncController.menuTitle) {
            tapSync.toggle()
        }
        .controlSize(.small)
        .disabled(query == nil)
        .help("Press any key each time a lyric line starts; Lunalyr sets this song’s timing from the taps")
    }

    private var lyricsSource: some View {
        ThemedSection("Lyrics") {
            if let provenance = model.provenance {
                LabeledContent("From", value: provenance.source)
                    .accessibilityLabel("Lyrics from")
                    .accessibilityValue(provenance.source)
                LabeledContent("Timing", value: provenance.precision)
                if provenance.precision == "Mixed timing" {
                    InspectorCaption("Some lines animate word by word.", more: "Some lines animate word by word. The other lines appear all at once because they only have a line timestamp.")
                }
            }
            if model.isRefining {
                InspectorCaption("Showing the best match so far.", more: "Showing the best match so far. Slower sources are still being checked; a better one replaces it between lines.")
                    .environment(\.captionFollowsControl, model.provenance != nil)
            }
            if model.pinnedMatch {
                LabeledContent {
                    Button("Return to Automatic") { model.forgetSelection() }
                        .controlSize(.small)
                        .accessibilityLabel("Return to automatic selection")
                        .help("Let Lunalyr choose this song’s lyrics again")
                } label: {
                    Label("Using your saved choice", systemImage: "pin.fill")
                }
            }
        }
    }

    // MARK: Find lyrics

    private var findLyrics: some View {
        ThemedSection("Find lyrics") {
            LabeledField("Title", text: $title, prompt: "Title", onSubmit: search)
                .accessibilityLabel("Song title")
            LabeledField("Artist", text: $artist, prompt: "Artist", onSubmit: search)
            HStack(spacing: 8) {
                Button("Search", action: search)
                    .disabled(!canSearch)
                    .help("Ask every enabled source for these words")
                Button("Use Playing Song") { fillTrack(force: true) }
                    .disabled(query == nil)
                    .help("Put the playing song’s title and artist back")
                Spacer(minLength: 0)
                if model.isLoading || model.isRefining {
                    ProgressView().controlSize(.small).accessibilityLabel("Searching")
                }
            }
            .controlSize(.small)
            Button("Import LRC or TTML…") { importLyrics() }
                .controlSize(.small)
                .disabled(query == nil)
                .help("Use a lyrics file of your own for the playing song (up to 1 MB)")
            if query == nil {
                DisabledReason("Searching and importing work on the song playing.")
            }
            ForEach(model.candidates) { candidate in
                CandidateCard(candidate: candidate, reason: nil) { model.select(candidate) }
            }
            if !model.nearMisses.isEmpty {
                Text("Close matches not used")
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(Color.studio(.ink2))
                    .padding(.top, 4)
                    .accessibilityAddTraits(.isHeader)
                ForEach(model.nearMisses) { miss in
                    CandidateCard(candidate: miss.candidate, reason: miss.reason) { model.select(miss.candidate) }
                }
            }
            if model.sourceStates.values.contains(where: { $0.hasPrefix(LyricsFetcher.notNeeded) }) {
                InspectorCaption("Search asks every source.", more: "Sources that couldn’t beat the match showing weren’t asked further. Search asks every source.")
                    .environment(\.captionFollowsControl, false)
            }
            // A file that couldn't be imported warns; the cache's notes stay captions.
            if let message {
                WarningCaption(message)
                    .id(message)
                    .transition(.opacity)
            } else if let status = model.cacheMessage {
                InspectorCaption(status)
                    .environment(\.captionFollowsControl, false)
                    .id(status)
                    .transition(.opacity)
            }
        }
    }

    // MARK: Sources

    private var sources: some View {
        ThemedSection("Sources") {
            ChoiceMenu("Choose by", selection: $options.mode, selectedTitle: \.title) {
                ForEach(LyricSourceOptions.Mode.allCases) { Text($0.title).tag($0) }
            }
            InspectorCaption(options.mode.detail)
            InspectorCaption(Self.rankingCaption, more: LyricSourceOptions.ranking)
                .environment(\.captionFollowsControl, false)
            VStack(alignment: .leading, spacing: 8) {
                ForEach(options.order) { source in
                    SourceRow(source: source, enabled: Binding(
                        get: { options.enabled.contains(source) },
                        set: { enabled in
                            if enabled { options.enabled.insert(source) } else { options.enabled.remove(source) }
                        }),
                              state: model.sourceStates[source],
                              isFirst: options.order.first == source, isLast: options.order.last == source,
                              // Rows are keyed by source, so they slide to their new places.
                              move: { step in withAnimation(motion) { options.move(source, by: step) } })
                }
            }
            .padding(.top, 2)
            .accessibilityElement(children: .contain)
            .accessibilityLabel("Sources in priority order")
            Button("Restore Recommended Order", action: restoreRecommendedOrder)
                .controlSize(.small)
                .accessibilityLabel("Restore recommended source order")
                .help("Put the sources back in the order Lunalyr recommends")
            CheckboxList {
                Toggle(isOn: $artistAliases) {
                    CheckboxLabel("Search under other names",
                                  caption: "Asks again as “米津玄師” when “Kenshi Yonezu” finds nothing.",
                                  more: Self.aliasesDetail)
                }
                .accessibilityLabel("Search under other names")
            }
            InspectorCaption("Online sources receive song metadata.", more: "Online services receive song metadata. Apple Music’s local cache stays on this Mac. New sources are available to enable here; existing choices are preserved.")
                .environment(\.captionFollowsControl, false)
        }
    }

    /// The ranking in one line; `LyricSourceOptions.ranking` behind "More".
    static let rankingCaption = "Word-by-word lyrics come first, then line-synced; Music\u{2019}s own and LRCLIB lead each."

    static let aliasesDetail = "When NetEase, QQ, Kugou or Kuwo find nothing under a name like “Kenshi Yonezu”, they search again as “米津玄師”. The other names come from MusicBrainz, which receives the artist’s name. A song listed under an English title, like “Racing into the Night”, is searched again under its Japanese title, “夜に駆ける”, from Apple’s catalogue, which receives the title and artist."

    // MARK: Actions

    /// The song offset slider: ±10 s, widened to the ±30 s that tap to sync
    /// and the store allow once an offset is beyond 10 s, so the thumb shows
    /// it and a drag can't snap it back.
    static func offsetRange(for offset: Double) -> ClosedRange<Double> {
        abs(offset) > 10 ? -30...30 : -10...10
    }

    private var motion: Animation {
        LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion)
    }

    /// The sources slide back into the recommended order; Edit › Undo puts
    /// the order and Choose by back as they were.
    private func restoreRecommendedOrder() {
        let options = options
        let before = (order: options.order, mode: options.mode)
        withAnimation(motion) { options.resetOrder() }
        let after = (order: options.order, mode: options.mode)
        guard before.order != after.order || before.mode != after.mode else { return }
        let animation = motion
        undoManager?.registerSettingsChange("Restore Recommended Order", undo: {
            withAnimation(animation) { options.order = before.order; options.mode = before.mode }
        }, redo: {
            withAnimation(animation) { options.order = after.order; options.mode = after.mode }
        })
    }

    private var localExports: some View {
        ThemedSection("Local exports") {
            LabeledContent("Lyrimuse lyrics folder") {
                Text(local.folder.isEmpty ? "None chosen" : local.folder)
                    .textSelection(.enabled)
                    .lineLimit(2)
                    .truncationMode(.middle)
            }
            ViewThatFits(in: .horizontal) {
                HStack(spacing: 8) { folderButtons }
                    .fixedSize()
                VStack(alignment: .leading, spacing: 8) { folderButtons }
            }
            .controlSize(.small)
            if defaultFolderUnavailable {
                StatusBanner(.warning, title: "Default folder unavailable",
                             message: "Export lyrics in Lyrimuse first, or choose the folder where you already save them.")
            }
        }
    }

    @ViewBuilder private var folderButtons: some View {
        Button("Use Default Lyrimuse Folder", action: useDefaultFolder)
            .help("Read exported lyrics from Lyrimuse’s standard folder")
        Button("Choose…", action: chooseFolder)
            .accessibilityLabel("Choose Lyrics Folder")
            .help("Choose the folder Lyrimuse exports lyrics to")
    }

    private var canSearch: Bool { query != nil && !model.isLoading }

    private func search() {
        guard canSearch else { return }
        message = nil
        model.refresh(force: true, title: title, artist: artist)
    }

    private func chooseFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.title = "Choose the lyrics folder"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        local.folder = url.path
        useLocalExports()
    }

    private func useDefaultFolder() {
        guard local.selectDefaultFolder() else {
            defaultFolderUnavailable = true
            return
        }
        useLocalExports()
    }

    private func useLocalExports() {
        defaultFolderUnavailable = false
        if options.enabled.contains(.lyrimuse) {
            model.refresh(force: false)
        } else {
            // Changing the enabled sources already schedules a refresh.
            options.enabled.insert(.lyrimuse)
        }
    }

    /// What "Treat long pauses as instrumental" changes. QQ Music's lines are
    /// cut short; for other sources a break is shown only by the three dots,
    /// so without them the setting changes nothing there.
    private var pauseCaption: String {
        let caption = "Lyrics timed only by line don’t say when singing stops. With Show three dots during instrumental breaks on (Lyric style), a long gap after a line is shown as an instrumental break instead of one held line. Turn this off for songs with long held notes."
        return settings.showInterludes
            ? caption
            : caption + " The three dots are off, so this changes only QQ Music’s lyrics, whose held lines end early, and how long the words of a line before a long gap take to light up."
    }

    private func importLyrics() {
        switch InspectorImport.lyrics(title: "Import lyrics for the playing song") {
        case nil: return
        case .failure(let failure)?: message = failure.localizedDescription
        case .success(let text)?: message = nil; model.importLyrics(text)
        }
    }

    /// Fills the search fields for a newly playing song. Pausing, or the track
    /// briefly disappearing, never wipes what was typed. A note about the
    /// previous song's import goes with it.
    private func fillTrack(force: Bool = false) {
        guard let query, force || filledFor != query.recordingKey else { return }
        if filledFor != query.recordingKey { message = nil }
        filledFor = query.recordingKey
        title = query.title
        artist = query.artist
    }
}

/// A lyric source: on or off, what timing it gives, what it said about the
/// song playing, and buttons to move it up or down the priority order.
@MainActor
private struct SourceRow: View {
    let source: LyricSourceID
    @Binding var enabled: Bool
    let state: String?
    let isFirst: Bool
    let isLast: Bool
    let move: (Int) -> Void

    var body: some View {
        HStack(alignment: .center, spacing: 8) {
            Toggle(isOn: $enabled) {
                VStack(alignment: .leading, spacing: 1) {
                    Text(source.title)
                    Text(source.timing)
                        .font(LunalyrDesign.Studio.TypeScale.caption)
                        .foregroundStyle(Color.studio(.ink2))
                    if let state {
                        Text(state)
                            .font(LunalyrDesign.Studio.TypeScale.caption)
                            .foregroundStyle(Color.studio(.ink2))
                    }
                }
            }
            .toggleStyle(ThemedCheckboxStyle())
            .accessibilityLabel(source.title)
            HStack(spacing: 4) {
                orderButton("chevron.up", title: "Raise \(source.title) priority", disabled: isFirst) { move(-1) }
                orderButton("chevron.down", title: "Lower \(source.title) priority", disabled: isLast) { move(1) }
            }
        }
    }

    private func orderButton(_ symbol: String, title: String, disabled: Bool,
                             action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 10, weight: .semibold))
                .frame(width: 24, height: 20)
                .themedControl(radius: 5, state: disabled ? .disabled : .normal)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .foregroundStyle(Color.studio(.ink))
        .disabled(disabled)
        .help(title)
        .accessibilityLabel(title)
    }
}

/// A set of lyrics a source offered, with the way to use it for this song;
/// for a close match not used, why not.
@MainActor
private struct CandidateCard: View {
    let candidate: LyricCandidate
    let reason: String?
    let use: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.xSmall) {
            HStack(alignment: .firstTextBaseline) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(candidate.title)
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(Color.studio(.ink))
                    Text(Self.details(candidate))
                        .font(LunalyrDesign.Studio.TypeScale.caption)
                        .foregroundStyle(Color.studio(.ink2))
                }
                Spacer(minLength: LunalyrDesign.Spacing.small)
                Button("Use for This Song", action: use)
                    .controlSize(.small)
                    .fixedSize()
                    .accessibilityLabel("Use \(candidate.source.title) for this song")
            }
            Text([candidate.source.title, candidate.provenance.precision].joined(separator: " · "))
                .font(LunalyrDesign.Studio.TypeScale.caption)
                .foregroundStyle(Color.studio(.ink2))
            if !candidate.metadataReported {
                Label("The service didn’t confirm which recording it matched", systemImage: "questionmark.circle")
                    .labelStyle(CaptionLabelStyle())
            }
            if candidate.hasReadings {
                Label("Saved pronunciation readings", systemImage: "character.book.closed")
                    .labelStyle(CaptionLabelStyle())
            }
            if let reason {
                WarningCaption(reason)
                    .accessibilityLabel("Not used: \(reason)")
            }
        }
        .padding(.vertical, 2)
        .accessibilityElement(children: .contain)
    }

    /// Artist and, when the service reported it, the matched recording's length.
    static func details(_ candidate: LyricCandidate) -> String {
        var parts = [candidate.artist.isEmpty ? "No artist given" : candidate.artist]
        if let duration = candidate.duration, duration > 0 { parts.append(LyricQuery.clock(duration)) }
        return parts.joined(separator: " · ")
    }
}

/// Lyric and reading files chosen in an open panel, read with the limits
/// the lyric parsers expect.
@MainActor
enum InspectorImport {
    /// The chosen lyric file's text, or why it can't be used; nil when cancelled.
    static func lyrics(title: String) -> Result<String, LyricTextDecoder.Failure>? {
        guard let url = choose(title: title) else { return nil }
        return read(url).flatMap { LyricTextDecoder.lyrics($0) }
    }

    private static func choose(title: String) -> URL? {
        let panel = NSOpenPanel()
        panel.title = title
        panel.allowedContentTypes = [.plainText, UTType(filenameExtension: "lrc") ?? .plainText,
                                     UTType(filenameExtension: "ttml") ?? .xml]
        guard panel.runModal() == .OK else { return nil }
        return panel.url
    }

    private static func read(_ url: URL) -> Result<Data, LyricTextDecoder.Failure> {
        if let size = try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize, size > 1_000_000 { return .failure(.tooLarge) }
        guard let data = try? Data(contentsOf: url) else { return .failure(.unopenable) }
        return data.count <= 1_000_000 ? .success(data) : .failure(.tooLarge)
    }
}
