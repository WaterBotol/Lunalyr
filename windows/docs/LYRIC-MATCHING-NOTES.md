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

Two decisions by the owner, made the same day on both apps. The Mac aligns with the rules exactly as written here.
They bump the automatic-cache policy to **v13**, so choices cached under the old rules are looked up again.

### 1. Apple Music's lyrics relayed by Lyrics+ rank as Apple Music

**Rule.** A Lyrics+ match whose node names its upstream "Apple" ranks exactly as Apple Music's own lyrics would.
The provenance label is "Apple via Lyrics+", and the upstream match is case-insensitive. Its *ranking source* is
Apple Music. Every other match, including other Lyrics+ relays such as "Musixmatch via Lyrics+", ranks as its own
source. The ranking source is used wherever rank is decided:

- **Leads at each timing tier.** Word or mixed timing: Apple Music, then LRCLIB, then the others. Line timing: Apple
  Music, then the others. The relay therefore beats LRCLIB's word timing. Timing still comes first: LRCLIB's word
  timing beats the relay's line timing.
- **Apple Music's own lyrics and the relay.** They tie on everything above and rank deterministically, with the real
  Apple Music cache first. This tie-break comes right after the leads, before agreement, name closeness and the
  configured order.
- **Configured order.** The final tie-break uses the ranking source's position, so the relay takes Apple Music's
  place.
- **Agreement.** Apple Music and its relay are one source. Neither counts as the other's peer. Their agreement
  with each other never counts as "two other sources agree", and peers are counted per ranking source.
- **Early stopping.** A pending Lyrics+ might relay Apple, so it counts as Apple Music when deciding whether it could
  still beat what shows (`CouldBeat`/`IsSettled`). It is waited for whenever Apple Music could beat the match in
  hand, for example LRCLIB's or another source's word timing. It is not waited for when Apple Music's own word or
  mixed timing shows, or when its relay of Apple already shows.

**Unchanged.** The match is still listed, labelled, enabled or disabled, and cached as Lyrics+. The source list
says "Apple via Lyrics+". Only the real Apple Music cache makes every service unnecessary ("Apple Music's own lyrics
are showing").

**Behaviour change.** When LRCLIB's word timing answers first and Lyrics+ is still pending, LRCLIB now shows when the
grace period ends (1.5 s), not at once. Lyrics+ then runs to its own limit (8 s) unless something it cannot beat
shows.

### 2. A later agreement demotes LRCLIB already showing

**Rule.** LRCLIB loses its lead when two other sources agree against its timing. Its timing must disagree with
every other timed source with the same words, while two of those, with different ranking sources, agree with each
other. This now holds whenever the agreement forms, including after LRCLIB is shown. The publication step judges the
match showing as it ranks now among every accepted answer, agreement included, not as it ranked when it was shown.
When later answers make LRCLIB the odd one out, the best of the others replaces it through the usual upgrade path:

- It is held while a pending source could still beat it, for up to one settle period.
- On screen it swaps in at a line boundary, never mid-line. The app waits until the line being sung ends, for at most
  12 s.

A lookup still ends on the top of the final ranking.

**Stopping (the agreement window).** An automatic lookup stops sources that could no longer change what shows. While
LRCLIB's word or mixed timing leads, a pending *timed* source is not stopped just because it cannot beat LRCLIB
itself. It keeps running until the grace period is over and one upgrade-settle period (0.75 s) has passed since
LRCLIB was shown, and is then stopped as before. The window applies only while an agreement against LRCLIB is still
possible:

- No other source with the same words agrees with LRCLIB's timing. One that agrees means LRCLIB can never be the odd
  one out.
- The timed sources with the same words that have answered, plus the timed sources still pending, number at least
  two.

Untimed sources (lyrics.ovh) stop as before. A search asks every source anyway.

**Behaviour change.** While LRCLIB leads, up to all the timed services keep running for at most 0.75 s after LRCLIB
shows. With the defaults, LRCLIB shows at the end of the grace period, so they stop about 2.25 s into the lookup
instead of about 1.5 s.

**Where it lives (Windows).**
- `LyricCandidate.RankingSource` and `LyricSourceRanking`.
- `LyricRanking.Outranks`, `CouldBeat` and `LeadCouldBeLostToAgreement`.
- `LyricAgreementRules.Weigh`.
- `LyricPublication.Step`, with the accepted set.
- `LyricLookup`, the agreement window.
- `LyricSwap` and `AppController.SwapAtLineBoundaryAsync`, the swap at a line boundary (ported from the Mac's
  `LyricsViewModel`).
- Tests: `RankingDecisionsTests`, `LyricServiceEndToEndTests` and `LyricSwapTests`, plus
  `AppControllerTests.ABetterMatchWaitsForTheLineBeingSungToEnd`.
