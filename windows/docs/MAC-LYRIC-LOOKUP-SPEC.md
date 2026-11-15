# Lunalyr (macOS) lyric lookup: technical spec for the Windows port

Source read: branch `next/integration` at `7544de6` (worktree `wt-int`), static reading only. Windows port baseline: `79b84db`. Section 8 lists everything in `Sources/Lyrics` that changed since then. All paths below are relative to the Mac repo root. "(inferred)" marks anything not read directly in code.

The aim is parity: Windows should find lyrics as often as the Mac, pick the same match, and replace it at the same moments.

---

## 0\. Overview

```
LyricsViewModel.handleTrackChange
  -> LyricQuery(title, artist, album, duration) + genre        (raw player metadata, no clean-up)
  -> LyricsFetcher.lookup(query, force:false, onUpdate:)
       pin?        -> return pin (no network)
       duration<=0 -> skippedStream (no lookup)
       saved automatic choice (14 d) for this key+fingerprint -> use it, ask no online source
       else drop sources that answered "nothing" < 600 s ago (LyricMissCache)
       collect(): every source as its own task, each bounded (LRCLIB 20 s, others 8 s)
         Publication.step after every event -> wait / holdForUpgrade / publish
         stop sources whose answer can no longer matter (matters / couldBeat / couldDemote)
       save the best online candidate if settled, else record a miss
  -> LyricsViewModel.receive: first result shows at once, later ones swap at a line boundary
```

Main files:

- `Sources/Lyrics/LyricsFetcher.swift`: lookup, collect, Publication, screen, outranks, couldBeat, matters, couldDemote, rejection, LRCLIB client, LRC and Lyricsfile parsers, timing tiers.  
- `Sources/Lyrics/LyricSources.swift`: `LyricSourceID` (timingCeiling, isLocal), `LyricSourceOptions` (mode, order, cache fingerprint `v14`), `LyricQuery` (normalize, similarity, core, checkable, recordingKey), `LyricCandidate`, `SavedLyrics` (disk cache and pins), `LyricMissCache`, `LyricHTTP`.  
- `Sources/Lyrics/LyricAgreement.swift`: peers, timingOutlier, agreementFamily.  
- `Sources/Lyrics/LyricPhraseTiming.swift`: isPhraseTimed, hasWordTimingEvidence (new since 79b84db).  
- `Sources/Lyrics/LyricLanguage.swift`: script detection, `languageMismatch`, `OriginalTitles` (iTunes JP lookup), `withOriginalTitle`.  
- `Sources/Lyrics/VersionTags.swift`: Live/Remix/... and language-version tags.  
- `Sources/Lyrics/ArtistAliases.swift`: MusicBrainz native-script artist names, `withAliases`.  
- `Sources/Lyrics/AdditionalLyricsProviders.swift`: QQ, Kugou, Kuwo, lyrics.ovh.  
- `Sources/Lyrics/NetEaseLyricsProvider.swift`, `KPoeLyricsProvider.swift` (Lyrics+), `AMLLLyricsProvider.swift`, `AppleMusicCachedLyrics.swift`, `LocalLyricBridge.swift` (Lyrimuse).  
- `Sources/Lyrics/LyricGapTiming.swift`: timing faults (bunched and crushed lines).  
- `Sources/Lyrics/LyricsViewModel.swift`: `reception`, swap at a line boundary, offline retry.  
- `Shared/LyricRules/LyricRules.c`: `ll_recording_match`. The Windows repo owns this file and the Mac compiles the same C (see `Shared/README.md`, `Shared/LyricRules/Fixtures.json`).  
- `Sources/Support/ConstellationGeometry.c`: `vc_match_score` (each provider's own hit score).

Sources (`LyricSourceID`): `appleMusicCache, lyricsPlus, netEase, qqMusic, kugou, lrclib, lyrimuse, kuwo, lyricsOVH, amll`.

- `isLocal`: `appleMusicCache` and `lyrimuse` only. Local results are never written to the automatic cache and are re-read on every lookup.  
- `timingCeiling` (the best timing tier a source can give): `lyricsOVH` 0, `kuwo` 1, every other source 3\.  
- `recommendedOrder`: `appleMusicCache, lyrimuse, lyricsPlus, amll, netEase, lrclib, qqMusic, kugou, kuwo, lyricsOVH`.  
- Enabled by default: every source except `lyrimuse` and `appleMusicCache`. `amll` is switched on once for existing users (`custom.lyricSourcesSeenAMLL`).  
- **AMLL is a Mac source that the Windows list in the brief leaves out.** It has word timing (ceiling 3\) and finds songs by NetEase id. Without it, Windows has fewer word-timed matches for CJK songs, so expect a different *timing* tier more often than a total miss (inferred).

---

## 1\. Query building

### 1.1 No clean-up before searching

`LyricQuery(title:artist:album:duration:)` (`LyricSources.swift`) takes the player's title, artist and album **as they are**. Nothing strips "feat.", brackets, " \- Remastered" or version tags before the request: every provider is sent the raw title and artist. The init only clamps `duration` to `0...864000` (non-finite becomes 0). `genre` is set separately (`LyricsViewModel.handleTrackChange`: `query.genre = track.genre`) and is only a hint (§4.4).

Tolerance for qualifiers lives in **matching** (`LyricQuery.similarity` / `core`, §4.1), not in the query. A port that cleans titles before searching will send different requests from the Mac. It may well find more, but it is not parity, and LRCLIB `get` then fails on titles stored with their qualifier (inferred).

`recordingKey` \= `normalize(title) | normalize(artist) | normalize(album ?? "") | Int(duration.rounded())`. Test: `"Don’t Stop (2011 Remaster)", "Beyoncé", album "Live", 221.4` gives `"don t stop 2011 remaster|beyonce|live|221"`. `normalize` folds diacritics and case (`locale: .current`), splits on anything that isn't alphanumeric and joins with single spaces. Pins, the automatic cache and per-song offsets are keyed by it.

`hasArtist` \= the artist is not blank after trimming. `artistScore(x)` \= `similarity(artist, x)`, or **0.5** when the query has no artist.

### 1.2 Fields each source receives

| Source | Request | Title / artist | Album | Duration |
| :---- | :---- | :---- | :---- | :---- |
| LRCLIB `get` | `https://lrclib.net/api/get` | `track_name`, `artist_name` | `album_name` if non-empty | `duration` \= `Int(duration.rounded())` if \> 0 |
| LRCLIB `search` | `https://lrclib.net/api/search` | `track_name` \+ `artist_name` (title only when there is no artist) | no | no |
| Lyrics+ / KPoe | `{host}/v2/lyrics/get` | `title`, `artist` | `album` if non-empty | `duration` \= `Int(duration)` (**truncated**, not rounded) if \> 0 |
| NetEase | `https://music.163.com/api/search/get` | `s` \= `"title artist"`, `type=1`, `limit=8` | no | used only to pick a hit |
| QQ | `https://c.y.qq.com/soso/fcgi-bin/client_search_cp` | `w` \= `title + " " + artist`, `p=1`, `n=8`, `format=json`, Referer `https://y.qq.com/` | no | used to pick a hit |
| Kugou search | `https://mobileservice.kugou.com/api/v3/search/song` (the old `mobilecdn` host fails TLS) | `keyword` \= `title + " " + artist`, `page=1`, `pagesize=8` | no | used to pick a hit |
| Kugou lyric search | `https://lyrics.kugou.com/search` | `keyword` \= `title + " " + artist`, `hash`, `ver=1`, `man=yes`, `client=pc` | no | `duration` \= `Int(duration*1000)` ms |
| Kuwo | `https://search.kuwo.cn/r.s` | `all` \= `title + " " + artist`, `rn=8`, `pn=0`, ... | no | used to pick a hit |
| lyrics.ovh | `https://api.lyrics.ovh/v1/{artist}/{title}` (path-encoded, `/;?#%` escaped) | requires an artist | no | no |
| AMLL | NetEase search, then `ncm-lyrics/<id>.ttml` | via NetEase | no | via NetEase |
| Apple Music cache | local SQLite | compared locally | compared | compared (±1.5 s) |
| Lyrimuse folder | local `.lrc` (+ `.ttml` / `.yrc` / `.roma.lrc` / `.tr.lrc`) | compared | compared | `[length:]` |

### 1.3 Artist aliases (`ArtistAliases.swift`, `LyricsFetcher.withAliases`)

- Applies only to `ArtistAliases.catalogues` \= **NetEase, QQ, Kugou, Kuwo**. Not to LRCLIB, Lyrics+ or AMLL.  
- Setting `custom.artistAliases` ("Search under other names"), default **true**. The same switch gates original titles.  
- Skipped when the query has no artist, or the artist is already in CJK script (`isOtherScript`: U+3040–30FF, U+3400–9FFF, U+AC00–D7AF, U+F900–FAFF).  
- Search order: the library artist first. The result is kept if non-nil and either `!metadataReported` or `artistScore(first.artist) >= 0.65`. Otherwise each alias is tried in turn, and the first hit whose `similarity(found.artist, alias) >= 0.65` is kept, **with `artist` rewritten to the library artist**.  
- MusicBrainz request: `https://musicbrainz.org/ws/2/artist?query="<artist>"&limit=5&fmt=json` (quotes and backslashes removed from the name), timeout **4 s**, User-Agent `Lunalyr/1.0 (desktop lyrics app)`, requests spaced **1.1 s** apart (a queue of slots).  
- `pick`: exactly one artist with `score >= 90` whose name or any alias has `similarity >= 0.95` to the asked name. Two such artists means ambiguous, and nothing is returned. Ranking: aliases in locales `ja, zh, zh_Hans, zh_Hant, zh_TW, zh_CN, zh_HK, ko`, primary first (rank 0), then the main name (1), then other aliases (2). Keep names with `similarity(name, artist) < 0.65` that are written in CJK script, deduplicated by `normalize`, **at most 2**.  
- Cache: `ArtistAliases.json` beside the lyric folder, keyed by `normalize(artist)`, **30 days**, empty answers included. Failed requests are not stored. At most 2000 entries, oldest dropped. In-flight requests are shared.  
- Test (`ArtistAliasTests`): "Lemon — Kenshi Yonezu" on NetEase asks `["Kenshi Yonezu", "米津玄师", "米津玄師"]`.

### 1.4 Original titles (`OriginalTitles` actor, `LyricsFetcher.withOriginalTitle`, in `LyricLanguage.swift`)

- Wraps **LRCLIB, NetEase, QQ, Kugou, Kuwo, Lyrics+, AMLL** (`originalTitleSources`). lyrics.ovh, Apple cache and Lyrimuse are not wrapped. For the four catalogues the nesting is `withOriginalTitle { withAliases { provider } }`, so the original-title retry also tries aliases.  
- First search: the listed title. A retry happens only when **all** of these hold: the switch is on, the source is in the set, the query has an artist and duration \> 0, the title has no CJK script, the title carries no language tag, and the first answer is **not usable**. `usable` means non-nil, carrying no language-version tag the song lacks, and not Latin-only lyrics when `eastAsianGenre(genre)`.  
- `OriginalTitles.original(for:)` returns nil (without asking) if there is no artist, duration is ≤ 0, the title is already native, or **the storefront is `jp`**. The storefront is `Locale.current.region` lowercased, default `us`.  
  1. `GET https://itunes.apple.com/search?term=<primaryArtist(artist) + " " + title>&entity=song&country=<storefront>&limit=15`. `primaryArtist` is the text before the first `&` or `,`.  
  2. Match (`OriginalTitles.match`): `similarity(trackName, title) >= 0.95`, the same `VersionTags.tags`, `ArtworkProvider.artistMatches` (credits split on `, & + / ; × feat ft with vs x and`, any shared name), and `|trackTimeMillis/1000 − duration| <= 3` (an entry without a length passes). The nearest length wins.  
  3. `GET https://itunes.apple.com/lookup?id=<trackId>&country=jp&lang=ja_jp`.  
  4. `originalName`: the Japanese name must contain CJK script and have `similarity < 0.95` to the listed title. The album (`collectionName`) is returned too.  
  - Timeout **4 s**, requests spaced **1 s** apart, both answers and "none" cached in `OriginalTitles.json` for **30 days** (max 2000 entries). Failures are not cached. Key: `normalize(title)|normalize(artist)|Int(duration.rounded())`.  
- Retry query: `title = original.title`, `album = original.album ?? query.album`. The hit is accepted only if it has no version-tag mismatch against the *original* title, has `similarity(found.title, original) >= 0.65` (unless `!metadataReported`), and its lyrics are not Latin-only. Then `found.title = listed title` (so the recording check and the cache key hold) and `viaOriginalTitle = true`.  
- Test: "Racing into the Night — YOASOBI" asks `["Racing into the Night", "夜に駆ける"]` and is found as `夜に駆ける - Single`.

---

## 2\. Search flow per source

### 2.1 Orchestration (`LyricsFetcher.lookup` / `collect`)

- **All enabled sources run in parallel**, each in its own `Task` so it can be cancelled on its own. Each is wrapped by `bounded`: it races the fetch against **LRCLIB 20 s / every other source 8 s** (`Timing.lrclibLimit`, `Timing.sourceLimit`). On timeout the state is "Timed out", which does not count as *answered*, so no miss is cached.  
- **Music first** (automatic lookups with `appleMusicCache` enabled and at least one online source): only the local sources start. The online sources start when Music answers or after **0.7 s** (`Timing.musicFirst`), whichever comes first. The Apple reader has its own **650 ms** deadline (`AppleMusicCachedLyrics.Reader`).  
- **Apple second look**: if the Music cache returned nil on an automatic lookup, it is asked again after **4 s** (`appleRetry`). That pending retry does not hold back the first result. **Windows: none of this applies; every source starts at t \= 0\.**  
- Inside each provider, requests are **sequential** (hit 1, then hit 2, ...). Within a source, the alias and original-title retries are also sequential.

### 2.2 LRCLIB (`LyricsFetcher.lrclib`, `requestLRCLIB`)

1. If the query has an artist: `GET /api/get` (track, artist, album?, duration?).  
   - HTTP 404 means nil. 503/429 on the first attempt: wait `Retry-After` (default 1 s), only if ≤ **3 s**, then retry **once**. A longer wait throws "LRCLIB is busy". Any status other than 200 throws. Each request has a **4 s** timeout. Bodies over 2 MB throw. User-Agent `Lunalyr/0.4.6 (https://github.com/WaterBotol/Lunalyr)`.  
2. **Instrumental**: if the exact payload has `instrumental == true` and `similarity(title, trackName) >= 0.95`, **return nil and skip the search**. A search would only find the album's sung edition with the same name and length.  
3. If the exact payload has **sound timed lyrics** (Lyricsfile or synced LRC that parse non-empty with `timingFault == nil`), there is no search. Otherwise, so for no exact match, plain-only, or faulty timing: `GET /api/search` with track \+ artist (title only without an artist), reading **the first 30** results.  
4. Every payload is scored with `vc_match_score(sim(title), artistScore, |Δduration|, known = (payload.duration != nil && query.duration > 0), words = 0, lines = 1, album = normalize equal)`. Scores ≤ 0 are dropped. Lines come from `lyricsfile` (YAML) first, then `syncedLyrics` (LRC), then `plainLyrics`. Penalties: **−12 plain, −6 timing fault**.  
5. `bestOffer`: prefer candidates that pass `rejection`, then the highest score. **One** candidate per source.  
- No romanised or stripped-title fallback. The only other fallbacks are the original-title wrapper and the title-only search when there is no artist.

`vc_match_score(title, artist, delta, known, words, lines, album)` (`ConstellationGeometry.c`): `-1000` if title \< **0.62**, artist \< **0.45**, or (known and delta \> **25**). Otherwise `title*48 + artist*27 + (known ? max(-22, 14 - 2*delta) : 0) + (words ? 20 : lines ? 7 : 0) + (album ? 4 : 0)`. This score only orders **one source's** own hits. `outranks` never compares scores across sources.

### 2.3 Lyrics+ / KPoe (`KPoeLyricsProvider.lookup`)

- Mirrors in order: `lyricsplus.prjktla.workers.dev`, `lyricsplus.binimum.org`, `lyricsplus.prjktla.my.id`. Each request has a **4 s** timeout. User-Agent `Lunalyr/0.1 (macOS now-playing lyrics)`. The body may be gzip without a `Content-Encoding` header, so check the magic bytes `1f 8b` and inflate.  
- Results: 200 with a decodable body and lines means lyrics. 200 with nothing usable, or 404 with a JSON body, means **notFound**. 429 rests the mirror **60 s**. Any other status, an unreadable body, a timeout or no response rests it **600 s**. Offline or cancelled aborts.  
- Order: healthy mirrors, then at most one resting mirror (the one due back soonest). After a `notFound`, **one** second opinion comes from a healthy mirror, only within **3.5 s** of the lookup start. If the second mirror has lyrics, the empty-handed mirror rests 600 s. notFound everywhere returns nil (a real miss). No usable mirror at all throws (not cached as a miss).  
- Conversion: only lines with a non-empty `syllabus` are kept. A line without syllables is **dropped**, so a reply with no syllables at all counts as notFound (whether nodes send syllables for line-type lyrics is (inferred)). Syllables are glued into words when written without a space. CJK, kana, Hangul and Thai syllables stay separate tokens (`LyricScript.words`). Each syllable lasts at least min(start \+ 0.06, line end).  
- `metadata.source` is cut at `" ("`, and provenance becomes `"<upstream> via Lyrics+"`. **`isAppleRelay`**: the suffix is `" via Lyrics+"` and the upstream, lowercased, is `"apple"` or `"apple music"`. "Applesauce", "Musixmatch", "Spotify", "QQ" and "Lyrics+" are not relays.  
- Candidate: `title`/`artist` \= `LyricQuery.checkable(reported, against: asked)` ?? the asked name. `checkable` keeps the reported name if `similarity >= 0.75`, or if both names are Latin (code points \< U+0250 or in U+1E00–1EFF); otherwise it uses the library's name, since it may be the same name in another script. **`duration = nil` always.** `metadataReported` is true only when both names were checkable.

### 2.4 NetEase (`NetEaseLyricsProvider`, `LyricsFetcher.netEase`)

- A single search, `limit=8`, timeout **6 s**, no retry. A reply without `result` is an error (thrown, not cached as a miss). `result` with no songs is a real miss.  
- `bestSong`: `titleMatch >= 0.75`, `artistMatch >= 0.65` (the best of each listed artist and the joined credit; 1 when there is no artist), the entry must have a length when the query does, and `delta < 8 s`. Sort key, highest first: `[fits = delta <= max(3, 2% of duration), titleMatch, artistMatch, -delta]`.  
- Lyrics: `https://music.163.com/api/song/lyric?id=<id>&lv=1&kv=1&tv=-1&yv=1&rv=-1`. Prefer `yrc` (word timing), then `lrc`. `romalrc` is attached as readings.  
- The candidate carries the matched song's title, artist and duration (`metadataReported = song != nil`).

### 2.5 QQ / Kugou / Kuwo (`AdditionalLyricsProviders`)

- Each search hit is scored with `vc_match_score(..., words 0, lines 1, album 0)` and kept if \> 0\. Hits that **pass the recording check** (`namesRecording`, the same rule as `rejection`) are tried first, then by score.  
- QQ: tries **3** hits. `fcg_query_lyric_new.fcg?songmid=…&nobase64=1&g_tk=5381`. Base64 is decoded if the text has no `[`.  
- Kugou: tries **2** hits. For each, `lyrics.kugou.com/search`, then up to **2** lyric candidates. **KRC (`fmt=krc`) first** for word timing (`KugouKRC.enhancedLRC`), then `fmt=lrc`.  
- Kuwo: tries **2** hits. `www.kuwo.cn/openapi/v1/www/lyric/getlyric?musicId=` builds line LRC (ceiling 1).  
- The first hit that yields parsed lines is returned (one candidate). \+13 to the score for word timing. HTTP via `LyricHTTP.json`: timeout **6 s**, any status other than 200 throws, max 2 MB.

### 2.6 lyrics.ovh

Plain text only (ceiling 0). Needs an artist. Title and artist are copied from the query, with `duration nil` and score 0\.

### 2.7 AMLL

`NetEaseLyricsProvider.matchSong` (the same `bestSong`), then `fetchText` (timeout **8 s**) from `raw.githubusercontent.com/amll-dev/amll-ttml-db/refs/heads/main/ncm-lyrics/<id>.ttml`, with `Steve-xmh/amll-ttml-db` as fallback. Uses NetEase's song metadata.

### 2.8 Lyrimuse local folder (`LocalLyricBridge.candidate`)

Reads at most 30000 files. Name pre-filter (`mayBelong`): the file name, normalised, contains `core(title)` (or the normalised title). Only the first **150** `.lrc` files, sorted by name, are parsed. Each needs `[ti:]`, and `[ar:]` when the query has an artist. Thresholds: title `>= 0.92`, artist `>= 0.85` (0 without an artist), album `>= 0.80` if both are present, last line start `<= duration + 8`. Sidecars `.ttml` / `.yrc` supplement word timing. Score \= `vc_match_score(..., words = wordTimed, lines 1, album) + 32`. Never cached, and re-read when the folder fingerprint (names, sizes and mtimes of matching files) changes (`refreshLocal`).

### 2.9 Apple Music cache (Mac only)

SQLite `~/Library/Caches/com.apple.Music/Cache.db`, read-only, `busy_timeout` 150 ms. The most recent **180** `amp-api.music.apple.com/v1/catalog/%/songs%` responses that include `syllable-lyrics`. `matchLevel`: length within **±1.5 s** is required. Level 2 means title, artist and album exact. Level 1 means the same version tags, title and artist similarity `>= 0.95`, and either no album or the same `albumCore` (" \- Single"/" \- EP" and bracketed edition words removed) with the same album tags. The single best level must name exactly one song id, or nothing is returned. The TTML's last line end must be ≤ duration \+ 3\.

---

## 3\. When lyrics show, and when they are replaced

### 3.1 Timing constants (`LyricsFetcher.Timing.standard`)

| Name | Value | Meaning |
| :---- | :---- | :---- |
| `grace` | **1.5 s** | show the best match in hand even if sources are pending |
| `upgradeSettle` | **0.75 s** | an upgrade waits this long for something better still |
| `appleRetry` | 4 s | Music cache second look (Mac only) |
| `lrclibLimit` | **20 s** | LRCLIB cap |
| `sourceLimit` | **8 s** | every other source's cap |
| `musicFirst` | 0.7 s | online sources wait for Music (Mac only) |

### 3.2 `Publication.step(best, settled, graceOver, settleOver, latest)`

Runs after **every** event (each outcome, grace, settle, onlineDue), with `best = choose(screen(found).accepted)` and `settled = isSettled(best, pending)`.

1. `locked` (a pin is showing) or no best: `.wait`.  
2. **New since 79b84db**: if something is shown, refresh it from `latest` (the same source and same result), so that its agreement and `timingOutlier` reflect later answers.  
3. Nothing shown yet: publish if `settled || graceOver`, else wait.  
4. Something shown: `isUpgrade` \= a different source **and** `outranks(best, current)`. If not, wait (**a lower or equal match never replaces**). If `settled || settleOver`, publish. Otherwise `.holdForUpgrade`: start one 0.75 s settle timer (a token makes a stale timer ineffective).  
5. At the end of `collect`, it steps once more with `settled = graceOver = settleOver = true`.

### 3.3 `isSettled` / `couldBeat(best, from: source)`

`isSettled` is true when no enabled pending source `couldBeat` the best. `couldBeat`:

1. The same source as best: false.  
2. `timing = rankedTiming(best)` and `ceiling = min(source.timingCeiling, 2)`. If exactly one of them is \> 0, return `ceiling > 0`.  
3. Best is timed but another version (`!fitsVersion`): true.  
4. `ceiling != timing`: return `ceiling > timing`.  
5. Compare `lead(source)` (by source alone) with `lead(best)` (by candidate): if they differ, return `lead(source) < lead(best)`.  
6. **Smart only**: if `confidence(best) < closestMatch`, return true. `closestMatch` \= **2.25** when the duration is known, else 2\. So a best match **without a reported length (Lyrics+ always, lyrics.ovh)**, or with any name difference, keeps same-lead sources running.  
7. Otherwise: `order(source) < order(best.source)`.

Agreement is deliberately *not* part of `couldBeat` (waiting for agreement would undo early stopping).

### 3.4 Early stop: `matters(source, best, onlineBest, among)`

Only on automatic lookups, once something is shown. A pending source is stopped (state `"Not needed: …"`) unless it matters:

- `best` is the Apple cache with rankedTiming 2: nothing matters (Mac only).  
- `couldBeat(best, from: source)`: matters.  
- **`couldDemote(best, from: source, among)` (new since 79b84db)**: matters. It holds when best is **LRCLIB** with rankedTiming 2 that fits the version and is not already an outlier, the source is not LRCLIB with ceiling ≥ 1, **no** other candidate in hand (fitting the version) has the same words *and* agreeing timing with LRCLIB, and either a sound word-or-mixed-timed rival is in hand or the source's ceiling is ≥ 2\.  
- Local sources stop here. For online sources, they matter if they could beat the **online best**, since that is what gets cached (true when there is no online best).  
- Stopped sources' answers are ignored. A cached choice is only saved if `isSettled(best, pending: stopped online sources)`.  
- A forced lookup ("Search") **never stops sources early**, so every alternative is listed.

### 3.5 Reception in the UI (`LyricsViewModel.reception` / `upgrade`)

- `reception(update, applied, rerun)`: on a rerun (a non-forced lookup while lyrics are already on screen), **ignore every update until complete**. Ignore if `revision <= applied`, unless complete with `applied == 0`. If `applied > 0` (or a rerun with a result): `.swapAtBoundary`, else `.show`.  
- The first result shows **at once**. Every later revision goes through `upgrade`: if a line is being sung now, poll every **150 ms** until the sung line index changes (into a gap or the next line), for at most **12 s**, then apply. A newer upgrade cancels a waiting one. An identical result (`sameResult`: every field of every line, translations included) is not re-applied.  
- Pinned/selected (`select`) and imported lyrics write a **pin** and stop the lookup. A pin always wins (`lookup` returns it before anything else unless `force`). With `force`, `Publication.locked = true`, so automatic results never replace it.  
- Offline: if nothing was found and the network looked unreachable, `retryWhenOnline` is set and the lookup runs again **2 s** after the connection returns. If already online, it reruns at once, **once per track**.  
- Streams: `duration <= 0` means no automatic lookup (`skippedStream`).

---

## 4\. Matching a candidate to the song

### 4.1 Name similarity (`LyricQuery.similarity(lhs, rhs)`)

- If either side is empty after `normalize`, compare the raw strings, trimmed and folded (symbol- or emoji-only names: "🦋", "\!\!\!"): 1 or 0\.  
- Equal normalised strings: **1**.  
- `core(a) == core(b)`: **0.95**. `core` removes bracketed segments (`( [ { （ 【`) whose words start with feat/ft/featuring/with or contain remaster, remastered, live, edit, version, mono or stereo. It truncates at the first `" - "`/`–`/`—` if the rest is such a qualifier (not counting "with"), and truncates at `" feat. "`, `" ft. "` or `" featuring "`. Examples: "Song (2011 Remaster)" vs "Song \- Remastered 2011" \= 0.95; "(feat. Latto)" vs "(with Latto)" \= 0.95; "Song – Live at Wembley" vs "Song \[Radio Edit\]" \= 0.95. "(I Can't Get No) Satisfaction" keeps its brackets.  
- Otherwise `max(tokenOverlap = |A∩B| / max(|A|,|B|), contains ? 0.85 : 0)`. `contains` means whole-word containment, or substring containment when the shorter side has unspaced script (Thai, Myanmar, Khmer, kana, CJK). "Coldplay" vs "Coldplay, BTS" \= 0.85; "夜に駆ける" vs "夜に駆ける英語版" \= 0.85; "Love" vs "Lovers" \< 0.75; "Pink" vs "Pinkfong" \< 0.65.

### 4.2 Recording check (`LyricsFetcher.rejection`, shared C `ll_recording_match`)

`ll_recording_match(titleSim, artistScore, compareArtist = hasArtist, lengthRequired = !hasArtist, compareLength = lengthDecides, trackDur, candDur)`:

- title \< **0.75**: "Rejected: title “…”".  
- compareArtist and artist \< **0.65**: "Rejected: artist “…”" (or "no artist given").  
- lengthRequired and either length unknown: "Rejected: no artist or song length to compare". So without an artist, a candidate needs a length.  
- compareLength, both known, and `|Δ| > max(3, 2% of track)`: "Rejected: duration m:ss vs m:ss". Fixture: 180 vs 183 matches, 186 fails.  
- `lengthDecides` is false only for **Lyrimuse when the query has an artist**.  
- Extra rule: a non-local candidate **without a length** whose last vocal line starts after `duration + 3` gives "Rejected: lyrics run past the end of this recording".  
- Rejected candidates are kept as `rejected` (listed with the reason, can be chosen by hand, and remembered with the miss, §6.2). They never show automatically.

### 4.3 Version fit (`fitsVersion` \= `!languageMismatch && VersionTags.mismatch(query, candidate.title).isEmpty`)

- `VersionTags.tags(name)` reads only qualifier segments: brackets `( [ { （ 【 〔 「 『 < ＜`, everything after the first `" - "`, and a trailing `~…~`.  
- Word tags: live, unplugged, acoustic (acustico), remix (remixes, remixed, rmx, "X Mix" except Original/DJ/Continuous/Album mix; "Extended Mix" is extended), demo, instrumental (inst, karaoke, offvocal, "off vocal"), a cappella, sped up (spedup, nightcore, "speed up"), slowed, **edit**, extended, orchestral (symphonic), rehearsal, piano ("piano version"/"piano ver"), dj mix ("\[Mixed\]", "continuous mix").  
- CJK spans: 现场/現場/ライブ/라이브 \= live, 伴奏/纯音乐/オフボーカル/インスト \= instrumental, 混音/リミックス \= remix, and others.  
- Language tags (`languages` \= english, japanese, korean, chinese): "English Version", "Eng ver.", "(English)" (a lone word needs more than 3 letters), 英語ver., 日本語ver., 中文版, and so on. "(JP)", "(Album Version)", "English Girl" and "(English Rose Remaster)" are not tags.  
- `mismatch` \= (offered − playing − album's tags) ∪ (playing − offered). A tag the album carries ("Live at Wembley") is not held against a match.  
- **Another version is never rejected.** It ranks below every timed match for the playing version (§5), and still shows if it is all there is.  
- Note the asymmetry: `core()` treats "live"/"edit" as the same name (0.95, so it passes the recording check), while `VersionTags` treats them as another version (ranked lower).

### 4.4 Language (`LyricLanguage.weigh`, run inside `screen`)

- `script(of lines)`: lines with letters, at least **4** of them. **native** when ≥ 4 native lines and native ≥ **30%**; **latin** when native ≤ **5%** (1 in 20); otherwise mixed. Native means kana, Han or Hangul ranges.  
- `expected(query, candidates)`, in order: (a) a language tag on the title, else on the album (only english gives latin; a set including english with others gives nil; others give native); (b) **the Apple Music cache candidate's script** (mixed gives nil); (c) native script in the title or album gives native; (d) a vote among candidates without a language tag: a native candidate with `viaOriginalTitle` decides native outright; otherwise native if `native > latin`, or `native == latin > 0` with an East Asian genre (`j-pop, jpop, j-rock, jrock, anime, japan, kayokyoku, enka, vocaloid, k-pop, kpop, korea, c-pop, cpop, mandopop, cantopop, chinese, visual kei`, or a genre in native script). Otherwise nil.  
- `languageMismatch` \= (expected native and the candidate is latin) or (expected latin and the candidate is native). **Local sources are never marked.**  
- Mixed lyrics (K-pop) are never held against a song.

### 4.5 Instrumental / no lyrics

- LRCLIB `instrumental: true` on the exact match (title ≥ 0.95) means no lyrics, and no search runs (§2.2).  
- Lines that are empty or only `♪♫•·-–—` are instrumental breaks (`isInstrumentalMark`) and excluded from every "vocal" count. Credits ("Produced by:", 作词, ...) become `credit` lines (`isInstrumental`, never lead lines).  
- A synced result whose vocal lines are all empty gets tier 0\.

---

## 5\. Ranking end to end

### 5.1 Timing tiers (`timingTier`, `rankedTiming = min(tier, 2)`)

- `validWordTiming(line)`: `wordTimed`, has words, finite with end \> start, the words' letters and digits equal the line's, **not phrase-timed**, every word within `[line.start − 0.05, line.end + 0.05]` with end \> start, and starts non-decreasing.  
- **`isPhraseTimed(text, letters, tokens)` (new since 79b84db)**:  
  - Unspaced script (`LyricScript.keepsSyllables`): `(tokens == 1 && letters > 2) || letters/tokens > 4`.  
  - Spaced script: `words` \= whitespace parts containing a letter or digit; `(tokens == 1 && words > 1) || (tokens*2 <= words && words > 2)`.  
  - A real one-word line ("Yeah\!", "晴天") still counts as word timing.  
- **`hasWordTimingEvidence(vocal)` (new)**: `real = vocal lines with ≥ 2 words and validWordTiming`. True when `real > 0 && real*4 >= vocal.count` (at least a quarter).  
- Tier 3: every vocal line valid and evidence present ("Word-by-word"). Tier 2: some line valid and evidence present ("Mixed timing"). Tier 1: synced with vocal lines ("Line-synced"). Tier 0: plain or empty.  
- So **phrase-timed "words" count as line timing**: LRCLIB Lyricsfile rows of one token per line, "Lemon"'s phrase rows, and "晴天" with 1 real line in 10 all rank as tier 1\. The Lyricsfile parser also refuses phrase-timed tokens (`parseLyricsFile`), and the display un-word-times such lines and gives them estimated words (`LyricsViewModel.presentation`).

### 5.2 Timing faults (`timingFault`, `LyricGapTiming`)

- **bunchedRun**: among sung, non-credit lines, a line is "too quick" when the gap to the next line is in `[0, min(0.6, syllables × 0.08))`. A run counts once it reaches **5** lines (the line it leads into included). Label: "N lines bunched at m:ss".  
- **crushedLines**: a word-timed line with ≥ 3 words and ≥ 4 estimated syllables whose words span `< syllables × 0.06 s`. Label: "a line crammed at m:ss".

### 5.3 Lead (`lead(candidate, timing)`): **changed since 79b84db**

| lead | who |
| :---- | :---- |
| 0 | Apple Music cache, timing ≥ 1 |
| 1 | **Lyrics+ relaying Apple (`isAppleRelay`)**, timing ≥ 1 (new) |
| 2 | LRCLIB at rankedTiming 2 (word or mixed) |
| 3 | everyone else, including Lyrics+ from another upstream, LRCLIB at line timing, and any source at plain text |
| 4 | **LRCLIB with `agreement.timingOutlier`** (new; was 2 \= "other"). It now ranks *after* every other source at its timing, whatever the order |

At 79b84db the table was: 0 Apple, 1 LRCLIB word, 2 others, and an outlier became 2\.

### 5.4 `outranks(a, b)`: the total order (the first difference decides)

1. Timed (rankedTiming \> 0\) beats plain.  
2. **Sound timing beats a timing fault**. This sits above version and tier: a sound line-synced match beats crammed word timing, though faulty timing still beats plain.  
3. `fitsVersion` beats another version or language.  
4. Higher `rankedTiming` (2 \> 1).  
5. Lower `lead`.  
6. **Smart ("Accuracy first") only**: more `agreement.peers` (capped at **2**), then higher `confidence` \= `sim(title) + sim(artist) + 0.25 × max(0, 1 − |Δlen| / 10)` (the length bonus applies only when both lengths are known).  
7. The configured source order (`options.enabled` index; 999 if absent).

`score` is not used here. Priority ("Source order") mode skips step 6 only: the timing tier, leads, version and faults still apply in both modes.

### 5.5 `screen(candidates)`

1. Keep enabled sources only. `rejection`: rejected candidates go to `rejected` (sorted by source order).  
2. `LyricLanguage.weigh(accepted)` sets `languageMismatch`.  
3. `weighAgreement` runs on the accepted candidates that **fit the version** only.  
4. Sort by `outranks`. `choose` \= the top-ranked candidate.

### 5.6 Agreement (`LyricAgreement.swift`)

- `agreementText` \= normalised vocal line texts (plain lines for plain). `agreementTiming` \= (normalised text, start) per vocal line.  
- `sameWords(a, b)`: both have ≥ **4** lines, and the shared lines (multiset) ≥ **60%** of the longer.  
- `timingAgrees(a, b)`: pair equal texts in order, at least **4** pairs, median `|Δstart| <= 0.75 s`.  
- **`agreementFamily` (new since 79b84db)**: Apple cache and the Apple relay via Lyrics+ are both "apple". Every other source is its own family. Candidates of the same family are never peers.  
- `peers` \= the number of other-family candidates with the same words. `timingOutlier` \= at least **2** other-family timed candidates with the same words, the candidate agrees with **none** of them, and at least two of those agree with **each other while being of different families** (new condition).  
- Effect: only LRCLIB's lead uses `timingOutlier` (lead becomes 4). Peers break ties in Smart mode only.  
- **Late agreement (new)**: Publication refreshes the shown candidate from `latest`. LRCLIB word timing already on screen whose lead is taken by two later agreeing sources is replaced by the better match (at the next line boundary through reception). `couldDemote` keeps the sources that could still form that pair running. A pin is never swapped. If LRCLIB agrees with any timed candidate in hand, it keeps its lead whatever arrives.

### 5.7 Modes

- `Mode.smart` ("Accuracy first", default) and `Mode.priority` ("Source order"). The mode is part of the cache fingerprint.

---

## 6\. Cache, misses, pins

### 6.1 Automatic choice cache (`SavedLyrics`, `LyricSources.swift`)

- Folder: `~/Library/Application Support/Verso Custom/Lyrics/`. File `cache-<sha256(key)>.json`, where key \= `recordingKey + "|" + fingerprint`.  
- **Fingerprint** \= `"v14|<mode>|<enabled online sources in order, comma-joined>"` (`cachePolicy = "v14"`; at 79b84db it was **v13**). Local sources are excluded, so enabling Lyrimuse or the Apple cache does not invalidate. **Reordering or toggling online sources does.**  
- Entry: `result, storedAt, source, title, artist, duration`. Read only if **\< 14 days** old, file ≤ 2 MB, and the source is still an enabled online source. A cache hit asks **no** online source (status "Saved match" / "Not checked: a saved match was used"). Local sources still run and can outrank it.  
- Saved only when the lookup actually asked an online source, the best **online** candidate exists, and it `isSettled` against the online sources stopped early. On save, the key's misses are cleared. At most **150** `cache-*` files, the oldest by mtime deleted.  
- History: v7 Music and LRCLIB lead word timing; v8 Lyrics+ keeps unspaced syllables; v9 AMLL and Kugou KRC; v10 credits; v11 other versions rank lower; v13 language versions and original titles; **v14 phrase timing is line timing and the Apple relay ranks as Apple**.

### 6.2 Misses (`LyricMissCache`, in memory only)

- Key as above. **Per source**, lifetime **600 s** (10 min), each source on its own clock. A replay does not renew an older miss.  
- Recorded only when there was no acceptable online candidate, the lookup was not forced, and it was not offline. Only sources that **answered** (`answered = true`) are recorded; failures, timeouts and stopped sources are asked again next time. Rejected close matches are stored with their source and listed again on a replay ("No match a few minutes ago").  
- Offline detection: `URLError` notConnectedToInternet, networkConnectionLost, dataNotAllowed or internationalRoamingOff, or `LyricConnectivity` reporting offline at the end.

### 6.3 Pins

- `pin-<sha256(recordingKey)>.json` (**no fingerprint**, no expiry, never evicted). Written by `select(candidate)`, `importLyrics`, `importRomanization`. `forgetSelection` unpins and forces a lookup.

### 6.4 Other caches

`ArtistAliases.json` and `OriginalTitles.json`: 30 days, 2000 entries each, "none" cached, failures not cached.

---

## 7\. Without Apple Music's local cache (Windows)

- **No lead 0\.** The top source is the **Apple relay via Lyrics+ (lead 1\)**, which settles everything as soon as it arrives with word timing: nothing can beat lead 1 at tier 2, and `couldDemote` only targets LRCLIB. So Lyrics+ availability (mirrors, health, the second opinion) matters more on Windows. The Mac also has lyrics Music already downloaded, even when every Lyrics+ mirror is down.  
- No `musicFirst` 0.7 s hold and no 4 s second look: all sources start at once, and the first result can appear sooner.  
- `matters`' first rule (Apple word timing, so stop everything) never fires. The relay case stops the others through `couldBeat`.  
- **Language anchor lost.** `LyricLanguage.expected` step (b) (Music's lyrics decide the script) is missing. For an English-titled Japanese song, Windows depends on the original-title lookup (`viaOriginalTitle` decides native), the genre, or a native majority. If Windows does not pass the genre through (SMTC exposes genres (inferred)), ties between one English and one Japanese candidate are not broken.  
- `agreementFamily` still matters: never count the Apple relay as a peer of itself. Without the cache, the "apple" family is just Lyrics+'s relay.  
- Expect more cases where LRCLIB word timing (lead 2\) shows first and Lyrics+ arrives later to replace it at a line boundary.

---

## 8\. Everything in `Sources/Lyrics` changed since 79b84db

`git diff 79b84db..HEAD --stat -- Sources/Lyrics`: 8 files, \+220 / −39. Commits: 1b6fa9f, afc0981, 68b0b76, c578f4e, 9bbd1fe, 9b5bb7a.

1. **Phrase timing is line timing** (1b6fa9f, new `LyricPhraseTiming.swift`): `isPhraseTimed` in `validWordTiming` and `parseLyricsFile`; `hasWordTimingEvidence` (≥ 1 real line and ≥ 25%) in `timingTier` (tier 2), `hasWordTiming` (tier 3\) and `precision`. In the view model, phrase-timed lines are shown with estimated words.  
2. **The Apple relay ranks as Apple** (afc0981): `lead` 0/1/2/3, `isAppleRelay`, `lyricsPlusSuffix`, the source-list note "Apple Music’s lyrics", and the ranking description text.  
3. **Late agreement swaps LRCLIB** (68b0b76): outlier lead 4; `Publication.step(latest:)` refreshes the shown candidate; `couldDemote` added to `matters(among:)`.  
4. **Agreement family** (c578f4e): the Apple cache and the relay are one source in `weighAgreement` (both peers and the outlier pair).  
5. `cachePolicy` v13 → **v14**.  
6. Readings only (9bbd1fe, 9b5bb7a; `JapaneseAlignment`, `JapaneseReading`, `LyricRomanization`): sokuon words ("ikkai", not "ik kai") and phrase readings shared by character. These do not affect lookup.

`VersionTags`, `LyricLanguage` / `OriginalTitles`, `ArtistAliases`, `LyricAgreement`, timing faults and AMLL were already present at 79b84db.

---

## 9\. Hard cases (real songs from the tests and CHANGELOG)

| Song | What a naive port does | What the Mac does |
| :---- | :---- | :---- |
| **Racing into the Night — YOASOBI** (listed for 夜に駆ける) | Finds English lyrics, or nothing | If the listed title gives nothing usable (none, an "English Version" tag, or Latin-only for a J-Pop genre), it reads the JP title from iTunes and searches again as `夜に駆ける`, marked `viaOriginalTitle`. English-only lyrics get `languageMismatch` and rank below. `LanguageVersionTests` |
| **Idol (English Version) / アイドル (英語ver.) — YOASOBI** | Prefers Japanese lyrics | A language tag on the title makes the expected script Latin, so English lyrics fit and no original-title search runs |
| **Lemon — Kenshi Yonezu** | Nothing from the Chinese catalogues (they index 米津玄師); LRCLIB's phrase rows win as "word timing" | Aliases: NetEase/QQ/Kugou/Kuwo retry as 米津玄师 / 米津玄師. LRCLIB's one-token rows are line timing, so Kugou's KRC word timing wins (`testKugouWordTimingBeatsLRCLIBPhraseRows`, `testLemonOnLRCLIBIsLineSynced`) |
| **晴天 — 周杰倫** | LRCLIB with 1 real word line in 10 labelled word timing, beating Kugou | Evidence \< 25%, so line-synced; Kugou wins (`testOneRealLineInTenIsNotEnoughEvidence`) |
| **Hurts Me — Wallows** | LRCLIB word timing (11 words in 0.04 s) shoots to the end | A crushed line is a timing fault: ranks below any sound timing; LRCLIB's search uploads are also read |
| **Rather Be — Clean Bandit** | LRCLIB's 17 lines in 5 s race | bunchedRun ≥ 5 is a fault, ranked below sound timing; still used if nothing else is timed |
| An album's instrumental edition on LRCLIB (`testAnInstrumentalRecordingOnLRCLIBHasNoLyrics`) | Search finds the sung edition (same name and length) and shows its lyrics | Exact `instrumental:true` means nil, with no search |
| "Our Song" 3:00 vs a word-timed 3:06 edit (`testFormerNearMissesAreAccepted…`) | The word-timed longer edit wins | Rejected: \|Δ\| 6 \> max(3, 3.6) |
| "Our Song (2011 Remaster)" vs LRCLIB "Our Song \- Remastered 2011" | Title mismatch, nothing | similarity 0.95: accepted, and no version tag (remaster is not a version) |
| "Blinding Lights (Live)" — The Weeknd, or studio lyrics for a live track | The studio lyrics are shown as an equal match | The "live" tag mismatch means another version, ranked below any fitting timed match, but not rejected |
| Lyrics+ with no length, coda at 3:15 for a 3:00 song (`testServiceThatReportsNoRecording…`) | Accepted | Rejected: "lyrics run past the end of this recording" (\> duration \+ 3\) |
| Untagged artist (`"Our Song", artist " "`) | Matches anything, or rejects all | Artist not compared (score 0.5); a length is **required**, else "no artist or song length to compare"; LRCLIB skips `get` and searches by title only |
| "🦋" / "\!\!\!" | Normalises to empty and rejects | Compared as written: 1 |
| Lyrics+ answering for 米津玄師 when asked "Kenshi Yonezu" | Rejected on artist | `checkable`: a reported name in another script is replaced by the library's name; only length is checked (Lyrics+ has none) |
| LRCLIB word timing shown, then NetEase and QQ arrive agreeing on different timing (`LyricLateAgreementTests`) | Keeps LRCLIB | LRCLIB becomes an outlier (lead 4\) and the new best swaps in at the next line boundary. Not if Apple cache \+ Apple relay are the "pair" (one family) |
| Apple's lyrics via Lyrics+ vs LRCLIB word timing (`LyricAppleRelayTests`) | The configured order decides | The relay (lead 1\) beats LRCLIB (2) and others (3) in any order. Lyrics+ from Musixmatch/Spotify/QQ is lead 3 |
| Two equal Smart matches answering in different orders (`testTheSameMatchWinsWhicheverSourceAnswersFirst`) | Whichever answered first is cached | `couldBeat` keeps same-lead sources alive until confidence \= 2.25 (exact names and length), so the result is deterministic |
| Radio stream (duration 0\) | Junk matches from the station name | No automatic lookup |
| Kugou/Kuwo stamps near a whole minute (`<01:60.00>`) | A lost line or a stray stamp | `KugouKRC.clock` never writes 60 seconds (`testConvertedStampsNeverSaySixtySeconds`) |
| `[00:12.00] [00:45.00]Chorus` | The second stamp shown as text | Every leading stamp, spaces allowed, leads the row |

Fixtures shared with Windows (`Shared/LyricRules/Fixtures.json`, recordings): "Rain Again (feat. Clouds)" 180 matches; "Rain Again" at 183 and at 0 matches; at 186 it does not; "Other Band" does not; "On the Coast" does not.