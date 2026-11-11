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
- Kugou: 米津玄師, "Lemon"
