import SwiftUI

/// Animation › "Word symbols and actions": the two switches, and under
/// More options their editable lists (`WordAccentPlan`).
@MainActor
struct WordAccentsSection: View {
    @Model(\.settings) private var s

    static let title = "Word symbols and actions"
    static let symbolsToggle = "Show symbols beside listed words"
    static let symbolsList = "Word symbol list"
    static let actionsToggle = "Animate listed action words"
    static let actionsList = "Action word list"
    static let strength = "Action word strength"

    var body: some View {
        InspectorSection(Self.title) {
            Toggle(Self.symbolsToggle, isOn: $s.wordSymbols)
                .toggleStyle(StudioCheckboxStyle())
                .help("When a listed word is sung, a small symbol appears beside it in the word’s colour. English lyrics only.")
            Toggle(Self.actionsToggle, isOn: $s.actionWords)
                .toggleStyle(StudioCheckboxStyle())
                .help("When a listed action word is sung, it moves once: spin whirls round, fall dips, burn glows like embers. A song's repeats of the same word move more gently every other time. With Reduce Motion it only glows.")
            if s.actionWords {
                ChoiceMenu(Self.strength, selection: $s.actionWordStrength, selectedTitle: { (strength: ActionWordStrength) -> String in strength.title }) {
                    ForEach(ActionWordStrength.allCases) { Text($0.title).tag($0) }
                }
                .help("How big the action words' motions are. Wild is the boldest.")
                .revealRow()
            }
            if s.wordSymbols || s.actionWords {
                MoreOptions {
                    if s.wordSymbols {
                        DisclosureGroup(Self.symbolsList) {
                            WordListEditor(entries: symbolEntries, isDefault: s.wordSymbolList == nil,
                                           valueLabel: "SF Symbol", newValue: "star",
                                           preview: { Image(systemName: $0).foregroundStyle(.secondary) },
                                           editor: { value in TextField("SF Symbol", text: value).textFieldStyle(.roundedBorder) },
                                           commit: { s.wordSymbolList = $0 }, reset: { s.wordSymbolList = nil })
                        }
                        .revealRow()
                        InspectorCaption("At most two symbols a line, each shown once.",
                                         more: "Words match in any case, with simple plurals and -ing forms (stars, raining). A line shows at most two symbols, and the same symbol only once.")
                            .revealRow()
                    }
                    if s.actionWords {
                        DisclosureGroup(Self.actionsList) {
                            WordListEditor(entries: actionEntries, isDefault: s.actionWordList == nil,
                                           valueLabel: "Action", newValue: WordAction.spin.rawValue,
                                           preview: { _ in EmptyView() },
                                           editor: { value in
                                               Picker("Action", selection: value) {
                                                   ForEach(WordAction.allCases) { Text($0.title).tag($0.rawValue) }
                                                   // A saved value no longer offered (or a former
                                                   // name) shows as it is, so the row still reads.
                                                   if WordAction(rawValue: value.wrappedValue) == nil {
                                                       Text(WordAction(listValue: value.wrappedValue)?.title
                                                            ?? value.wrappedValue).tag(value.wrappedValue)
                                                   }
                                               }
                                               .labelsHidden()
                                           },
                                           commit: { s.actionWordList = $0 }, reset: { s.actionWordList = nil })
                        }
                        .revealRow()
                        InspectorCaption("One action word a line, in English and other common languages.",
                                         more: "Only the first action word in a line moves, once, as it is sung; repeats of a word later in the song move gently every other time. Besides this list, common words in Spanish, Portuguese, French, German, Italian, Indonesian, Tagalog, Vietnamese, Japanese, Korean and Chinese move too. Words you add here, in any language, come first. With Reduce Motion it glows instead.")
                            .revealRow()
                    }
                }
                .revealRow()
            }
        }
    }

    /// The default order first, then the user's own words A to Z.
    private var symbolEntries: [WordListEditor.Entry] {
        Self.ordered(s.effectiveWordSymbolList, order: LyricWordIcons.defaultSymbolList.map(\.word))
    }

    private var actionEntries: [WordListEditor.Entry] {
        Self.ordered(s.effectiveActionWordList, order: WordAction.defaultList.map(\.word))
    }

    static func ordered(_ list: [String: String], order: [String]) -> [WordListEditor.Entry] {
        let known = order.filter { list[$0] != nil }
        let others = list.keys.filter { !order.contains($0) }.sorted()
        return (known + others).map { WordListEditor.Entry(word: $0, value: list[$0] ?? "") }
    }
}

/// An editable word → value list: one row per word, a row to add, and
/// Restore Defaults.
@MainActor
struct WordListEditor: View {
    struct Entry: Identifiable, Equatable {
        var word: String
        var value: String
        var id: String { word }
    }

    let entries: [Entry]
    let isDefault: Bool
    let valueLabel: String
    let newValue: String
    let preview: (String) -> AnyView
    let editor: (Binding<String>) -> AnyView
    let commit: ([String: String]) -> Void
    let reset: () -> Void
    @State private var newWord = ""

    init<Preview: View, Editor: View>(entries: [Entry], isDefault: Bool, valueLabel: String, newValue: String,
                                      @ViewBuilder preview: @escaping (String) -> Preview,
                                      @ViewBuilder editor: @escaping (Binding<String>) -> Editor,
                                      commit: @escaping ([String: String]) -> Void, reset: @escaping () -> Void) {
        self.entries = entries
        self.isDefault = isDefault
        self.valueLabel = valueLabel
        self.newValue = newValue
        self.preview = { AnyView(preview($0)) }
        self.editor = { AnyView(editor($0)) }
        self.commit = commit
        self.reset = reset
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            ForEach(entries) { entry in
                HStack(spacing: 8) {
                    Text(entry.word).frame(width: 96, alignment: .leading).lineLimit(1)
                    editor(Binding(get: { entry.value }, set: { save(entry.word, $0) }))
                    preview(entry.value).frame(width: 18)
                    Button {
                        save(entry.word, nil)
                    } label: {
                        Image(systemName: "minus.circle")
                    }
                    .buttonStyle(.borderless)
                    .help("Remove “\(entry.word)”")
                    .accessibilityLabel("Remove \(entry.word)")
                }
            }
            HStack(spacing: 8) {
                TextField("Add a word", text: $newWord)
                    .textFieldStyle(.roundedBorder)
                    .onSubmit(add)
                Button("Add", action: add)
                    .disabled(LyricWordIcons.englishKey(newWord) == nil)
            }
            Button("Restore Defaults", action: reset)
                .disabled(isDefault)
        }
        .padding(.top, 4)
    }

    private func current() -> [String: String] {
        Dictionary(entries.map { ($0.word, $0.value) }, uniquingKeysWith: { first, _ in first })
    }

    private func save(_ word: String, _ value: String?) {
        var list = current()
        list[word] = value
        commit(list)
    }

    /// A new word is kept as it will be matched: lowercased, letters only.
    private func add() {
        guard let word = LyricWordIcons.englishKey(newWord) else { return }
        var list = current()
        if list[word] == nil { list[word] = newValue }
        commit(list)
        newWord = ""
    }
}
