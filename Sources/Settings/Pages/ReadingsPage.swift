import SwiftUI

/// Romanisation: readings for Japanese, Korean and Chinese lyrics, saved
/// readings, and translations.
@MainActor
struct ReadingsPage: View {
    @Model(\.readings) private var o
    @Model(\.lyrics) private var model
    @State private var message: String?

    var body: some View {
        ThemedForm {
            ThemedSection("Romanisation") {
                ChoiceSegments("Display", selection: $o.display,
                               cases: Array(RomanizationOptions.Display.allCases), label: \.title)
                ChoiceMenu("Language", selection: $o.language, selectedTitle: \.title) {
                    ForEach(RomanizationOptions.Language.allCases) { Text($0.title).tag($0) }
                }
                Toggle("Keep tone marks", isOn: $o.toneMarks)
                Toggle("Prefer saved readings", isOn: $o.preferSaved)
                InspectorCaption("Readings follow the original timing.", more: "Readings follow the original timing. Local transliteration can miss names and unusual pronunciations.")
                InspectorCaption("Cantonese shows only readings from the source.", more: "Lunalyr can’t generate Jyutping on this Mac. Lyrics written in Cantonese show only readings from the lyric source or an imported reading LRC, never Mandarin pinyin. Cantonese songs in standard written Chinese look like Mandarin, so choose Cantonese for them.")
            }
            ThemedSection("Saved readings") {
                Button("Import Reading LRC…") {
                    // A file that can't be used says why, as on Timing & sources.
                    switch InspectorImport.lyrics(title: "Choose a reading LRC file") {
                    case nil: break
                    case .failure(let failure)?: message = failure.localizedDescription
                    case .success(let text)?: message = model.importRomanization(text)
                    }
                }
                .disabled(model.lines.isEmpty)
                .help("Use readings from an LRC file for the lyrics showing")
                if model.lines.isEmpty {
                    DisabledReason("Readings can be imported once a song’s lyrics are showing.")
                }
                if let message {
                    InspectorCaption(message)
                }
                InspectorCaption("Saved readings are pronunciation guides.", more: "Saved readings are pronunciation guides. Translations are a separate feature and are not generated here.")
                InspectorCaption("Japanese readings are matched to the lyric’s kana.", more: "Japanese readings are matched to the lyric’s kana, however they’re spaced. A reading written for other words isn’t shown; Lunalyr’s own reading is used for that line.")
            }
            TranslationSection()
        }
        .formStyle(.grouped)
        .scrollContentBackground(.hidden)
    }
}
