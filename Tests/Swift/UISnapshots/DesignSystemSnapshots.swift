import SwiftUI
@testable import VersoCustom

/// The shared components in Sources/UI/Design, alone and in their states,
/// and three reference sketches that compose them the way gui/spec.md lays
/// out the menu-bar panel, the Settings window and an onboarding step.
/// The sketches live here, in the test target, as references for the
/// builders: they are not product code. Owned by the foundation.
@MainActor
extension UISnapshotCatalogue {
    static var designSystem: [UISnapshotCase] {
        let every: [UISnapshotRenderer.Look] = [.light, .dark, .lightContrast, .darkContrast]
        return [
            UISnapshotCase("design/style-gallery", note: "Calm and Bold: every component, every state",
                           chrome: .plain, size: .fitting(width: 1080), looks: every, view: { _ in StyleGallery() }),
            UISnapshotCase("design/tokens", note: "colours, status, type scale", chrome: .plain, size: .fitting(width: 640),
                           looks: every, view: { _ in DesignTokensGallery() }),
            UISnapshotCase("design/status", note: "StatusBanner and StatusChip", chrome: .plain, size: .fitting(width: 420),
                           looks: every, view: { _ in StatusGallery() }),
            UISnapshotCase("design/now-playing", note: "TrackSummary, TransportControls, LyricQuoteView",
                           chrome: .plain, size: .fitting(width: 360), player: .playing,
                           view: { _ in NowPlayingGallery() }),
            UISnapshotCase("design/quick-controls", note: "WallpaperModeGrid, ToggleTile, TimingNudge, menu rows",
                           chrome: .plain, size: .fitting(width: 340), looks: every, player: .playing,
                           prepare: { $0.setSongOffset(0.25) }, view: { _ in QuickControlsGallery() }),
            UISnapshotCase("design/form", note: "captions, disabled reasons, value sliders, sidebar icons",
                           chrome: .plain, size: .fitting(width: 420), view: { _ in FormGallery() }),
            UISnapshotCase("design/player-access", note: "PlayerAccessRow in every state", chrome: .plain,
                           size: .fitting(width: 420), view: { _ in PlayerAccessGallery() }),
            UISnapshotCase("design/sketch-menu-panel", note: "reference: the menu-bar panel as the spec lays it out",
                           chrome: .menuBarPanel, size: .menuBarPanel, player: .playing,
                           prepare: { scene in
                               scene.lyricNow = LyricNow(line: "Static on the radio, gold across the bay",
                                                         next: "We were never meant to stay",
                                                         source: "LRCLIB", precision: "Word by word")
                               scene.setSongOffset(0.25)
                           }, view: { _ in MenuPanelSketch() }),
            UISnapshotCase("design/sketch-menu-panel-denied", note: "reference: the panel with Music refused",
                           chrome: .menuBarPanel, size: .menuBarPanel, player: .denied,
                           view: { _ in MenuPanelSketch() }),
            UISnapshotCase("design/sketch-settings", note: "reference: Settings, stage beside the inspector (13-inch default)",
                           chrome: .settingsWindow,
                           size: .frame(CGSize(width: 1100, height: 720)), player: .playing,
                           view: { _ in SettingsSketch() }),
            UISnapshotCase("design/sketch-onboarding", note: "reference: the access step of onboarding",
                           chrome: .window(styleMask: [.titled, .closable, .fullSizeContentView],
                                           configure: { $0.titlebarAppearsTransparent = true; $0.title = "Welcome to Lunalyr" }),
                           size: .frame(CGSize(width: 640, height: 540 + 28)), player: .needsConsent,
                           view: { _ in OnboardingSketch() }),
        ]
    }
}

// MARK: - Galleries

private struct GalleryTitle: View {
    let text: String
    init(_ text: String) { self.text = text }
    var body: some View {
        Text(text).font(.caption.weight(.semibold)).foregroundStyle(.tertiary).textCase(.uppercase)
    }
}

private struct DesignTokensGallery: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            GalleryTitle("Accent")
            HStack(spacing: 12) {
                swatch(Color.lunalyrAccent, "lunalyrAccent", textOn: .white)
                VStack(alignment: .leading, spacing: 4) {
                    Text("lunalyrAccentText").foregroundStyle(Color.lunalyrAccentText).font(.body.weight(.semibold))
                    Text("Links, values and symbols on the window").lunalyrText(.caption)
                }
                Spacer()
                Toggle("Switch", isOn: .constant(true)).labelsHidden()
                Button("Prominent") {}.buttonStyle(.lunalyrProminent)
            }
            GalleryTitle("Status")
            HStack(spacing: 10) {
                ForEach(LunalyrStatus.allCases, id: \.self) { status in
                    VStack(alignment: .leading, spacing: 6) {
                        Label(status.rawValue.capitalized, systemImage: status.symbol)
                            .foregroundStyle(status.text).font(.callout.weight(.semibold))
                        Text("Text on fill").font(.caption).foregroundStyle(status.text)
                    }
                    .padding(10)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(RoundedRectangle(cornerRadius: LunalyrDesign.Radius.card, style: .continuous).fill(status.fill))
                }
            }
            GalleryTitle("Type")
            VStack(alignment: .leading, spacing: 6) {
                Text("Large title · 22 semibold").lunalyrText(.largeTitle)
                Text("Title · 17 semibold").lunalyrText(.title)
                Text("Group title · 11 semibold, secondary").lunalyrText(.groupTitle)
                Text("Lyric · Static on the radio, gold across the bay").lunalyrText(.lyric)
                Text("Emphasis · Golden Hour Static").lunalyrText(.emphasis)
                Text("Body · Wallpaper type, Lyric size, Pause wallpaper").lunalyrText(.body)
                Text("Caption · Light covers get a soft shadow behind the lyrics, just strong enough for white text to stay readable.")
                    .lunalyrText(.caption)
                Text("Footnote · LRCLIB · Word by word · 9 sources").lunalyrText(.footnote)
                Text("Value · +0.25 s   120%").lunalyrText(.value)
            }
            GalleryTitle("Radii and surfaces")
            HStack(spacing: 12) {
                ForEach([("tag", LunalyrDesign.Radius.tag), ("artwork", LunalyrDesign.Radius.artwork),
                         ("card", LunalyrDesign.Radius.card), ("preview", LunalyrDesign.Radius.preview)], id: \.0) { item in
                    RoundedRectangle(cornerRadius: item.1, style: .continuous).fill(.quaternary)
                        .frame(width: 70, height: 46)
                        .overlay(Text("\(item.0) \(Int(item.1))").font(.caption2))
                }
                RoundedRectangle(cornerRadius: LunalyrDesign.Radius.card, style: .continuous).fill(Color.lunalyrStage)
                    .frame(width: 90, height: 46).overlay(Text("stage").font(.caption2))
            }
        }
        .padding(24)
        .lunalyrTint()
    }

    private func swatch(_ colour: Color, _ name: String, textOn: Color) -> some View {
        Text(name).font(.callout.weight(.semibold)).foregroundStyle(textOn)
            .padding(.horizontal, 12).padding(.vertical, 8)
            .background(RoundedRectangle(cornerRadius: LunalyrDesign.Radius.card, style: .continuous).fill(colour))
    }
}

private struct StatusGallery: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            GalleryTitle("StatusBanner · regular")
            StatusBanner(.warning, title: "Lunalyr can’t read Music",
                         message: "Turn on Music for Lunalyr in System Settings › Privacy & Security › Automation.") {
                Button("Open Privacy Settings") {}
                Button("Follow Spotify Only") {}
            }
            StatusBanner(.info, title: "Lunalyr needs permission to read Music",
                         message: "macOS asks once, when you choose Allow Access.", symbol: "lock.shield.fill") {
                Button("Allow Access…") {}
            }
            StatusBanner(.info, title: "Power saving",
                         message: "Low power visuals is on, so Lunalyr is showing Line patterns instead of “GPU effects”.",
                         symbol: "leaf.fill") {
                Button("Turn off Low power visuals") {}
                Button("Performance settings…") {}
            }
            StatusBanner(.critical, title: "Couldn’t start audio", message: "Check capture permission, then Reconnect.")
            StatusBanner(.success, title: "Music: allowed")
            GalleryTitle("StatusBanner · compact (menu-bar panel)")
            StatusBanner(.warning, title: "No audio is arriving",
                         message: "If macOS asked about System Audio Recording, allow Lunalyr, then Reconnect.",
                         density: .compact) {
                Button("Reconnect") {}
                Button("Open Privacy Settings…") {}
            }
            GalleryTitle("StatusChip")
            HStack {
                StatusChip(.info, "Wallpaper paused", symbol: "pause.rectangle.fill") { Button("Resume") {} }
                StatusChip(.neutral, "Lyrics hidden while mirroring", symbol: "rectangle.on.rectangle")
            }
        }
        .padding(20)
        .lunalyrTint()
    }
}

private struct NowPlayingGallery: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            GalleryTitle("TrackSummary + TransportControls")
            TrackSummary { TransportControls() }
            GalleryTitle("TrackSummary, small")
            TrackSummary(artworkSize: LunalyrDesign.Size.artworkSmall, titleLines: 1) { TransportControls(size: .small) }
            GalleryTitle("ArtworkView placeholders")
            HStack(spacing: 10) {
                ArtworkView(image: nil, placeholderSymbol: "music.note")
                ArtworkView(image: nil, placeholderSymbol: "dot.radiowaves.left.and.right")
                ArtworkView(image: nil, placeholderSymbol: "megaphone", size: 40)
            }
            GalleryTitle("LyricQuoteView")
            LyricQuoteView(now: LyricNow(line: "Static on the radio, gold across the bay", next: "We were never meant to stay",
                                        source: "LRCLIB", precision: "Word by word"))
            Divider()
            LyricQuoteView(now: LyricNow(isLoading: true))
            Divider()
            LyricQuoteView(now: LyricNow(notFound: true))
            Divider()
            LyricQuoteView(now: .none, hasTrack: false)
        }
        .padding(20)
        .lunalyrTint()
    }
}

private struct QuickControlsGallery: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            PanelSectionHeader("Wallpaper")
            WallpaperModeGrid(chooseImport: {})
            HStack(spacing: 6) {
                ToggleTile("Pause", symbol: "pause.fill", isOn: true, detail: "⌃⌥⌘P", accessibilityTitle: "Resume Wallpaper") {}
                ToggleTile("Hide lyrics", symbol: "eye.slash", detail: "⌃⌥⌘L") {}
                ToggleTile("Copy card", symbol: "photo.on.rectangle", detail: "⌃⌥⌘C") {}.disabled(true)
            }
            PanelSectionHeader("Lyric timing") { SongTimingNudge() }
            TimingNudge(offset: 0, earlier: {}, later: {}, reset: {})
            Divider()
            VStack(spacing: 0) {
                Button("Settings…") {}.buttonStyle(MenuRowButtonStyle(shortcut: "⌘,"))
                Button("Check for Updates…") {}.buttonStyle(MenuRowButtonStyle())
                Button("Quit Lunalyr") {}.buttonStyle(MenuRowButtonStyle(shortcut: "⌘Q"))
            }
            LunalyrPlayerPicker().controlSize(.small)
        }
        .padding(16)
        .lunalyrTint()
    }
}

private struct FormGallery: View {
    @State private var size = 1.2
    @State private var offset = 0.25

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Form {
                Section("Music response") {
                    Toggle("System audio", isOn: .constant(false)).disabled(true)
                    DisabledReason("Available once Visualisers return; power saving is showing Line patterns for now.")
                    ValueSlider("Lyric size", value: $size, in: 0.7...1.5,
                                format: { $0.formatted(.percent.precision(.fractionLength(0))) })
                    ValueSlider("Global offset", value: $offset, in: -3...3, step: 0.05, format: TimingNudge.format,
                                spoken: TimingNudge.spoken)
                    InspectorCaption("Saved for this recording and the lyric source showing. Positive offsets bring lyrics in earlier.")
                }
            }
            .formStyle(.grouped)
            .frame(height: 250)
            VStack(alignment: .leading, spacing: 8) {
                GalleryTitle("SettingsIcon")
                HStack(spacing: 14) {
                    ForEach(SettingsSketch.pages, id: \.title) { page in
                        SettingsIcon(symbol: page.symbol, tint: page.tint)
                    }
                }
            }
            .padding(.horizontal, 20).padding(.bottom, 20)
        }
        .lunalyrTint()
    }
}

private struct PlayerAccessGallery: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            let statuses: [PlayerAccessStatus] = [.granted, .notDetermined, .denied, .notRunning, .notInstalled, .unknown]
            ForEach(Array(statuses.enumerated()), id: \.offset) { _, status in
                PlayerAccessRow(kind: .appleMusic, status: status)
            }
            Divider()
            GalleryTitle("PlayerAccessList (live, from the scene)")
            PlayerAccessList()
        }
        .padding(20)
        .lunalyrTint()
    }
}

// MARK: - Reference sketches

/// The menu-bar panel as gui/spec.md lays it out, from shared components.
private struct MenuPanelSketch: View {
    @Model(\.extras) private var extras
    @Model(\.nowPlaying) private var playing

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            TrackSummary()
            HStack {
                Spacer()
                TransportControls()
                Spacer()
            }
            CurrentLyricLine()
            AutomationStatusBanner(density: .compact)
            Divider()
            PanelSectionHeader("Wallpaper") {
                Button("Options…") {}
                    .buttonStyle(.borderless).controlSize(.small).foregroundStyle(Color.lunalyrAccentText)
                    .help("Open Settings at Wallpaper")
            }
            WallpaperModeGrid()
            HStack(spacing: 6) {
                ToggleTile(extras.wallpaperPaused ? "Resume" : "Pause", symbol: extras.wallpaperPaused ? "play.fill" : "pause.fill",
                           isOn: extras.wallpaperPaused, accessibilityTitle: extras.pauseActionTitle) {}
                ToggleTile("Hide lyrics", symbol: "eye.slash", isOn: extras.lyricsHidden) {}
                ToggleTile("Copy card", symbol: "photo.on.rectangle") {}
                    .disabled(playing.current == nil)
            }
            PanelSectionHeader("Lyric timing") { SongTimingNudge() }
            Divider()
            VStack(spacing: 0) {
                HStack {
                    LunalyrPlayerPicker().controlSize(.small).fixedSize()
                    Spacer()
                }
                .padding(.horizontal, 8).padding(.bottom, 4)
                Button("Settings…") {}.buttonStyle(MenuRowButtonStyle(shortcut: "⌘,"))
                Button("Check for Updates…") {}.buttonStyle(MenuRowButtonStyle())
                Button("Quit Lunalyr") {}.buttonStyle(MenuRowButtonStyle(shortcut: "⌘Q"))
            }
            .padding(.horizontal, -8)
        }
        .padding(LunalyrDesign.Spacing.panel)
        .frame(width: LunalyrDesign.Layout.menuPanelWidth)
        .lunalyrTint()
    }
}

/// Settings at the 13-inch default size: sidebar, the preview stage with the
/// song beneath it, and the page's inspector beside it.
struct SettingsSketch: View {
    struct Page {
        let title: String
        let symbol: String
        let tint: Color
    }

    static let pages = [
        Page(title: "Wallpaper", symbol: "photo.artframe", tint: .indigo),
        Page(title: "Lyric style", symbol: "textformat", tint: .pink),
        Page(title: "Animated covers", symbol: "square.stack.fill", tint: .orange),
        Page(title: "Timing & sources", symbol: "clock.fill", tint: .blue),
        Page(title: "Romanisation", symbol: "character.book.closed.fill", tint: .teal),
        Page(title: "General", symbol: "gearshape.fill", tint: .gray),
        Page(title: "Menu bar & shortcuts", symbol: "command", tint: .gray),
        Page(title: "Performance", symbol: "leaf.fill", tint: .green),
        Page(title: "About", symbol: "info.circle.fill", tint: .gray),
    ]
    @State private var selection = "Wallpaper"
    @State private var speed = 1.0
    @State private var darkening = 1.0

    var body: some View {
        NavigationSplitView {
            List(selection: $selection) {
                Section("Look") { rows(0..<3) }
                Section("Lyrics") { rows(3..<5) }
                Section("Lunalyr") { rows(5..<9) }
            }
            .navigationSplitViewColumnWidth(min: 190, ideal: 210, max: 240)
        } detail: {
            HStack(spacing: 0) {
                VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.large) {
                    Spacer(minLength: 0)
                    PreviewStage(desktop: UISnapshotDisplay.screen) { LiveContentPlaceholder() }
                    TrackSummary(artworkSize: LunalyrDesign.Size.artworkSmall, titleLines: 1) {
                        TransportControls(size: .small)
                    }
                    AutomationStatusBanner()
                    Spacer(minLength: 0)
                }
                .padding(LunalyrDesign.Spacing.window)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(Color.lunalyrStage)
                Divider()
                Form {
                    Section("Wallpaper") {
                        WallpaperModeGrid()
                        InspectorCaption(Settings.BackgroundMode.auto.summary)
                    }
                    Section("Adjustments") {
                        Slider(value: $speed, in: 0.2...2) { Text("Motion speed") }
                        Slider(value: $darkening, in: 0...2) { Text("Darkening") }
                        Button("Reset Adjustments") {}
                    }
                }
                .formStyle(.grouped)
                .frame(width: LunalyrDesign.Layout.inspectorWidth)
            }
            .navigationTitle(selection)
            .toolbar {
                ToolbarItem(placement: .primaryAction) {
                    Button {} label: { Label("Hide Preview", systemImage: "eye.slash") }
                        .help("Hide the preview")
                }
            }
        }
        .lunalyrTint()
    }

    private func rows(_ range: Range<Int>) -> some View {
        ForEach(Self.pages[range], id: \.title) { page in
            Label {
                Text(page.title)
            } icon: {
                SettingsIcon(symbol: page.symbol, tint: page.tint)
            }
            .tag(page.title)
        }
    }
}

/// Onboarding's access step: what is asked and why, each player's state,
/// the way forward.
private struct OnboardingSketch: View {
    var body: some View {
        VStack(spacing: 0) {
            VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.large) {
                Spacer(minLength: 0)
                Image(systemName: "music.note.list")
                    .font(.system(size: 30, weight: .medium))
                    .foregroundStyle(Color.lunalyrAccentText)
                    .accessibilityHidden(true)
                Text("Let Lunalyr see what’s playing").lunalyrText(.largeTitle)
                Text("Lunalyr reads the song and its position from Music or Spotify, to time the lyrics. It controls playback only when you use its buttons. macOS asks once for each player.")
                    .font(.body).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                PlayerAccessList()
                    .padding(LunalyrDesign.Spacing.medium)
                    .background(RoundedRectangle(cornerRadius: LunalyrDesign.Radius.card, style: .continuous).fill(.quinary))
                LabeledContent("Follow") { LunalyrPlayerPicker(title: "Follow").labelsHidden().fixedSize() }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 48)
            .padding(.top, 28)
            Divider()
            HStack {
                HStack(spacing: 6) {
                    ForEach(0..<6) { step in
                        Circle().fill(step == 1 ? AnyShapeStyle(Color.lunalyrAccent) : AnyShapeStyle(.quaternary))
                            .frame(width: 7, height: 7)
                    }
                }
                .accessibilityElement(children: .ignore)
                .accessibilityLabel("Step 2 of 6")
                Spacer()
                Button("Back") {}
                Button("Continue") {}.keyboardShortcut(.defaultAction).buttonStyle(.lunalyrProminent)
            }
            .padding(.horizontal, 24).padding(.vertical, 14)
        }
        .frame(width: 640, height: 540)
        .lunalyrTint()
    }
}
