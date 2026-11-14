import XCTest
@testable import VersoCustom

/// Every setting stays reachable through a redesign: each stored key is
/// catalogued (or deliberately left out), each catalogued setting's page is
/// in the sidebar, and searching for a setting by its label finds it.
/// Touches no defaults and no network.
final class SettingsReachabilityTests: XCTestCase {
    // MARK: Keys

    func testEveryCustomKeyInTheSourcesIsCataloguedOrExcluded() {
        let source = Set(SourceKeys.sourceCustomKeys)
        let catalogued = SettingsCatalogue.keys.filter { $0.hasPrefix("custom.") }
        let excluded = Set(SettingsCatalogue.excludedKeys.keys.filter { $0.hasPrefix("custom.") })
        let known = catalogued.union(excluded)
        XCTAssertEqual(source.subtracting(known).sorted(), [],
                       "Keys in the sources with no catalogue entry: add one, or exclude them with a reason")
        XCTAssertEqual(known.subtracting(source).sorted(), [],
                       "Catalogued keys the sources no longer name (rerun Scripts/settings-keys.sh)")
    }

    func testEveryPlainSettingsKeyIsCataloguedOrExcluded() {
        let source = Set(SourceKeys.sourcePlainKeys)
        let catalogued = SettingsCatalogue.keys.filter { !$0.hasPrefix("custom.") }
        let excluded = Set(SettingsCatalogue.excludedKeys.keys.filter { !$0.hasPrefix("custom.") })
        XCTAssertEqual(source.subtracting(catalogued.union(excluded)).sorted(), [])
        XCTAssertEqual(catalogued.union(excluded).subtracting(source).sorted(), [])
    }

    func testExcludedKeysAreNotAlsoCatalogued() {
        let both = SettingsCatalogue.keys.intersection(SettingsCatalogue.excludedKeys.keys)
        XCTAssertEqual(both.sorted(), [])
        for (key, reason) in SettingsCatalogue.excludedKeys {
            XCTAssertFalse(reason.isEmpty, "\(key) needs a reason")
        }
    }

    func testEveryEntryStoresSomethingOrActs() {
        for entry in SettingsCatalogue.all where entry.kind != .action && entry.kind != .textField {
            XCTAssertTrue(!entry.keys.isEmpty || entry.store != nil, "\(entry.id) names no key or store")
        }
    }

    // MARK: Pages

    func testEveryEntryIsOnAPageTheSidebarShows() {
        let sidebar = Set(SettingsPage.Group.allCases.flatMap(\.pages))
        XCTAssertEqual(sidebar, Set(SettingsPage.allCases), "every page is in a sidebar group")
        for entry in SettingsCatalogue.all {
            XCTAssertTrue(sidebar.contains(entry.page), "\(entry.id) is on a page the sidebar doesn't show")
        }
    }

    func testEveryPageHasEntries() {
        for page in SettingsPage.allCases {
            XCTAssertFalse(SettingsCatalogue.entries(on: page).isEmpty, "\(page.title) has no catalogued settings")
        }
        XCTAssertGreaterThan(SettingsCatalogue.all.count, 150)
    }

    // MARK: Search

    func testSearchingAnEntrysLabelFindsItsPageAndTheEntry() {
        for entry in SettingsCatalogue.all {
            for query in [entry.label, entry.label.lowercased()] {
                XCTAssertTrue(entry.page.matches(query), "“\(query)” doesn't find \(entry.page.title)")
                XCTAssertTrue(SettingsCatalogue.search(query).contains(entry), "“\(query)” doesn't find \(entry.id)")
            }
        }
    }

    func testSearchingAKeywordFindsTheEntry() {
        for entry in SettingsCatalogue.all {
            for keyword in entry.keywords {
                XCTAssertTrue(SettingsCatalogue.search(keyword).contains(entry), "“\(keyword)” doesn't find \(entry.id)")
                XCTAssertTrue(entry.page.matches(keyword), "“\(keyword)” doesn't find \(entry.page.title)")
            }
        }
    }

    func testSearchStillFindsWhatItFoundBefore() {
        // The page titles and search terms alone, as before the catalogue.
        for page in SettingsPage.allCases {
            XCTAssertTrue(page.matches(""))
            XCTAssertTrue(page.matches(page.title))
            for term in page.searchTerms { XCTAssertTrue(page.matches(term), "\(page.title): \(term)") }
        }
        XCTAssertTrue(SettingsPage.extras.matches("lyric card"))
        XCTAssertFalse(SettingsPage.allCases.contains { $0.matches("zzqx") })
        XCTAssertEqual(SettingsCatalogue.search(""), [])
        XCTAssertEqual(SettingsCatalogue.search("zzqx"), [])
    }

    func testSearchAddsSettingsThePageTermsMissed() {
        // Words only a setting's label carries now find its page.
        XCTAssertTrue(SettingsPage.typography.matches("backing vocals"))
        XCTAssertTrue(SettingsPage.lyrics.matches("global offset"))
        XCTAssertTrue(SettingsPage.performance.matches("show player on hover"))
        XCTAssertTrue(SettingsPage.share.matches("listen along"))
        XCTAssertTrue(SettingsPage.wallpaper.matches("text backing"))
        // Case, accents and curly quotes don't matter.
        XCTAssertTrue(SettingsPage.share.matches("WHAT I’M LISTENING"))
        XCTAssertEqual(SettingsCatalogue.search("darkening").first?.label, "Darkening")
    }

    func testSearchPutsLabelMatchesFirst() {
        let results = SettingsCatalogue.search("shade")
        let labelled = results.prefix { SettingsCatalogue.fold($0.label).contains("shade") }
        XCTAssertFalse(labelled.isEmpty)
        XCTAssertTrue(results.dropFirst(labelled.count).allSatisfy { !SettingsCatalogue.fold($0.label).contains("shade") })
    }

    // MARK: Labels

    func testLabelsAreUniquePerPage() {
        for page in SettingsPage.allCases {
            let labels = SettingsCatalogue.entries(on: page).map { SettingsCatalogue.fold($0.label) }
            let repeated = Dictionary(grouping: labels, by: { $0 }).filter { $0.value.count > 1 }.keys
            XCTAssertEqual(repeated.sorted(), [], "\(page.title) repeats labels")
        }
    }

    func testLabelsHaveNoTrailingPeriodOrStraySpace() {
        for entry in SettingsCatalogue.all {
            XCTAssertFalse(entry.label.hasSuffix("."), entry.id)
            XCTAssertEqual(entry.label, entry.label.trimmingCharacters(in: .whitespacesAndNewlines), entry.id)
            XCTAssertFalse(entry.section.isEmpty, entry.id)
            for keyword in entry.keywords { XCTAssertEqual(keyword, keyword.lowercased(), "\(entry.id): \(keyword)") }
        }
    }

    /// Sentence case: a capital first letter, and later words lowercase
    /// unless a name or an acronym. Buttons (actions) use title case, as
    /// macOS buttons do; names (a lyric source, an entrance) are as named.
    func testLabelsAreSentenceCase() {
        for entry in SettingsCatalogue.all where !entry.labelIsName {
            let words = entry.label.split(separator: " ").map(String.init)
            guard let first = words.first?.first else { return XCTFail("\(entry.id) has no label") }
            XCTAssertTrue(first.isUppercase || first.isNumber, "\(entry.id) starts in lowercase")
            guard entry.kind != .action else { continue }
            for word in words.dropFirst() {
                let bare = word.trimmingCharacters(in: CharacterSet.punctuationCharacters.union(.symbols))
                XCTAssertTrue(Self.allowedInSentence(bare), "\(entry.id): “\(bare)” is capitalised")
            }
        }
    }

    private static let names: Set<String> = [
        "Discord", "YouTube", "Google", "Fonts", "Lunalyr", "Editorial", "Mac", "macOS", "Low", "Power", "Mode",
        "Play", "Get", "Join", "Lyrimuse", "Swinsian", "I’m", "I'm",
    ]

    private static func allowedInSentence(_ word: String) -> Bool {
        guard word.contains(where: \.isUppercase) else { return true }
        if names.contains(word) { return true }
        let letters = word.filter(\.isLetter)
        return letters.count > 1 && letters.allSatisfy(\.isUppercase)
    }

    // MARK: Agreement with main-actor sources

    @MainActor
    func testGeneratedRowsAgreeWithTheirSources() {
        let shortcutTitles = SettingsCatalogue.entries(on: .extras)
            .filter { $0.keys.contains("custom.globalShortcutsOff") }.map(\.label)
        XCTAssertEqual(shortcutTitles, GlobalHotkeys.shortcuts.map(\.action.title))
        XCTAssertTrue(SettingsCatalogue.entries(on: .lyrics).contains { $0.label == LyricTapSyncController.menuTitle })
        XCTAssertTrue(SettingsCatalogue.entries(on: .extras).contains { $0.label == LunalyrExtras.Titles.showLyricsAnyway })
    }
}

/// The keys the sources name, from `Scripts/settings-keys.sh`. When a key is
/// added or removed, rerun the script and paste its output here.
private enum SourceKeys {
    static let sourceCustomKeys: [String] = [
        "custom.albumGradientWallpaper",
        "custom.animationRate",
        "custom.artistAliases",
        "custom.audioCaptureConfirmed",
        "custom.audioCaptureMethod",
        "custom.audioReactiveEnabled",
        "custom.audioSensitivity",
        "custom.backingVocalStyle",
        "custom.backingVocalsLayer",
        "custom.creditsLayer",
        "custom.desktopPlayer.pendingQueuePlaylistIDs",
        "custom.desktopPlayer.player",
        "custom.desktopPlayer.queuePlaylistID",
        "custom.desktopPlayer.timeMode",
        "custom.discordButton",
        "custom.discordCover",
        "custom.discordJoinParty",
        "custom.discordLyric",
        "custom.discordLyricLead",
        "custom.discordPaused",
        "custom.discordPresence",
        "custom.discordProgress",
        "custom.discordSongButton",
        "custom.discordStatusText",
        "custom.editorialAmount",
        "custom.editorialTypography",
        "custom.floating.backdrop",
        "custom.floating.display",
        "custom.floating.fadeUnderPointer",
        "custom.floating.frames",
        "custom.floating.placement",
        "custom.floating.sizes",
        "custom.floating.textSize",
        "custom.globalShortcutModifiers",
        "custom.globalShortcuts",
        "custom.globalShortcutsOff",
        "custom.globalShortcutsOn",
        "custom.hideLyricsWhileMirroring",
        "custom.libraryVideoFraming",
        "custom.light3DColours",
        "custom.listenRelayURL",
        "custom.listenSharesLyrics",
        "custom.listenSharing",
        "custom.lowPowerVisuals",
        "custom.lyricCardAspect",
        "custom.lyricCardBackgroundPath",
        "custom.lyricCardComposition",
        "custom.lyricCardFormats",
        "custom.lyricCardLinesAfter",
        "custom.lyricCardLinesBefore",
        "custom.lyricCardTextScale",
        "custom.lyricCardUseSong",
        "custom.lyricColourCustom",
        "custom.lyricColourMode",
        "custom.lyricColourSpeed",
        "custom.lyricColourStrength",
        "custom.lyricHighlightCustom",
        "custom.lyricHighlightMode",
        "custom.lyricMatchMode",
        "custom.lyricOffset",
        "custom.lyricSourceOrder",
        "custom.lyricSources",
        "custom.lyricSourcesSeenAMLL",
        "custom.lyricStyle.",
        "custom.lyricsHidden",
        "custom.lyricsSurface",
        "custom.lyrimuseFolder",
        "custom.machineTranslation",
        "custom.machineTranslationTarget",
        "custom.menuBarLyric",
        "custom.menuBarLyricLength",
        "custom.menuBarShade",
        "custom.musicVideoAutoOffsets",
        "custom.musicVideoBlur",
        "custom.musicVideoCache",
        "custom.musicVideoDim",
        "custom.musicVideoDisplays",
        "custom.musicVideoFallback",
        "custom.musicVideoFraming",
        "custom.musicVideoLinks",
        "custom.musicVideoOffsetVideos",
        "custom.musicVideoOffsets",
        "custom.musicVideoOnline",
        "custom.musicVideoStill",
        "custom.nextWordPreviewByStyle",
        "custom.nextWordPreviewLead",
        "custom.openingTitle",
        "custom.openingTitleCover",
        "custom.pauseWhenCovered",
        "custom.player",
        "custom.player.localPlayers",
        "custom.player.webPlayers",
        "custom.preferWordTimedLRCLIB",
        "custom.preferWordTiming",
        "custom.randomizeLyricFonts",
        "custom.repairQQGaps",
        "custom.romanizationDisplay",
        "custom.romanizationLanguage",
        "custom.romanizationSaved",
        "custom.romanizationTones",
        "custom.rotateLayoutCounts",
        "custom.rotateLayouts",
        "custom.rotateLayoutsCursor",
        "custom.screenAudioPermissionDeclined",
        "custom.settingsLargePreview",
        "custom.settingsPage",
        "custom.settingsShowsPreview",
        "custom.settingsStyle",
        "custom.settingsStyleChosen",
        "custom.settingsWindow",
        "custom.showControlsOnHover",
        "custom.showInterludes",
        "custom.showTranslations",
        "custom.songOffsets",
        "custom.starTrailsColours",
        "custom.styleMemory.artists",
        "custom.styleMemory.enabled",
        "custom.styleMemory.songs",
        "custom.textBacking",
        "custom.textBackingAutomatic",
        "custom.updates.adoptedDailyChecks",
        "custom.visualizerHeavyScenesOnMainDisplay",
        "custom.visualizerPunch",
        "custom.visualizerStyle",
        "custom.wallpaperName",
        "custom.wallpaperPath",
        "custom.wallpaperPaused",
        "custom.wallpaperPlacement",
        "custom.wavecoreColours",
        "custom.wavefieldColours",
        "custom.wavefieldPreset",
        "custom.wavefieldProjection",
        "custom.wordArrivalByStyle",
        "custom.wordEntranceMix",
        "custom.wordRevealByStyle",
    ]
    static let sourcePlainKeys: [String] = [
        "backgroundMode",
        "constellationLinkOpacity",
        "constellationSpread",
        "echoStrength",
        "effectSpeed",
        "fontChoice",
        "googleFontFamily",
        "hasOnboarded",
        "lyricComposition",
        "lyricScale",
        "motionBlur",
        "motionExclusions",
        "motionPlacement",
        "noLyrics",
        "onBattery",
        "patternOpacity",
        "patternStyle",
        "respectLowPower",
        "scrimStrength",
        "shaderStyle",
        "useMotionArtwork",
        "vividness",
    ]
}
