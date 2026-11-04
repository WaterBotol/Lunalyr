import AppKit
import SwiftUI
import UniformTypeIdentifiers

/// A single live desktop, with native inspectors. No sample animations or
/// duplicate renderer timelines are mounted inside the inspectors.
@MainActor
struct SettingsWindowView: View {
    enum Page: String, CaseIterable, Identifiable {
        case wallpaper, typography, lyrics, readings, covers, performance, about
        var id: String { rawValue }
        var title: String {
            switch self {
            case .wallpaper: return "Wallpaper"
            case .typography: return "Typography"
            case .lyrics: return "Lyrics"
            case .readings: return "Readings"
            case .covers: return "Album covers"
            case .performance: return "Performance"
            case .about: return "About"
            }
        }
        var icon: String {
            switch self {
            case .wallpaper: return "rectangle.on.rectangle"
            case .typography: return "textformat"
            case .lyrics: return "text.magnifyingglass"
            case .readings: return "character.book.closed"
            case .covers: return "square.stack"
            case .performance: return "gauge.with.dots.needle.50percent"
            case .about: return "info.circle"
            }
        }
    }
    @State private var page: Page = .wallpaper
    @ObservedObject private var settings = Settings.shared
    @ObservedObject private var playing = NowPlayingMonitor.shared
    @ObservedObject private var lyrics = LyricsViewModel.shared
    var body: some View {
        NavigationSplitView {
            List(selection: $page) {
                Section("CREATE") {
                    ForEach([Page.wallpaper, .typography]) { item in navigation(item) }
                }
                Section("LISTEN") {
                    ForEach([Page.lyrics, .readings, .covers]) { item in navigation(item) }
                }
                Section("LUNALYR") {
                    ForEach([Page.performance, .about]) { item in navigation(item) }
                }
            }
            .listStyle(.sidebar)
            .navigationTitle("Lunalyr")
            .navigationSplitViewColumnWidth(min: 150, ideal: 160, max: 190)
        } detail: {
            HSplitView {
                workspace.frame(minWidth: 320, maxWidth: .infinity, maxHeight: .infinity)
                inspector.frame(minWidth: 330, idealWidth: 370, maxWidth: 430, maxHeight: .infinity)
            }
            .navigationTitle(page.title)
        }
        .tint(.mint)
        .frame(minWidth: 880, minHeight: 620)
    }
    private func navigation(_ item: Page) -> some View {
        Label(item.title, systemImage: item.icon).tag(item)
            .padding(.vertical, 3)
    }
    private var workspace: some View {
        VStack(alignment: .leading, spacing: 20) {
            HStack {
                Label("YOUR DESKTOP", systemImage: "display").font(.caption.weight(.semibold)).tracking(1.5)
                Spacer()
                HStack(spacing: 5) {
                    Circle().fill(playing.isPlaying ? .mint : .secondary).frame(width: 5, height: 5)
                    Text(playing.isPlaying ? "Live" : "Paused").font(.caption)
                }
            }.foregroundStyle(.secondary)
            Spacer(minLength: 0)
            SettingsLivePreview()
                .aspectRatio(16 / 9, contentMode: .fit)
                .clipShape(RoundedRectangle(cornerRadius: 14))
                .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(.white.opacity(0.08)))
                .shadow(color: .black.opacity(0.18), radius: 18, y: 8)
            HStack(spacing: 12) {
                if let image = playing.artwork {
                    Image(nsImage: image).resizable().scaledToFill().frame(width: 44, height: 44)
                        .clipShape(RoundedRectangle(cornerRadius: 8))
                } else {
                    Image(systemName: "music.note").frame(width: 44, height: 44).background(.quaternary, in: RoundedRectangle(cornerRadius: 8))
                }
                VStack(alignment: .leading, spacing: 4) {
                    Text(playing.current?.title ?? "Play a song in Music").font(.headline).lineLimit(2)
                    Text(playing.current?.artist ?? "Your wallpaper follows playback").font(.subheadline).foregroundStyle(.secondary).lineLimit(1)
                }
                Spacer(minLength: 0)
            }
            HStack(spacing: 24) {
                Spacer()
                Button { playing.previousTrack() } label: { Image(systemName: "backward.fill") }
                Button { playing.playPause() } label: { Image(systemName: playing.isPlaying ? "pause.fill" : "play.fill").font(.title3) }
                Button { playing.nextTrack() } label: { Image(systemName: "forward.fill") }
                Spacer()
            }.buttonStyle(.plain).disabled(playing.current == nil).padding(.vertical, 8)
            VStack(alignment: .leading, spacing: 7) {
                Label(lyrics.provenance?.source ?? "Waiting for lyrics", systemImage: "text.quote")
                Label(lyrics.provenance?.precision ?? "No timing selected", systemImage: "clock")
            }.font(.caption).foregroundStyle(.secondary)
            if settings.effectiveBackgroundMode != settings.backgroundMode {
                Label("Low power wallpaper · 30 fps", systemImage: "leaf").font(.caption).foregroundStyle(.mint)
            }
            Spacer(minLength: 0)
            Text("Changes apply to your desktop immediately.")
                .font(.caption).foregroundStyle(.tertiary)
        }
        .padding(24)
        .background(Color(nsColor: .underPageBackgroundColor))
    }
    @ViewBuilder private var inspector: some View {
        switch page {
        case .wallpaper: WallpaperInspector()
        case .typography: TypographyInspector()
        case .lyrics: LyricsInspector()
        case .readings: ReadingsInspector()
        case .covers: CoversInspector()
        case .performance: PerformanceInspector()
        case .about: AboutInspector()
        }
    }
}

@MainActor
private struct WallpaperInspector: View {
    @ObservedObject private var s = Settings.shared
    @ObservedObject private var audio = AudioReactiveController.shared
    @State private var importMessage: String?
    @State private var importing = false
    private func chooseWallpaper() {
        let panel = NSOpenPanel()
        panel.title = "Choose your wallpaper"
        panel.allowedContentTypes = [.image, .movie]
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        importing = true; importMessage = nil
        Task {
            defer { importing = false }
            do {
                let imported = try await CustomWallpaperStore.importFile(url)
                s.customWallpaperPath = imported.url.path
                s.customWallpaperName = imported.originalName
                s.backgroundMode = .custom
            } catch { importMessage = error.localizedDescription }
        }
    }
    var body: some View {
        Form {
            Section("Wallpaper") {
                Picker("Kind", selection: $s.backgroundMode) {
                    ForEach(Settings.BackgroundMode.allCases) { Text($0.title).tag($0) }
                }
                if s.backgroundMode == .visualizer {
                    Picker("Scene", selection: $s.audioVisualizerStyle) {
                        ForEach(AudioVisualizerStyle.allCases) { Text($0.title).tag($0) }
                    }
                    Text(s.audioVisualizerStyle.detail).font(.caption).foregroundStyle(.secondary)
                }
                if s.backgroundMode == .shader || s.backgroundMode == .auto {
                    Picker("GPU effect", selection: $s.shaderStyle) {
                        Text("Match the music").tag(Optional<ShaderStyle>.none)
                        ForEach(ShaderStyle.allCases) { Text($0.title).tag(Optional($0)) }
                    }
                }
                if s.backgroundMode == .pattern || s.backgroundMode == .auto {
                    Picker("Pattern", selection: $s.patternStyle) {
                        Text("Match the music").tag(Optional<BackgroundStyle>.none)
                        ForEach(BackgroundStyle.allCases) { Text($0.title).tag(Optional($0)) }
                    }
                }
                if s.backgroundMode != .custom {
                    Slider(value: $s.effectSpeed, in: 0.2...2) { Text("Generated motion speed") }
                }
            }
            if s.backgroundMode == .custom {
                Section("Your wallpaper") {
                    Button(importing ? "Importing…" : "Choose image or video…") { chooseWallpaper() }.disabled(importing)
                    if !s.customWallpaperName.isEmpty { Text(s.customWallpaperName).font(.caption).foregroundStyle(.secondary) }
                    Picker("Placement", selection: $s.customWallpaperPlacement) {
                        ForEach(Settings.MotionPlacement.allCases) { Text($0.title).tag($0) }
                    }
                    Text("Images stay still. Videos loop silently. Lunalyr keeps a copy so moving the original won’t break your wallpaper.")
                        .font(.caption).foregroundStyle(.secondary)
                    if let importMessage { Text(importMessage).font(.caption).foregroundStyle(.orange) }
                }
            }
            if s.backgroundMode == .visualizer {
                Section("Music response") {
                    Toggle("System audio", isOn: Binding(get: { s.audioReactiveEnabled }, set: { enabled in
                        if enabled { audio.start() } else { audio.stop() }
                    }))
                    Text(audio.status).font(.caption).foregroundStyle(.secondary)
                    HStack {
                        ProgressView("Bass", value: min(1, audio.levels.bass * 5))
                        ProgressView("Mids", value: min(1, audio.levels.mid * 5))
                        ProgressView("Highs", value: min(1, audio.levels.treble * 5))
                    }.font(.caption2)
                    Text("\(audio.packetsReceived) audio packets").font(.caption2).foregroundStyle(.secondary)
                    Picker("Capture", selection: $s.audioCaptureMethod) {
                        ForEach(AudioCaptureMethod.allCases) { Text($0.title).tag($0) }
                    }.onChange(of: s.audioCaptureMethod) { _, _ in if s.audioReactiveEnabled { audio.reconnect() } }
                    Slider(value: $s.audioSensitivity, in: 0.3...3) { Text("Sensitivity") }
                    HStack {
                        Button("Reconnect") { audio.reconnect() }
                        Button("Permissions…") { audio.openPrivacySettings() }
                    }
                    Button("Copy audio diagnostics") { audio.copyDiagnostics() }
                    Text("Audio is analysed on this Mac. No recording is saved or uploaded.").font(.caption).foregroundStyle(.secondary)
                }
            }
            Section("Colour & contrast") {
                if s.backgroundMode != .custom {
                    Slider(value: $s.vividness, in: 0.4...1.8) { Text("Colour richness") }
                }
                Slider(value: $s.scrimStrength, in: 0...2) { Text("Darkening") }
                if s.backgroundMode == .pattern {
                    Slider(value: $s.patternOpacity, in: 0...1) { Text("Pattern visibility") }
                }
                Button("Reset appearance adjustments") { s.resetAdvanced() }
            }
        }.formStyle(.grouped)
    }
}

@MainActor
private struct TypographyInspector: View {
    @ObservedObject private var s = Settings.shared
    private var style: LyricComposition { s.lyricComposition ?? .scattered }
    var body: some View {
        Form {
            Section("Composition") {
                Picker("Layout", selection: $s.lyricComposition) {
                    Text("Match the music").tag(Optional<LyricComposition>.none)
                    ForEach(LyricComposition.allCases) { Text($0.title).tag(Optional($0)) }
                }
                Text(style.detail).font(.caption).foregroundStyle(.secondary)
                Toggle("Add Editorial typography", isOn: $s.editorialTypography)
                Slider(value: $s.lyricScale, in: 0.7...1.5) { Text("Lyric size") }
            }
            Section("Typeface") {
                Picker("Family", selection: $s.fontChoice) {
                    ForEach(Settings.FontChoice.allCases) { Text($0.title).tag($0) }
                }
                if s.fontChoice == .google {
                    TextField("Google Fonts family", text: $s.googleFontFamily)
                    Button("Apply font") { s.applyGoogleFont(family: s.googleFontFamily) }
                    if let message = s.googleStatus { Text(message).font(.caption).foregroundStyle(.secondary) }
                }
            }
            Section("Word entrances") {
                Toggle("Reveal sung words", isOn: Binding(get: { s.revealsWords(in: style) }, set: { s.wordRevealByStyle[style.rawValue] = $0 }))
                    .disabled(style.arrivalOnly)
                Picker("Entrance", selection: Binding(get: { s.arrival(in: style) }, set: { s.wordArrivalByStyle[style.rawValue] = $0.rawValue })) {
                    ForEach(WordArrival.allCases) { Text($0.title).tag($0) }
                }
                Toggle("Faint next-word preview", isOn: Binding(get: { s.previewsNextWord(in: style) }, set: { s.nextWordPreviewByStyle[style.rawValue] = $0 }))
                Text("Saved for the selected layout. Choose a fixed layout to edit these controls. Line-only lyrics stay together.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            .disabled(s.lyricComposition == nil)
            if style.hasConnections {
                Section("Constellation") {
                    Slider(value: $s.constellationSpread, in: 0.4...1.8) { Text("Spread") }
                    Slider(value: $s.constellationLinkOpacity, in: 0...0.6) { Text("Links") }
                }
            }
            if style == .echo { Section("Echo") { Slider(value: $s.echoStrength, in: 0...1.8) { Text("Repeats") } } }
        }.formStyle(.grouped)
    }
}

@MainActor
private struct ReadingsInspector: View {
    @ObservedObject private var o = RomanizationOptions.shared
    @ObservedObject private var model = LyricsViewModel.shared
    @State private var message: String?
    var body: some View {
        Form {
            Section("Romanisation") {
                Picker("Display", selection: $o.display) { ForEach(RomanizationOptions.Display.allCases) { Text($0.title).tag($0) } }
                Picker("Language", selection: $o.language) { ForEach(RomanizationOptions.Language.allCases) { Text($0.title).tag($0) } }
                Toggle("Keep tone marks", isOn: $o.toneMarks)
                Toggle("Prefer saved readings", isOn: $o.preferSaved)
                Text("Readings follow the original timing. Local transliteration can miss names and unusual pronunciations.").font(.caption).foregroundStyle(.secondary)
            }
            Section("Saved readings") {
                Button("Import reading LRC…") {
                    if let text = InspectorImport.text(title: "Choose a reading LRC file") { message = model.importRomanization(text) }
                }.disabled(model.lines.isEmpty)
                if let message { Text(message).font(.caption) }
                Text("Saved readings are pronunciation guides. Translations are a separate feature and are not generated here.").font(.caption).foregroundStyle(.secondary)
            }
        }.formStyle(.grouped)
    }
}

@MainActor
private struct CoversInspector: View {
    @ObservedObject private var s = Settings.shared
    @ObservedObject private var playing = NowPlayingMonitor.shared
    var body: some View {
        Form {
            Section("Animated album artwork") {
                Toggle("Use animated covers", isOn: $s.useMotionArtwork)
                Text("Available covers play in Match the music mode. Low power visuals take precedence.").font(.caption).foregroundStyle(.secondary)
                Label(playing.motionArtwork == nil ? "No motion cover for this album" : "Motion cover available", systemImage: "play.rectangle")
                    .font(.caption).foregroundStyle(.secondary)
                Picker("Placement", selection: $s.motionPlacement) { ForEach(Settings.MotionPlacement.allCases) { Text($0.title).tag($0) } }
                Slider(value: $s.motionBlur, in: 0...25) { Text("Softening") }
                if let track = playing.current {
                    Toggle("Exclude this album", isOn: Binding(get: { s.excludesMotion(artist: track.artist, album: track.album) },
                        set: { _ in s.setMotionExcluded(!s.excludesMotion(artist: track.artist, album: track.album), artist: track.artist, album: track.album) }))
                }
            }
            if !s.motionExclusions.isEmpty {
                Section("Excluded albums") {
                    ForEach(s.motionExclusions, id: \.self) { name in
                        HStack {
                            Text(name.replacingOccurrences(of: "|", with: " · ")).font(.caption)
                            Spacer()
                            Button { s.motionExclusions.removeAll { $0 == name } } label: { Image(systemName: "minus.circle") }
                                .help("Allow this album")
                        }
                    }
                }
            }
        }.formStyle(.grouped)
    }
}

@MainActor
private struct PerformanceInspector: View {
    @ObservedObject private var s = Settings.shared
    @ObservedObject private var updates = AppUpdates.shared
    var body: some View {
        Form {
            Section("Rendering") {
                Picker("Frame rate", selection: $s.animationRate) { ForEach(Settings.AnimationRate.allCases) { Text($0.title).tag($0) } }
                Toggle("Low power visuals", isOn: $s.lowPowerVisuals)
                Text("Uses a lightweight wallpaper at 30 fps. Your full wallpaper returns when this is switched off.").font(.caption).foregroundStyle(.secondary)
                Toggle("Follow macOS Low Power Mode", isOn: $s.respectLowPower)
                Picker("On battery", selection: $s.onBattery) { ForEach(Settings.PowerBehaviour.allCases) { Text($0.title).tag($0) } }
                Toggle("Pause fully covered wallpapers", isOn: $s.pauseWhenCovered)
            }
            Section("Between songs") {
                Toggle("Show instrumental interludes", isOn: $s.showInterludes)
                Picker("Without lyrics", selection: $s.noLyrics) { ForEach(Settings.NoLyricsBehaviour.allCases) { Text($0.title).tag($0) } }
            }
            Section("App") {
                Toggle("Launch at login", isOn: $s.launchAtLogin)
                if let error = s.launchAtLoginError { Text(error).font(.caption).foregroundStyle(.orange) }
                Button("Check for updates…") { updates.check() }.disabled(updates.buttonDisabled)
            }
        }.formStyle(.grouped)
    }
}

@MainActor
private struct AboutInspector: View {
    var body: some View {
        Form {
            Section {
                Text("Lunalyr").font(.largeTitle.weight(.semibold))
                Text("Desktop lyrics, in motion.").foregroundStyle(.secondary)
                Text("Version \(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "")").font(.caption)
                Link("Project & downloads", destination: URL(string: "https://github.com/WaterBotol/Lunalyr")!)
            }
            Section("Built on open source") {
                Text("Based on Verso by Akki / AkkiCode06. The upstream GPLv3 with Commons Clause licence and attribution are preserved.")
                Text("Visualisers adapted from Synesthia (MIT). Sparkle, Yams and libyaml retain their bundled notices.")
                Button("View bundled licences") { NSWorkspace.shared.open(Bundle.main.resourceURL!) }
            }.font(.caption)
            Section("Privacy") {
                Text("Audio analysis and readings stay on this Mac. Enabled lyric services receive song metadata. No Apple Music account cookies are requested.").font(.caption)
            }
        }.formStyle(.grouped)
    }
}

@MainActor
private enum InspectorImport {
    static func text(title: String) -> String? {
        let panel = NSOpenPanel(); panel.title = title
        panel.allowedContentTypes = [.plainText, UTType(filenameExtension: "lrc") ?? .plainText, UTType(filenameExtension: "ttml") ?? .xml]
        guard panel.runModal() == .OK, let url = panel.url,
              let data = try? Data(contentsOf: url), data.count <= 1_000_000 else { return nil }
        return String(data: data, encoding: .utf8) ?? String(data: data, encoding: .utf16)
    }
}

@MainActor
private struct LyricsInspector: View {
    @ObservedObject private var options = LyricSourceOptions.shared
    @ObservedObject private var model = LyricsViewModel.shared
    @ObservedObject private var offsets = SongOffsets.shared
    @ObservedObject private var settings = Settings.shared
    @ObservedObject private var local = LocalLyricOptions.shared
    @State private var title = ""
    @State private var artist = ""
    @State private var message: String?
    var body: some View {
        Form {
            Section("This song") {
                if let query = model.currentQuery {
                    Text(query.title).font(.headline)
                    Text(query.artist).foregroundStyle(.secondary)
                    HStack {
                        Text("Song offset")
                        Spacer()
                        Text(String(format: "%+.2f s", offsets.offset(for: query))).monospacedDigit()
                    }
                    Slider(value: Binding(get: { offsets.offset(for: query) }, set: { offsets.set($0, for: query) }), in: -10...10, step: 0.05)
                    HStack {
                        Button("Earlier") { offsets.set(offsets.offset(for: query) + 0.1, for: query) }
                        Button("Later") { offsets.set(offsets.offset(for: query) - 0.1, for: query) }
                        Spacer()
                        Button("Reset") { offsets.set(0, for: query) }
                    }.controlSize(.small)
                    Text("Saved for this recording. Positive offsets bring lyrics in earlier.").font(.caption).foregroundStyle(.secondary)
                } else { Text("Play a song to adjust its timing or find lyrics.").foregroundStyle(.secondary) }
                if let provenance = model.provenance {
                    Label(provenance.source, systemImage: "text.quote")
                    Label(provenance.precision, systemImage: "clock")
                }
                if model.pinnedMatch {
                    Label("Using your saved choice", systemImage: "pin.fill")
                    Button("Return to automatic selection") { model.forgetSelection() }
                }
            }
            Section("Find lyrics") {
                TextField("Song title", text: $title)
                TextField("Artist", text: $artist)
                HStack {
                    Button("Search") { model.refresh(force: true, title: title, artist: artist) }
                        .disabled(model.currentQuery == nil || model.isLoading)
                    Button("Use playing song") { fillTrack() }.disabled(model.currentQuery == nil)
                    if model.isLoading { ProgressView().controlSize(.small) }
                }
                Button("Import LRC or TTML…") {
                    if let text = InspectorImport.text(title: "Import lyrics for the playing song") { _ = model.importLyrics(text) }
                }.disabled(model.currentQuery == nil)
                ForEach(model.candidates) { candidate in
                    VStack(alignment: .leading, spacing: 8) {
                        Text(candidate.title).font(.headline)
                        Text(candidate.artist).font(.caption).foregroundStyle(.secondary)
                        Label(candidate.source.title, systemImage: "text.quote").font(.caption)
                        Label(candidate.provenance.precision, systemImage: "clock").font(.caption).foregroundStyle(.secondary)
                        if candidate.hasReadings { Label("Saved pronunciation readings", systemImage: "character.book.closed").font(.caption) }
                        Button("Use for this song") { model.select(candidate) }.controlSize(.small)
                    }.padding(.vertical, 6)
                }
                if let status = message ?? model.cacheMessage { Text(status).font(.caption).foregroundStyle(.secondary) }
            }
            Section("Selection") {
                Picker("Choose by", selection: $options.mode) {
                    ForEach(LyricSourceOptions.Mode.allCases) { Text($0.title).tag($0) }
                }
                Toggle("Prefer real word timing across sources", isOn: $options.preferWordTiming)
                Toggle("Prefer LRCLIB with real word timing", isOn: $options.preferWordTimedLRCLIB)
                Text("Accuracy first checks the recording match and actual timing. Complete word timing wins over line timing. Source order breaks ties; catalogue size is not used.").font(.caption).foregroundStyle(.secondary)
                Button("Restore recommended source order") { options.resetOrder() }
            }
            Section("Sources · priority order") {
                ForEach(options.order) { source in
                    VStack(alignment: .leading, spacing: 6) {
                        Toggle(source.title, isOn: Binding(get: { options.enabled.contains(source) }, set: { enabled in
                            if enabled { options.enabled.insert(source) } else { options.enabled.remove(source) }
                        }))
                        HStack {
                            Text(source.timing).font(.caption).foregroundStyle(.secondary)
                            Spacer()
                            Button { options.move(source, by: -1) } label: { Image(systemName: "chevron.up") }
                                .disabled(options.order.first == source).help("Raise priority")
                            Button { options.move(source, by: 1) } label: { Image(systemName: "chevron.down") }
                                .disabled(options.order.last == source).help("Lower priority")
                        }.controlSize(.mini)
                        if let state = model.sourceStates[source] { Text(state).font(.caption2).foregroundStyle(.secondary) }
                    }.padding(.vertical, 4)
                }
                Text("Enabled services receive song metadata. Unofficial endpoints may be unavailable. New sources are available to enable here; existing choices are preserved.").font(.caption).foregroundStyle(.secondary)
            }
            Section("Local exports") {
                Button("Choose lyrics folder…") {
                    let panel = NSOpenPanel(); panel.canChooseDirectories = true; panel.canChooseFiles = false
                    if panel.runModal() == .OK, let url = panel.url { local.folder = url.path; options.enabled.insert(.lyrimuse); model.refresh() }
                }
                if !local.folder.isEmpty { Text(local.folder).font(.caption).foregroundStyle(.secondary).textSelection(.enabled) }
            }
            Section("Advanced timing") {
                HStack { Text("Global offset"); Spacer(); Text(String(format: "%+.2f s", settings.lyricOffset)).monospacedDigit() }
                Slider(value: $settings.lyricOffset, in: -3...3, step: 0.05)
                Toggle("Treat long QQ pauses as instrumental", isOn: $options.repairQQGaps)
            }
        }.formStyle(.grouped)
        .onAppear { fillTrack() }
        .onChange(of: model.currentQuery?.recordingKey) { _, _ in fillTrack() }
    }
    private func fillTrack() { title = model.currentQuery?.title ?? ""; artist = model.currentQuery?.artist ?? "" }
}
