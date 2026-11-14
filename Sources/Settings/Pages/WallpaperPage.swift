import AppKit
import SwiftUI

/// The Wallpaper inspector: the Looks, the type's own options, how
/// visualisers hear the music, how the lyrics stay readable, and the
/// adjustments every drawn type shares. The type itself is chosen on the
/// canvas, under the picture (`WallpaperTypeChoices`).
@MainActor
struct WallpaperPage: View {
    /// Opens the Performance page, where the power settings live.
    var showPerformance: () -> Void = {}
    var scrollRequest: SettingsScrollRequest?
    /// Shared with the canvas's type tiles, whose Image or video tile asks
    /// for a file through it, so its progress and errors show here.
    @ObservedObject var importer: WallpaperImporter
    @Model(\.settings) private var s
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.undoManager) private var undoManager

    init(showPerformance: @escaping () -> Void = {}, scrollRequest: SettingsScrollRequest? = nil,
         importer: WallpaperImporter? = nil) {
        self.showPerformance = showPerformance
        self.scrollRequest = scrollRequest
        // On its own (UI snapshots' inspector probe), a page keeps its own.
        self.importer = importer ?? WallpaperImporter()
    }

    var body: some View {
        LookInspectorForm {
            // Above everything: what power saving changed is about the whole picture.
            if let notice = s.powerSavingNotice {
                PowerSavingBanner(notice: notice, override: { s.overridePowerSaving(notice.constraint) },
                                  showPerformance: showPerformance)
            }
            LooksSection()
            VStack(alignment: .leading, spacing: LunalyrDesign.Studio.Layout.inspectorRowSpacing) {
                InspectorTitle(title: "Wallpaper", subtitle: s.backgroundMode.summary)
                // A failed import leaves the type as it was, so its error
                // shows here rather than in a section that isn't showing.
                if importer.showsProgress, s.backgroundMode != .custom {
                    ImportProgress()
                        .revealRow()
                } else if let message = importer.message, s.backgroundMode != .custom {
                    ImportFailure(message: message)
                        .revealRow()
                }
            }
            typeSection
                .revealRow()
            if s.backgroundMode == .visualizer {
                MusicResponseSection()
                    .id(SettingsAnchor.musicResponse)
                    .revealRow()
            }
            LyricBackingSection()
            if s.backgroundMode != .desktop {
                adjustments
                    .revealRow()
            }
        }
        // Rows and sections that come and go with the type glide in; the
        // sections below slide rather than jump.
        .lunalyrReveal(s.backgroundMode)
        .lunalyrReveal(importer.message)
        .lunalyrReveal(importer.showsProgress)
        .scrollsToSettingsAnchor(scrollRequest)
    }

    /// The chosen type's own options, in a section named for it.
    @ViewBuilder private var typeSection: some View {
        switch s.backgroundMode {
        case .auto:
            InspectorSection("Match the music") {
                shaderPicker
                patternPicker
                InspectorCaption("Match the music picks one of each per song; choose one to pin it.")
            }
        case .shader:
            InspectorSection("GPU effects") { shaderPicker }
        case .pattern:
            InspectorSection("Line patterns") { patternPicker }
        case .visualizer:
            InspectorSection("Visualiser") { VisualiserSceneSection() }
        case .desktop:
            InspectorSection("Your wallpaper") {
                Text("Only lyrics are drawn over your desktop. Keep using your current macOS wallpaper or wallpaper app; Lunalyr leaves it in place.")
                    .fixedSize(horizontal: false, vertical: true)
                InspectorCaption("Shows your macOS desktop picture behind the lyrics.", more: "The preview puts your macOS desktop picture behind the lyrics. A wallpaper app’s own artwork can’t be shown there, but stays visible on each monitor.")
            }
        case .custom:
            InspectorSection("Image or video") { importedOptions }
        case .musicVideo:
            InspectorSection("Music video") { MusicVideoSection() }
        case .colourOnly:
            InspectorSection("Colour only") {
                Toggle("Album gradient", isOn: $s.albumGradientWallpaper)
                InspectorCaption("Blends the cover’s colours into a moving gradient.", more: "Blend the colours from across the cover into a slowly moving gradient.")
            }
        }
    }

    private var shaderPicker: some View {
        ChoiceMenu("GPU effect", selection: $s.shaderStyle, selectedTitle: { $0?.title ?? "Match the music" }) {
            Text("Match the music").tag(Optional<ShaderStyle>.none)
            ForEach(ShaderStyle.allCases) { Text($0.title).tag(Optional($0)) }
        }
    }

    private var patternPicker: some View {
        ChoiceMenu("Pattern", selection: $s.patternStyle, selectedTitle: { $0?.title ?? "Match the music" }) {
            Text("Match the music").tag(Optional<BackgroundStyle>.none)
            ForEach(BackgroundStyle.allCases) { Text($0.title).tag(Optional($0)) }
        }
    }

    @ViewBuilder private var importedOptions: some View {
        LabeledContent {
            HStack(spacing: LunalyrDesign.Spacing.small) {
                if importer.showsProgress {
                    ProgressView().controlSize(.small).accessibilityLabel("Importing")
                }
                Button(importer.importing ? "Importing…" : "Choose…") { importer.choose(into: s) }
                    .disabled(importer.importing)
                    .accessibilityLabel(importer.importing ? "Importing" : "Choose Image or Video")
                    .help("Choose a picture or video for the wallpaper")
            }
        } label: {
            VStack(alignment: .leading, spacing: 2) {
                Text("File")
                Text(WorkspaceCopy.importedFileLabel(name: s.customWallpaperName, path: s.customWallpaperPath))
                    .lunalyrText(.caption)
                    .lineLimit(2)
                    .truncationMode(.middle)
            }
        }
        ChoiceMenu("Placement", selection: $s.customWallpaperPlacement, selectedTitle: \.title) {
            ForEach(Settings.MotionPlacement.allCases) { Text($0.title).tag($0) }
        }
        InspectorCaption(WorkspaceCopy.importedWallpaperDetail(reduceMotion: reduceMotion))
        if let message = importer.message {
            ImportFailure(message: message)
                .revealRow()
        }
    }

    /// The sliders glide back (outside a drag they animate their own
    /// changes), and Edit › Undo puts every value back, Softening included.
    private func resetAdjustments() {
        let before = s.advancedValues
        s.resetAdvanced()
        let after = s.advancedValues
        let settings = s
        undoManager?.registerSettingsChange("Reset Adjustments",
                                            undo: { settings.restoreAdvanced(before) },
                                            redo: { settings.restoreAdvanced(after) })
    }

    private var adjustments: some View {
        InspectorSection("Adjustments") {
            if s.backgroundMode != .custom && s.backgroundMode != .musicVideo {
                ValueSlider("Motion speed", value: $s.effectSpeed, in: 0.2...2, step: 0.05,
                            format: SettingsFormat.multiplier)
                    .revealRow()
                if s.backgroundMode != .visualizer || s.audioVisualizerStyle != .light3D || s.light3DColours == .album {
                    ValueSlider("Colour richness", value: $s.vividness, in: 0.4...1.8, step: 0.01,
                                format: SettingsFormat.percent)
                        .revealRow()
                }
            }
            ValueSlider("Darkening", value: $s.scrimStrength, in: s.scrimRange, step: 0.01, format: SettingsFormat.percent)
            if let note = WorkspaceCopy.increasedContrastDarkening(preferred: s.preferredScrimStrength,
                                                                   increaseContrast: s.increaseContrast) {
                InspectorCaption(note)
            }
            if s.backgroundMode == .pattern {
                ValueSlider("Pattern visibility", value: $s.patternOpacity, in: 0...1, step: 0.01,
                            format: SettingsFormat.percent)
                    .revealRow()
            }
            Button("Reset Adjustments", action: resetAdjustments)
                .disabled(s.advancedIsDefault)
                .help("Motion speed, Colour richness, Darkening, Pattern visibility and the covers’ Softening go back to how Lunalyr comes")
        }
    }
}

/// What power saving changed, with the way out on the spot and the way to
/// the settings behind it. Only while it changes what is drawn.
@MainActor
private struct PowerSavingBanner: View {
    let notice: Settings.PowerSavingNotice
    let override: () -> Void
    let showPerformance: () -> Void

    var body: some View {
        StatusBanner(.info, title: "Power saving",
                     message: [notice.message, notice.frameRateNote].compactMap { $0 }.joined(separator: " "),
                     symbol: symbol) {
            Button(notice.constraint.overrideTitle, action: override)
            Button("Performance Settings…", action: showPerformance)
                .help("Open the Performance page, where power saving is set up")
        }
        // A section header's bold type and header trait are not the banner's.
        .font(.body)
        .fontWeight(.regular)
        .accessibilityRemoveTraits(.isHeader)
        .padding(.top, LunalyrDesign.Spacing.xSmall)
    }

    private var symbol: String {
        switch notice.constraint {
        case .lowPowerVisuals, .lowPowerMode: return "leaf.fill"
        case .battery: return "battery.50percent"
        }
    }
}

/// Why the chosen picture or video couldn't be used.
private struct ImportProgress: View {
    var body: some View {
        HStack(spacing: LunalyrDesign.Spacing.small) {
            ProgressView().controlSize(.small)
            Text("Importing…").lunalyrText(.caption)
        }
        .accessibilityElement(children: .combine)
    }
}

private struct ImportFailure: View {
    let message: String

    var body: some View {
        StatusBanner(.warning, title: "Couldn’t use that file", message: message)
    }
}

/// The visualiser's scene and its own options.
@MainActor
private struct VisualiserSceneSection: View {
    @Model(\.settings) private var s

    var body: some View {
        ChoiceMenu("Scene", selection: $s.audioVisualizerStyle, selectedTitle: \.title) {
            ForEach(AudioVisualizerStyle.allCases) { Text($0.title).tag($0) }
        }
        InspectorCaption(s.audioVisualizerStyle.detail)
        if s.audioVisualizerStyle == .wavefieldOriginal {
            ChoiceMenu("Wavefield preset", selection: $s.wavefieldPreset, selectedTitle: { id in
                WavefieldWebPreset.catalogue.first { $0.id == id }?.name ?? id
            }) {
                ForEach(WavefieldWebPreset.catalogue) { Text($0.name).tag($0.id) }
            }
            ChoiceSegments("Projection", selection: $s.wavefieldProjection,
                           cases: Array(WavefieldProjection.allCases), label: \.title)
            VisualizerColoursPicker(engine: .wavefield)
        }
        if s.audioVisualizerStyle == .wavecore {
            VisualizerColoursPicker(engine: .wavecore)
        }
        if s.audioVisualizerStyle == .light3D {
            ChoiceMenu("Colours", selection: $s.light3DColours, selectedTitle: \.title) {
                ForEach(Settings.Light3DColours.allCases) { Text($0.title).tag($0) }
            }
        }
        VisualizerSceneControls(style: s.audioVisualizerStyle)
    }
}

/// System audio for the visualisers: on or off, how strongly scenes answer,
/// what capture is doing, the fixes when it needs them, and diagnostics.
@MainActor
private struct MusicResponseSection: View {
    @Model(\.settings) private var s
    @Environment(\.lunalyrModels) private var models
    @State private var showsAudioSetup = false
    /// Not observed: the meters publish many times a second.
    private var audio: AudioReactiveController { models.resolved.audio }

    /// Capture only runs while a visualiser is on screen. When power saving
    /// has replaced it, switching on would do nothing, so it waits;
    /// switching off still works.
    private var waitsForVisualiser: Bool {
        s.effectiveBackgroundMode != .visualizer && !s.audioReactiveEnabled
    }

    var body: some View {
        InspectorSection("Music response") {
            Toggle("System audio", isOn: Binding(get: { s.audioReactiveEnabled }, set: { enabled in
                if enabled { audio.start() } else { audio.stop() }
            }))
            .disabled(waitsForVisualiser)
            if waitsForVisualiser {
                DisabledReason(WorkspaceCopy.systemAudioWaits(showing: s.effectiveBackgroundMode))
            }
            ValueSlider("Sensitivity", value: $s.audioSensitivity, in: 0.3...3, step: 0.05,
                        minimumLabel: "Low", maximumLabel: "High", format: SettingsFormat.multiplier)
            .disabled(!s.audioReactiveEnabled)
            if !s.audioReactiveEnabled && !waitsForVisualiser {
                DisabledReason("Switch on System audio to set how strongly scenes respond.")
            }
            // Switched on, the status below already says why capture is
            // paused, as the power-saving banner does.
            if s.audioReactiveEnabled {
                AudioCaptureStatus()
            }
            DisclosureGroup("Audio setup & diagnostics", isExpanded: $showsAudioSetup) {
                ChoiceMenu("Capture method", selection: $s.audioCaptureMethod, selectedTitle: \.title) {
                    ForEach(AudioCaptureMethod.allCases) { Text($0.title).tag($0) }
                }
                .onChange(of: s.audioCaptureMethod) { _, _ in if s.audioReactiveEnabled { audio.reconnect() } }
                AudioMeters()
                // The same action, and name, as beside a failing status.
                HStack {
                    Button("Open Privacy Settings…") { audio.openPrivacySettings() }
                    Button("Copy Audio Diagnostics") { audio.copyDiagnostics() }
                        .help("Copies a report of how audio capture is set up, to paste into a bug report")
                }
                .controlSize(.small)
            }
        }
    }
}

/// What capture is doing: a caption while it works, a warning with its
/// fixes once it needs attention. Reads only the status and state, so the
/// meters don't redraw the page.
@MainActor
private struct AudioCaptureStatus: View {
    @Environment(\.lunalyrModels) private var models
    // Filled from the controller as the view appears (a published value
    // arrives on subscription).
    @State private var status = AudioReactiveController.offMessage
    @State private var state = AudioCaptureState.off
    /// Whether compatibility capture is offered depends on it.
    @State private var usingTap = false

    var body: some View {
        let audio = models.resolved.audio
        let _ = usingTap
        Group {
            if state.needsAttention {
                StatusBanner(.warning, title: "System audio needs attention", message: status) {
                    if audio.offersReconnect {
                        Button("Reconnect") { audio.reconnect() }
                    }
                    if audio.suggestsPrivacySettings {
                        Button("Open Privacy Settings…") { audio.openPrivacySettings() }
                            .help("Opens the Privacy & Security pane that lets Lunalyr hear system audio")
                    }
                    if audio.offersCompatibilityCapture {
                        Button("Try Compatibility Capture") { audio.tryCompatibilityCapture() }
                            .help("Uses Screen Recording permission instead of System Audio Recording.")
                    }
                }
            } else {
                Label {
                    Text(status)
                } icon: {
                    Image(systemName: "waveform")
                }
                .labelStyle(CaptionLabelStyle())
                .accessibilityElement(children: .combine)
            }
        }
        .onReceive(audio.$status.removeDuplicates()) { status = $0 }
        .onReceive(audio.$state.removeDuplicates()) { state = $0 }
        .onReceive(audio.$usingTap.removeDuplicates()) { usingTap = $0 }
    }
}

/// Live levels and the packet count, only while diagnostics are open: the
/// one view on the page that redraws with the meters.
@MainActor
private struct AudioMeters: View {
    @Model(\.audio) private var audio

    var body: some View {
        VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.small) {
            HStack(spacing: LunalyrDesign.Spacing.medium) {
                ProgressView("Bass", value: min(1, audio.levels.bass * 5))
                ProgressView("Mids", value: min(1, audio.levels.mid * 5))
                ProgressView("Highs", value: min(1, audio.levels.treble * 5))
            }
            Text("\(audio.packetsReceived) audio packets").lunalyrText(.footnote)
        }
        .font(LunalyrTextStyle.footnote.font)
        .foregroundStyle(.secondary)
    }
}
