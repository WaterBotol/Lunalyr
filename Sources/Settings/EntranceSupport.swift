import Foundation

/// Which word entrances a layout actually shows, for the Lyric style page.
///
/// The stage layouts move word-timed lyrics their own way
/// (`StageLyricView`): Hinge, Broadside, Portal and Mosaic never read the
/// chosen entrance's motion, and Torque and Fable take every chosen motion
/// except Soft focus (their own default) and the swipes (Satin Ribbon,
/// Split Ink). Every stage layout still draws Cutout · Highlight's tiles.
/// Lyrics timed only by the line enter as a whole with any entrance, in every
/// layout, so an entrance a layout ignores is marked, not removed.
enum EntranceSupport {
    /// Whether `layout` shows `arrival` on lyrics timed word by word.
    static func appliesToSungWords(_ arrival: WordArrival, in layout: LyricComposition) -> Bool {
        switch arrival {
        // Match the style is the layout's own; a custom mix is judged by
        // its parts; every layout tiles the sung words.
        case .automatic, .mix, .cutoutHighlight: return true
        default: break
        }
        switch layout {
        case .hinge, .broadside, .portal, .mosaic: return false
        case .torque, .fable: return arrival != .fade && !arrival.isSwipe
        case .scattered, .constellation, .constellationEditorial, .echo, .editorial, .spoken, .cascade, .cutout:
            return true
        }
    }

    /// The entrance as the menu lists it. `layout` is nil for Match the
    /// music, which marks nothing: its own picks show every entrance.
    static func menuTitle(_ arrival: WordArrival, in layout: LyricComposition?) -> String {
        guard let layout, !appliesToSungWords(arrival, in: layout) else { return arrival.title }
        return "\(arrival.title) (whole lines only)"
    }

    /// The entrances in `mix` that `layout` leaves out for word-timed lyrics.
    static func ignored(in mix: [WordArrival], layout: LyricComposition) -> [WordArrival] {
        mix.filter { !appliesToSungWords($0, in: layout) }
    }

    /// Under the entrance menu: why the chosen entrance, or part of the
    /// custom mix, doesn't show on word-timed lyrics in this layout.
    static func note(selected: WordArrival, layout: LyricComposition?, mix: [WordArrival]) -> String? {
        guard let layout else { return nil }
        let left = selected == .mix ? ignored(in: mix, layout: layout) : ignored(in: [selected], layout: layout)
        guard !left.isEmpty else { return nil }
        let names = list(left.map(\.title))
        let verb = left.count == 1 ? "plays" : "play"
        return "\(layout.title) moves each sung word its own way, so \(names) \(verb) only on lyrics timed by the line."
    }

    /// "A", "A and B", "A, B and C".
    static func list(_ names: [String]) -> String {
        guard names.count > 1 else { return names.first ?? "" }
        return names.dropLast().joined(separator: ", ") + " and " + names[names.count - 1]
    }
}
