import XCTest
@testable import VersoCustom

/// What the workspace says about a control must match what the renderer
/// does with it.
@MainActor
final class WorkspaceCopyTests: XCTestCase {
    func testEveryRevealLockIsExplained() {
        let locks: [Settings.WordRevealLock?] = LyricComposition.allCases.map { .layout($0) }
            + WordArrival.allCases.map { .entrance($0) } + [.mix]
        for lock in locks {
            let reason = WorkspaceCopy.revealLockReason(lock)
            XCTAssertNotNil(reason, "\(String(describing: lock)) locks Reveal sung words without saying why")
        }
        XCTAssertTrue(WorkspaceCopy.revealLockReason(.layout(.cascade))?.contains(LyricComposition.cascade.title) == true)
        XCTAssertTrue(WorkspaceCopy.revealLockReason(.entrance(.splitInk))?.contains(WordArrival.splitInk.title) == true)
        XCTAssertNil(WorkspaceCopy.revealLockReason(nil))
    }

    func testMatchTheMusicDescribesEveryLayoutItCanPick() {
        var picked = Set<LyricComposition>()
        for energy in stride(from: 0.0, through: 1.0, by: 0.05) {
            for organic in stride(from: 0.0, through: 1.0, by: 0.05) {
                for seed in 0..<4 {
                    let mood = TrackMood(energy: energy, face: .sans, background: .contours,
                                         motionRate: 1, intensity: 1, organic: organic)
                    picked.insert(LyricComposition.matching(mood, seed: seed))
                }
            }
        }
        XCTAssertFalse(picked.isEmpty)
        for composition in picked {
            XCTAssertTrue(WorkspaceCopy.automaticLayoutDetail.contains(composition.title),
                          "Match the music can pick \(composition.title) but does not say so")
        }
    }

    func testEditorialSwitchIsExplainedWhereTheLayoutAlreadyMixesFaces() {
        // Editorial typography is built into these, so the note explains the
        // switch rather than leaving it to do nothing visible.
        for layout in LyricComposition.allCases {
            XCTAssertEqual(WorkspaceCopy.editorialNote(layout: layout) != nil, layout.hasEditorialTypography, layout.title)
        }
        XCTAssertNotNil(WorkspaceCopy.editorialNote(layout: nil), "Match the music can pick Editorial")
    }

    /// Animated covers are looked up only for songs playing in Music and
    /// never for streams, so neither may be told nothing was found.
    func testTheCoversPageNeverReportsALookupThatNeverRan() {
        let album = NowPlayingMonitor.Track(title: "First Light", artist: "Artist", duration: 200, genre: nil, album: "Dawn")
        var radio = NowPlayingMonitor.Track(title: "Live", artist: "Station", duration: 0, genre: nil, album: nil)
        radio.isStream = true
        XCTAssertNil(WorkspaceCopy.coverLookupSkipped(track: album, player: .appleMusic))
        XCTAssertEqual(WorkspaceCopy.coverLookupSkipped(track: album, player: .spotify),
                       "Animated covers come from Apple Music, so they play only for songs playing in Music.")
        XCTAssertEqual(WorkspaceCopy.coverLookupSkipped(track: radio, player: .appleMusic),
                       "Radio streams have no animated cover.")
        // The rule NowPlayingMonitor looks covers up by.
        for player in PlayerKind.allCases {
            for track in [album, radio] {
                XCTAssertEqual(WorkspaceCopy.coverLookupSkipped(track: track, player: player) == nil,
                               player.backend.usesAppleCatalogue && !track.isStream, "\(player) \(track.title)")
            }
        }
        XCTAssertEqual(WorkspaceCopy.coverIdlePrompt(followed: PlayerKind.allCases), "Play an album in Music to check its cover.")
        XCTAssertEqual(WorkspaceCopy.coverIdlePrompt(followed: [.appleMusic]), "Play an album in Music to check its cover.")
        XCTAssertEqual(WorkspaceCopy.coverIdlePrompt(followed: [.spotify]),
                       "Animated covers come from Apple Music, and Lunalyr follows Spotify only.")
    }

    /// Under Reduce Motion an imported video holds on a still frame, so the
    /// caption must not also promise that it loops.
    func testTheImportCaptionSaysWhatReduceMotionDoesToVideos() {
        XCTAssertEqual(WorkspaceCopy.importedWallpaperDetail(reduceMotion: false),
                       "Images stay still. Videos loop silently. Lunalyr keeps a copy so moving the original won’t break your wallpaper.")
        let reduced = WorkspaceCopy.importedWallpaperDetail(reduceMotion: true)
        XCTAssertEqual(reduced,
                       "Images stay still. Reduce Motion is on, so videos hold on a still frame. Lunalyr keeps a copy so moving the original won’t break your wallpaper.")
        XCTAssertFalse(reduced.contains("loop"))
    }
}
