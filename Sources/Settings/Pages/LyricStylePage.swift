import SwiftUI

/// The Text inspector: the Looks (or, in the Floating view, what the
/// Floating style is), then the style (layout notes and remembered styles),
/// size and place, the typeface, the colours, how the lyrics stay readable
/// and what shows with them. Each section's rarer options are under More
/// options. The layout itself is chosen on the canvas (`LayoutTileChoices`).
/// How the words move is on Animation (`AnimationPage`).
@MainActor
struct LyricStylePage: View {
    @Model(\.settings) private var s
    @State private var familyDraft = ""
    @ObservedObject private var floating: FloatingLyricsOptions

    init(floating: FloatingLyricsOptions? = nil) {
        self.floating = floating ?? .shared
    }

    /// The layout the controls read from. Custom Mix saves them for every
    /// layout it picks; Scattered, its general-purpose pick, stands in when
    /// reading them back.
    private var style: LyricComposition { s.lyricComposition ?? .scattered }
    private var automatic: Bool { s.lyricComposition == nil }

    var body: some View {
        LookInspectorForm {
            if floating.isFloating {
                InspectorTitle(title: "Floating lyrics",
                               subtitle: "The Floating style. The desktop keeps its own and shows no lyrics while they float.")
                    .revealRow()
            } else {
                LooksSection()
                    .revealRow()
                InspectorTitle(title: "Text")
            }
            styleSection
            sizeAndPlace
            typeface
            LyricColourSection()
            ReadabilitySection(floating: floating.isFloating ? floating : nil)
            shownWithLyrics
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
        .onAppear { familyDraft = s.googleFontFamily }
        .onChange(of: s.googleFontFamily) { _, family in familyDraft = family }
    }

    @AppStorage(LyricLayoutRotation.enabledKey) private var rotateLayouts = true
    @AppStorage(BackingVocals.settingKey) private var backingLayer = true
    @AppStorage(BackingVocals.styleKey) private var backingStyle = ""
    @AppStorage(LyricCreditCues.settingKey) private var showCredits = true
    @AppStorage(OpeningTitle.settingKey) private var showOpeningTitle = true
    @AppStorage(OpeningTitle.coverKey) private var showOpeningCover = true
    @AppStorage(OpeningTitle.alwaysKey) private var keepsOpeningTitle = false
    @ObservedObject private var rotation = LyricLayoutRotation.shared

    /// What the chosen layout does, a style remembered for this song, the
    /// remembered-styles menu, and the rotation's counts (the switch for it
    /// sits by the layout tiles) under More options.
    private var styleSection: some View {
        InspectorSection("Style") {
            InspectorCaption(automatic ? WorkspaceCopy.automaticLayoutDetail : style.detail)
            RememberedStyleNote(aspect: .layout)
            LyricStyleMemorySection()
            if automatic && rotateLayouts {
                MoreOptions {
                    Text("Layouts in the rotation")
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundStyle(Color.studio(.ink2))
                        .accessibilityAddTraits(.isHeader)
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
        }
    }

    private func rotationLabel(_ count: Int) -> String {
        if count == 0 { return "Never" }
        return count == 1 ? "1 song" : "\(count) songs in a row"
    }

    /// Lyric size on the desktop; in the Floating view, the window's size
    /// and place.
    private var sizeAndPlace: some View {
        InspectorSection("Size and place", value: floating.isFloating ? floating.placement.title : nil) {
            if floating.isFloating {
                FloatingSizeAndPlace(options: floating)
            } else {
                ValueSlider("Lyric size", value: $s.lyricScale, in: LyricSizeDrag.range, step: 0.01,
                            format: SettingsFormat.percent)
                    .help("Drag a corner of the line on the picture to change it there too")
            }
        }
    }

    /// The layers shown with the lyrics: checkboxes, as related options;
    /// how backing vocals move and Editorial typography under More options.
    private var shownWithLyrics: some View {
        InspectorSection("Shown with the lyrics") {
            VStack(alignment: .leading, spacing: 7) {
                Toggle("Show the song title at the start", isOn: $showOpeningTitle)
                    .help("The title and artist appear low on the left as the song begins, in the lyrics’ typeface, and leave before the first line is sung.")
                Toggle("Show the album cover", isOn: $showOpeningCover)
                    .padding(.leading, 23)
                    .disabled(!showOpeningTitle)
                Toggle("Keep the song title on screen", isOn: $keepsOpeningTitle)
                    .padding(.leading, 23)
                    .disabled(!showOpeningTitle)
                    .help("The title stays for the whole song, quieter once the singing starts, so you always know what’s playing. Low on the left beside the lyrics; in the floating lyrics, a small line above them.")
                Toggle("Show credits in the background", isOn: $showCredits)
                    .help("Lines like “Produced by: …” appear one at a time behind the lyrics.")
                Toggle("Backing vocals in the background", isOn: $backingLayer)
                    .help("Bracketed backing words like (oh oh) sit small and soft behind the lead.")
            }
            .toggleStyle(StudioCheckboxStyle())
            MoreOptions {
                if backingLayer {
                    ChoiceMenu("Backing vocals animate like", selection: $backingStyle,
                               selectedTitle: { $0.isEmpty ? "The lead’s layout" : (LyricComposition.saved($0)?.title ?? "The lead’s layout") }) {
                        Text("The lead’s layout").tag("")
                        ForEach(BackingVocals.choices) { Text($0.title).tag($0.rawValue) }
                    }
                    .revealRow()
                }
                Toggle("Add Editorial typography", isOn: $s.editorialTypography)
                    .toggleStyle(StudioCheckboxStyle())
                if s.editorialTypography {
                    ValueSlider("Editorial amount", value: $s.editorialAmount, in: 0...1, step: 0.01,
                                format: SettingsFormat.percent)
                        .padding(.leading, 23)
                        .revealRow()
                }
            }
        }
    }

    private var typeface: some View {
        InspectorSection("Typeface", value: s.fontChoice.title) {
            TypefaceTiles()
            RememberedStyleNote(aspect: .font)
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


