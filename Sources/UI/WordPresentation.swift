import Foundation

struct TimedWordKey: Hashable {
    let text: String
    let start: Double
    let end: Double
    init(_ word: LyricsFetcher.Word) { text = word.text; start = word.start; end = word.end }
}
extension LyricsFetcher.Word {
    var presentationID: TimedWordKey { TimedWordKey(self) }
}

struct WordPresentation: Equatable {
    var progress: Double
    var opacity: Double
    var preview: Bool
    @MainActor
    static func make(word: LyricsFetcher.Word, index: Int, nextIndex: Int?, time: Double,
                     composition: LyricComposition) -> WordPresentation {
        let settings = Settings.shared
        guard settings.revealsWords(in: composition) else {
            return .init(progress: 1, opacity: 1, preview: false)
        }
        let ahead = settings.previewsNextWord(in: composition)
        return .init(progress: vc_word_reveal(time, word.start, word.end),
                     opacity: vc_word_visibility(time, word.start, word.end, ahead ? 1 : 0, index == nextIndex ? 1 : 0),
                     preview: ahead && index == nextIndex && time < word.start)
    }
}
