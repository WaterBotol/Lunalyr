import AppKit
import Carbon.HIToolbox
import SwiftUI

/// System-wide shortcuts. Registered as Carbon hot keys, which need no
/// Accessibility or Input Monitoring permission: only these exact
/// combinations reach the app, nothing else that is typed. Off until
/// switched on in Settings, and each can be switched off on its own.
///
/// They keep clear of the keys window managers ship with: Rectangle, Magnet
/// and Spectacle move windows between displays with ⌃⌥⌘← and ⌃⌥⌘→, so the
/// sync nudges use [ and ]. Each combination is registered exclusively, which
/// fails -- and the Settings page says so -- when another app already holds
/// it, instead of both apps acting on one press.
@MainActor
final class GlobalHotkeys: ObservableObject {
    static let shared = GlobalHotkeys()

    enum Action: UInt32, CaseIterable, Identifiable {
        case toggleLyrics = 1, syncEarlier, syncLater, cycleLayout, copyCard, pauseWallpaper
        var id: UInt32 { rawValue }

        var title: String {
            switch self {
            case .toggleLyrics: return "Show or hide lyrics"
            case .syncEarlier: return "Lyrics 0.1 s earlier for this song"
            case .syncLater: return "Lyrics 0.1 s later for this song"
            case .cycleLayout: return "Next layout"
            case .copyCard: return "Copy a lyric card"
            case .pauseWallpaper: return "Pause or resume the wallpaper"
            }
        }

        /// How a switched-off shortcut is remembered.
        var storageName: String {
            switch self {
            case .toggleLyrics: return "toggleLyrics"
            case .syncEarlier: return "syncEarlier"
            case .syncLater: return "syncLater"
            case .cycleLayout: return "cycleLayout"
            case .copyCard: return "copyCard"
            case .pauseWallpaper: return "pauseWallpaper"
            }
        }
    }

    /// The modifier keys the shortcuts share.
    enum Modifiers: String, CaseIterable, Identifiable {
        case controlOptionCommand
        /// Without Option, so never also a VoiceOver command: VoiceOver's
        /// VO keys are Control-Option, and VO-Command with a letter is its
        /// jump-to-next commands.
        case controlShiftCommand

        var id: String { rawValue }
        var symbols: String { self == .controlOptionCommand ? "⌃⌥⌘" : "⌃⇧⌘" }
        var title: String {
            self == .controlOptionCommand ? "⌃⌥⌘  Control-Option-Command" : "⌃⇧⌘  Control-Shift-Command"
        }
        var carbon: UInt32 {
            UInt32(self == .controlOptionCommand ? controlKey | optionKey | cmdKey : controlKey | shiftKey | cmdKey)
        }
        var cocoa: NSEvent.ModifierFlags {
            self == .controlOptionCommand ? [.control, .option, .command] : [.control, .shift, .command]
        }
    }

    struct Shortcut: Equatable {
        let action: Action
        let keyCode: UInt32
        /// The key as a US keyboard prints it; labels use the layout in use.
        let key: String
    }

    static let shortcuts: [Shortcut] = [
        .init(action: .toggleLyrics, keyCode: UInt32(kVK_ANSI_L), key: "L"),
        .init(action: .syncEarlier, keyCode: UInt32(kVK_ANSI_LeftBracket), key: "["),
        .init(action: .syncLater, keyCode: UInt32(kVK_ANSI_RightBracket), key: "]"),
        .init(action: .cycleLayout, keyCode: UInt32(kVK_ANSI_K), key: "K"),
        .init(action: .copyCard, keyCode: UInt32(kVK_ANSI_C), key: "C"),
        .init(action: .pauseWallpaper, keyCode: UInt32(kVK_ANSI_P), key: "P"),
    ]

    static func shortcut(for action: Action) -> Shortcut? {
        shortcuts.first { $0.action == action }
    }

    enum Keys {
        static let modifiers = "custom.globalShortcutModifiers"
        static let switchedOff = "custom.globalShortcutsOff"
    }

    /// Takes and releases combinations. The app uses Carbon; tests use a
    /// stand-in, so they never hold a key system-wide.
    struct Registrar {
        /// Registers `keyCode` with `modifiers` for `id`, exclusively. False
        /// when another app already holds the combination.
        var register: @MainActor (_ keyCode: UInt32, _ modifiers: UInt32, _ id: UInt32) -> Bool
        var unregister: @MainActor (_ id: UInt32) -> Void
    }

    @Published var modifiers: Modifiers {
        didSet {
            guard modifiers != oldValue else { return }
            defaults.set(modifiers.rawValue, forKey: Keys.modifiers)
            reregister()
        }
    }
    /// Shortcuts the user switched off one by one.
    @Published private(set) var switchedOff: Set<Action>
    /// Combinations another app already holds, so they are not registered.
    @Published private(set) var unavailable: Set<Action> = []
    /// Combinations registered now.
    @Published private(set) var active: Set<Action> = []
    /// Global shortcuts are switched on and registration has run.
    private(set) var registered = false
    private let defaults: UserDefaults
    private let registrar: Registrar
    private var terminations: NotificationObservation?
    /// 'LNLR', so presses meant for another app's hot keys are ignored.
    nonisolated static let signature: OSType = 0x4C4E_4C52

    /// Carbon registration unless a test passes its own `registrar`.
    init(defaults: UserDefaults = .standard, registrar: Registrar? = nil) {
        self.defaults = defaults
        self.registrar = registrar ?? .carbon
        modifiers = Modifiers(rawValue: defaults.string(forKey: Keys.modifiers) ?? "") ?? .controlOptionCommand
        let off = Set(defaults.stringArray(forKey: Keys.switchedOff) ?? [])
        switchedOff = Set(Action.allCases.filter { off.contains($0.storageName) })
    }

    func setEnabled(_ enabled: Bool) {
        if enabled {
            guard !registered else { return }
            registered = true
            registerMissing()
        } else {
            unregister()
        }
    }

    func isSwitchedOn(_ action: Action) -> Bool { !switchedOff.contains(action) }

    func setSwitchedOn(_ on: Bool, for action: Action) {
        if on { switchedOff.remove(action) } else { switchedOff.insert(action) }
        defaults.set(switchedOff.map(\.storageName).sorted(), forKey: Keys.switchedOff)
        guard registered else { return }
        if !on {
            if active.contains(action) {
                registrar.unregister(action.rawValue)
                active.remove(action)
            }
            unavailable.remove(action)
        }
        registerMissing()
    }

    /// Registers every switched-on shortcut that is not registered yet.
    private func registerMissing() {
        var live = active
        var missing = Set<Action>()
        for shortcut in Self.shortcuts where isSwitchedOn(shortcut.action) && !live.contains(shortcut.action) {
            if registrar.register(shortcut.keyCode, modifiers.carbon, shortcut.action.rawValue) {
                live.insert(shortcut.action)
            } else {
                missing.insert(shortcut.action)
            }
        }
        if active != live { active = live }
        if unavailable != missing { unavailable = missing }
        watchForReleasedKeys(!missing.isEmpty)
    }

    private func reregister() {
        guard registered else { return }
        for action in active { registrar.unregister(action.rawValue) }
        active = []
        unavailable = []
        registerMissing()
    }

    private func unregister() {
        for action in active { registrar.unregister(action.rawValue) }
        active = []
        unavailable = []
        registered = false
        watchForReleasedKeys(false)
    }

    /// A combination another app held is free again once that app quits.
    private func watchForReleasedKeys(_ wanted: Bool) {
        if wanted, terminations == nil {
            terminations = NotificationObservation(NSWorkspace.shared.notificationCenter,
                                                   NSWorkspace.didTerminateApplicationNotification) { [weak self] in
                MainActor.assumeIsolated { self?.retryUnavailable() }
            }
        } else if !wanted {
            terminations = nil
        }
    }

    func retryUnavailable() {
        guard registered, !unavailable.isEmpty else { return }
        registerMissing()
    }

    // MARK: - Labels

    /// "⌃⌥⌘L", with the key as the keyboard layout in use types it.
    func label(for action: Action) -> String? {
        Self.shortcut(for: action).map { modifiers.symbols + Self.keyName($0) }
    }

    /// The combination while it is registered, for the menu-bar window.
    func activeLabel(for action: Action) -> String? {
        active.contains(action) ? label(for: action) : nil
    }

    /// For a menu item, which then shows the combination natively. Only
    /// while it is registered: the hot key then takes the press, so the item
    /// never runs twice.
    func keyEquivalent(for action: Action) -> (key: String, modifiers: NSEvent.ModifierFlags)? {
        guard active.contains(action), let shortcut = Self.shortcut(for: action) else { return nil }
        return (Self.keyName(shortcut).lowercased(), modifiers.cocoa)
    }

    static func keyName(_ shortcut: Shortcut) -> String {
        layoutCharacter(for: shortcut.keyCode) ?? shortcut.key
    }

    /// The character a key types on the current keyboard layout -- its
    /// ASCII-capable one, as menus show shortcuts -- upper-cased. Hot keys
    /// follow the key's position, so on a German keyboard ⌃⌥⌘[ is ⌃⌥⌘Ü.
    static func layoutCharacter(for keyCode: UInt32) -> String? {
        guard let source = TISCopyCurrentASCIICapableKeyboardLayoutInputSource()?.takeRetainedValue(),
              let property = TISGetInputSourceProperty(source, kTISPropertyUnicodeKeyLayoutData) else { return nil }
        let data = Unmanaged<CFData>.fromOpaque(property).takeUnretainedValue()
        guard let bytes = CFDataGetBytePtr(data) else { return nil }
        var deadKeys: UInt32 = 0
        var length = 0
        var characters = [UniChar](repeating: 0, count: 4)
        let status = bytes.withMemoryRebound(to: UCKeyboardLayout.self, capacity: 1) { layout in
            UCKeyTranslate(layout, UInt16(keyCode), UInt16(kUCKeyActionDisplay), 0, UInt32(LMGetKbdType()),
                           OptionBits(kUCKeyTranslateNoDeadKeysMask), &deadKeys, characters.count, &length, &characters)
        }
        guard status == noErr, length > 0 else { return nil }
        let text = String(utf16CodeUnits: characters, count: length).trimmingCharacters(in: .whitespacesAndNewlines)
        return text.isEmpty ? nil : text.uppercased()
    }

    // MARK: - Presses

    func perform(_ raw: UInt32) {
        guard registered, let action = Action(rawValue: raw), active.contains(action) else { return }
        let extras = LunalyrExtras.shared
        switch action {
        case .toggleLyrics: extras.toggleLyrics()
        case .syncEarlier: extras.nudgeSync(by: 0.1)
        case .syncLater: extras.nudgeSync(by: -0.1)
        case .cycleLayout: extras.cycleLayout()
        case .copyCard: extras.copyLyricCard()
        case .pauseWallpaper: extras.toggleWallpaperPause()
        }
    }
}

extension GlobalHotkeys.Registrar {
    /// Carbon hot keys for this process, with one handler for all of them.
    @MainActor static var carbon: Self {
        let keys = CarbonHotKeys()
        return Self(register: { keys.register($0, $1, $2) }, unregister: { keys.unregister($0) })
    }
}

@MainActor
private final class CarbonHotKeys {
    private var references: [UInt32: EventHotKeyRef] = [:]
    private var handler: EventHandlerRef?

    func register(_ keyCode: UInt32, _ modifiers: UInt32, _ id: UInt32) -> Bool {
        installHandler()
        unregister(id)
        var reference: EventHotKeyRef?
        let identifier = EventHotKeyID(signature: GlobalHotkeys.signature, id: id)
        // Exclusive: fails while any other app holds the combination, and
        // while held here keeps one press from reaching two apps.
        let status = RegisterEventHotKey(keyCode, modifiers, identifier, GetApplicationEventTarget(),
                                         OptionBits(kEventHotKeyExclusive), &reference)
        guard status == noErr, let reference else { return false }
        references[id] = reference
        return true
    }

    func unregister(_ id: UInt32) {
        if let reference = references.removeValue(forKey: id) { UnregisterEventHotKey(reference) }
    }

    private func installHandler() {
        guard handler == nil else { return }
        var pressed = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, event, _ in
            var identifier = EventHotKeyID()
            let status = GetEventParameter(event, EventParamName(kEventParamDirectObject),
                EventParamType(typeEventHotKeyID), nil, MemoryLayout<EventHotKeyID>.size, nil, &identifier)
            guard status == noErr, identifier.signature == GlobalHotkeys.signature else {
                return OSStatus(eventNotHandledErr)
            }
            let action = identifier.id
            Task { @MainActor in GlobalHotkeys.shared.perform(action) }
            return noErr
        }, 1, &pressed, nil, &handler)
    }
}
