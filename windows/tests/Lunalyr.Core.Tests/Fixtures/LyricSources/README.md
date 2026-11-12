Recorded lyric-service replies, replayed by `FixtureHandler`. Recorded 11 Nov 2026 with the generic agent
`Lunalyr/0.1 (Windows port; dev)`. The envelopes are kept as the services sent them, trimmed to the fields the sources
read. Lyric text is replaced with invented lines, so no copyrighted lyrics are stored. `kugou/download-krc.json`
holds an invented KRC, encrypted the way Kugou serves it.

- LRCLIB: Ed Sheeran, "Shape of You" (`/api/get`, `/api/search`)
- lyrics.ovh: the same song
- Lyrics+: the same song (`lyricsplus.binimum.org`); `dead-node.txt` is what `lyricsplus.prjktla.workers.dev` answered (HTTP 404)
- NetEase: 米津玄師, "Lemon" (search, lyric; NetEase has no YRC for it) and 周杰伦, "晴天" (search). `lyric-yrc.json`
  follows the reply for 陈奕迅, "孤勇者" (id 1901371647), which carries YRC with `yv=-1` as well as `yv=1`.
- AMLL TTML DB: NetEase id 536622304 ("Lemon")
- QQ Music, Kuwo: 周杰伦, "晴天"
- Kugou: 米津玄師, "Lemon"; and `search-qingtian-traditional.json`, the song search for "晴天 周杰倫" (traditional
  script), recorded 13 Nov 2026 with the same agent and trimmed to the fields the source reads: Kugou lists the song
  as 周杰伦 (simplified).
- MusicBrainz: artist searches for "Kenshi Yonezu" and "Jay Chou" (`/ws/2/artist?query="…"&limit=5&fmt=json`), recorded
  12 Nov 2026 with the same generic agent and kept whole (no lyrics in them).
- Apple's catalogue (iTunes Search API), recorded 12 Nov 2026 with the same generic agent and trimmed to the song fields
  (no lyrics, no account): `search-au-yoasobi-idol.json` (`/search?country=au&entity=song&limit=15&term=YOASOBI Idol`)
  and the Japanese storefront's entries for its two "Idol" singles (`lookup-jp-<id>.json`, `/lookup?country=jp&id=…&lang=ja_jp`).
  The `mac-*.json` files are the Mac test's invented catalogue replies (ids 1001–1003, "Racing into the Night"), kept
  as the Mac wrote them so the ported tests use its inputs.

The end-to-end tests (`Lyrics/Pipeline`) also build replies in code (`ServiceReplies`) in these same envelopes, filled with
invented lines, so each test can choose the timing and names it needs.
