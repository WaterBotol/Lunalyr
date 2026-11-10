import AppIntents

// Shortcuts actions for what the global shortcuts do. Each runs inside
// Lunalyr, in the background, exactly as the shortcut or menu item does.
// An action that launches Lunalyr waits until the app has started, like a
// `lunalyr://` link (`LunalyrLinkRouter`).

struct ToggleLyricsIntent: AppIntent {
    static let title: LocalizedStringResource = "Show or Hide Lyrics"
    static let description = IntentDescription("Hides the lyrics if they are showing, and otherwise shows them.")
    @MainActor func perform() async throws -> some IntentResult & ProvidesDialog { LunalyrCommand.dialogResult(.toggleLyrics) }
}

struct LyricsEarlierIntent: AppIntent {
    static let title: LocalizedStringResource = "Lyrics Earlier"
    static let description = IntentDescription("Moves the lyrics 0.1 s earlier for the song playing, saved with that song.")
    @MainActor func perform() async throws -> some IntentResult & ProvidesDialog { LunalyrCommand.dialogResult(.syncEarlier) }
}

struct LyricsLaterIntent: AppIntent {
    static let title: LocalizedStringResource = "Lyrics Later"
    static let description = IntentDescription("Moves the lyrics 0.1 s later for the song playing, saved with that song.")
    @MainActor func perform() async throws -> some IntentResult & ProvidesDialog { LunalyrCommand.dialogResult(.syncLater) }
}

struct NextLayoutIntent: AppIntent {
    static let title: LocalizedStringResource = "Next Lyric Layout"
    static let description = IntentDescription("Switches to the next lyric layout.")
    @MainActor func perform() async throws -> some IntentResult & ProvidesDialog { LunalyrCommand.dialogResult(.cycleLayout) }
}

struct LyricCardIntent: AppIntent {
    static let title: LocalizedStringResource = "Make a Lyric Card"
    static let description = IntentDescription("Opens the lyric card window for the line playing.")
    @MainActor func perform() async throws -> some IntentResult & ProvidesDialog { LunalyrCommand.dialogResult(.lyricCard) }
}

struct PauseWallpaperIntent: AppIntent {
    static let title: LocalizedStringResource = "Pause or Resume Wallpaper"
    static let description = IntentDescription("Pauses the lyric wallpaper, or resumes it if it is paused.")
    @MainActor func perform() async throws -> some IntentResult & ProvidesDialog { LunalyrCommand.dialogResult(.pauseWallpaper) }
}

struct OpenLunalyrSettingsIntent: AppIntent {
    static let title: LocalizedStringResource = "Open Lunalyr Settings"
    static let description = IntentDescription("Opens Lunalyr's Settings window.")
    @MainActor func perform() async throws -> some IntentResult & ProvidesDialog { LunalyrCommand.dialogResult(.openSettings) }
}

extension LunalyrCommand {
    /// The note the command posted while it ran, else what it does: what a
    /// Shortcuts action says back.
    @MainActor
    static func intentDialogText(for command: LunalyrCommand, run: (@MainActor (LunalyrCommand) -> Void)? = nil,
                                 extras: LunalyrExtras? = nil) -> String {
        let extras = extras ?? .shared
        let run = run ?? { LunalyrLinkRouter.shared.run($0) }
        let before = extras.note
        run(command)
        if let note = extras.note, note != before { return note.text }
        return command.doneText
    }

    @MainActor
    static func dialogResult(_ command: LunalyrCommand) -> some IntentResult & ProvidesDialog {
        let text = intentDialogText(for: command)
        return .result(dialog: "\(text)")
    }
}

struct LunalyrShortcuts: AppShortcutsProvider {
    static var appShortcuts: [AppShortcut] {
        AppShortcut(intent: ToggleLyricsIntent(), phrases: ["Show or hide lyrics in \(.applicationName)",
                                                            "Toggle \(.applicationName) lyrics"],
                    shortTitle: "Show or Hide Lyrics", systemImageName: "eye")
        AppShortcut(intent: LyricCardIntent(), phrases: ["Make a lyric card in \(.applicationName)"],
                    shortTitle: "Lyric Card", systemImageName: "photo")
        AppShortcut(intent: PauseWallpaperIntent(), phrases: ["Pause \(.applicationName) wallpaper",
                                                              "Resume \(.applicationName) wallpaper"],
                    shortTitle: "Pause Wallpaper", systemImageName: "pause.circle")
        AppShortcut(intent: NextLayoutIntent(), phrases: ["Next \(.applicationName) layout"],
                    shortTitle: "Next Layout", systemImageName: "square.grid.3x3")
    }
}
