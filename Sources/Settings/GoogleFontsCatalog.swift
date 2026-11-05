import Foundation

/// Families offered in the picker.
///
/// Google's full catalogue listing needs an API key, and enumerating ~1,800
/// families would be unusable to scroll anyway. This is a hand-picked set
/// that suits lyric display -- strong display faces, readable text faces, and
/// a few oddities -- grouped so the list can be skimmed. Existing selections
/// outside these groups remain available to the user.
enum GoogleFontsCatalog {
    struct Group: Identifiable {
        var id: String { name }
        var name: String
        var families: [String]
    }

    static let groups: [Group] = [
        Group(name: "Display", families: [
            "Anton", "Bebas Neue", "Archivo Black", "Alfa Slab One", "Righteous",
            "Bungee", "Passion One", "Titan One", "Staatliches", "Chivo",
            "Fjalla One", "Oswald", "Teko", "Rampart One", "Monoton",
        ]),
        Group(name: "Sans", families: [
            "Inter", "Montserrat", "Poppins", "Raleway", "Work Sans",
            "DM Sans", "Manrope", "Outfit", "Figtree", "Sora",
            "Space Grotesk", "Rubik", "Nunito", "Barlow", "Lexend",
        ]),
        Group(name: "Serif", families: [
            "Playfair Display", "Lora", "Merriweather", "Cormorant Garamond",
            "Libre Baskerville", "EB Garamond", "Spectral", "Bitter",
            "Crimson Text", "Source Serif 4", "Zilla Slab", "Roboto Slab",
        ]),
        Group(name: "Handwriting", families: [
            "Caveat", "Dancing Script", "Pacifico", "Satisfy",
            "Shadows Into Light", "Kalam", "Sacramento", "Amatic SC",
        ]),
        Group(name: "Mono", families: [
            "JetBrains Mono", "Space Mono", "IBM Plex Mono", "Fira Code",
            "Roboto Mono", "Source Code Pro",
        ]),
    ]

    static var allFamilies: [String] { groups.flatMap(\.families) }

    /// The listed spelling of a family typed in any case -- Google's names
    /// are case-sensitive, so "dm sans" must be asked for as "DM Sans".
    static func canonicalName(_ family: String) -> String? {
        allFamilies.first { $0.caseInsensitiveCompare(family) == .orderedSame }
    }

    /// Slab serifs, wherever they are filed above.
    private static let slabFamilies: Set<String> = ["alfa slab one", "bitter", "zilla slab", "roboto slab"]

    /// Fragments that mark a family as a serif when it is not in the list.
    private static let serifHints = ["serif", "garamond", "baskerville", "caslon", "bodoni", "didot",
                                     "playfair", "merriweather", "cormorant", "crimson", "spectral",
                                     "mincho", "times", "georgia", "palatino", "charter"]

    /// The voice a family reads in, so Editorial can set its second typeface
    /// against it. Listed families are known; others are judged by name, and
    /// `nil` means the name alone cannot tell (the caller then asks the font
    /// itself). Display, handwriting and mono faces count as sans here.
    static func voice(of family: String) -> LyricFace? {
        let name = family.split(whereSeparator: \.isWhitespace).joined(separator: " ").lowercased()
        guard !name.isEmpty else { return nil }
        if slabFamilies.contains(name) || name.contains("slab") { return .slab }
        if let group = groups.first(where: { $0.families.contains { $0.lowercased() == name } }) {
            return group.name == "Serif" ? .serif : .sans
        }
        // "Sans" wins over a serif hint: PT Sans, Alegreya Sans, Noto Sans.
        if name.contains("sans") || name.contains("grotesk") || name.contains("mono") { return .sans }
        if serifHints.contains(where: name.contains) { return .serif }
        return nil
    }
}
