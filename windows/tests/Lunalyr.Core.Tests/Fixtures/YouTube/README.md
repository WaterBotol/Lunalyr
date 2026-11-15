YouTube's search results page, as `YouTubeSearchPage` reads it. Recorded 13 Nov 2026 with exactly the request the app
sends (the Mac's): one plain GET of `https://www.youtube.com/results?search_query=…` with the Mac's WebKit User-Agent,
`Accept-Language: en`, no cookies and no redirects followed. One request per page, three in all; each came back 200
with `ytInitialData`. No player, embed or video was loaded.

- `results-ed-sheeran-shape-of-you.html`: "Ed Sheeran Shape of You" (19 videos)
- `results-yoasobi-idol.html`: "YOASOBI アイドル" (18 videos)
- `results-the-weeknd-blinding-lights-official-music-video.html`: the automatic lookup's query for "Blinding Lights" by
  The Weeknd (18 videos)

Five more were recorded the same way later on 13 Nov 2026, for the songs that missed their videos in the owner's
listening (Apple Music for Windows; see docs/milestones/M6.md, "Songs from Apple Music for Windows missed their videos").
One request per page, five in all, each 200 with `ytInitialData`:

- `results-tame-impala-lost-in-yesterday-official-music-video.html`: the automatic query for "Lost in Yesterday" by
  Tame Impala (19 videos)
- `results-tame-impala-the-slow-rush-lost-in-yesterday-official-music-video.html`: the query the lookup sent before the
  fix, with Apple Music's joined "Tame Impala — The Slow Rush" as the artist (13 videos; its six playlist
  `lockupViewModel`s are not videos)
- `results-dayglow-hot-rod-official-music-video.html`: the automatic query for "Hot Rod" by Dayglow (18 videos)
- `results-wallows-pleaser-official-music-video.html`: the automatic query for "Pleaser" by Wallows (18 videos)
- `results-hers-what-once-was-official-music-video.html`: the automatic query for "What Once Was" by Her's (18 videos)

The served pages had no `<title>`; each fixture's `<title>` is written from its query.

Each is trimmed to what the parser reads: the JSON path to every `videoRenderer`, and in each only `videoId`, the text of
`title`, `ownerText`, `longBylineText`, `lengthText`, `shortViewCountText`, `viewCountText` and `publishedTimeText`, and
the `style` of `ownerBadges`. Everything else (scripts, links, thumbnails, tracking and session values) is dropped. The
trimmed page was checked to parse to exactly the same results as the page as served.

The wall pages are hand-made for the guardrail tests, not copies of any real page: `consent.html` ("Before you continue
to YouTube"), `bot-check.html` ("Sign in to confirm you’re not a bot") and `captcha.html` (unusual traffic and a
captcha).
