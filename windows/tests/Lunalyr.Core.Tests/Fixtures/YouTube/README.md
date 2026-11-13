YouTube's search results page, as `YouTubeSearchPage` reads it. Recorded 13 Nov 2026 with exactly the request the app
sends (the Mac's): one plain GET of `https://www.youtube.com/results?search_query=…` with the Mac's WebKit User-Agent,
`Accept-Language: en`, no cookies and no redirects followed. One request per page, three in all; each came back 200
with `ytInitialData`. No player, embed or video was loaded.

- `results-ed-sheeran-shape-of-you.html`: "Ed Sheeran Shape of You" (19 videos)
- `results-yoasobi-idol.html`: "YOASOBI アイドル" (18 videos)
- `results-the-weeknd-blinding-lights-official-music-video.html`: the automatic lookup's query for "Blinding Lights" by
  The Weeknd (18 videos)

Each is trimmed to what the parser reads: the JSON path to every `videoRenderer`, and in each only `videoId`, the text of
`title`, `ownerText`, `longBylineText`, `lengthText`, `shortViewCountText`, `viewCountText` and `publishedTimeText`, and
the `style` of `ownerBadges`. Everything else (scripts, links, thumbnails, tracking and session values) is dropped. The
trimmed page was checked to parse to exactly the same results as the page as served.

The wall pages are hand-made for the guardrail tests, not copies of any real page: `consent.html` ("Before you continue
to YouTube"), `bot-check.html` ("Sign in to confirm you’re not a bot") and `captcha.html` (unusual traffic and a
captcha).
