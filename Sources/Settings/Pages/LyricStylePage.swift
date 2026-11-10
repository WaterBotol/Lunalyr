import SwiftUI

/// Lyric style: the layout, the typeface, the text colour, how words enter,
/// the layouts' own controls, instrumental breaks and remembered styles.
@MainActor
struct LyricStylePage: View {
    @Model(\.settings) private var s
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var familyDraft = ""

    /// The layout the word controls read from. Match the music saves them
    /// for every layout it picks; Scattered, its general-purpose pick, stands
    /// in when reading them back.
    private var style: LyricComposition { s.lyricComposition ?? .scattered }
    private var automatic: Bool { s.lyricComposition == nil }

    var body: some View {
        ThemedForm {
            layout
            typeface
            LyricColourSection()
            wordEntrances
            // Match the music can pick either, so both stay reachable there.
            if style.hasConnections || automatic {
                constellation
            }
            if style == .echo || automatic {
                echo
            }
            ThemedSection("Instrumental breaks") {
                Toggle("Show three dots during instrumental breaks", isOn: $s.showInterludes)
            }
            LyricStyleMemorySection()
        }
        // Sub-options glide in and out with the choice that shows them.
        .lunalyrReveal(s.lyricComposition)
        .lunalyrReveal(rotateLayouts)
        .lunalyrReveal(backingLayer)
        .lunalyrReveal(s.editorialTypography)
        .lunalyrReveal(s.randomizeLyricFonts)
        .lunalyrReveal(s.fontChoice)
        .formStyle(.grouped)
        .scrollContentBackground(.hidden)
        .onAppear { familyDraft = s.googleFontFamily }
        .onChange(of: s.googleFontFamily) { _, family in familyDraft = family }
    }

    private var constellation: some View {
        ThemedSection("Constellation") {
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
        ThemedSection("Echo") {
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
    @ObservedObject private var rotation = LyricLayoutRotation.shared

    private var layout: some View {
        ThemedSection("Layout") {
            ChoiceMenu("Layout", selection: $s.lyricComposition, selectedTitle: { $0?.title ?? "Match the music" }) {
                Text("Match the music").tag(Optional<LyricComposition>.none)
                LyricCompositionOptions()
            }
            InspectorCaption(automatic ? WorkspaceCopy.automaticLayoutDetail : style.detail)
            if automatic {
                Group {
                    Toggle("Change layout every song", isOn: $rotateLayouts)
                    InspectorCaption("Each new song takes the next layout.", more: "Each new song takes the next layout, skipping ones that don’t suit its energy. Off: the layout follows the song’s energy alone.")
                    if rotateLayouts {
                        DisclosureGroup("Layouts in the rotation") {
                            ForEach(LyricLayoutRotation.order) { layout in
                                Stepper(value: Binding(get: { rotation.count(for: layout) },
                                                       set: { rotation.setCount($0, for: layout) }),
                                        in: 0...LyricLayoutRotation.maximumCount) {
                                    HStack {
                                        Text(layout.title)
                                        Spacer()
                                        Text(rotation.count(for: layout) == 0 ? "Never"
                                             : rotation.count(for: layout) == 1 ? "1 song" : "\(rotation.count(for: layout)) songs in a row")
                                            .lunalyrText(.caption)
                                    }
                                }
                            }
                            InspectorCaption("Songs in a row per layout. 0 leaves one out.", more: "How many songs in a row each layout gets before the next one. 0 leaves a layout out.")
                        }
                        .revealRow()
                    }
                }
                .revealRow()
            }
            RememberedStyleNote(aspect: .layout)
            Toggle("Show credits in the background", isOn: $showCredits)
            InspectorCaption("Credit lines appear one at a time behind the lyrics.", more: "Lines like “Produced by: …” and “作词 : …” appear one at a time behind the lyrics, at full size, in the same layout as backing vocals.")
            Toggle("Backing vocals in the background", isOn: $backingLayer)
            if backingLayer {
                ChoiceMenu("Backing vocals animate like", selection: $backingStyle,
                           selectedTitle: { $0.isEmpty ? "The lead’s layout" : (LyricComposition(rawValue: $0)?.title ?? "The lead’s layout") }) {
                    Text("The lead’s layout").tag("")
                    ForEach(BackingVocals.choices) { Text($0.title).tag($0.rawValue) }
                }
                .revealRow()
            }
            InspectorCaption("Bracketed backing words sit small and soft behind the lead.", more: "Bracketed backing words like (oh oh) are animated word by word like the lyrics, but small and soft behind the lead, easing in and lingering. Spoken, Cascade and Cutout can’t be used here; they follow Editorial.")
            Toggle("Add Editorial typography", isOn: $s.editorialTypography)
            if let note = WorkspaceCopy.editorialNote(layout: s.lyricComposition) {
                InspectorCaption(note)
                    .revealRow()
            }
            if s.editorialTypography {
                ValueSlider("Editorial amount", value: $s.editorialAmount, in: 0...1, step: 0.01,
                            format: SettingsFormat.percent)
                    .revealRow()
            }
            ValueSlider("Lyric size", value: $s.lyricScale, in: 0.7...1.5, step: 0.01, format: SettingsFormat.percent)
        }
    }

    private var typeface: some View {
        ThemedSection("Typeface") {
            Toggle("Randomise lyric fonts", isOn: $s.randomizeLyricFonts)
            if s.randomizeLyricFonts {
                InspectorCaption("Words use a mix of built-in typefaces while this is on.")
                    .revealRow()
            }
            ChoiceMenu("Typeface", selection: Binding(get: { s.fontChoice }, set: { s.selectFontChoice($0) }),
                       selectedTitle: \.title) {
                ForEach(Settings.FontChoice.allCases) { Text($0.title).tag($0) }
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
                                .disabled(Settings.normalizedFontFamily(familyDraft) == nil
                                          || (Settings.normalizedFontFamily(familyDraft) == s.googleFontFamily && s.googleRegular != nil))
                                .help("Use this family for the lyrics")
                        }
                    }
                    InspectorCaption("Type a Google Fonts or installed family.", more: "Type a family from fonts.google.com or one installed on this Mac.")
                    if let message = s.googleStatus {
                        InspectorCaption(message)
                    }
                }
                .revealRow()
            }
        }
    }

    private var wordEntrances: some View {
        ThemedSection("Word entrances") {
            Toggle("Reveal sung words", isOn: Binding(get: { s.revealsWords(in: style) },
                                                       set: { s.wordRevealByStyle[s.wordControlsKey] = $0 }))
                .disabled(s.requiresWordEntrances(in: style))
            if let reason = WorkspaceCopy.revealLockReason(s.wordRevealLock(in: style)) {
                DisabledReason(reason)
            }
            ChoiceMenu("Entrance", selection: Binding(get: { s.arrival(in: style) },
                                                      set: { s.wordArrivalByStyle[s.wordControlsKey] = $0.rawValue }),
                       selectedTitle: \.title) {
                ForEach(WordArrival.allCases) { Text($0.title).tag($0) }
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

    private func applyFamilyDraft() {
        guard let typed = Settings.normalizedFontFamily(familyDraft) else { return }
        let family = GoogleFontsCatalog.canonicalName(typed) ?? typed
        familyDraft = family
        s.applyGoogleFont(family: family)
    }
}
