import XCTest
@testable import VersoCustom

// The YouTube and library picks must be the song itself, from the artist,
// and never a live or fan upload. The two first cases are what a user saw.

@MainActor
final class MusicVideoMatchTests: XCTestCase {
    private typealias Result = YouTubeSearchPage.Result

    private func pick(_ results: [Result], title: String, artist: String) -> String? {
        YouTubeSearchPage.officialVideo(in: results, title: title, artist: artist)?.videoID
    }

    func testAFansLiveRecordingOfAnotherSongIsNeverPicked() {
        let fan = Result(videoID: "AAAAAAAAAAA",
                         title: "Dig What You Dug - 4K - Wallows - Republik, Honolulu - 02/28/23 - PhonesUP",
                         channel: "PhonesUP")
        // The song playing was another Wallows song; and even for this
        // very song the fan's concert recording is refused.
        XCTAssertNil(pick([fan], title: "I Don't Want to Talk", artist: "Wallows"))
        XCTAssertNil(pick([fan], title: "Dig What You Dug", artist: "Wallows"))
        var verified = fan
        verified.verifiedArtist = true
        verified.channel = "Wallows"
        XCTAssertNil(pick([verified], title: "Dig What You Dug", artist: "Wallows"), "live: 4K, a venue and a date")
        XCTAssertTrue(MusicVideoTitleMatch.isLiveOrFanUpload(fan.title, songTitle: "Dig What You Dug"))
    }

    func testLoveIsNotCherryFlavoredLove() {
        let results = [
            Result(videoID: "BBBBBBBBBBB", title: "Wallows – Cherry Flavored Love (Official Video)", channel: "Wallows",
                   verifiedArtist: true),
            Result(videoID: "CCCCCCCCCCC", title: "Wallows - Love (Live)", channel: "Wallows", verifiedArtist: true),
            Result(videoID: "DDDDDDDDDDD", title: "Wallows - Lovesick (Official Video)", channel: "Wallows"),
        ]
        XCTAssertNil(pick(results, title: "Love", artist: "Wallows"), "contains is not equals")
        let love = Result(videoID: "EEEEEEEEEEE", title: "Wallows – Love (Official Music Video)", channel: "Wallows")
        XCTAssertEqual(pick(results + [love], title: "Love", artist: "Wallows"), "EEEEEEEEEEE")
        XCTAssertEqual(pick(results, title: "Cherry Flavored Love", artist: "Wallows"), "BBBBBBBBBBB")
        XCTAssertEqual(pick(results, title: "Lovesick", artist: "Wallows"), "DDDDDDDDDDD")
    }

    func testTitleIsComparedWithoutArtistQualifiersAndCredits() {
        let cases: [(video: String, song: String, artist: String)] = [
            ("Wallows - Are You Bored Yet? (feat. Clairo) [Official Video]", "Are You Bored Yet? (feat. Clairo)", "Wallows"),
            ("Wallows - Are You Bored Yet? (feat. Clairo) [Official Video]", "Are You Bored Yet?", "Wallows & Clairo"),
            ("The Weeknd - Blinding Lights (Official Video) [HD]", "Blinding Lights", "The Weeknd"),
            ("Blinding Lights | The Weeknd | Official Music Video", "Blinding Lights", "The Weeknd"),
            ("The Weeknd Blinding Lights", "Blinding Lights", "The Weeknd"),
            ("The Weeknd: Blinding Lights - Remastered 2021", "Blinding Lights", "The Weeknd"),
            ("Song 2 ft. Someone (Official Video)", "Song 2", "Blur"),
            ("The Rolling Stones - (I Can't Get No) Satisfaction (Official Lyric Video)",
             "(I Can't Get No) Satisfaction", "The Rolling Stones"),
            ("Train - Hey, Soul Sister (Official Video)", "Hey, Soul Sister", "Train"),
        ]
        for (video, song, artist) in cases {
            XCTAssertTrue(MusicVideoTitleMatch.matches(videoTitle: video, songTitle: song, artist: artist), video)
        }
        let misses: [(video: String, song: String)] = [
            ("Wallows - Cherry Flavored Love", "Love"), ("Wallows - Lovesick", "Love"),
            ("Wallows - Love Me Back", "Love"), ("Wallows - Remember When / Love", "Love"),
        ]
        for (video, song) in misses {
            XCTAssertFalse(MusicVideoTitleMatch.matches(videoTitle: video, songTitle: song, artist: "Wallows"), video)
        }
        XCTAssertFalse(MusicVideoTitleMatch.isLiveOrFanUpload("Train - Hey, Soul Sister (Official Video)",
                                                              songTitle: "Hey, Soul Sister"))
        for live in ["Wallows - Love (Live at Red Rocks)", "Wallows - Love | Concert", "Wallows - Love [Fancam]",
                     "Wallows - Love (Pro-Shot)", "Wallows - Love 2023-05-01", "Wallows - Love - Tour Edition"] {
            XCTAssertTrue(MusicVideoTitleMatch.isLiveOrFanUpload(live, songTitle: "Love"), live)
        }
        XCTAssertFalse(MusicVideoTitleMatch.isLiveOrFanUpload("Live Forever (Official Video)", songTitle: "Live Forever"))
    }

    func testOnlyTheArtistsOwnVevoOrVerifiedChannelCounts() {
        let title = "Wallows - Love (Official Video)"
        XCTAssertNil(pick([Result(videoID: "AAAAAAAAAAA", title: title, channel: "Random Uploads")],
                          title: "Love", artist: "Wallows"))
        XCTAssertNil(pick([Result(videoID: "AAAAAAAAAAA", title: title, channel: "Wallows - Topic")],
                          title: "Love", artist: "Wallows"))
        XCTAssertEqual(pick([Result(videoID: "AAAAAAAAAAA", title: title, channel: "WallowsVEVO")],
                            title: "Love", artist: "Wallows"), "AAAAAAAAAAA")
        XCTAssertEqual(pick([Result(videoID: "AAAAAAAAAAA", title: title, channel: "Wallows Official")],
                            title: "Love", artist: "Wallows"), "AAAAAAAAAAA")
        XCTAssertEqual(pick([Result(videoID: "AAAAAAAAAAA", title: title, channel: "Atlantic Records",
                                   verifiedArtist: true)], title: "Love", artist: "Wallows"), "AAAAAAAAAAA")
        XCTAssertEqual(pick([Result(videoID: "AAAAAAAAAAA", title: "Are You Bored Yet? [Official Video]", channel: "Clairo")],
                            title: "Are You Bored Yet?", artist: "Wallows & Clairo"), "AAAAAAAAAAA",
                       "any of the credited artists")
    }

    func testVerifiedArtistBadgeIsReadFromTheResults() {
        let html = """
        <html><script>var ytInitialData = {"contents":[{"videoRenderer":{"videoId":"4NRXx6U8ABQ",
        "title":{"runs":[{"text":"Wallows - Love (Official Video)"}]},"ownerText":{"runs":[{"text":"Atlantic"}]},
        "ownerBadges":[{"metadataBadgeRenderer":{"icon":{"iconType":"OFFICIAL_ARTIST_BADGE"},
        "style":"BADGE_STYLE_TYPE_VERIFIED_ARTIST"}}]}},{"videoRenderer":{"videoId":"BBBBBBBBBBB",
        "title":{"runs":[{"text":"x"}]},"ownerText":{"runs":[{"text":"y"}]},
        "ownerBadges":[{"metadataBadgeRenderer":{"style":"BADGE_STYLE_TYPE_VERIFIED"}}]}}]};</script></html>
        """
        let results = YouTubeSearchPage.results(fromHTML: html)
        XCTAssertEqual(results.map(\.verifiedArtist), [true, false], "a plain verified tick is not the artist badge")
    }

    func testLibraryVideoMustBeTheSameSong() {
        let song = MusicVideoSong(title: "Love", artist: "Wallows", duration: 200)
        let hits = [
            AppleMusicVideoLibrary.Hit(title: "Cherry Flavored Love", artist: "Wallows", path: "/cherry.m4v"),
            AppleMusicVideoLibrary.Hit(title: "Love (Live)", artist: "Wallows", path: "/live.m4v"),
            AppleMusicVideoLibrary.Hit(title: "Lovesick", artist: "Wallows", path: "/sick.m4v"),
        ]
        XCTAssertNil(AppleMusicVideoLibrary.pick(hits, for: song))
        let real = AppleMusicVideoLibrary.Hit(title: "Love (Official Video)", artist: "Wallows", path: "/love.m4v")
        XCTAssertEqual(AppleMusicVideoLibrary.pick(hits + [real], for: song), URL(fileURLWithPath: "/love.m4v"))
        let other = AppleMusicVideoLibrary.Hit(title: "Love", artist: "Someone Else", path: "/other.m4v")
        XCTAssertNil(AppleMusicVideoLibrary.pick([other], for: song))
    }

    func testEveryOlderCachedPickIsThrownAway() {
        let name = "LunalyrTests.MusicVideoMatch.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: name)!
        defer { defaults.removePersistentDomain(forName: name) }
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        XCTAssertEqual(MusicVideoCache.version, 3)
        defaults.set(["song": ["at": now.timeIntervalSince1970, "video": "4NRXx6U8ABQ", "v": 2]], forKey: MusicVideoCache.key)
        XCTAssertNil(MusicVideoCache(defaults: defaults).lookup("song", now: now), "a pick from before the exact match")
    }

    // MARK: Embed page

    func testEmbedShowsOnlyTheVideo() {
        let html = YouTubeEmbed.html(videoID: "4NRXx6U8ABQ", start: 0, page: 1, language: "fr")
        for needle in ["controls: 0", "disablekb: 1", "fs: 0", "iv_load_policy: 3", "rel: 0", "playsinline: 1",
                       "cc_load_policy: 0", "hl: 'fr'", "mute: 1", "pointer-events:none", "opacity:0",
                       "scale(\(YouTubeEmbed.overscan))", "overflow:hidden"] {
            XCTAssertTrue(html.contains(needle), needle)
        }
        XCTAssertGreaterThanOrEqual(YouTubeEmbed.overscan, 1.2, "enough to push the title bar and logo off-screen")
        XCTAssertTrue(html.contains("function visibleFor(state) { return state === 1; }"))
        XCTAssertTrue(html.contains("post('state', state)"))
        XCTAssertFalse(html.contains("cc_lang_pref"))
        XCTAssertFalse(YouTubeEmbed.html(videoID: "4NRXx6U8ABQ", start: 0, language: "x'); alert(1").contains("alert"))
    }

    func testCaptionsAreForcedOff() {
        let html = YouTubeEmbed.html(videoID: "4NRXx6U8ABQ", start: 0)
        for needle in ["player.unloadModule('captions')", "player.unloadModule('cc')",
                       "player.setOption('captions', 'track', {})"] {
            XCTAssertTrue(html.contains(needle), needle)
        }
        XCTAssertTrue(html.contains("onReady: function (event) { ready = true; event.target.mute(); hideCaptions();"),
                      "on ready")
        XCTAssertTrue(html.contains("if (state === -1 || state === 1 || state === 5) { hideCaptions(); }"),
                      "and on each video load")
        XCTAssertTrue(html.contains("onApiChange: function () { hideCaptions(); }"), "and when the captions module loads")
        XCTAssertEqual(YouTubeEmbed.playerLanguage(Locale(identifier: "fr_FR")), "fr")
        XCTAssertEqual(YouTubeEmbed.playerLanguage(Locale(identifier: "en_AU")), "en")
    }

    func testPlayerIsVisibleOnlyWhilePlaying() {
        // -1 unstarted, 0 ended, 1 playing, 2 paused, 3 buffering, 5 cued.
        XCTAssertEqual([-1, 0, 1, 2, 3, 5].filter(YouTubeEmbed.showsVideo(state:)), [1])
        XCTAssertTrue(YouTubeEmbed.showsVideo(sample: .init(videoTime: 3, videoPlaying: true)))
        XCTAssertFalse(YouTubeEmbed.showsVideo(sample: .init(videoTime: 3, videoPlaying: false)))
        XCTAssertFalse(YouTubeEmbed.showsVideo(sample: .init(videoTime: 3, videoPlaying: true, buffering: true)))

        let session = YouTubeEmbedSession(sleep: { _ in try? await Task.sleep(for: .seconds(3600)) })
        guard case .load(_, let page) = session.attach("4NRXx6U8ABQ") else { return XCTFail() }
        func state(_ value: Int, trusted: Bool = true) -> YouTubeEmbedSession.Effect {
            session.receive(.init(kind: .state(value), video: "4NRXx6U8ABQ", page: page), trusted: trusted)
        }
        XCTAssertEqual(state(1), .visible(true))
        XCTAssertEqual(state(2), .visible(false), "paused: YouTube's pause button would show")
        XCTAssertEqual(state(0), .visible(false), "held past the end")
        XCTAssertEqual(state(3), .visible(false))
        XCTAssertEqual(state(1, trusted: false), .none)
        XCTAssertEqual(YouTubeEmbedSession.Message(body: ["type": "state", "value": 1, "video": "4NRXx6U8ABQ", "page": page]),
                       .init(kind: .state(1), video: "4NRXx6U8ABQ", page: page))
        _ = session.receive(.init(kind: .error(150), video: "4NRXx6U8ABQ", page: page), trusted: true)
        XCTAssertEqual(state(1), .visible(false), "a failed video stays hidden")
    }

    func testNonLatinAndKpopTitlesMatch() {
        let cases: [(video: String, song: String, artist: String)] = [
            ("YOASOBI「アイドル」 Official Music Video", "アイドル", "YOASOBI"),
            ("米津玄師 MV「Lemon」", "Lemon", "米津玄師"),
            ("ヨルシカ - 『ただ君に晴れ』 (Music Video)", "ただ君に晴れ", "ヨルシカ"),
            ("BLACKPINK - 'Shut Down' M/V", "Shut Down", "BLACKPINK"),
            ("NewJeans (뉴진스) 'Ditto' Official MV", "Ditto", "NewJeans"),
            ("IVE 아이브 'I AM' MV", "I AM", "IVE"),
            ("BTS (방탄소년단) ‘Dynamite’ Official MV", "Dynamite", "BTS"),
        ]
        for (video, song, artist) in cases {
            XCTAssertTrue(MusicVideoTitleMatch.matches(videoTitle: video, songTitle: song, artist: artist), video)
        }
        // Still exact: another song in quotes, or the artist's other words.
        XCTAssertFalse(MusicVideoTitleMatch.matches(videoTitle: "BLACKPINK - 'Shut Down' M/V", songTitle: "Down",
                                                    artist: "BLACKPINK"))
        XCTAssertFalse(MusicVideoTitleMatch.matches(videoTitle: "YOASOBI「アイドル」 Official Music Video",
                                                    songTitle: "アイ", artist: "YOASOBI"))
        XCTAssertFalse(MusicVideoTitleMatch.matches(videoTitle: "IVE 아이브 'I AM' MV", songTitle: "I", artist: "IVE"))
        // Apostrophes are not quotes.
        XCTAssertTrue(MusicVideoTitleMatch.matches(videoTitle: "Wallows - Don't Want to Talk (Official Video)",
                                                   songTitle: "Don't Want to Talk", artist: "Wallows"))
        XCTAssertTrue(MusicVideoTitleMatch.matches(videoTitle: "Artist - Rock 'n' Roll Star (Official Video)",
                                                   songTitle: "Rock 'n' Roll Star", artist: "Artist"))
        XCTAssertTrue(MusicVideoTitleMatch.matches(videoTitle: "IU 좋은 날", songTitle: "좋은 날", artist: "IU"),
                      "the artist written before the title still works")
        let pick = YouTubeSearchPage.officialVideo(
            in: [Result(videoID: "AAAAAAAAAAA", title: "YOASOBI「アイドル」 Official Music Video", channel: "Ayase / YOASOBI")],
            title: "アイドル", artist: "YOASOBI")
        XCTAssertEqual(pick?.videoID, "AAAAAAAAAAA")
    }

    func testTheSampleKeepsThePlayerMuted() {
        let html = YouTubeEmbed.html(videoID: "4NRXx6U8ABQ", start: 0)
        XCTAssertTrue(html.contains("if (player.isMuted && !player.isMuted()) player.mute();"),
                      "every sync sample mutes the player again if anything unmuted it")
    }
}
