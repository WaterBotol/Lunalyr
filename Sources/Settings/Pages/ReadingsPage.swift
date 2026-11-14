import SwiftUI

/// Romanisation: readings for Japanese, Korean and Chinese lyrics, saved
/// readings, and translations. A small picture of a line with its reading
/// shows the Display choice.
@MainActor
struct ReadingsPage: View {
    @Model(\.readings) private var o
    @Model(\.lyrics) private var model
    @ObservedObject private var floating: FloatingLyricsOptions = .shared
    @State private var message: String?
    /// The file itself couldn't be used: shown as a warning, not a note.
    @State private var failed = false

    var body: some View {
        ThemedForm(title: SettingsPage.readings.title) {
            ReadingPicture(display: o.display)
            ThemedSection("Romanisation") {
                InspectorCaption(Self.styleNote(floating: floating.isFloating))
                    .environment(\.captionFollowsControl, false)
                ChoiceSegments("Display", selection: $o.display,
                               cases: Array(RomanizationOptions.Display.allCases), label: \.title)
                ChoiceMenu("Language", selection: $o.language, selectedTitle: \.title) {
                    ForEach(RomanizationOptions.Language.allCases) { Text($0.title).tag($0) }
                }
                CheckboxList {
                    Toggle("Keep tone marks", isOn: $o.toneMarks)
                    Toggle("Prefer saved readings", isOn: $o.preferSaved)
                }
                InspectorCaption("Readings follow the original timing.", more: "Readings follow the original timing. Local transliteration can miss names and unusual pronunciations.")
                    .environment(\.captionFollowsControl, false)
                InspectorCaption("Cantonese shows only readings from the source.", more: "Lunalyr can’t generate Jyutping on this Mac. Lyrics written in Cantonese show only readings from the lyric source or an imported reading LRC, never Mandarin pinyin. Cantonese songs in standard written Chinese look like Mandarin, so choose Cantonese for them.")
                    .environment(\.captionFollowsControl, false)
            }
            ThemedSection("Saved readings") {
                Button("Import Reading LRC…") {
                    // A file that can't be used says why, as on Timing & sources.
                    switch InspectorImport.lyrics(title: "Choose a reading LRC file") {
                    case nil: break
                    case .failure(let failure)?: message = failure.localizedDescription; failed = true
                    case .success(let text)?: message = model.importRomanization(text); failed = false
                    }
                }
                .controlSize(.small)
                .disabled(model.lines.isEmpty)
                .help("Use readings from an LRC file for the lyrics showing")
                if model.lines.isEmpty {
                    DisabledReason("Readings can be imported once a song’s lyrics are showing.")
                }
                if let message {
                    Group {
                        if failed { WarningCaption(message) } else { InspectorCaption(message) }
                    }
                    .id(message)
                    .transition(.opacity)
                }
                InspectorCaption("Saved readings are pronunciation guides.", more: "Saved readings are pronunciation guides. Translations are a separate feature and are not generated here.")
                InspectorCaption("Japanese readings are matched to the lyric’s kana.", more: "Japanese readings are matched to the lyric’s kana, however they’re spaced. A reading written for other words isn’t shown; Lunalyr’s own reading is used for that line.")
                    .environment(\.captionFollowsControl, false)
            }
            TranslationSection()
        }
        .lunalyrReveal(message)
    }

    /// Which lyric style the romanisation choices change.
    static func styleNote(floating: Bool) -> String {
        floating ? "Changes the Floating style; the desktop keeps its own."
            : "Changes the Desktop style; the Floating style keeps its own."
    }
}

/// A line with its reading on the sample wallpaper, as Display sets it:
/// the reading below, in place of the line, or not at all. A static drawing.
private struct ReadingPicture: View {
    let display: RomanizationOptions.Display
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    static let line = "夜に駆ける"
    static let reading = "yoru ni kakeru"

    var body: some View {
        ZStack(alignment: .leading) {
            SampleWallpaper()
            VStack(alignment: .leading, spacing: 4) {
                if display != .replace {
                    Text(Self.line).font(.system(size: 26, weight: .bold))
                }
                if display == .below {
                    Text(Self.reading).font(.system(size: 14, weight: .medium)).opacity(0.75)
                } else if display == .replace {
                    Text(Self.reading).font(.system(size: 26, weight: .bold))
                }
            }
            .foregroundStyle(.white)
            .padding(.leading, 28)
            .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion), value: display)
        }
        .frame(maxWidth: ThemedPageMetrics.column)
        .frame(height: 110)
        .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("A line with its reading: \(display.title)")
    }
}
