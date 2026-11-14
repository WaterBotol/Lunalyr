import SwiftUI

/// The Lyric style inspector: the Looks, what shows with the lyrics, the
/// typeface, Lyric size, the colours, how words enter, the layouts' own
/// controls, instrumental breaks and remembered styles. The layout itself is
/// chosen on the canvas (`LayoutTileChoices`). In the Floating view the floating
/// window's own settings come first and the Looks (desktop styles) don't show.
@MainActor
struct LyricStylePage: View {
    @Model(\.settings) private var s
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var familyDraft = ""
    @ObservedObject private var floating: FloatingLyricsOptions

    init(floating: FloatingLyricsOptions? = nil) {
        self.floating = floating ?? .shared
    }

    /// The layout the word controls read from. Match the music saves them
    /// for every layout it picks; Scattered, its general-purpose pick, stands
    /// in when reading them back.
    private var style: LyricComposition { s.lyricComposition ?? .scattered }
    private var automatic: Bool { s.lyricComposition == nil }

    var body: some View {
        LookInspectorForm {
            if floating.isFloating {
                FloatingLyricsSection(options: floating)
                    .revealRow()
            } else {
                LooksSection()
                    .revealRow()
                InspectorTitle(title: "Lyric style")
            }
            shownWithLyrics
            typeface
            // Floating has its own size (Size, above).
            if !floating.isFloating {
                ValueSlider("Lyric size", value: $s.lyricScale, in: LyricSizeDrag.range, step: 0.01,
                            format: SettingsFormat.percent)
                    .fontWeight(.semibold)
                    .help("Drag a corner of the line on the picture to change it there too")
            }
            LyricColourSection()
            wordEntrances
            layoutNotes
            InspectorSection("Instrumental breaks") {
                Toggle("Show three dots during instrumental breaks", isOn: $s.showInterludes)
                    .toggleStyle(StudioCheckboxStyle())
            }
            // Match the music can pick either, so both stay reachable there.
            if style.hasConnections || automatic {
                constellation
            }
            if style == .echo || automatic {
                echo
            }
            LyricStyleMemorySection()
        }
        // Sub-options glide in and out with the choice that shows them.
        .lunalyrReveal(s.lyricComposition)
        .lunalyrReveal(floating.surface)
        .lunalyrReveal(rotateLayouts)
        .lunalyrReveal(backingLayer)
        .lunalyrReveal(s.editorialTypography)
        .lunalyrReveal(s.randomizeLyricFonts)
        .lunalyrReveal(s.fontChoice)
        .lunalyrReveal(s.googleFontState)
        .lunalyrReveal(EntranceSupport.note(selected: s.arrival(in: style), layout: s.lyricComposition, mix: chosenMix))
        .onAppear { familyDraft = s.googleFontFamily }
        .onChange(of: s.googleFontFamily) { _, family in familyDraft = family }
    }

    private var constellation: some View {
        InspectorSection("Constellation") {
            ValueSlider("Spread", value: $s.constellationSpread, in: 0.4...1.8, step: 0.01,
                        format: SettingsFormat.percent)
            ValueSlider("Links", value: $s.constellationLinkOpacity, in: 0...0.6, step: 0.01,
                        format: SettingsFormat.percent)
            if automatic {
                InspectorCaption("Used when Match the music picks Constellation.")
                    .revealRow()
            }
        }
        .revealRow()
    }

    private var echo: some View {
        InspectorSection("Echo") {
            ValueSlider("Repeats", value: $s.echoStrength, in: 0...1.8, step: 0.01,
                        format: SettingsFormat.percent)
            if automatic {
                InspectorCaption("Used when Match the music picks Echo.")
                    .revealRow()
            }
        }
        .revealRow()
    }

    @AppStorage(LyricLayoutRotation.enabledKey) private var rotateLayouts = true
    @AppStorage(BackingVocals.settingKey) private var backingLayer = true
    @AppStorage(BackingVocals.styleKey) private var backingStyle = ""
    @AppStorage(LyricCreditCues.settingKey) private var showCredits = true
    @AppStorage(OpeningTitle.settingKey) private var showOpeningTitle = true
    @AppStorage(OpeningTitle.coverKey) private var showOpeningCover = true
    @ObservedObject private var rotation = LyricLayoutRotation.shared

    /// What the chosen layout does, the rotation's counts (the switch for
    /// it sits by the layout tiles) and a style remembered for this song.
    private var layoutNotes: some View {
        InspectorSection("Layout") {
            InspectorCaption(automatic ? WorkspaceCopy.automaticLayoutDetail : style.detail)
            if automatic && rotateLayouts {
                DisclosureGroup("Layouts in the rotation") {
                    ForEach(LyricLayoutRotation.order) { layout in
                        Stepper(value: Binding(get: { rotation.count(for: layout) },
                                               set: { rotation.setCount($0, for: layout) }),
                                in: 0...LyricLayoutRotation.maximumCount) {
                            HStack {
                                Text(layout.title)
                                Spacer()
                                Text(rotationLabel(rotation.count(for: layout)))
                                    .lunalyrText(.caption)
                            }
                        }
                    }
                    InspectorCaption("Songs in a row per layout. 0 leaves one out.", more: "How many songs in a row each layout gets before the next one. 0 leaves a layout out.")
                }
                .revealRow()
            }
            RememberedStyleNote(aspect: .layout)
        }
    }

    private func rotationLabel(_ count: Int) -> String {
        if count == 0 { return "Never" }
        return count == 1 ? "1 song" : "\(count) songs in a row"
    }

    /// The layers shown with the lyrics: checkboxes, as related options.
    private var shownWithLyrics: some View {
        InspectorSection("Shown with the lyrics") {
            VStack(alignment: .leading, spacing: 7) {
                Toggle("Show the song title at the start", isOn: $showOpeningTitle)
                    .help("The title and artist appear low on the left as the song begins, in the lyrics’ typeface, and leave before the first line is sung.")
                Toggle("Show the album cover", isOn: $showOpeningCover)
                    .padding(.leading, 23)
                    .disabled(!showOpeningTitle)
                Toggle("Show credits in the background", isOn: $showCredits)
                    .help("Lines like “Produced by: …” appear one at a time behind the lyrics.")
                Toggle("Backing vocals in the background", isOn: $backingLayer)
                    .help("Bracketed backing words like (oh oh) sit small and soft behind the lead.")
                if backingLayer {
                    ChoiceMenu("Backing vocals animate like", selection: $backingStyle,
                               selectedTitle: { $0.isEmpty ? "The lead’s layout" : (LyricComposition(rawValue: $0)?.title ?? "The lead’s layout") }) {
                        Text("The lead’s layout").tag("")
                        ForEach(BackingVocals.choices) { Text($0.title).tag($0.rawValue) }
                    }
                    .padding(.leading, 23)
                    .revealRow()
                }
                Toggle("Add Editorial typography", isOn: $s.editorialTypography)
                if let note = WorkspaceCopy.editorialNote(layout: s.lyricComposition) {
                    InspectorCaption(note)
                        .padding(.leading, 23)
                        .revealRow()
                }
                if s.editorialTypography {
                    ValueSlider("Editorial amount", value: $s.editorialAmount, in: 0...1, step: 0.01,
                                format: SettingsFormat.percent)
                        .padding(.leading, 23)
                        .revealRow()
                }
            }
            .toggleStyle(StudioCheckboxStyle())
        }
    }

    private var typeface: some View {
        InspectorSection("Typeface", value: s.fontChoice.title) {
            TypefaceTiles()
            Toggle("Randomise lyric fonts", isOn: $s.randomizeLyricFonts)
                .toggleStyle(StudioCheckboxStyle())
            if s.randomizeLyricFonts {
                InspectorCaption("Words use a mix of built-in typefaces while this is on.")
                    .revealRow()
            }
            if s.randomizeLyricFonts {
                InspectorCaption(WorkspaceCopy.randomisedTypeface)
                    .revealRow()
            }
            if s.fontChoice == .google {
                Group {
                    ChoiceMenu("Google Fonts family",
                               selection: Binding(get: { s.googleFontFamily }, set: { s.applyGoogleFont(family: $0) }),
                               selectedTitle: { $0.isEmpty ? "Select a family" : $0 }) {
                        if !GoogleFontsCatalog.allFamilies.contains(s.googleFontFamily) {
                            Text(s.googleFontFamily.isEmpty ? "Select a family" : s.googleFontFamily).tag(s.googleFontFamily)
                        }
                        ForEach(GoogleFontsCatalog.groups) { group in
                            Section(group.name) {
                                ForEach(group.families, id: \.self) { family in Text(family).tag(family) }
                            }
                        }
                    }
                    LabeledContent("Any family") {
                        HStack(spacing: LunalyrDesign.Spacing.small) {
                            TextField("Any family", text: $familyDraft, prompt: Text("Family name"))
                                .labelsHidden()
                                .textFieldStyle(.roundedBorder)
                                .onSubmit(applyFamilyDraft)
                            // The current family can be asked for again after
                            // it failed, but not re-fetched once it works.
                            Button("Use", action: applyFamilyDraft)
                                .disabled(s.googleFontState == .loading
                                          || Settings.normalizedFontFamily(familyDraft) == nil
                                          || (Settings.normalizedFontFamily(familyDraft) == s.googleFontFamily && s.googleRegular != nil))
                                .help("Use this family for the lyrics")
                        }
                    }
                    InspectorCaption("Type a Google Fonts or installed family.", more: "Type a family from fonts.google.com or one installed on this Mac.")
                    if let message = s.googleStatus {
                        GoogleFontStatus(state: s.googleFontState, message: message)
                            .id(s.googleFontState)
                            .transition(.opacity)
                    }
                }
                .revealRow()
            }
        }
    }

    private var wordEntrances: some View {
        InspectorSection("Word entrances") {
            Toggle("Reveal sung words", isOn: Binding(get: { s.revealsWords(in: style) },
                                                       set: { s.wordRevealByStyle[s.wordControlsKey] = $0 }))
                .disabled(s.requiresWordEntrances(in: style))
            if let reason = WorkspaceCopy.revealLockReason(s.wordRevealLock(in: style)) {
                DisabledReason(reason)
            }
            ChoiceMenu("Entrance", selection: Binding(get: { s.arrival(in: style) },
                                                      set: { s.wordArrivalByStyle[s.wordControlsKey] = $0.rawValue }),
                       selectedTitle: \.title) {
                // An entrance this layout leaves out for word-timed lyrics is
                // marked: lines without word timing still enter with it.
                ForEach(WordArrival.allCases) { Text(EntranceSupport.menuTitle($0, in: s.lyricComposition)).tag($0) }
            }
            if let note = EntranceSupport.note(selected: s.arrival(in: style), layout: s.lyricComposition,
                                               mix: chosenMix) {
                InspectorCaption(note)
                    .revealRow()
            }
            if reduceMotion {
                InspectorCaption(WorkspaceCopy.reduceMotionEntrances)
            }
            if s.arrival(in: style) == .mix {
                ForEach(WordArrival.mixChoices) { option in
                    Toggle(option.title, isOn: Binding(
                        get: { s.wordEntranceMix.contains(option.rawValue) },
                        set: { enabled in
                            if enabled { s.wordEntranceMix.append(option.rawValue) }
                            else { s.wordEntranceMix.removeAll { $0 == option.rawValue } }
                        }
                    ))
                }
            }
            Toggle("Faint next-word preview", isOn: Binding(get: { s.previewsNextWord(in: style) },
                                                             set: { s.nextWordPreviewByStyle[s.wordControlsKey] = $0 }))
            if s.previewsNextWord(in: style) {
                ValueSlider("Preview lead", value: $s.nextWordPreviewLead, in: 0.15...1.25, step: 0.05,
                            format: SettingsFormat.seconds,
                            spoken: { SettingsFormat.spokenSeconds($0) + " before the next word" })
                    .help("How long before each word its faint preview appears")
            }
            InspectorCaption(automatic ? "These controls apply to layouts picked by Match the music."
                                       : "These controls apply to the selected layout.")
        }
    }

    /// The custom mix's entrances, in the order the page lists them.
    private var chosenMix: [WordArrival] {
        WordArrival.mixChoices.filter { s.wordEntranceMix.contains($0.rawValue) }
    }

    private func applyFamilyDraft() {
        guard let typed = Settings.normalizedFontFamily(familyDraft) else { return }
        let family = GoogleFontsCatalog.canonicalName(typed) ?? typed
        familyDraft = family
        s.applyGoogleFont(family: family)
    }
}

/// The chosen family's load: a spinner while it downloads, a tick once in
/// use, and a warning when it can't be used, so a failure no longer reads
/// like a note.
private struct GoogleFontStatus: View {
    let state: GoogleFontState
    let message: String

    var body: some View {
        switch state {
        case .failed:
            WarningCaption(message)
        case .loading:
            HStack(spacing: LunalyrDesign.Spacing.small) {
                DelayedSpinner()
                Text(message).lunalyrText(.caption).fixedSize(horizontal: false, vertical: true)
            }
            .accessibilityElement(children: .combine)
        case .ready:
            Label {
                Text(message).fixedSize(horizontal: false, vertical: true)
            } icon: {
                Image(systemName: "checkmark.circle.fill").foregroundStyle(LunalyrStatus.success.text)
            }
            .labelStyle(CaptionLabelStyle())
            .accessibilityElement(children: .combine)
        case .idle:
            InspectorCaption(message)
        }
    }
}
