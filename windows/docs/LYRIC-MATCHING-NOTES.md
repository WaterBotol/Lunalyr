# Lyric matching notes (Windows-specific)

Facts about what Windows media sources report that the lyric core has to cope with. The ranking rules themselves
come from the Mac app; these are additions or checks on top of them.

## Apple Music: English titles on Japanese songs

Reported by the user on 2026-11-12. Apple Music usually lists a Japanese song that also has an English version
under its **English title**, even when the track playing is sung in Japanese. The recording that is actually in
English normally carries an "English version" (or similar) tag in its title.

What the lyric core should do (to confirm against the Mac app's version-tag and artist-alias logic in milestone 2):
- No "English ver." / "English version" tag on an Apple Music title → don't assume English lyrics. Also search
  by the song's Japanese title (from aliases, the album, or the provider's own cross-references) and prefer
  Japanese-language results whose duration matches.
- An explicit "English version" tag → English lyrics are correct; treat the tag as a version tag so the Japanese
  original ranks below it, just as Live and Remix rank below the playing version.
- Needs a live check with real Apple Music for Windows playback (what GSMTC reports for title, artist and album).
- The Mac app has an agent working on exactly this on branch `next/localized-titles` (per the 11 Nov context file).
  Port that logic once it's in a refreshed bundle, rather than inventing a separate rule here.

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
- **Label position.** The note comes straight after "Match found", before other notes and "· showing".
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
