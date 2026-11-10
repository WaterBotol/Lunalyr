import SwiftUI
#if canImport(Translation)
import Translation
#endif

/// What Settings says about on-device translation, for the song on screen.
enum MachineTranslationCopy {
    static let toggle = "Translate lyrics on this Mac"
    static let about = "When a song’s lyrics have no translation and are in a language you don’t read, Lunalyr translates them on this Mac with Apple’s Translation and shows them under the lyrics. Each song is translated once and kept in Lunalyr’s cache. Lyrics are never sent anywhere to be translated. A lyric source’s own translation is always shown instead when it has one."

    static func name(_ identifier: String, locale: Locale = .current) -> String {
        locale.localizedString(forIdentifier: identifier) ?? identifier
    }

    static func status(_ state: LyricMachineTranslator.State, enabled: Bool, showing: Bool,
                       locale: Locale = .current) -> String {
        guard enabled else { return "Off. Songs show only their lyric source’s translations." }
        let note = showing ? "" : " Turn on “Show translations under lyrics” to see them."
        switch state {
        case .idle: return "Translated on this Mac when a song needs it." + note
        case .translating(let source, _): return "Translating this song from \(name(source, locale: locale)) on this Mac…"
        case .translated(let source, _, _): return "This song: translated on this Mac from \(name(source, locale: locale))." + note
        case .needsDownload(let source, let target):
            return "This song is in \(name(source, locale: locale)). Download \(name(source, locale: locale)) and \(name(target, locale: locale)) to translate it on this Mac."
        case .unsupported(let source, let target):
            return "Apple’s Translation can’t translate \(name(source, locale: locale)) into \(name(target, locale: locale))."
        case .failed: return "This song couldn’t be translated."
        case .skipped(let reason):
            switch reason {
            case .sourceHasTranslations: return "This song’s lyric source has its own translation, which is shown instead."
            case .readsLanguage: return "This song is in a language you read, so it isn’t translated."
            case .unknownLanguage: return "This song’s language couldn’t be recognised."
            case .noLines, .off, .unavailable: return "Translated on this Mac when a song needs it." + note
            }
        }
    }
}

#if canImport(Translation)
/// The opt-in on the Romanisation page. macOS 15 and later only.
@available(macOS 15.0, *)
@MainActor
struct MachineTranslationControls: View {
    @ObservedObject private var options = MachineTranslationOptions.shared
    @ObservedObject private var translator = LyricsViewModel.shared.machineTranslator
    @Model(\.extras) private var extras
    @State private var languages: [String] = []
    /// Set when the listener turns translation on, so the first missing
    /// language pair asks to download straight away; never otherwise.
    @State private var armed = false
    @State private var download: LyricLanguagePair?

    var body: some View {
        Group {
            Toggle(MachineTranslationCopy.toggle, isOn: $options.enabled)
            Picker("Translate into", selection: $options.target) {
                Text("System language (\(MachineTranslationCopy.name(MachineTranslationPolicy.systemLanguage())))").tag("")
                ForEach(languages, id: \.self) { Text(MachineTranslationCopy.name($0)).tag($0) }
                if !options.target.isEmpty, !languages.contains(options.target) {
                    Text(MachineTranslationCopy.name(options.target)).tag(options.target)
                }
            }
            .disabled(!options.enabled)
            if options.enabled, case .needsDownload(let source, let target) = translator.state {
                Button("Download Languages…") { download = .init(source: source, target: target) }
                    .help("Apple’s Translation asks before downloading")
            }
            InspectorCaption(MachineTranslationCopy.status(translator.state, enabled: options.enabled,
                                                           showing: extras.showsTranslations),
                             more: MachineTranslationCopy.about)
        }
        .task {
            let supported = await LanguageAvailability().supportedLanguages
            languages = Array(Set(supported.map { MachineTranslationPolicy.key($0.minimalIdentifier) }))
                .sorted { MachineTranslationCopy.name($0).localizedCaseInsensitiveCompare(MachineTranslationCopy.name($1)) == .orderedAscending }
        }
        .onChange(of: options.enabled) { _, enabled in armed = enabled }
        .onChange(of: translator.state) { _, state in
            guard armed, options.enabled, case .needsDownload(let source, let target) = state else { return }
            armed = false
            download = .init(source: source, target: target)
        }
        .modifier(TranslationDownloadPrompt(request: $download) {
            LyricsViewModel.shared.retryMachineTranslation()
        })
    }
}
#endif
