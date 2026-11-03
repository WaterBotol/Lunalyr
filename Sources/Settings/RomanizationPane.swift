import AppKit
import SwiftUI
import UniformTypeIdentifiers

@MainActor
struct RomanizationPane: View {
    @ObservedObject private var options = RomanizationOptions.shared
    @ObservedObject private var lyrics = LyricsViewModel.shared
    @State private var message: String?
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                PaneHeader(title: "Romanisation", subtitle: "Read along in Latin letters. Local readings follow the original word timestamps.")
                Picker("Display", selection: $options.display) {
                    ForEach(RomanizationOptions.Display.allCases) { Text($0.title).tag($0) }
                }.pickerStyle(.segmented)
                Picker("Reading language", selection: $options.language) {
                    ForEach(RomanizationOptions.Language.allCases) { Text($0.title).tag($0) }
                }
                Toggle("Keep tone marks and long vowels", isOn: $options.toneMarks)
                Toggle("Prefer saved readings below lyrics", isOn: $options.preferSaved)
                Text("Japanese uses the macOS Japanese tokenizer; Mandarin uses pinyin; Korean and other supported scripts use macOS transliteration. Automatic readings can miss names or unusual pronunciations. Choose a language when a mixed-language song is detected incorrectly.")
                    .font(AppFont.ui(12)).foregroundStyle(.white.opacity(0.55)).fixedSize(horizontal: false, vertical: true)
                Divider().opacity(0.15)
                Text("Saved readings").font(AppFont.ui(14, .semibold))
                Text("A Lyrimuse .roma.lrc file can supply source readings, including Cantonese Jyutping. Saved readings have line timing, so their word entrances are estimated. Replace lyrics uses on-device readings to preserve the original word boundaries.")
                    .font(AppFont.ui(12)).foregroundStyle(.white.opacity(0.55)).fixedSize(horizontal: false, vertical: true)
                Button("Import .roma.lrc for the playing song…") { importReadings() }
                    .disabled(lyrics.lines.isEmpty)
                if let message { Text(message).font(AppFont.ui(11)).foregroundStyle(.white.opacity(0.65)) }
                if let first = lyrics.lines.indices.first(where: { lyrics.romanized[$0] != nil }),
                   let readings = lyrics.romanized[first] {
                    VStack(alignment: .leading, spacing: 9) {
                        Text("CURRENT SONG PREVIEW").font(AppFont.ui(9, .semibold)).tracking(1.2).foregroundStyle(.white.opacity(0.4))
                        Text(lyrics.lines[first].text).font(AppFont.ui(18, .medium))
                        Text(readings.map(\.text).joined(separator: " ")).font(AppFont.ui(14)).foregroundStyle(Verso.cream.opacity(0.75))
                    }
                    .padding(16).frame(maxWidth: .infinity, alignment: .leading)
                    .background(RoundedRectangle(cornerRadius: 12).fill(.white.opacity(0.045)))
                }
            }
            .padding(32)
        }
        .background(Verso.surface)
    }
    private func importReadings() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [UTType(filenameExtension: "lrc") ?? .plainText, .plainText]
        panel.title = "Choose readings for the playing song"
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            let data = try Data(contentsOf: url)
            guard data.count <= 1_000_000, let text = String(data: data, encoding: .utf8) else {
                message = "Choose a UTF-8 LRC file smaller than 1 MB."; return
            }
            message = lyrics.importRomanization(text)
        } catch { message = "That reading file could not be opened." }
    }
}
