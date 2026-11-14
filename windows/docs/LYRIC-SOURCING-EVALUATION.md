# Lyric sourcing evaluation (Windows, 15 Nov 2026)

Branch `fix/tester-0.9`. Method: `tools/LyricProbe` (automatic lookup, the app's real services, fresh cache each run, storefront `au`), 41 songs run one after another, then 18 follow-up probe runs (59 probe runs in all) and roughly 25 direct HTTP requests to diagnose failures. Nothing under `src/` or `tests/` was changed.

Caveats:
- The songs and durations are from memory, not from Apple Music. Durations I was unsure of: Hotel California live 400, Pretender 325, Plastic Love 292, Gurenge 237, 打上花火 289, Sandstorm 225, Around the World 429, Billie Jean 293, Experience 315. Two were wrong, which the services showed: 좋은 날 (I gave 228 s, sources say 3:55-3:56) and Hotel California live (I gave 400 s, sources say 6:31 to 7:00), so the live-version case is effectively untested.
- First-pass runs passed no `--genre`, although the app sends the player's genre and the language rules use it. The one re-run with a genre (Idol, "J-Pop") gave the same result.
- Services answer inconsistently from run to run (Pretender, Idol, Lyrics+ mirrors), so one run per song is a snapshot, not a rate.
- The raw outputs are in the session scratchpad (`lyric-eval\t01.txt` ... `t41.txt`, `rr_*.txt`, `r01-r07.txt`), which is temporary.
- "First lyrics" is the time the first result was shown in the probe's own clock, which starts about 0.7 s after launch.

## Summary

**37 of 41 songs ended with the right answer** (right song shown, or nothing for a song with no lyrics): 90%. A looser count that accepts Idol's English lyrics and 좋은 날's mis-paired Kuwo lines gives 39 of 41. **34 of 41 (83%) were clean**, meaning also the right language/version and no visible defect in the first lines. Failures: Idol (a language version), 좋은 날 on its own artist name (found only on Kuwo, with defects), and two instrumentals that got filler text. By category (right outcome / clean):

| Category | Right outcome | Clean | Notes |
|---|---|---|---|
| English pop/rock/hip-hop (16) | 16 / 16 | 16 / 16 | 15 word-by-word, 1 line (Hotel California live, inconclusive); Apple via Lyrics+ chosen for 6 |
| Japanese (8, 6 under English titles) | 7 / 8 | 6 / 8 | Idol got English lyrics (F6); 打上花火 starts with credit lines (F5) |
| Chinese (6, simplified and traditional) | 6 / 6 | 5 / 6 | 晴天 starts with credit lines (F5) |
| Korean (4) | 3 / 4 | 3 / 4 | 좋은 날 found only line-timed from Kuwo, with translation lines mis-paired (F7, F8) |
| Spanish/German (3) | 3 / 3 | 2 / 3 | Despacito has split accents (F5) |
| Instrumental / sparse (4) | 2 / 4 | 2 / 4 | Two instrumentals got filler lines (F4) |

Timing tiers of what was chosen in the first pass: word-by-word 34, mixed 1, line 4, none 2 (Time, correctly; 좋은 날 on the first run). Apple via Lyrics+ was chosen 9 times and was found but outranked once (Mr. Brightside, line timing only). Plain text was never chosen when a timed source existed. No wrong-song pick was seen; wrong-artist Lyrics+ hits for La Camisa Negra and Time were rejected correctly.

### Most important findings

1. **QQ Music returned HTTP 500 every time it was asked (F1):** 42 of 42 runs in which it was not stopped early, and a plain `curl` of the same search. It adds nothing and held 29 lookups open until about 5.8 s (lyrics were already showing by 1.7 s, except one at 3.2 s).
2. **Lyrics+, the only route to Apple word timing on Windows, was often unreachable (F3), and when a mirror answered it matched the raw title (F2).** Two of three mirrors were dead (a Cloudflare 404 page and a 502) and the third allows "2 requests per 10 seconds" while the app spends two or more per miss. "Mirrors unavailable" in 11 of 41 first-pass runs and 10 of 11 re-runs; of the 30 first-pass songs where a mirror did answer, Apple via Lyrics+ was accepted for 10. "Uptown Funk (feat. Bruno Mars)" got nothing; "Uptown Funk" got Apple's word-timed lyrics and a 0.71 s lookup instead of 5.83 s.
3. **Instrumental filler slips through (F4).** "该歌曲为纯音乐请欣赏" (Kuwo) and "DJ音乐，请欣赏" (Kugou) were shown as lyrics for Experience and Sandstorm.
4. **Native-script artist names lose sources (F7).** 좋은 날 by 아이유: only Kuwo found it. The same song by "IU": Kugou, NetEase, LRCLIB and Kuwo all match.
5. **An English-titled Japanese song with an English twin gets English lyrics (F6).** Idol by YOASOBI, as already noted in `LYRIC-MATCHING-NOTES.md`; a "J-Pop" genre did not change it.
6. **Text quality (F5, F8):** Kugou credit lines shown first (晴天, 打上花火) and accented letters split ("mirá ndote"); Kuwo's Chinese translation lines shown as lyric lines for 좋은 날.

## Song table

"Apple via Lyrics+" column: found = relay lyrics accepted; mirror errors = every Lyrics+ mirror failed; no match = mirrors answered "not found". Times are the probe's clock in seconds: first lyrics shown / lookup finished.

| 1 | EN | Heathens / twenty one pilots, 195.9 | Word-by-word - Apple via Lyrics+ | found | All my friends are heathen | - | 0.85s / 0.85s | OK |
| 2 | EN | Blinding Lights / The Weeknd, 200.0 | Word-by-word - Apple via Lyrics+ | found | I been tryna call | - | 0.90s / 0.90s | OK |
| 3 | EN | Shape of You / Ed Sheeran, 233.7 | Word-by-word - Apple via Lyrics+ | found | The club isn't the best pl | - | 1.35s / 1.35s | OK |
| 4 | EN | Bohemian Rhapsody (Remastered 2011) / Queen, 354.3 | Word-by-word - Kugou Music | no match | Is this the real life | QQ 500 | 1.67s / 5.87s | OK |
| 5 | EN | Uptown Funk (feat. Bruno Mars) / Mark Ronson, 269.2 | Word-by-word - Kugou Music | no match | This hit | QQ 500 | 1.64s / 5.83s | OK. Lyrics+ has Apple lyrics for the plain title, not for the "(feat.)" title (F2) |
| 6 | EN | bad guy / Billie Eilish, 194.1 | Word-by-word - Apple via Lyrics+ | found | white shirt | - | 1.07s / 1.07s | OK |
| 7 | EN | Lose Yourself / Eminem, 326.5 | Word-by-word - Kugou Music | no match | Look if you had one shot o | QQ 500 | 1.67s / 5.88s | OK |
| 8 | EN | rockstar (feat. 21 Savage) / Post Malone, 218.3 | Word-by-word - Apple via Lyrics+ | found | Tank God | - | 1.04s / 1.04s | OK |
| 9 | EN | Don't Start Now / Dua Lipa, 183.3 | Word-by-word - LRCLIB | no match | If you don't wanna see me | QQ 500 | 1.66s / 5.89s | OK |
| 10 | EN | Hotel California (Live) / Eagles, 400.0 | Line-synced - LRCLIB | no match | On a dark desert highway | QQ 500 | 1.74s / 5.93s | INCONCLUSIVE: right song, but sources say 6:31, 6:48 and 7:00 against my 400 s, so the live case is untested |
| 11 | EN | Billie Jean (2012 Remaster) / Michael Jackson, 293.0 | Word-by-word - Kugou Music | mirror errors | She was more like a beauty | QQ 500, Lyrics+ 404/busy/502 | 1.64s / 5.90s | OK. Lyrics+ answered the plain title in my curl test, not "(2012 Remaster)" (F2); also mirror errors (F3) |
| 12 | EN | Closer (feat. Halsey) / The Chainsmokers, 244.0 | Word-by-word - LRCLIB | no match | Hey, I was doing just fine | QQ 500 | 1.67s / 5.90s | OK. LRCLIB 4:08 vs 4:04 given; three other sources say 4:05 |
| 13 | EN | Old Town Road (Remix) [feat. Billy Ray Cyrus] / Lil Nas X, 157.0 | Word-by-word - LRCLIB | mirror errors | Yeah,I'm gonna take my hor | QQ 500, Lyrics+ 404/busy/502 | 1.49s / 5.83s | OK (Remix lyrics) |
| 14 | EN | Shallow / Lady Gaga & Bradley Cooper, 215.7 | Word-by-word - LRCLIB | no match | Tell me something, girl | - | 1.66s / 3.01s | OK |
| 15 | EN | Someone Like You / Adele, 285.0 | Word-by-word - Apple via Lyrics+ | found | I heard that you're settle | - | 1.26s / 1.26s | OK |
| 16 | EN | Mr. Brightside / The Killers, 222.6 | Word-by-word - Kugou Music | found | Coming out of my cage | QQ 500 | 1.67s / 5.86s | OK. Kugou word timing beat the Apple relay (line timing only), per the tier rule |
| 17 | JA | Lemon / Kenshi Yonezu, 255.0 | Word-by-word - NetEase | no match | 夢ならばどれほどよかったでしょう | QQ 500 | 1.62s / 5.87s | OK |
| 18 | JA | Yoru ni Kakeru / YOASOBI, 261.0 | Word-by-word - AMLL TTML DB | mirror errors | 沈むように溶けてゆくように | QQ 500, Lyrics+ 404/busy/502 | 1.63s / 5.82s | OK, found under original title |
| 19 | JA | Idol / YOASOBI, 213.2 | Word-by-word - LRCLIB | mirror errors | Couldn't beat her smile, i | Lyrics+ 404/busy/502 | 1.69s / 5.25s | FAIL (language version, F6): English lyrics, not the Japanese ones. The English and Japanese recordings have the same length; a re-run with genre "J-Pop" still gave Apple's English lyrics  |
| 20 | JA | Gurenge / LiSA, 237.0 | Word-by-word - NetEase | mirror errors | 強くなれる理由を知った | QQ 500, Lyrics+ 404/busy/502 | 1.63s / 5.79s | OK; LRCLIB's other-script lyrics were marked other language |
| 21 | JA | Pretender / Official HIGE DANDism, 325.0 | Line-synced - LRCLIB | mirror errors | 君とのラブストーリー | QQ 500, Lyrics+ 404/busy/502 | 1.62s / 5.79s | OK. First pass: line only (only LRCLIB had it). Re-run 13 s later: Kugou word-by-word chosen, NetEase and LRCLIB also matched (results vary between runs) |
| 22 | JA | Plastic Love / Mariya Takeuchi, 292.0 | Line-synced - LRCLIB | mirror errors | 突然のキスや 熱いまなざしで | QQ 500, Lyrics+ 404/busy/502, Kuwo timeout | 1.63s / 8.15s | OK (line only); Kuwo timed out |
| 23 | JA | 夜に駆ける / YOASOBI, 261.0 | Word-by-word - AMLL TTML DB | mirror errors | 沈むように溶けてゆくように | QQ 500, Lyrics+ 404/busy/502 | 1.64s / 5.87s | OK |
| 24 | JA | 打上花火 / DAOKO × 米津玄師, 289.0 | Word-by-word - Kugou Music | no match | Produce：米津玄師 | QQ 500 | 1.62s / 5.79s | OK, but first timed lines are credits ("Produce：…") (F5) |
| 25 | ZH | 晴天 / 周杰倫, 269.0 | Word-by-word - Kugou Music | found, rejected (past end) | 录音工程：杨瑞代（Alfa Studio） | QQ 500 | 1.64s / 5.83s | OK, but first lines are credits (录音工程…) (F5). Lyrics+ relay rejected as running past the end |
| 26 | ZH | 七里香 / 周杰伦, 299.0 | Word-by-word - Kugou Music | no match | 窗外的麻雀在电线杆上多嘴 | QQ 500 | 1.63s / 5.87s | OK |
| 27 | ZH | 告白氣球 / 周杰倫, 215.0 | Word-by-word - Kugou Music | no match | 塞纳河畔 左岸的咖啡 | QQ 500 | 1.63s / 5.79s | OK; lyrics in simplified script for a traditional-script listing |
| 28 | ZH | 光年之外 / G.E.M.邓紫棋, 235.0 | Word-by-word - Kugou Music | no match | 感受停在我发端的指尖 | QQ 500 | 1.69s / 5.97s | OK |
| 29 | ZH | 成都 / 赵雷, 328.0 | Word-by-word - Apple via Lyrics+ | found | 让我 掉下眼泪的 | - | 0.98s / 0.98s | OK |
| 30 | ZH | 稻香 / 周杰伦, 223.0 | Word-by-word - Kugou Music | no match | 对这个世界如果 | QQ 500 | 1.64s / 5.84s | OK |
| 31 | KO | Dynamite / BTS, 199.0 | Word-by-word - Apple via Lyrics+ | found | 'Cause, ah-ah, I'm in the  | - | 1.08s / 1.08s | OK |
| 32 | KO | How You Like That / BLACKPINK, 181.0 | Word-by-word - Apple via Lyrics+ | found | BLACKPINK in your area | - | 0.77s / 0.77s | OK |
| 33 | KO | 좋은 날 / 아이유, 228.0 | nothing | mirror errors | - | QQ 500, Lyrics+ 404/busy/502 | none / 6.05s | FIRST RUN FAILED because my 228 s was wrong (sources say 3:55-3:56). Re-run with 236 s: only Kuwo, line-timed, with Chinese translation lines shown as lyrics (F7, F8). With artist "IU" and 236 s Kugou word-by-word wins  |
| 34 | KO | Hype Boy / NewJeans, 179.0 | Mixed timing - Kugou Music | no match | 1 2 3 4 | QQ 500 | 1.63s / 5.79s | OK |
| 35 | ES | Despacito (feat. Daddy Yankee) / Luis Fonsi, 227.0 | Word-by-word - Kugou Music | mirror errors | Sí sabes que ya llevo un r | QQ 500, Lyrics+ 404/busy/502 | 1.63s / 5.86s | OK, but accents split ("mirá ndote") (F5) |
| 36 | ES | La Camisa Negra / Juanes, 216.0 | Word-by-word - LRCLIB | wrong song/artist, rejected | Uno por pobre y feo, hombr | QQ 500 | 1.04s / 5.86s | OK; a wrong-artist Lyrics+ hit was rejected |
| 37 | DE | 99 Luftballons / Nena, 232.0 | Word-by-word - LRCLIB | no match | Hast du etwas Zeit für mic | - | 1.64s / 2.91s | OK (German original) |
| 38 | INS | Experience / Ludovico Einaudi, 315.0 | Line-synced - Kuwo Music | mirror errors | 该歌曲为纯音乐请欣赏 | QQ 500, Lyrics+ 404/busy/502 | 3.16s / 5.90s | FAIL: Kuwo placeholder "纯音乐请欣赏" shown as a lyric (F4) |
| 39 | INS | Time / Hans Zimmer, 275.0 | nothing | wrong song/artist, rejected | - | QQ 500 | none / 7.23s | OK: nothing shown; a wrong-artist Lyrics+ hit was rejected |
| 40 | INS | Sandstorm / Darude, 225.0 | Word-by-word - Kugou Music | other version only (DJ mix) | DJ音乐，请欣赏 | QQ 500 | 1.63s / 5.85s | FAIL: Kugou placeholder "DJ音乐，请欣赏" shown as word-timed (F4) |
| 41 | INS | Around the World / Daft Punk, 429.0 | Word-by-word - Kugou Music | no match | Around the world  around t | QQ 500 | 1.63s / 5.78s | OK (sparse lyrics, 73 timed lines) |

## Findings in detail

Each finding says where it was seen and whether the Mac would have it too. "Mac too" is read from `docs/MAC-LYRIC-LOOKUP-SPEC.md`, not tested.

**F1. QQ Music returns HTTP 500 on every search (shared code path, Mac probably too).**
Seen in all 42 runs in which QQ was not stopped early (29 first-pass, 13 follow-up). A direct request to `https://c.y.qq.com/soso/fcgi-bin/client_search_cp?format=json&p=1&n=8&w=Lemon%20Kenshi%20Yonezu` with the Referer `https://y.qq.com/` also gave 500 after 5.6 s. The app sends the same URL and parameters the spec lists for the Mac, so the Mac likely fails too (guess, not tested). Effect: the lookup's "done" time is about 5.8 s whenever Lyrics+ does not end it early (30 of 41 runs over 5 s). Lyrics were on screen at 1.5-1.7 s in all but one run, so the visible cost is a long "searching" state and, I assume (not checked), a choice that cannot be saved as settled while QQ is pending.

**F2. Lyrics+ is sent the raw title and album, and answers "not found" for qualified titles (shared with the Mac by design, spec section 1.1).**
The clean probe evidence is Uptown Funk: "Uptown Funk (feat. Bruno Mars)" got no Lyrics+ match in the first pass (mirrors answered); the title "Uptown Funk" (same artist, album, length) gave Apple via Lyrics+ with word timing (113 lines) and the whole lookup finished in 0.71 s instead of 5.83 s. For Billie Jean the probe runs with "(2012 Remaster)" failed with mirror errors, so they do not show a title miss; "Billie Jean" with album "Thriller" gave Apple via Lyrics+ (mixed timing). Direct requests to `lyricsplus.binimum.org` show the title miss: the remaster title, and the plain title with the long album name, returned a JSON 404; the plain title without album returned 200 with `"source":"qApple"`. The existing album-less retry in `LyricsPlusSource.FetchAsync` does not help when the title itself carries a qualifier. Plain-title requests for Don't Start Now, Lose Yourself, Bohemian Rhapsody, Closer and Lemon also returned 404, so those songs are simply not on that mirror (this does not show a general gap).

**F3. Lyrics+ mirrors are mostly down or rate limited (the impact is larger on Windows, because Lyrics+ is the only Apple source here).**
What is solid, by direct request: `lyricsplus.prjktla.workers.dev` returns 404 with a non-JSON Cloudflare body (`error code: 1042`), `lyricsplus.prjktla.my.id` returns 502, and `lyricsplus.binimum.org` works but returns 429 "Rate limit exceeded ... (2 requests per 10 seconds allowed)" after as few as three requests. The app makes at least two requests per miss on a node (with album, then without), plus the original-title retry when it applies, and rests a 429 node for a minute (`BusyRest`). Observed: "Lyrics+ mirrors unavailable (HTTP 404, busy, HTTP 502)" in 11 of 41 first-pass runs and in 10 of 11 re-runs made 13 s apart (only Idol then answered). Caution: my first-pass runs were about 7 s apart with two or more Lyrics+ requests each, so some of those 429s were plausibly caused by my own requests; the re-runs used 13 s gaps and still failed, but I cannot tell whether the 429s then came from my requests or from other users of the same public service. Apple via Lyrics+ was accepted in 10 of the 30 first-pass runs where a mirror answered (9 chosen, 1 outranked); for the other 11 the answer is unknown, and with working mirrors the figure might be higher (guess).

**F4. Instrumental filler is shown as lyrics (the filler rule is the Mac's "exactly", so shared).**
Experience (Einaudi): only Kuwo answered, with the single line "该歌曲为纯音乐请欣赏", shown as line-synced lyrics. Sandstorm (Darude): Kugou's one line "DJ音乐，请欣赏" was chosen as word-by-word lyrics, above a Lyrics+ Apple lyric for "Sandstorm / Smells Like Teen Spirit (Mixed)" that ranked as another version. `LyricFiller.IsFiller` accepts only `^(此歌曲为没有填词的)?纯音乐请您?欣赏$` after stripping punctuation, so "该歌曲为..." (该, not 此) and "DJ音乐请欣赏" do not match. Time (Hans Zimmer) was handled right: nothing shown. Around the World (Daft Punk, mostly repeated words) got 73 word-timed lines, which looks right.

**F5. Kugou line quality.**
Credit lines were the first timed lines for 晴天 ("录音工程：杨瑞代（Alfa Studio）", then 混音工程) and 打上花火 ("Produce：米津玄師", "Executive Produce：...", "Sound Produce：...", "Drum：..."). The probe prints every line and cannot show whether the app flags them as credits (`LyricCredits.Flagging` runs in `LyricCandidateBuilder`), so whether the user sees them is not verified. Despacito from Kugou has accented letters split from their words ("mirá ndote", "Mué strame", "llamá ndome"); that text reached the probe output as is. The cause is a guess (the KRC word split in `KugouKrc.cs`).

**F6. Idol (YOASOBI) gets English lyrics (shared, already open in `LYRIC-MATCHING-NOTES.md`).**
LRCLIB, AMLL and NetEase all returned the English version for title "Idol" at 213.2 s, and a re-run answered with Apple's own English lyrics via Lyrics+. With the playing recording unknown (both recordings have the same length) nothing can tell the two apart. I count this as ambiguous, not wrong.

**F7. A Hangul artist name loses most sources (the alias rule skips artists already in CJK script: `ArtistAliases.cs` returns early when `IsOtherScript(query.Artist)`, as the Mac spec 1.3 does, so shared).**
좋은 날 / 아이유 / 228 s: nothing on the first run (Kuwo rejected for length 3:55 vs 3:48). My 228 s was a mistake: NetEase, LRCLIB and Kuwo all say 3:55-3:56. Re-run with 236 s and 아이유: only Kuwo matched (line-timed, see F8). The same song as "IU" with 236 s: Kugou word-by-word (3:55) wins, NetEase and LRCLIB (3:56) also match. With the Latin name three more sources had it than with the Hangul name.

**F8. Kuwo translation lines are shown as lyrics (observed once, 좋은 날).**
In the 236 s run the first shown lines were Korean "어쩜 이렇게 하늘은 더 파란건지", then Chinese "天空多么蔚蓝啊" as a lyric line carrying the next Korean line as its "translation", and so on. The Chinese line is a translation of the Korean line before it, so the pairing is shifted by one line and Chinese text is displayed as lyrics. The pairing happens where a second line with the same time stamp becomes `Translation` (`Lyrics/Parsing/LrcParser.cs`, about lines 190-196); the cause in Kuwo's data is a guess.

**Other observations**
- 17 of 41 runs first showed a result at about 1.6 s and replaced it at 2.7-4.2 s (mostly LRCLIB line-synced replaced by Kugou, NetEase or AMLL word-synced). In the app that swap waits for a line boundary.
- Closer (feat. Halsey): LRCLIB word timing (4:08) was chosen over three sources that agree on 4:05 for a 4:04 track. It is inside the length tolerance, so the rules allow it; whether the timing is off was not checked.
- Kuwo timed out ("No answer in time") once (Plastic Love, 8.15 s lookup). lyrics.ovh returned 404 for 좋은 날 and Time, as expected.
- Simplified/traditional: 告白氣球 (traditional listing) was matched on Kugou's simplified entry and shown in simplified script.
- Hotel California (Live): inconclusive. LRCLIB's entry is labelled `{live}` and runs 6:48; Kugou says 6:31 and Kuwo 7:00, against the 400 s I gave. The right song was shown, but the live-version case was not properly tested.
- Pretender changed between runs: first pass, only LRCLIB (line timing); 13 s later Kugou word-by-word, plus NetEase and LRCLIB. Treat single-run tiers as a snapshot.

## How to improve

Ordered by expected gain per effort. "Windows impact" means the cause is that Apple Music for Windows has no readable lyric cache; "shared" means the Mac's rules or code would behave the same.

1. **Retry Lyrics+ with a cleaned title (F2; shared rule, biggest gain for Windows).** In `Lyrics/Sources/LyricsPlusSource.cs` (`FetchAsync` / `AttemptAsync`), when a node answers "not found" and the title has a qualifier, ask once more with the title reduced by the same rule `LyricQuery.Core` uses (drop "(feat. ...)", "- Remastered ...", "(2012 Remaster)") and the album left out. Keep the ranking checks (`LyricRanking`, `LyricMatchScore`) as they are: they already compare the returned title and artist, so a wrong hit is still rejected. This departs from the Mac's "no clean-up before searching", so it should be agreed with the Mac, or limited to Lyrics+ where it is cheap. Verified by hand for two songs (Uptown Funk, Billie Jean); the retry costs another request, so see item 2.
2. **Treat Lyrics+ rate limits properly (F3; Windows impact).** In `LyricsPlusSource` read the wait from a 429 body ("Please wait N seconds") instead of resting the node a fixed `BusyRest` (1 minute), try the next mirror on 429 instead of only recording it, and cut requests per lookup (for example let the cleaned-title retry of item 1 replace the album-less retry). A non-JSON 404 (Cloudflare page) is already treated as the node being down. Mirror health lives in the source instance, so it survives between songs in the app but not between probe runs; a probe option that keeps one service across several songs would make this testable.
3. **Stop QQ Music from holding every lookup open (F1).** First find out whether the search endpoint or its parameters changed (`Lyrics/Sources/QQMusicSource.cs`, `QQMusicOptions.SearchAddress`). Whatever the cause, a source that returns 5xx repeatedly should rest for a while (the pattern `LyricsPlusSource` already uses for mirrors) and its 6 s `RequestTimeout` should not decide when the lookup is "done". Probably shared with the Mac (guess).
4. **Widen the instrumental filler rule (F4; shared, so change both apps).** `Lyrics/Parsing/LyricFiller.cs`: also accept "该歌曲为纯音乐请欣赏" and short notices of the form "<word>音乐，请欣赏" ("DJ音乐"). Also consider treating a candidate whose only content is one or two short lines as no lyrics (guess at a rule; test it against Around the World, which has few distinct words but 73 lines).
5. **Search a Latin artist name for Hangul, kana and Chinese artist names (F7; shared).** `Lyrics/Sources/MusicBrainzArtistSearch.cs` and `Lyrics/Ranking/ArtistAliases.cs`: today aliases are looked up only for non-CJK artists, so 아이유 never becomes "IU". Add the reverse direction (native name to its Latin primary name) for NetEase, Kugou, Kuwo and LRCLIB, which returned nothing for "아이유" but found the song for "IU". Check that the MusicBrainz match for short names like "IU" is not ambiguous (the spec's `pick` already refuses ambiguous answers).
6. **Check credit lines and accents from Kugou (F5).** Confirm in the app whether the 晴天 / 打上花火 credit lines are flagged, and if not add the missing labels to `Lyrics/Parsing/LyricCredits.cs` ("Produce", "Executive Produce", "Sound Produce", "Drum", "录音工程", "混音工程"). Look at `Lyrics/Parsing/KugouKrc.cs` for how words are cut around accented letters.
7. **Kuwo translation pairing (F8).** Check `Lyrics/Parsing/LrcParser.cs` (the same-time-stamp rule that sets `Translation`) against Kuwo's raw LRC for 좋은 날; shown Chinese lines should not become lyric lines. One observation only, so confirm with the raw data first.
8. **Recordings with the same title and length (F6; shared, open question already recorded).** No change proposed without the owner's decision. Idol still gave English lyrics with genre "J-Pop", and Apple's own relay agrees with the English recording for this title, so only the iTunes entries (see the notes) can tell the two apart.
9. **Hide the lookup's tail from the user (F1, other observations).** The first result arrives at about 1.6 s because of the grace period. If the product accepts it, do not show a "searching" state for sources that cannot change the result; this is a UI change, not a ranking one.

Not proposed: more sources. In 41 songs some source had every song that has lyrics; what failed was reachability (Lyrics+, QQ), text clean-up (filler, credits, accents) and query shape (qualified titles, native artist names).
