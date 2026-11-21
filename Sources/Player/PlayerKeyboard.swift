import AppKit

/// Routes key presses in a player panel to that display's player
/// (`PlayerKeyMap` decides what a key means). The views register what they
/// can do; the panel controller feeds it key-down events.
@MainActor
final class PlayerKeyboard {
    static let shared = PlayerKeyboard()

    struct Handlers {
        var scrub: ScrubController
        var playPause: () -> Void
        var track: (_ forward: Bool) -> Void
        var focusSearch: () -> Void
        /// Esc outside a drag: true when it cleared a search.
        var clearSearch: () -> Bool
        var hasResults: () -> Bool
        var moveSelection: (Int) -> Void
        var playSelection: () -> Void
    }

    private var handlers: [CGDirectDisplayID: Handlers] = [:]

    func register(_ handlers: Handlers, for displayID: CGDirectDisplayID) {
        self.handlers[displayID] = handlers
    }

    func unregister(_ displayID: CGDirectDisplayID) {
        handlers[displayID] = nil
    }

    /// True when the key was the player's.
    func handle(_ event: NSEvent, on displayID: CGDirectDisplayID) -> Bool {
        guard let handlers = handlers[displayID] else { return false }
        let editing = (event.window?.firstResponder as? NSTextView)?.isFieldEditor == true
        let flags = event.modifierFlags
        guard let action = PlayerKeyMap.action(
            keyCode: event.keyCode, characters: event.charactersIgnoringModifiers ?? "",
            command: flags.contains(.command), option: flags.contains(.option), control: flags.contains(.control),
            editingText: editing, hasResults: handlers.hasResults()) else { return false }
        perform(action, with: handlers)
        return true
    }

    func perform(_ action: PlayerKeyAction, with handlers: Handlers) {
        switch action {
        case .playPause: handlers.playPause()
        case .seekBy(let step): handlers.scrub.seek(by: step)
        case .line(let forward): handlers.scrub.seekLine(forward: forward)
        case .track(let forward): handlers.track(forward)
        case .jump(let digit): handlers.scrub.jump(digit: digit)
        case .focusSearch: handlers.focusSearch()
        case .moveSelection(let step): handlers.moveSelection(step)
        case .playSelection: handlers.playSelection()
        case .escape:
            if handlers.scrub.isDragging {
                handlers.scrub.cancel()
            } else if !handlers.clearSearch() {
                ControlPanelController.shared.releaseKeyboard()
            }
        }
    }
}
