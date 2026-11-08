import SwiftUI

/// Text colour, strength, speed and the custom gradient.
@MainActor
struct LyricColourSection: View {
    @Model(\.colours) private var options

    var body: some View {
        Section("Text colour") {
            ChoiceMenu("Colour", selection: $options.mode, selectedTitle: \.title) {
                ForEach(LyricColourMode.choices) { Text($0.title).tag($0) }
            }
            LyricColourSwatch()
            ValueSlider("Colour strength", value: $options.strength, in: LyricColourOptions.strengthRange,
                        step: 0.01, minimumLabel: "Neutral", maximumLabel: "Vivid", format: SettingsFormat.percent)
            InspectorCaption(options.mode.detail)
            RememberedStyleNote(aspect: .colour)
            if options.mode.drifts {
                ValueSlider("Colour speed", value: $options.speed, in: LyricColourOptions.speedRange,
                            minimumLabel: "Slow", maximumLabel: "Lively", format: SettingsFormat.percent)
            }
            if options.mode == .customGradient {
                ForEach(Array(options.customHex.enumerated()), id: \.offset) { index, _ in
                    LabeledContent {
                        HStack(spacing: LunalyrDesign.Spacing.small) {
                            ColorPicker("Colour \(index + 1)", selection: Binding(
                                get: { options.colour(at: index) },
                                set: { options.setCustom(index, to: $0) }), supportsOpacity: false)
                                .labelsHidden()
                            if options.customHex.count > 2 {
                                Button { options.removeCustom(at: index) } label: {
                                    Image(systemName: "minus.circle")
                                        .frame(width: LunalyrDesign.Size.minimumHitTarget - 8,
                                               height: LunalyrDesign.Size.minimumHitTarget - 8)
                                }
                                .buttonStyle(.borderless)
                                .help("Remove this colour")
                                .accessibilityLabel("Remove colour \(index + 1)")
                            }
                        }
                    } label: {
                        Text("Colour \(index + 1)")
                    }
                }
                LabeledContent {
                    if options.customHex.count < 4 {
                        Button("Add Colour") { options.addCustom() }
                            .help("Add a fourth colour to the gradient")
                    }
                } label: {
                    CustomGradientSwatch(colours: options.customColours, strength: options.strength)
                        .frame(maxWidth: 180)
                }
            }
            InspectorCaption("Automatic text backing helps the pastels stay visible on bright wallpapers. Colours fade between shades and hold almost still with Reduce Motion on.")
        }
        Section("Highlight colour") {
            LyricHighlightColourControls()
        }
    }
}

/// The custom gradient as the lyrics will use it: blended in OKLab and
/// lightened for a dark wallpaper.
private struct CustomGradientSwatch: View {
    let colours: [OKLab]
    let strength: Double

    var body: some View {
        let ink = LyricInk(mode: .customGradient, stops: stops, stopCount: max(1, min(4, colours.count)))
        let samples = (0...24).map { Perceptual.color(Perceptual.readable(ink.gradient(slot: Double($0) / 24 / 0.42 * 0.999),
                                                                           background: 0.08)) }
        LinearGradient(colors: samples, startPoint: .leading, endPoint: .trailing)
            .frame(height: 18)
            .clipShape(RoundedRectangle(cornerRadius: LunalyrDesign.Radius.tag, style: .continuous))
            .overlay {
                RoundedRectangle(cornerRadius: LunalyrDesign.Radius.tag, style: .continuous).strokeBorder(.quaternary)
            }
            .accessibilityHidden(true)
    }

    private var stops: LyricInkDirector.Stops {
        func at(_ index: Int) -> OKLab {
            guard !colours.isEmpty else { return .white }
            return LyricInk.applyingStrength(LyricColourOptions.clampedStrength(strength),
                to: LyricInk.pastel(colours[index % colours.count]))
        }
        return (at(0), at(1), at(2), at(3))
    }
}

/// Remember the current layout and colour for the playing song or artist.
@MainActor
struct LyricStyleMemorySection: View {
    @Model(\.styleMemory) private var memory
    @Model(\.settings) private var settings
    @Model(\.colours) private var colours
    @Model(\.nowPlaying) private var playing

    private var chosen: StyleMemory.Style {
        .init(composition: settings.lyricComposition?.rawValue ?? "automatic", colour: colours.mode.rawValue)
    }

    var body: some View {
        Section("Remember a style") {
            Toggle("Use remembered styles", isOn: $memory.enabled)
            if let track = playing.current {
                if let active = memory.remembered(for: track) {
                    LabeledContent("This \(active.scope.rawValue) uses") {
                        Text(Self.describe(active.style))
                    }
                    InspectorCaption("Changes above apply to other music until you update or forget this.")
                    HStack(spacing: LunalyrDesign.Spacing.small) {
                        Button("Update with the Choices Above") { memory.remember(chosen, for: track, scope: active.scope) }
                        Button("Forget") { memory.forget(for: track, scope: active.scope) }
                            .help("Use the choices above for this \(active.scope.rawValue) too")
                    }
                } else {
                    ViewThatFits(in: .horizontal) {
                        HStack(spacing: LunalyrDesign.Spacing.small) { rememberButtons(track) }
                        VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.small) { rememberButtons(track) }
                    }
                    if track.artist.isEmpty {
                        DisabledReason("This song names no artist, so its style can only be remembered for the song.")
                    }
                    InspectorCaption("Saves the layout and text colour chosen above. A song’s own style wins over its artist’s.")
                }
            } else {
                InspectorCaption("Play a song to remember a layout and colour for it or its artist.")
            }
            if !memory.songs.isEmpty || !memory.artists.isEmpty {
                LabeledContent {
                    Button("Forget All") { memory.forgetAll() }
                        .controlSize(.small)
                        .help("Forget every remembered style")
                } label: {
                    Text(Self.rememberedCount(songs: memory.songs.count, artists: memory.artists.count))
                        .lunalyrText(.caption)
                }
            }
        }
    }

    @ViewBuilder private func rememberButtons(_ track: NowPlayingMonitor.Track) -> some View {
        Button("Remember for This Song") { memory.remember(chosen, for: track, scope: .song) }
        Button(track.artist.isEmpty ? "Remember for Artist" : "Remember for \(track.artist)") {
            memory.remember(chosen, for: track, scope: .artist)
        }
        .disabled(track.artist.isEmpty)
    }

    /// "1 song · 2 artists remembered".
    static func rememberedCount(songs: Int, artists: Int) -> String {
        "\(songs) \(songs == 1 ? "song" : "songs") · \(artists) \(artists == 1 ? "artist" : "artists") remembered"
    }

    static func describe(_ style: StyleMemory.Style) -> String {
        let layout = style.composition == "automatic" ? "Match the music" : style.layout?.title ?? "Match the music"
        return [layout, style.colourMode?.title].compactMap { $0 }.joined(separator: " · ")
    }
}

/// Under the Layout and Colour pickers: a style remembered for the playing
/// song or artist is what the wallpaper shows, so the picker would otherwise
/// look broken. Update saves the picker's choice there; Forget drops this
/// part of the memory.
@MainActor
struct RememberedStyleNote: View {
    enum Aspect { case layout, colour }
    let aspect: Aspect
    @Model(\.styleMemory) private var memory
    @Model(\.settings) private var settings
    @Model(\.colours) private var colours
    @Model(\.nowPlaying) private var playing

    var body: some View {
        if let track = playing.current, let active = memory.remembered(for: track),
           let name = Self.rememberedName(aspect, in: active.style) {
            VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.small) {
                Label("This \(active.scope.rawValue) uses \(name), remembered for it. The choice above applies to other music.",
                      systemImage: "bookmark.fill")
                    .labelStyle(CaptionLabelStyle())
                HStack(spacing: LunalyrDesign.Spacing.small) {
                    Button("Update") { memory.remember(updated(active.style), for: track, scope: active.scope) }
                        .help("Remember the choice above for this \(active.scope.rawValue) instead")
                    Button("Forget") { forget(track, active) }
                        .help("Use the choice above for this \(active.scope.rawValue) too")
                }
                .controlSize(.small)
                .padding(.leading, 20)
            }
            .accessibilityElement(children: .contain)
        }
    }

    /// The remembered part this note is about, by name, or nil if the
    /// memory leaves it to the general setting.
    static func rememberedName(_ aspect: Aspect, in style: StyleMemory.Style) -> String? {
        switch aspect {
        case .layout:
            guard let raw = style.composition else { return nil }
            return raw == "automatic" ? "Match the music" : style.layout?.title ?? "Match the music"
        case .colour:
            return style.colourMode?.title
        }
    }

    private func updated(_ style: StyleMemory.Style) -> StyleMemory.Style {
        var style = style
        switch aspect {
        case .layout: style.composition = settings.lyricComposition?.rawValue ?? "automatic"
        case .colour: style.colour = colours.mode.rawValue
        }
        return style
    }

    private func forget(_ track: NowPlayingMonitor.Track, _ active: (style: StyleMemory.Style, scope: StyleMemory.Scope)) {
        var style = active.style
        switch aspect {
        case .layout: style.composition = nil
        case .colour: style.colour = nil
        }
        if style.composition == nil && style.colour == nil {
            memory.forget(for: track, scope: active.scope)
        } else {
            memory.remember(style, for: track, scope: active.scope)
        }
    }
}

/// Menu bar & shortcuts: hiding and pausing, the menu-bar line, global
/// shortcuts and the lyric card.
@MainActor
struct ExtrasInspector: View {
    @Model(\.extras) private var extras
    @Model(\.hotkeys) private var hotkeys
    @Model(\.nowPlaying) private var playing

    var body: some View {
        Form {
            Section("Lyrics on screen") {
                Toggle("Hide lyrics", isOn: $extras.lyricsHidden)
                Toggle("Hide lyrics while a display is mirrored", isOn: $extras.hidesWhileMirroring)
                InspectorCaption(Self.mirroringNote(mirroring: extras.mirroring, hides: extras.hidesWhileMirroring,
                                                    showsAnyway: extras.showsWhileMirrored))
                if extras.hiddenByMirroring {
                    Button(LunalyrExtras.Titles.showLyricsAnyway) { extras.showLyrics() }
                        .help("Shows the lyrics on every display until mirroring ends")
                }
                Toggle("Pause wallpaper", isOn: $extras.wallpaperPaused)
                InspectorCaption("Freezes every desktop wallpaper, lyrics included, until you resume it or Lunalyr next starts. The preview here keeps moving, so changes can still be judged.")
            }
            Section("Menu bar") {
                Toggle("Show the current line in the menu bar", isOn: $extras.showsMenuBarLine)
                if extras.showsMenuBarLine {
                    ValueSlider("Longest line", value: $extras.menuBarLength, in: LunalyrExtras.menuBarLengthRange, step: 1,
                                minimumLabel: "Short", maximumLabel: "Long",
                                format: { "\(Int($0.rounded())) characters" })
                    InspectorCaption("Hidden lyrics stay hidden there too, and a paused wallpaper says so.")
                }
            }
            Section("Keyboard shortcuts") {
                Toggle("Use global shortcuts", isOn: $extras.shortcutsEnabled)
                ChoiceMenu("Keys", selection: $hotkeys.modifiers, selectedTitle: \.title) {
                    ForEach(GlobalHotkeys.Modifiers.allCases) { Text($0.title).tag($0) }
                }
                ForEach(GlobalHotkeys.shortcuts, id: \.action) { shortcut in
                    shortcutRow(shortcut.action)
                }
                InspectorCaption(Self.shortcutsCaption)
                if !hotkeys.unavailable.isEmpty && extras.shortcutsEnabled {
                    InspectorCaption("A shortcut another app already uses is left to that app. Switch it off there, or choose other keys above.")
                }
                if NSWorkspace.shared.isVoiceOverEnabled && hotkeys.modifiers == .controlOptionCommand {
                    StatusBanner(.warning, title: "VoiceOver uses these keys",
                                 message: "With Control-Option as its VO keys, ⌃⌥⌘ and a letter is also a VoiceOver command. Choose ⌃⇧⌘ above, or switch off the shortcuts you need for VoiceOver.")
                }
            }
            Section("Share") {
                ChoiceMenu("Card layout", selection: $extras.lyricCardComposition,
                           selectedTitle: { $0?.title ?? "Follow wallpaper" }) {
                    Text("Follow wallpaper").tag(Optional<LyricComposition>.none)
                    LyricCompositionOptions()
                }
                LabeledContent {
                    Button("Open Lyric Card…") { extras.copyLyricCard() }
                        .disabled(playing.current == nil)
                } label: {
                    Text("Lyric card")
                }
                InspectorCaption("Pick any lines from the song, the card’s shape and whether to make an MP4, GIF or PNG. Synced lyrics play in order.")
                if playing.current == nil {
                    DisabledReason("A card can be copied while a song is playing.")
                }
            }
        }
        .formStyle(.grouped)
        .scrollContentBackground(.hidden)
    }

    /// One shortcut: what it does, its keys, whether another app holds them,
    /// and a switch of its own.
    private func shortcutRow(_ action: GlobalHotkeys.Action) -> some View {
        let on = hotkeys.isSwitchedOn(action)
        let taken = extras.shortcutsEnabled && hotkeys.unavailable.contains(action)
        return LabeledContent {
            HStack(spacing: LunalyrDesign.Spacing.small) {
                Text(hotkeys.label(for: action) ?? "")
                    .monospaced()
                    .foregroundStyle(extras.shortcutsEnabled && on && !taken ? .primary : .secondary)
                // Its own label for VoiceOver, which reaches it on its own.
                Toggle(action.title, isOn: Binding(get: { hotkeys.isSwitchedOn(action) },
                                                   set: { hotkeys.setSwitchedOn($0, for: action) }))
                    .labelsHidden()
                    .toggleStyle(.switch)
                    .controlSize(.small)
            }
        } label: {
            VStack(alignment: .leading, spacing: 2) {
                Text(action.title)
                if taken {
                    Label("In use by another app", systemImage: "exclamationmark.triangle.fill")
                        .labelStyle(CaptionLabelStyle())
                        .foregroundStyle(LunalyrStatus.warning.text)
                }
            }
        }
    }

    /// Under the shortcuts. The rows above show each combination as the
    /// keyboard in use types it (⌃⌥⌘Ü for ⌃⌥⌘[ on a German one), so this
    /// names no key of Lunalyr's: only the arrows it leaves alone.
    static let shortcutsCaption = "They work from any app; switch off any you need for something else. Sync changes are saved for this song’s lyric source, like the song offset in Timing & sources. None of them use the arrow keys, which window managers such as Rectangle use with ⌃⌥⌘ to move windows between displays."

    /// The caption under "Hide lyrics while a display is mirrored".
    static func mirroringNote(mirroring: Bool, hides: Bool, showsAnyway: Bool) -> String {
        guard mirroring else { return "For presenting: mirroring to a projector or TV hides the lyrics until it ends." }
        guard hides else { return "A display is mirrored now; the lyrics stay on every display." }
        return showsAnyway
            ? "A display is mirrored now, and the lyrics are shown anyway until mirroring ends."
            : "A display is mirrored now, so the lyrics are hidden on every display until mirroring ends."
    }
}

/// Shown on the Romanisation page.
@MainActor
struct TranslationSection: View {
    @Model(\.extras) private var extras

    var body: some View {
        Section("Translations") {
            Toggle("Show translations under lyrics", isOn: $extras.showsTranslations)
            InspectorCaption("Appears when the lyric source includes a translation for a line, such as bilingual LRC files. Nothing is translated on this Mac or sent anywhere.")
        }
    }
}

/// Above the preview, what the desktop is doing that the preview does not
/// show by itself, with the way back: a paused wallpaper (the preview keeps
/// moving), or lyrics that mirroring hides on every display. One at a time,
/// a frozen desktop first.
@MainActor
struct WallpaperStateNote: View {
    @Model(\.extras) private var extras

    enum State: Equatable {
        case paused, hiddenByMirroring

        var symbol: String { self == .paused ? "pause.rectangle.fill" : "rectangle.on.rectangle" }
        var text: String { self == .paused ? "Wallpaper paused" : "Lyrics hidden while mirroring" }
        /// Short, beside the text; VoiceOver hears the whole action.
        var button: String { self == .paused ? "Resume" : "Show Anyway" }
        var action: String {
            self == .paused ? LunalyrExtras.Titles.resumeWallpaper : LunalyrExtras.Titles.showLyricsAnyway
        }
        var help: String {
            self == .paused ? "Let the desktop wallpaper move again"
                : "Show the lyrics on every display until mirroring ends"
        }
        /// A paused wallpaper is a mode the user chose; hidden lyrics a
        /// plain state.
        var status: LunalyrStatus { self == .paused ? .info : .neutral }
    }

    static func state(paused: Bool, hiddenByMirroring: Bool) -> State? {
        paused ? .paused : hiddenByMirroring ? .hiddenByMirroring : nil
    }

    var body: some View {
        if let state = Self.state(paused: extras.wallpaperPaused, hiddenByMirroring: extras.hiddenByMirroring) {
            StatusChip(state.status, state.text, symbol: state.symbol) {
                Button(state.button) {
                    if state == .paused { extras.toggleWallpaperPause() } else { extras.showLyrics() }
                }
                .help(state.help)
                .accessibilityLabel(state.action)
            }
        }
    }
}
