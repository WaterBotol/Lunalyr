import AppIntents

// Shortcuts actions for what the global shortcuts do. Each runs inside
// Lunalyr, in the background, exactly as the shortcut or menu item does.
// An action that launches Lunalyr waits until the app has started, like a
// `lunalyr://` link (`LunalyrLinkRouter`).

struct ToggleLyricsIntent: AppIntent {
    static let title: LocalizedStringResource = "Show or Hide Lyrics"
    static let description = IntentDescription("Hides the lyrics if they are showing, and otherwise shows them.")
    @MainActor func perform() async throws -> some IntentResult { LunalyrLinkRouter.shared.run(.toggleLyrics); return .result() }
}

struct LyricsEarlierIntent: AppIntent {
    static let title: LocalizedStringResource = "Lyrics Earlier"
    static let description = IntentDescription("Moves the lyrics 0.1 s earlier for the song playing, saved with that song.")
    @MainActor func perform() async throws -> some IntentResult { LunalyrLinkRouter.shared.run(.syncEarlier); return .result() }
}

struct LyricsLaterIntent: AppIntent {
    static let title: LocalizedStringResource = "Lyrics Later"
    static let description = IntentDescription("Moves the lyrics 0.1 s later for the song playing, saved with that song.")
    @MainActor func perform() async throws -> some IntentResult { LunalyrLinkRouter.shared.run(.syncLater); return .result() }
}

struct NextLayoutIntent: AppIntent {
    static let title: LocalizedStringResource = "Next Lyric Layout"
    static let description = IntentDescription("Switches to the next lyric layout.")
    @MainActor func perform() async throws -> some IntentResult { LunalyrLinkRouter.shared.run(.cycleLayout); return .result() }
}

struct LyricCardIntent: AppIntent {
    static let title: LocalizedStringResource = "Make a Lyric Card"
    static let description = IntentDescription("Opens the lyric card window for the line playing.")
    @MainActor func perform() async throws -> some IntentResult { LunalyrLinkRouter.shared.run(.lyricCard); return .result() }
}

struct PauseWallpaperIntent: AppIntent {
    static let title: LocalizedStringResource = "Pause or Resume Wallpaper"
    static let description = IntentDescription("Pauses the lyric wallpaper, or resumes it if it is paused.")
    @MainActor func perform() async throws -> some IntentResult { LunalyrLinkRouter.shared.run(.pauseWallpaper); return .result() }
}

struct OpenLunalyrSettingsIntent: AppIntent {
    static let title: LocalizedStringResource = "Open Lunalyr Settings"
    static let description = IntentDescription("Opens Lunalyr's Settings window.")
    @MainActor func perform() async throws -> some IntentResult { LunalyrLinkRouter.shared.run(.openSettings); return .result() }
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
