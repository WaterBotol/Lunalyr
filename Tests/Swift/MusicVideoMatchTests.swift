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

    /// YouTube's newer layout, trimmed from a real results page for
    /// "YOASOBI The Bloom official music video" (20 Nov 2026): videos as
    /// `lockupViewModel`, beside a channel and a playlist, which are not
    /// videos. Served either way: assigned in a script, or as plain JSON in
    /// `<script id="yt-initial-data">`. Either read as no videos would
    /// fail the lookup.
    func testVideosInYouTubesNewerLayoutAreRead() {
        let json = #"""
        {"contents":{"twoColumnSearchResultsRenderer":{"primaryContents":{"sectionListRenderer":{"contents":[{"itemSectionRenderer":{"contents":[{"lockupViewModel":{"contentId":"Y4nEEZwckuU","contentType":"LOCKUP_CONTENT_TYPE_VIDEO","metadata":{"lockupMetadataViewModel":{"title":{"content":"YOASOBI「群青」Official Music Video"},"metadata":{"contentMetadataViewModel":{"metadataRows":[{"metadataParts":[{"text":{"content":"YOASOBI"},"icon":{"name":"AUDIO_BADGE","accessibilityLabel":"Official Artist Channel"}},{"text":{"content":"and Echoes"}},{"text":{"content":"243M"},"accessibilityLabel":"243 million views"},{"text":{"content":"5y ago"},"accessibilityLabel":"5 years ago"}],"lockupContentMetadataRowExtension":{"contentType":"METADATA_ROW_CONTENT_TYPE_BYLINE"}}]}}}},"contentImage":{"thumbnailViewModel":{"overlays":[{"thumbnailBottomOverlayViewModel":{"badges":[{"thumbnailBadgeViewModel":{"text":"4:23"}}]}}]}}}},{"lockupViewModel":{"contentId":"UCvpredjG93ifbCP1Y77JyFA","contentType":"LOCKUP_CONTENT_TYPE_CHANNEL","metadata":{"lockupMetadataViewModel":{"title":{"content":"YOASOBI"},"metadata":{"contentMetadataViewModel":{"metadataRows":[]}}}}}},{"lockupViewModel":{"contentId":"qwYhKlY4Tx0","contentType":"LOCKUP_CONTENT_TYPE_VIDEO","metadata":{"lockupMetadataViewModel":{"title":{"content":"YOASOBI「咲き誇れ」Official Music Video / NHK連続テレビ小説『ブラッサム』主題歌"},"metadata":{"contentMetadataViewModel":{"metadataRows":[{"metadataParts":[{"text":{"content":"YOASOBI"},"icon":{"name":"AUDIO_BADGE","accessibilityLabel":"Official Artist Channel"}},{"text":{"content":"and Echoes"}},{"text":{"content":"6.3M"},"accessibilityLabel":"6.3 million views"},{"text":{"content":"1mo ago"},"accessibilityLabel":"1 month ago"}],"lockupContentMetadataRowExtension":{"contentType":"METADATA_ROW_CONTENT_TYPE_BYLINE"}}]}}}},"contentImage":{"thumbnailViewModel":{"overlays":[{"thumbnailBottomOverlayViewModel":{"badges":[{"thumbnailBadgeViewModel":{"text":"4:27"}}]}}]}}}},{"lockupViewModel":{"contentId":"t5oDcHxh2KU","contentType":"LOCKUP_CONTENT_TYPE_VIDEO","metadata":{"lockupMetadataViewModel":{"title":{"content":"咲き誇れ (The Bloom) - YOASOBI (romaji) Lyrics "},"metadata":{"contentMetadataViewModel":{"metadataRows":[{"metadataParts":[{"avatarStack":{"avatarStackViewModel":{"text":{"content":"Blu Blu"}}}},{"text":{"content":"1K"},"accessibilityLabel":"1 thousand views"},{"text":{"content":"1mo ago"},"accessibilityLabel":"1 month ago"}],"lockupContentMetadataRowExtension":{"contentType":"METADATA_ROW_CONTENT_TYPE_BYLINE"}}]}}}},"contentImage":{"thumbnailViewModel":{"overlays":[{"thumbnailBottomOverlayViewModel":{"badges":[{"thumbnailBadgeViewModel":{"text":"4:07"}}]}}]}}}},{"lockupViewModel":{"contentId":"PL_ykr4cserrqj6uLXPZzo0ZTupib7ZVZ9","contentType":"LOCKUP_CONTENT_TYPE_PLAYLIST","metadata":{"lockupMetadataViewModel":{"title":{"content":"YOASOBI - 公式MVまとめ"},"metadata":{"contentMetadataViewModel":{"metadataRows":[{"metadataParts":[{"text":{"content":"あらら"}},{"text":{"content":"Playlist"}}],"lockupContentMetadataRowExtension":{"contentType":"METADATA_ROW_CONTENT_TYPE_BYLINE"}}]}}}},"contentImage":{"thumbnailViewModel":{"overlays":[{"thumbnailBottomOverlayViewModel":{"badges":[{"thumbnailBadgeViewModel":{"text":"65 videos"}}]}}]}}}}]}}]}}}}}
        """#
        let pages = [
            "<html><script nonce=\"x\">var ytInitialData = \(json);</script></html>",
            "<html><script id=\"yt-initial-data\" type=\"application/json\">\(json)</script>"
                + "<script>window['ytInitialData'] = JSON.parse(document.getElementById('yt-initial-data').textContent);</script></html>",
        ]
        for html in pages {
            let results = YouTubeSearchPage.results(fromHTML: html)
            XCTAssertEqual(results.map(\.videoID), ["Y4nEEZwckuU", "qwYhKlY4Tx0", "t5oDcHxh2KU"], "videos only")
            let bloom = results[1]
            XCTAssertEqual(bloom.title, "YOASOBI「咲き誇れ」Official Music Video / NHK連続テレビ小説『ブラッサム』主題歌")
            XCTAssertEqual(bloom.channel, "YOASOBI and Echoes")
            XCTAssertTrue(bloom.verifiedArtist)
            XCTAssertEqual(bloom.duration, 267)
            XCTAssertEqual(bloom.views, "6.3M")
            XCTAssertEqual(bloom.uploaded, "1mo ago")
            XCTAssertEqual(results[2].channel, "Blu Blu", "a channel shown with its picture")
            XCTAssertFalse(results[2].verifiedArtist)
            XCTAssertEqual(pick(results, title: "咲き誇れ", artist: "YOASOBI"), "qwYhKlY4Tx0")
        }
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

    func testAShortArtistNameInsideAnotherChannelIsNotTheArtists() {
        XCTAssertFalse(YouTubeSearchPage.channel("liquidmusic", isOf: "iu"))
        XCTAssertTrue(YouTubeSearchPage.channel("iuofficial", isOf: "iu"))
        XCTAssertTrue(YouTubeSearchPage.channel("officialiu", isOf: "iu"))
        XCTAssertTrue(YouTubeSearchPage.channel("thisiswallowschannel", isOf: "wallows"))
        let title = "IU - Love Wins All (Official MV)"
        XCTAssertNil(YouTubeSearchPage.officialVideo(
            in: [Result(videoID: "AAAAAAAAAAA", title: title, channel: "Liquid Music")], title: "Love Wins All", artist: "IU"))
        XCTAssertEqual(YouTubeSearchPage.officialVideo(
            in: [Result(videoID: "AAAAAAAAAAA", title: title, channel: "IU Official")], title: "Love Wins All", artist: "IU")?
            .videoID, "AAAAAAAAAAA")
    }

    func testALibraryVideoOfTheSameTitleByAnotherBandIsNotPicked() {
        let song = MusicVideoSong(title: "Substitute", artist: "The Who", duration: 220)
        let kinks = AppleMusicVideoLibrary.Hit(title: "Substitute", artist: "The Kinks", path: "/kinks.m4v")
        XCTAssertNil(AppleMusicVideoLibrary.pick([kinks], for: song), "sharing \"The\" is not the same artist")
        let who = AppleMusicVideoLibrary.Hit(title: "Substitute", artist: "The Who", path: "/who.m4v")
        XCTAssertEqual(AppleMusicVideoLibrary.pick([kinks, who], for: song), URL(fileURLWithPath: "/who.m4v"))
        let duet = MusicVideoSong(title: "Are You Bored Yet?", artist: "Wallows & Clairo", duration: 180)
        let wallows = AppleMusicVideoLibrary.Hit(title: "Are You Bored Yet?", artist: "Wallows", path: "/w.m4v")
        XCTAssertEqual(AppleMusicVideoLibrary.pick([wallows], for: duet), URL(fileURLWithPath: "/w.m4v"),
                       "one of the credited artists")
        let feat = AppleMusicVideoLibrary.Hit(title: "Are You Bored Yet?", artist: "Wallows feat. Clairo", path: "/f.m4v")
        XCTAssertEqual(AppleMusicVideoLibrary.pick([feat], for: duet), URL(fileURLWithPath: "/f.m4v"))
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

    /// A version's plain title is searched and matched too (the owner,
    /// 20 Nov 2026): "Feel So Close - Radio Edit" plays the song's video.
    func testARadioEditFindsTheSongsOwnVideo() {
        XCTAssertEqual(MusicVideoTitleMatch.titleVersions("Feel So Close - Radio Edit"), ["Feel So Close - Radio Edit", "Feel So Close"])
        XCTAssertEqual(MusicVideoTitleMatch.titleVersions("Summer - Extended Mix").last, "Summer")
        XCTAssertEqual(MusicVideoTitleMatch.titleVersions("Let It Be - Remastered 2009"), ["Let It Be - Remastered 2009"],
                       "the same key already: nothing more to search")
        let video = Result(videoID: "AAAAAAAAAAA", title: "Calvin Harris - Feel So Close (Official Video)",
                           channel: "CalvinHarrisVEVO")
        XCTAssertEqual(pick([video], title: "Feel So Close - Radio Edit", artist: "Calvin Harris"), "AAAAAAAAAAA")
        // A video naming the version itself comes first.
        let edit = Result(videoID: "BBBBBBBBBBB", title: "Calvin Harris - Feel So Close - Radio Edit (Official Video)",
                          channel: "CalvinHarrisVEVO")
        XCTAssertEqual(pick([video, edit], title: "Feel So Close - Radio Edit", artist: "Calvin Harris"), "BBBBBBBBBBB")
        // The library too.
        let song = MusicVideoSong(title: "Feel So Close - Radio Edit", artist: "Calvin Harris", duration: 205)
        let hit = AppleMusicVideoLibrary.Hit(title: "Feel So Close", artist: "Calvin Harris", path: "/close.m4v")
        XCTAssertEqual(AppleMusicVideoLibrary.pick([hit], for: song), URL(fileURLWithPath: "/close.m4v"))
    }

    /// A title that quotes the song and goes on about its tie-in.
    func testAQuotedTitleBesideATieInIsTheSong() {
        let title = "YOASOBI「咲き誇れ」Official Music Video / NHK連続テレビ小説『ブラッサム』主題歌"
        XCTAssertTrue(MusicVideoTitleMatch.matches(videoTitle: title, songTitle: "咲き誇れ", artist: "YOASOBI"))
        XCTAssertFalse(MusicVideoTitleMatch.matches(videoTitle: title, songTitle: "ブラッサム", artist: "YOASOBI"),
                       "the drama's name is quoted too, but it is the drama's")
        XCTAssertFalse(MusicVideoTitleMatch.matches(videoTitle: title, songTitle: "祝福", artist: "YOASOBI"))
        XCTAssertEqual(pick([Result(videoID: "AAAAAAAAAAA", title: title, channel: "YOASOBI")], title: "咲き誇れ",
                            artist: "YOASOBI"), "AAAAAAAAAAA")
        XCTAssertEqual(MusicVideoTitleMatch.quoted(#"Wallows - "Love" (Official Video)"#), ["Love"])
        XCTAssertTrue(MusicVideoTitleMatch.matches(videoTitle: #"Wallows - "Love" (Official Video)"#, songTitle: "Love",
                                                   artist: "Wallows"))
        // A video about the song quotes it after words of its own (Windows, 20 Nov 2026).
        XCTAssertFalse(MusicVideoTitleMatch.matches(videoTitle: #"Dayglow - How I Made "Hot Rod""#, songTitle: "Hot Rod",
                                                    artist: "Dayglow"))
    }

    /// Jay Chou's 告白气球: the title in 【】 beside its English name, in
    /// traditional characters, after the artist in both scripts (YouTube's
    /// results page, 20 Nov 2026, as the Windows port read it).
    func testATitleInLenticularBracketsInTraditionalCharactersIsTheSong() {
        let official = "周杰倫 Jay Chou (特別演出: 派偉俊)【告白氣球 Love Confession】Official MV"
        let making = "周杰倫 Jay Chou【告白氣球 Love Confession】MV花絮 Making of"
        let rap = "周杰倫 Jay Chou【告白氣球 Love Confession】女生Rap版 - 蔡佩軒 Ariel Tsai"
        for artist in ["周杰伦", "周杰倫", "Jay Chou"] {
            XCTAssertTrue(MusicVideoTitleMatch.matches(videoTitle: official, songTitle: "告白气球", artist: artist), artist)
            XCTAssertTrue(MusicVideoTitleMatch.matches(videoTitle: official, songTitle: "告白氣球", artist: artist), artist)
            XCTAssertTrue(MusicVideoTitleMatch.matches(videoTitle: official, songTitle: "Love Confession", artist: artist),
                          artist)
            XCTAssertFalse(MusicVideoTitleMatch.matches(videoTitle: making, songTitle: "告白气球", artist: artist),
                           "a making-of is about the song")
            XCTAssertFalse(MusicVideoTitleMatch.matches(videoTitle: rap, songTitle: "告白气球", artist: artist),
                           "another singer's version")
        }
        XCTAssertFalse(MusicVideoTitleMatch.matches(videoTitle: official, songTitle: "告白", artist: "周杰伦"))
        XCTAssertFalse(MusicVideoTitleMatch.matches(videoTitle: official, songTitle: "Love", artist: "周杰伦"))
        XCTAssertFalse(MusicVideoTitleMatch.matches(videoTitle: official, songTitle: "晴天", artist: "周杰伦"))
        // A version beside the name is not the song.
        XCTAssertFalse(MusicVideoTitleMatch.matches(videoTitle: "周杰倫【告白氣球 Piano Version】Official MV",
                                                    songTitle: "告白气球", artist: "周杰伦"))
        // 《》 quote too, with anything after, like 「」.
        XCTAssertTrue(MusicVideoTitleMatch.matches(videoTitle: "周杰倫《晴天》官方MV", songTitle: "晴天", artist: "周杰伦"))
        XCTAssertEqual(MusicVideoTitleMatch.songKey("告白氣球"), MusicVideoTitleMatch.songKey("告白气球"))

        let results = [
            Result(videoID: "AAAAAAAAAAA", title: making, channel: "周杰倫 Jay Chou", verifiedArtist: true),
            Result(videoID: "BBBBBBBBBBB", title: rap, channel: "蔡佩軒 Ariel Tsai", verifiedArtist: true),
            Result(videoID: "bu7nU9Mhpyo", title: official, channel: "周杰倫 Jay Chou", verifiedArtist: true),
        ]
        XCTAssertEqual(pick(results, title: "告白气球", artist: "周杰伦"), "bu7nU9Mhpyo")
        // The channel is the artist's own in either script, badge or not.
        var unbadged = results[2]
        unbadged.verifiedArtist = false
        XCTAssertEqual(pick([unbadged], title: "告白气球", artist: "周杰伦"), "bu7nU9Mhpyo")
    }

    /// IU's 밤편지 (YouTube's results page, 20 Nov 2026): the MV is on her
    /// label's channel, 1theK, without the artist badge, named in both
    /// scripts; her own verified channel has only the art track, and
    /// 1theK also has the teaser.
    func testANamePairedAcrossScriptsOnTheLabelsChannelIsTheSong() {
        let mv = "[MV] IU(아이유) _ Through the Night(밤편지)"
        for (song, artist) in [("밤편지", "아이유"), ("Through the Night", "IU"), ("밤편지", "IU"),
                               ("Through the Night", "아이유")] {
            XCTAssertTrue(MusicVideoTitleMatch.matches(videoTitle: mv, songTitle: song, artist: artist), "\(song) \(artist)")
            XCTAssertTrue(MusicVideoTitleMatch.matches(videoTitle: "아이유(IU) - 밤편지(Through the Night)", songTitle: song,
                                                       artist: artist), "\(song) \(artist), the other way round")
        }
        XCTAssertFalse(MusicVideoTitleMatch.matches(videoTitle: mv, songTitle: "Through", artist: "IU"))
        XCTAssertFalse(MusicVideoTitleMatch.matches(videoTitle: mv, songTitle: "밤편지", artist: "BTS"))
        // A bracket in the same script, or in both, is not a pair.
        XCTAssertEqual(MusicVideoTitleMatch.pairReadings("Wallows - Love (Official Video)", artists: ["wallows"]), [])
        XCTAssertEqual(MusicVideoTitleMatch.pairReadings("밤편지 (Live 라이브)", artists: []), [])
        XCTAssertEqual(MusicVideoTitleMatch.pairReadings(mv, artists: ["iu"]),
                       ["[MV] IU _ Through the Night", "[MV] IU _ 밤편지"])

        let results = [
            Result(videoID: "BzYnNdJhZQw", title: mv, channel: "1theK (원더케이)", duration: 283),
            Result(videoID: "6744glqD6lk", title: "Through the Night (밤편지)", channel: "이지금 [IU Official]",
                   verifiedArtist: true, duration: 254, artTrack: true),
            Result(videoID: "cj_8wUhYk_o", title: "[Teaser] IU(아이유) _ Through the Night(밤편지)", channel: "1theK (원더케이)",
                   duration: 39),
        ]
        XCTAssertEqual(pick(results, title: "밤편지", artist: "아이유"), "BzYnNdJhZQw")
        XCTAssertEqual(pick(results, title: "Through the Night", artist: "IU"), "BzYnNdJhZQw")
        XCTAssertNil(pick(Array(results.dropFirst()), title: "Through the Night", artist: "IU"),
                     "never the art track, even on the artist's verified channel; never the teaser")
        XCTAssertNil(pick([Result(videoID: "AAAAAAAAAAA", title: "IU - 밤편지 (Official MV)", channel: "Fan Uploads")],
                          title: "밤편지", artist: "IU"), "another channel still doesn't count")
    }

    /// An art track's description, in both page layouts.
    func testArtTracksAreRecognisedFromTheirDescription() {
        let html = """
        <html><script>var ytInitialData = {"contents":[{"videoRenderer":{"videoId":"6744glqD6lk",
        "title":{"runs":[{"text":"Through the Night (밤편지)"}]},"ownerText":{"runs":[{"text":"이지금 [IU Official]"}]},
        "detailedMetadataSnippets":[{"snippetText":{"runs":[{"text":"Provided to YouTube by Kakao Entertainment "},
        {"text":"Through the Night"}]}}]}},{"videoRenderer":{"videoId":"BzYnNdJhZQw",
        "title":{"runs":[{"text":"[MV] IU(아이유) _ Through the Night(밤편지)"}]},"ownerText":{"runs":[{"text":"1theK"}]},
        "detailedMetadataSnippets":[{"snippetText":{"runs":[{"text":"*English subtitles are now available."}]}}]}},
        {"lockupViewModel":{"contentId":"AAAAAAAAAAA","contentType":"LOCKUP_CONTENT_TYPE_VIDEO",
        "metadata":{"lockupMetadataViewModel":{"title":{"content":"Song"},"metadata":{"contentMetadataViewModel":{
        "metadataRows":[{"metadataParts":[{"text":{"content":"Provided to YouTube by Label"}}],
        "lockupContentMetadataRowExtension":{"contentType":"METADATA_ROW_CONTENT_TYPE_DESCRIPTION"}}]}}}}}}]};</script></html>
        """
        XCTAssertEqual(YouTubeSearchPage.results(fromHTML: html).map(\.artTrack), [true, false, true])
    }

    /// A version with a recording of its own never takes the plain title's video.
    func testLiveAcousticRemixAndLanguageVersionsKeepTheirOwnTitle() {
        for title in ["Feel So Close - Live", "Feel So Close - Acoustic", "Feel So Close - Tiësto Remix",
                      "Feel So Close - Sped Up", "Feel So Close - English Version"] {
            XCTAssertEqual(MusicVideoTitleMatch.titleVersions(title), [title], title)
            let video = Result(videoID: "AAAAAAAAAAA", title: "Calvin Harris - Feel So Close (Official Video)",
                               channel: "CalvinHarrisVEVO")
            XCTAssertNil(pick([video], title: title, artist: "Calvin Harris"), title)
        }
        // A title that is only its own words is searched once.
        XCTAssertEqual(MusicVideoTitleMatch.titleVersions("Radio"), ["Radio"])
        XCTAssertEqual(MusicVideoTitleMatch.titleVersions("Clean"), ["Clean"])
    }

    func testMissesFromBeforeThePlainTitleSearchAreLookedUpAgain() {
        let name = "LunalyrTests.MusicVideoMatch.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: name)!
        defer { defaults.removePersistentDomain(forName: name) }
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        defaults.set(["old": ["at": now.timeIntervalSince1970, "miss": "none"]], forKey: MusicVideoCache.key)
        let cache = MusicVideoCache(defaults: defaults)
        XCTAssertNil(cache.lookup("old", now: now), "a miss from before plain titles were searched")
        cache.store(.missing(.notFound), for: "new", now: now)
        XCTAssertEqual(cache.lookup("new", now: now), .missing(.notFound))
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
        XCTAssertTrue(html.contains("ready = true; event.target.mute(); hideCaptions();"),
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

    func testAReadyPlayerThatNeverPlaysFallsBack() {
        let session = YouTubeEmbedSession(sleep: { _ in try? await Task.sleep(for: .seconds(3600)) })
        guard case .load(_, let page) = session.attach("4NRXx6U8ABQ") else { return XCTFail() }
        XCTAssertEqual(session.notePlayback(shouldPlay: true, playing: false, now: 0), .none, "not ready: the ready timeout's job")
        XCTAssertEqual(session.receive(.init(kind: .ready, video: "4NRXx6U8ABQ", page: page), trusted: true), .ready)
        XCTAssertEqual(session.notePlayback(shouldPlay: true, playing: false, now: 100), .none)
        XCTAssertEqual(session.notePlayback(shouldPlay: true, playing: false, now: 110), .none, "buffering a while is fine")
        XCTAssertEqual(session.notePlayback(shouldPlay: true, playing: true, now: 115), .none, "it played")
        XCTAssertEqual(session.notePlayback(shouldPlay: true, playing: false, now: 116), .none)
        XCTAssertEqual(session.notePlayback(shouldPlay: false, playing: false, now: 130), .none, "paused song: not stuck")
        XCTAssertEqual(session.notePlayback(shouldPlay: true, playing: false, now: 131), .none)
        XCTAssertEqual(session.notePlayback(shouldPlay: true, playing: false, now: 150), .none)
        XCTAssertEqual(session.notePlayback(shouldPlay: true, playing: false, now: 151),
                       .fail(videoID: "4NRXx6U8ABQ", .transient), "twenty seconds that should have played")
        XCTAssertEqual(session.notePlayback(shouldPlay: true, playing: false, now: 200), .none, "reported once")
        // A new page starts afresh.
        guard case .load(_, let next) = session.attach("4NRXx6U8ABQ") else { return XCTFail() }
        _ = session.receive(.init(kind: .ready, video: "4NRXx6U8ABQ", page: next), trusted: true)
        XCTAssertEqual(session.notePlayback(shouldPlay: true, playing: false, now: 300), .none)
        session.stop()
    }
}
