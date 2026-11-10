import Foundation

/// The desktop player's keys, while its panel has the keyboard (after a
/// click on it, or in the search field). Media keys stay the system's, and
/// nothing here shadows a system shortcut.
enum PlayerKeyAction: Equatable {
    case playPause
    case seekBy(Double)
    case line(forward: Bool)
    case track(forward: Bool)
    case jump(digit: Int)
    case focusSearch
    case moveSelection(Int)
    case playSelection
    /// Esc: cancel a drag, else clear the search, else give the keyboard back.
    case escape
}

enum PlayerKeyMap {
    /// What a key press does. `characters` is the key's characters ignoring
    /// modifiers; `editingText` is true while the search field has the
    /// keyboard, where letters, digits, Space and the arrows are its own;
    /// `hasResults` is true while search results are listed.
    static func action(keyCode: UInt16, characters: String, command: Bool, option: Bool, control: Bool,
                       editingText: Bool, hasResults: Bool) -> PlayerKeyAction? {
        switch keyCode {
        case KeyCode.escape: return .escape
        case KeyCode.returnKey, KeyCode.enter:
            return hasResults && !command && !option && !control ? .playSelection : nil
        case KeyCode.up where hasResults && !command && !option: return .moveSelection(-1)
        case KeyCode.down where hasResults && !command && !option: return .moveSelection(1)
        default: break
        }
        if control { return nil }
        if command {
            switch keyCode {
            case KeyCode.left where !editingText && !option: return .track(forward: false)
            case KeyCode.right where !editingText && !option: return .track(forward: true)
            default: break
            }
            return characters.lowercased() == "f" && !option ? .focusSearch : nil
        }
        // Everything else belongs to the text while it is being edited.
        if editingText { return nil }
        switch keyCode {
        case KeyCode.space: return option ? nil : .playPause
        case KeyCode.left: return option ? .line(forward: false) : .seekBy(-5)
        case KeyCode.right: return option ? .line(forward: true) : .seekBy(5)
        default: break
        }
        if option { return nil }
        if characters == "/" { return .focusSearch }
        if characters.count == 1, let digit = Int(characters), (0...9).contains(digit) { return .jump(digit: digit) }
        return nil
    }

    /// Without synced lyrics, ⌥← and ⌥→ step this far instead.
    static let lineFallbackStep: Double = 10

    /// The Carbon virtual key codes used here.
    enum KeyCode {
        static let returnKey: UInt16 = 36
        static let enter: UInt16 = 76
        static let space: UInt16 = 49
        static let escape: UInt16 = 53
        static let left: UInt16 = 123
        static let right: UInt16 = 124
        static let down: UInt16 = 125
        static let up: UInt16 = 126
    }
}
