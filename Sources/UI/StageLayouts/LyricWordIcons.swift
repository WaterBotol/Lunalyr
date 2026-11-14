import Foundation

/// A small monochrome SF Symbol for a lyric word with a clear picture of
/// its own ("love" a heart, "home" a house, "call" a phone), or nil. Most
/// words have none, and the commonest ("you", "me", "the") never do: the
/// icon marks a word, it is not decoration.
///
/// Matching folds case, punctuation, apostrophes and simple inflections
/// (loves, loved, loving, lovin', lover all read as "love"), and takes a few
/// common Chinese, Japanese and Korean words on a glued run's whole text.
enum LyricWordIcons {
    /// The SF Symbol for `word`, or nil when it has no clear one.
    static func symbol(for word: String) -> String? {
        entry(for: word)?.symbol
    }

    /// Whether `word`'s icon is a strong one: a thing you can picture (a
    /// heart, a house) rather than a person or a verb (she, run). A layout
    /// may let strong icons follow one another and space out the weak ones.
    static func isStrong(_ word: String) -> Bool {
        entry(for: word)?.strong ?? false
    }

    static func entry(for word: String) -> (symbol: String, strong: Bool)? {
        for key in candidates(word) {
            if let symbol = strong[key] { return (symbol, true) }
            if let symbol = weak[key] { return (symbol, false) }
        }
        return nil
    }

    /// `word` lowercased, without punctuation or apostrophes, then the stems
    /// its simple inflections fold back to, most literal first.
    static func candidates(_ word: String) -> [String] {
        let lowered = word.lowercased()
        let kept = lowered.unicodeScalars.filter { CharacterSet.letters.contains($0) || CharacterSet.decimalDigits.contains($0) }
        let base = String(String.UnicodeScalarView(kept))
        guard !base.isEmpty else { return [] }
        var result = [base]
        // Chinese, Japanese and Korean: a word with its particle (夜空に,
        // 밤에) reads as the word.
        guard base.unicodeScalars.allSatisfy({ $0.isASCII }) else {
            let hiragana = { (c: Character) in c.unicodeScalars.allSatisfy { (0x3041...0x3096).contains($0.value) } }
            var stem = base
            while let last = stem.last, hiragana(last), stem.count > 1 { stem.removeLast() }
            if stem != base { result.append(stem) }
            let particles: Set<Character> = ["\u{C5D0}", "\u{C774}", "\u{AC00}", "\u{C744}", "\u{B97C}", "\u{C740}",
                                             "\u{B294}", "\u{C758}", "\u{B3C4}", "\u{B85C}"]
            if base.count > 1, let last = base.last, particles.contains(last) { result.append(String(base.dropLast())) }
            return result
        }
        func add(_ stem: String) {
            guard stem.count >= 2, !result.contains(stem) else { return }
            result.append(stem)
        }
        let suffixes = ["ing", "in", "ers", "er", "ed", "es", "s"]
        for suffix in suffixes where base.hasSuffix(suffix) && base.count > suffix.count + 1 {
            let stem = String(base.dropLast(suffix.count))
            add(stem)
            // loving → love; danced → dance (only a stem of three or more,
            // so "seed" never reads as "see").
            if stem.count >= 3 { add(stem + "e") }
            // running → run; dropped → drop.
            if stem.count >= 3, let last = stem.last, stem.dropLast().last == last, !"aeiou".contains(last) {
                add(String(stem.dropLast()))
            }
        }
        // cried, cries, skies → cry, sky.
        for suffix in ["ied", "ies"] where base.hasSuffix(suffix) && base.count > 4 {
            add(String(base.dropLast(3)) + "y")
        }
        return result
    }

    /// Pictures: a layout may show these one after another.
    static let strong: [String: String] = [
        "love": "heart.fill", "heart": "heart.fill", "lovely": "heart.fill",
        "home": "house.fill", "house": "house.fill",
        "call": "phone.fill", "phone": "iphone", "ring": "phone.fill",
        "night": "moon.fill", "moon": "moon.fill", "midnight": "moon.fill", "tonight": "moon.fill",
        "sun": "sun.max.fill", "sunshine": "sun.max.fill", "morning": "sun.max.fill", "summer": "sun.max",
        "star": "star.fill",
        "rain": "cloud.rain.fill", "storm": "cloud.bolt.rain.fill", "thunder": "cloud.bolt.fill", "lightning": "bolt.fill",
        "fire": "flame.fill", "burn": "flame.fill", "flame": "flame.fill",
        "heartbreak": "heart.slash.fill", "heartbroken": "heart.slash.fill", "broken": "heart.slash.fill",
        "car": "car.fill", "drive": "car.fill",
        "road": "road.lanes", "highway": "road.lanes", "street": "road.lanes",
        "sea": "water.waves", "ocean": "water.waves", "wave": "water.waves", "river": "water.waves",
        "music": "music.note", "song": "music.note", "sing": "music.note", "melody": "music.note", "radio": "radio.fill",
        "dance": "figure.dance",
        "money": "dollarsign", "cash": "dollarsign", "dollar": "dollarsign",
        "clock": "clock.fill",
        "city": "building.2.fill", "town": "building.2.fill",
        "sky": "cloud.fill", "cloud": "cloud.fill",
        "snow": "snowflake", "winter": "snowflake", "cold": "snowflake", "ice": "snowflake",
        "tear": "drop.fill", "cry": "drop.fill",
        "dream": "moon.zzz.fill", "sleep": "moon.zzz.fill",
        "world": "globe", "earth": "globe",
        "text": "message.fill", "message": "message.fill", "letter": "envelope.fill",
        "eye": "eye.fill",
        "hand": "hand.raised.fill",
        "fly": "airplane", "plane": "airplane",
        "bird": "bird.fill", "wing": "bird.fill",
        "flower": "camera.macro", "rose": "camera.macro", "garden": "leaf.fill", "tree": "tree.fill", "leaf": "leaf.fill",
        "key": "key.fill", "door": "door.left.hand.open", "window": "window.vertical.closed",
        "bed": "bed.double.fill", "gift": "gift.fill", "crown": "crown.fill", "king": "crown.fill", "queen": "crown.fill",
        "diamond": "diamond.fill", "gold": "sparkles", "photo": "photo.fill", "picture": "photo.fill",
        "camera": "camera.fill", "book": "book.fill", "game": "gamecontroller.fill", "party": "party.popper.fill",
        "wind": "wind", "mountain": "mountain.2.fill", "island": "beach.umbrella.fill", "beach": "beach.umbrella.fill",
        "lock": "lock.fill", "chain": "link", "bell": "bell.fill", "clown": "theatermasks.fill", "mask": "theatermasks.fill",
        "coffee": "cup.and.saucer.fill", "wine": "wineglass.fill", "drink": "wineglass.fill", "champagne": "wineglass.fill",
        "smoke": "smoke.fill", "candle": "flame.fill", "angel": "sparkles", "magic": "wand.and.stars",
        "train": "tram.fill", "boat": "sailboat.fill", "ship": "ferry.fill",
        "cat": "cat.fill", "dog": "dog.fill",
        "rainbow": "rainbow", "earthquake": "waveform.path",
        // Chinese, Japanese and Korean, on a glued run's whole text.
        "愛": "heart.fill", "恋": "heart.fill", "爱": "heart.fill", "사랑": "heart.fill", "사랑해": "heart.fill",
        "家": "house.fill", "집": "house.fill",
        "夜": "moon.fill", "夜空": "moon.fill", "밤": "moon.fill", "月": "moon.fill", "달": "moon.fill",
        "雨": "cloud.rain.fill", "비": "cloud.rain.fill",
        "星": "star.fill", "별": "star.fill",
        "夢": "moon.zzz.fill", "梦": "moon.zzz.fill", "꿈": "moon.zzz.fill",
        "花": "camera.macro", "꽃": "camera.macro",
        "海": "water.waves", "바다": "water.waves",
        "雪": "snowflake", "눈물": "drop.fill", "涙": "drop.fill", "泪": "drop.fill",
        "空": "cloud.fill", "하늘": "cloud.fill", "太陽": "sun.max.fill", "太阳": "sun.max.fill",
        "火": "flame.fill", "歌": "music.note", "노래": "music.note",
    ]

    /// People and verbs: shown more sparingly.
    static let weak: [String: String] = [
        "her": "figure.stand.dress", "she": "figure.stand.dress", "girl": "figure.stand.dress", "woman": "figure.stand.dress",
        "his": "figure.stand", "he": "figure.stand", "him": "figure.stand", "boy": "figure.stand", "man": "figure.stand",
        "time": "clock.fill", "light": "lightbulb.fill", "day": "sun.max.fill",
        "see": "eye.fill", "look": "eye.fill", "watch": "eye.fill",
        "run": "figure.run", "walk": "figure.walk", "kiss": "mouth.fill", "talk": "bubble.left.fill",
    ]
}
