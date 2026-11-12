# Lyric matching notes (Windows-specific)

Facts about what Windows media sources report that the lyric core has to cope with. The ranking rules themselves
come from the Mac app; these are additions or checks on top of them.

## Apple Music: English titles on Japanese songs

Reported by the user on 2026-11-12. Apple Music usually lists a Japanese song that also has an English version
under its **English title**, even when the track playing is sung in Japanese. The recording that is actually in
English normally carries an "English version" (or similar) tag in its title.

**Ported from the Mac** (`next/localized-titles`, merged as 335e41f: commits 7e84884, 3c32c3d, 8a22aaf) on branch
`m4/localized-titles`. Automatic-cache policy **v15** (the Mac's v13; Windows' numbers were already past it).

- **Language versions are version tags.** "English Version", "English ver.", "Eng Ver.", "(English)", "英語ver.",
  "Japanese Ver.", "[Korean Version]", "(日本語ver.)"… give the tags `english`, `japanese`, `korean`, `chinese`
  (`VersionTags.Languages`). Lyrics for "Song (English Version)" rank below the playing "Song"'s, as Live and Remix do,
  and a title that says "English Version" prefers English lyrics.
- **Lyrics in another language rank as another version** (`LyricLanguage`, run by `LyricRanking.Screen`). Only CJK
  script is told apart from Latin (romaji is Latin; K-pop style mixes are never held against a song). The song's
  script comes from, in order: a language tag on the title or album; Apple Music's own lyrics (the real cache only); a
  native-script title or album; lyrics found under the original title; otherwise a majority of the other lyrics found
  (lyrics whose title names a language version don't vote), with an East Asian genre ("J-Pop", "Anime", "K-Pop"…)
  breaking a tie. A match in the other script gets `LanguageMismatch` and ranks with other versions; Apple Music's own
  lyrics and local files are never marked. The mark is worked out afresh at every screening.
- **Search under the original title** (`OriginalTitles`, `ItunesCatalogue`). When LRCLIB, NetEase, QQ, Kugou, Kuwo,
  Lyrics+ or AMLL find nothing usable under an English title (no match, a language version the title doesn't name, or
  Latin-only lyrics for an East Asian genre), the song is found in this PC's storefront on the iTunes Search API by
  primary artist + title (same title, version tags and artist, length within 3 s, nearest first), and the same entry is
  read from the `jp` storefront in Japanese. A different, native-script name there is searched under, around the
  artist-name search. A Japanese-script match found that way is recorded under the listed title and marked
  `ViaOriginalTitle`. Only the title, artist and storefront are sent; answers ("none" too) are kept 30 days in
  `OriginalTitles.json` beside `ArtistAliases.json`; failures are asked again; requests are a second apart and shared
  between sources.
- **Source list.** "Match found · lyrics in another language" (only when no version note applies) and
  "· found under the original title", after the version note and before " · Apple Music’s lyrics", the timing fault,
  agreement and " · showing" (the Mac's wording and order).
- **Setting.** The Mac shares "Search under other names" (`custom.artistAliases`, on) between artists' native names and
  songs' original titles. Windows: `AppSettings.ArtistAliases`, key `artistAliases`, default `true`; no settings window
  until milestone 5.
- **Genre.** The Mac reads the track's genre; Windows reads GSMTC's first `Genres` entry into `MediaTrackInfo.Genre`
  and `LyricQuery.Genre`.

**What the iTunes Search API actually says** (replies recorded 12 Nov 2026, in `Fixtures/LyricSources/itunes`):

- The US storefront lists YOASOBI's 夜に駆ける under its Japanese title, so the Mac test's "Racing into the Night"
  example is invented; the fallback is only needed in storefronts that romanise or translate.
- The Australian storefront does: "Yoru ni kakeru", "Haruka", "Idol"… Its "Idol" search returns **two singles of the
  same length (213.234 s)** both named "Idol": 1688334537 is the English recording (Japan also calls it "Idol") and
  1679278167 is アイドル. Neither carries an "English Version" tag. The Mac's rule takes the first of equally near
  entries, here the English one, so no original title is found and "none" is remembered. That is the safe outcome
  (guessing アイドル would mark English lyrics as wrong while the English recording plays), but it means the fallback
  can't help this song; the language vote (and the J-Pop genre) still can. **Open question for the owner**, and for
  the Mac, which behaves the same.
- Still needs a live check with real Apple Music for Windows playback: what GSMTC reports for title, album and genre.

Where Windows may differ from the Mac:

- **The Apple relay and the language.** Only Apple Music's own cache decides the song's language and is never marked.
  A Lyrics+ relay of Apple's lyrics, which ranks as Apple Music (decision 1 below), is judged like any service: it
  votes, and is marked when it is in the other script. (Apple Music's cache can't be read on Windows, so in practice no
  lyrics decide the language on their own here.)
- **Storefront.** The Mac uses the Mac's region; Windows uses the Windows region (`RegionInfo.CurrentRegion`), "us"
  when it names none. In Japan (`jp`) nothing is asked.
- **Primary artist.** The Mac's step comes from the Verso import, so Windows re-implements it from its behaviour: the
  credit up to the first "&" or comma, trimmed, or the whole credit when that leaves nothing.
- **Ported tests' word timing.** The Mac's fixture times each line as one token, which Windows' phrase-token rule
  (decision 3 below) ranks as line timing; the ported helper spreads the same 3 s over the line's words (per character
  in Japanese), so "word-timed" stays word-timed. Texts, names, times and expected results are the Mac's.

Where it lives (Windows): `VersionTags.Languages`, `LyricLanguage`, `LyricRanking.Screen`/`FitsVersion`/`Annotate`,
`OriginalTitles` (with `WithOriginalTitleAsync`), `ItunesCatalogue`, `LyricSourceSet`, `LyricQuery.Genre`,
`LyricCandidate.LanguageMismatch`/`ViaOriginalTitle`. Tests: `LanguageVersionTests`, `OriginalTitleTests`,
`LyricServiceEndToEndTests.AnEnglishTitledJapaneseSongIsFoundUnderItsOriginalTitle`,
`AppControllerTests.TheLookupCarriesThePlayersGenre`.

## Ranking decisions (12 Nov 2026)

These are the owner's decisions, made the same day on both apps. Windows matches the Mac's implementation (branch
`next/ranking-align`, as described by the Mac session). Both apps bump the automatic-cache policy to **v14**, so
choices cached under the old rules are looked up again.

### Leads within a timing tier

Ranking order is unchanged up to the lead: timed lyrics first, then sound timing, then a match for the playing
version, then the timing tier (word or mixed, then line, then plain). Within a tier, a lower lead ranks first:

| Lead | Who |
|---|---|
| 0 | Apple Music's own lyrics (the music app's cache), at line, word or mixed timing |
| 1 | Apple Music's lyrics relayed by Lyrics+ (provenance "Apple via Lyrics+"), at line, word or mixed timing |
| 2 | LRCLIB, with word or mixed timing only |
| 3 | Every other source (LRCLIB with line timing included) |
| 4 | LRCLIB when it is a timing outlier |

Within the same lead, Accuracy first then compares agreement (peers, capped at 2) and name and length closeness.
After that comes the configured source order, by the match's own source.

### 1. Apple Music's lyrics relayed by Lyrics+ rank as Apple Music

A Lyrics+ match whose node names its upstream "Apple" (case-insensitive) is a *relay*. Its ranking source is Apple
Music (`LyricCandidate.RankingSource`). It takes lead 1 at every timing tier, so:

- It beats LRCLIB's word timing.
- It ranks just after Apple Music's own lyrics.
- LRCLIB's word timing still beats a relay with line timing, because the timing tier comes first.

Other relays rank as Lyrics+, lead 3. "Musixmatch via Lyrics+" is one example.

- **Label.** The relay keeps the provenance source "Apple via Lyrics+", because per-song offsets are keyed by source
  name. Its line in the source list gains " · Apple Music’s lyrics", for example "Match found · Apple Music’s lyrics
  · showing". It is still listed, enabled or disabled, and cached as Lyrics+.
- **Early stopping.** A pending Lyrics+ might relay Apple Music's lyrics, so `CouldBeat` gives it its best case,
  lead 1. LRCLIB's word timing, or another source's, therefore waits for Lyrics+. Only the real Apple Music cache
  makes every service unnecessary ("Apple Music’s own lyrics are showing"). Once a relay of Apple Music's lyrics
  shows, only the Apple Music cache could still beat it.

### 2. A later agreement demotes LRCLIB showing

**The outlier rule.** LRCLIB's timing is an *outlier* when it disagrees with every other timed match for the same
words while two of those agree with each other. Matches count only for the playing version. "Same words" means at
least 60% of lines are shared over at least four lines. "Agree" means the median start difference is at most 0.75 s
over at least four paired lines. An outlier takes lead 4, after every other source at its timing tier, even when the
source order puts LRCLIB first. Before this change it only dropped level with the others. It keeps its timing tier,
so if the agreeing pair has only line timing, LRCLIB's word timing still wins.

**Re-ranking at every arrival.** At every arrival, the publication step (`LyricPublication.Step`) re-weighs agreement
over all the matches in hand. It judges the match showing as it ranks now, not as it ranked when it was shown. When
later answers make LRCLIB an outlier, the best of the others replaces it through the usual upgrade:

- The upgrade is held while a pending source could still beat it.
- On screen, the swap happens at a line boundary, never mid-line. The app waits for the line being sung to end, for
  at most 12 s.

Pinned choices never swap.

**couldDemote: which sources keep answering** (`LyricRanking.CouldDemote`). An automatic lookup normally stops a
pending source once its answer could no longer change what shows. A pending source keeps running instead, up to its
normal time limit, while all of these hold:

1. LRCLIB's word or mixed timing is the best match, and it is not an outlier.
2. LRCLIB agrees with no timed match for the same words that is already in hand.
3. The pending source can give word timing. Failing that, it has some timing and a sound word-timed rival from
   another source is already in hand.

The time limit is 8 s for services, or 20 s for LRCLIB, which is never the pending source here. Line-only sources
(Kuwo) are stopped as before unless such a rival is in hand. Plain-text sources (lyrics.ovh) are always stopped.

### 3. Phrase tokens are not word timing

`LyricTimingRules.IsPhraseTimed` checks whether a line's tokens are whole lines or phrases rather than words. In
scripts without spaces, that is one token over more than two letters, or more than four letters per token. In spaced
scripts, it is one token over several words, or fewer than half as many tokens as words.

- Such lines are not valid word timing when ranking.
- The LRCLIB Lyricsfile parser never marks such rows `WordTimed`. They keep the estimated words every line-timed line
  gets.
- Before display, `LyricPresentation.Prepare` re-estimates the words of any line still marked word-timed with phrase
  tokens, for example lyrics saved before the rule.

### Behaviour changes

- If LRCLIB's word timing answers first while Lyrics+ is pending, LRCLIB shows when the grace period ends (1.5 s),
  not at once.
- While LRCLIB's word timing shows, services that can give word timing keep running to their limit (8 s) unless one
  of them agrees with LRCLIB. Lyrics+, NetEase, QQ Music, Kugou and AMLL are among them. Before, they were stopped as
  soon as LRCLIB showed.
- An LRCLIB outlier now ranks below every other source at its tier.
- New: better lyrics found during a lookup now swap in at a line boundary. Before, Windows applied every new revision
  at once, with a short cross-fade.

### Where Windows may differ from the Mac

These points are not confirmed against the Mac's code:

- **Agreement and the relay.** Apple Music's own lyrics and their relay count as one source: neither is the other's
  peer, and their agreement with each other never makes LRCLIB an outlier.
- **Version and rival checks in couldDemote.** "In hand" covers only matches for the playing version, and the
  "sound word-timed rival" must be a match for the playing version too.
- **Line-only sources with a rival (open question).** Following condition 3, a line-only source such as Kuwo keeps
  answering when a sound word-timed rival is in hand. The Mac's summary says Kuwo is "still stopped", which
  presumably means when there is no rival.
- **Mixed timing.** couldDemote treats LRCLIB's mixed timing like its word timing, since both rank in the same tier.
- **Waiting for Lyrics+.** A pending Lyrics+ is waited for as a possible Apple relay.
- **Saved choices.** "Never swap" applies to pinned choices, which lock the lookup. A cached automatic choice can
  still be upgraded by a local source that outranks it, as before.
- **Label position.** The note comes after the version, language and original-title notes, before the timing fault,
  agreement and "· showing" (the Mac's order).
- **Apostrophe.** The label uses a typographic apostrophe ("Apple Music’s lyrics"), like the app's other source-list
  strings.

### Where it lives (Windows)

- `LyricSourceRanking` and `LyricCandidate.RankingSource`.
- `LyricRanking`: `Lead`, `Outranks`, `CouldBeat`, `CouldDemote` and `Annotate`.
- `LyricAgreementRules.Weigh`.
- `LyricPublication.Step`.
- `LyricLookup`.
- `LyricTimingRules.IsPhraseTimed`, `LyricsfileParser` and `LyricPresentation`.
- `LyricSwap` and `AppController.SwapAtLineBoundaryAsync`, the line-boundary swap ported from the Mac's
  `LyricsViewModel`.
- Tests: `RankingDecisionsTests`, `LyricServiceEndToEndTests`, `LyricSwapTests`,
  `LyricsfileTests.PhraseTokensAreNotWordTiming`, `LyricPresentationTests` and
  `AppControllerTests.ABetterMatchWaitsForTheLineBeingSungToEnd`.

## Parity with the Mac spec, 13 Nov 2026

Checked section by section against `docs/MAC-LYRIC-LOOKUP-SPEC.md` (the Mac's `next/integration` at 7544de6), on
branch `m5/lyric-parity`. Where the read-only checkout (79b84db) lacks something the spec describes, the spec was
followed.

**The same on both apps** (read in the Windows code, and covered by tests): the raw query and recording key (§1.1);
each source's fields (§1.2) apart from rows 3 and 15 below; artist aliases (§1.3); original titles (§1.4); every
source starting at once with its own limit, LRCLIB 20 s and the rest 8 s (§2.1); LRCLIB's exact match, instrumental
stop, sound-timing check, search of 30 rows and its −12/−6 penalties (§2.2); the Lyrics+ mirrors, rests, second
opinion, gzip, syllable rules and `checkable` names (§2.3); NetEase's `bestSong` (§2.4); QQ 3, Kugou 2×2 with KRC
first, and Kuwo 2 hits tried, recording-check hits first (§2.5); lyrics.ovh, AMLL and Lyrimuse (§2.6–2.8); grace,
settle, the publication steps with the shown match judged afresh, `couldBeat`, `matters` and `couldDemote` (§3.1–3.4,
apart from rows 2 and 5); reception and the line-boundary swap (§3.5, apart from row 17); similarity and `core` (§4.1);
the recording check (§4.2); version tags, the language rules and instrumental marks (§4.3–4.5); timing tiers with the
evidence rule, timing faults, leads 0–4, `outranks`, `screen` and agreement (§5); cache, misses and pins (§6).

| # | Spec (§) | Mac | Windows (before this check) | Verdict |
|---|---|---|---|---|
| 1 | §2.3, §5.3 Apple relay | `isAppleRelay`: upstream "apple" or "apple music", any case | "Apple" or "qApple" only, so "Apple Music via Lyrics+" ranked as plain Lyrics+ (lead 3) | **Fixed.** "Apple Music" is a relay; its label stays as written, as on the Mac. "qApple" stays (a Windows addition, owner decision). Cache v17. |
| 2 | §3.4 `couldDemote` | only for LRCLIB timing that fits the playing version | no version check | **Fixed.** Nothing shows differently: `CouldBeat` already keeps every timed source for another version's lyrics. |
| 3 | §1.2, §2.4 NetEase lyric request | `lv=1&kv=1&tv=-1&yv=1&rv=-1` | all five `-1` | **Fixed.** The Mac's parameters. Live: NetEase still answers with YRC word timing (Hurts Me) and line LRC (Lemon); `romalrc` was not checked live. |
| 4 | §5.1 word-timing evidence | `hasWordTimingEvidence` on the Mac | the same rule, but its comment said "Windows-only" | **Fixed** (comment). |
| 5 | §3.3 step 5 `couldBeat` | compares `lead(source)` by source alone, so a pending Lyrics+ is lead 3 (inferred: the spec gives lead 1 only to a relay in hand) | a pending Lyrics+ gets its best case, lead 1, since it might relay Apple | **Kept: owner decision 1** (Apple relay; "Waiting for Lyrics+" above). When LRCLIB's word or mixed timing answers first, Windows shows it when the grace period ends (1.5 s) unless Lyrics+ has answered; the Mac shows it at once and swaps if a relay comes. Both end on the same match. Live: Blinding Lights (Live), Racing into the Night and Lemon showed LRCLIB at 1.65 s. **Question for the Mac.** |
| 6 | §5.6 peers | the number of other-family *candidates* with the same words | the number of other *families* (Apple Music and its relay agreeing count once) | **Kept: owner decision** (the relay's family). No effect on Windows, which has no Apple Music cache. |
| 7 | §5.1 `isPhraseTimed` | `letters/tokens > 4` (integer or real division not stated) | more than 4 letters per token on average (real division) | **Kept: owner decision 3** (phrase-token rule). Can differ only between 4 and 5 letters per token. |
| 8 | §5.6 late demotion | outlier lead 4, `couldDemote`, refresh from `latest` | the same | **Kept: owner decision 2.** The open question above about line-only sources with a rival stands. |
| 9 | §6.1 cache policy | v14 | v16 | **Kept: Windows numbering**, now **v17** (row 1). |
| 10 | §2.2, §2.5, §2.8 hit score | `vc_match_score`: cut-offs title 0.62, artist 0.45, length 25 s; 48 / 27 / max(−22, 14 − 2Δ) / word 20, line 7 / album 4 | `LyricMatchScore`: 0.6, 0.5, 30 s; 50 / 25 / max(−25, 10 − 1.5Δ) / 20, 8 / 5 | **Kept: provenance rule.** `vc_match_score` is entirely Verso (blame: a99fd65), so it is not translated. It only orders one source's own hits, and every hit between the two sets of cut-offs fails the recording check anyway, so at most Windows lists a rejected near-miss that the Mac drops. **For the owner:** adopting the spec's constants would amount to translating Verso's function. |
| 11 | §2.6 lyrics.ovh 404 | an error (`LyricHTTP.json` throws), so not remembered as a miss | lyrics.ovh's own "No lyrics found": a miss for 10 minutes | **Kept, for the owner to decide.** What is found doesn't change (the 404 is the service's answer), only whether it is asked again within 10 minutes, and the state text. Suggest the Mac do the same. |
| 12 | (HTTP) | no spacing between requests to one host | 150 ms between requests to one host | **Kept** (politeness). Timing only: NetEase and AMLL share music.163.com. |
| 13 | §4.5, §5.1 tier 0 | a synced result whose vocal lines are all empty is a tier-0 candidate; such an LRCLIB exact match also stops the search | dropped ("credits alone are not lyrics"); LRCLIB then searches | **Kept, edge case, for the owner.** Only an LRC of nothing but ♪ marks; Windows shows "no lyrics" where the Mac shows an empty synced result. |
| 14 | §2.1, §2.9, §3.4, §4.4 (b), §5.3, §7 Apple Music cache | lead 0, music first (0.7 s), second look (4 s), "Apple Music's own lyrics are showing", Music's lyrics set the language | ported and tested with fakes; the source is unavailable on Windows | **Can't apply:** Apple Music for Windows keeps no lyric cache Lunalyr can read (`APPLE-MUSIC-WINDOWS.md`). As §7 says, every source starts at once and the relay is the top lead. |
| 15 | §1.3, §2.2, §2.3 User-Agents | three Mac agents (LRCLIB's names the Mac's repo) | one generic agent | **Can't apply:** the development rules ask for a generic agent. No service is known to answer differently. |
| 16 | §1.4 storefront | `Locale.current.region` | `RegionInfo.CurrentRegion` | Platform equivalent (above). |
| 17 | §3.5 offline | reruns 2 s after the connection returns; if already online, reruns at once, once per track | `AppController` reruns at once when the connection returns, and not at all when the network looked up | **Not fixed here:** app layer (`Lunalyr.Shell/AppController`), outside this branch. Recommended follow-up. |
| 18 | §3.5 rerun reception | a non-forced rerun while lyrics show ignores updates until complete | `LyricSwap.Reception` has the rule; the app never reruns (`rerun: false`) | The same rule; nothing to change until the app reruns (row 17). |

Mac tests not ported:

- `NextVersionTests.testLRCLIBWordTimingLeadsOtherServicesInEitherModeAndOrder`: its one-word line is line timing under
  §5.1, so the checkout's expectation is out of date. `RankingDecisionsTests` and `LyricPriorityTests` cover LRCLIB's
  lead with real word timing.
- `LyricParsingTests.testRecordingKeyIncludesAlbumAndDuration` and `testNonFiniteTrackDurationCannotCrashTheCacheKey`:
  their lines trace to the Verso import (a99fd65). `MacSpecParityTests` checks the same behaviour from the spec, with
  this port's inputs. `testTrackChangesRecogniseDifferentEdit` is about the Mac's now-playing track type, not the lookup.
- `AMLLAndKRCTests.testARealAMLLFileParsesToWordTimedLines`: needs a downloaded file named by an environment variable
  (skipped on the Mac too).

New tests: `MacSpecParityTests` (from the spec: `testKugouWordTimingBeatsLRCLIBPhraseRows`,
`testLemonOnLRCLIBIsLineSynced`, `testOneRealLineInTenIsNotEnoughEvidence`, the relay names, the relay leading LRCLIB
in any order, `couldDemote`'s version check, Our Song 180/183/186 and the recording key; ported:
`testAMLLIsAListedSourceAndOnByDefault`) and `NetEaseSourceTests` (the request parameters).

### Live check

`tools/LyricProbe`: automatic lookups, every source in the recommended order, Accuracy first, storefront `au` (this
PC's region), the generic User-Agent, a fresh temp cache per run. Lengths, albums and genres are from the iTunes Search
API (US storefront; 晴天 from the TW one). Outputs: `docs/measurements/lyric-parity/before-*.txt` (v16) and
`after-*.txt` (v17). Live answers vary with mirror health, so one run per song per pass is a sample, not proof.

| Song (length) | Before | After | The Mac (from the spec) |
|---|---|---|---|
| Racing into the Night — YOASOBI (261.013 s, J-Pop) | LRCLIB mixed timing ("Racing Into The Night", English lyrics), at 1.65 s | the same | The same for the same storefront. No match was found under an original title: as noted above, the `au` storefront lists the song as "Yoru ni kakeru", which isn't the listed title, so the §9 retry has nothing to search under (the Mac test's example is invented). |
| Idol (English Version) — YOASOBI (213.234 s, J-Pop) | Apple via Lyrics+, mixed timing; NetEase, AMLL and Kuwo's "Idol" marked other version (English) | the same; Lyrics+ answered first (0.59 s) and every other source was stopped | The same: the language tag expects Latin script, English lyrics fit, and there is no original-title search. |
| Lemon — Kenshi Yonezu (256.116 s, J-Pop) | LRCLIB line-synced at 1.65 s, NetEase line-synced at 2.99 s, Kugou word-by-word at 5.01 s (final) | first run: LRCLIB line-synced only (no catalogue found it; the MusicBrainz alias request evidently failed, and a failure isn't cached); rerun (`after-rerun-lemon.txt`): Kugou word-by-word at 3.05 s (final) | Kugou's KRC wins, and LRCLIB's phrase rows are line timing. Matches. |
| 晴天 — 周杰倫 (269.747 s, Mandopop) | QQ Music via Lyrics+ word-by-word at 1.65 s; LRCLIB line-synced second | the same | Spec §9 expects Kugou over LRCLIB. Here every Chinese catalogue found nothing: they list the artist as 周杰伦 (simplified), 周杰倫 vs 周杰伦 has similarity 0, and aliases skip an artist already in CJK script. **The Mac does the same** (same rules). |
| Hurts Me — Wallows (178.640 s) | NetEase word-by-word; LRCLIB "2 lines crammed from 1:39" second | the same | A crushed line is a timing fault and ranks below sound timing. Matches. |
| Rather Be (feat. Jess Glynne) — Clean Bandit (227.833 s) | Apple via Lyrics+ mixed timing, every other source stopped; LRCLIB's upload has no bunched run now | the same | The relay settles everything (§7). Matches. |
| Blinding Lights (Live) — The Weeknd (253.365 s) | LRCLIB word-by-word "Blinding Lights - Live" at 1.66 s | the same (Kugou word-by-word "(Live)" second) | A Live match fits the live recording. Matches, except that the Mac would show LRCLIB at once rather than at 1.65 s (row 5). |

No song chose differently after the fixes, which touch cases these songs don't hit (a node naming "Apple Music",
LRCLIB for another version). **Worth changing on both apps:** an artist written in traditional Chinese finds nothing in
catalogues that list it in simplified script (晴天 above). Folding traditional and simplified Han for name matching, or
letting the alias search run for CJK artists, would fix it.
