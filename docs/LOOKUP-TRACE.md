# Lyric lookup trace

A diagnostic run of Lunalyr's real lyric lookup over a list of songs. For each
song it records what was asked, of whom, what came back and why each answer
was kept or dropped, so the lookup can be compared song by song with another
implementation (for example the Windows port).

The app never traces. `LyricsFetcher` takes a `LyricLookupTracer` only when a
test injects one; without it every hook returns at once and no request,
candidate or string is built for the trace.

## Running it

Write the songs as JSON. `album`, `player` and `genre` are optional;
`duration` is in seconds, as a player reports it.

```json
[
  {"title": "Lemon", "artist": "米津玄師", "album": "STRAY SHEEP", "duration": 255},
  {"title": "Racing into the Night", "artist": "YOASOBI", "duration": 261, "player": "Music", "genre": "J-Pop"}
]
```

Then, from the repository root:

```sh
xcodegen generate --spec project.yml
TEST_RUNNER_LUNALYR_LOOKUP_TRACE=/path/to/songs.json \
TEST_RUNNER_LUNALYR_LOOKUP_TRACE_OUT=/path/to/out \
xcodebuild test -project VersoCustom.xcodeproj -scheme VersoCustom \
  -destination 'platform=macOS' -derivedDataPath .dd \
  -only-testing:VersoCustomTests/LyricLookupTraceTests
```

xcodebuild passes `TEST_RUNNER_…` variables to the tests without the prefix.
Without `TEST_RUNNER_LUNALYR_LOOKUP_TRACE` the test is skipped, so the normal
suite stays offline.

| Variable (after `TEST_RUNNER_`) | Meaning |
|---|---|
| `LUNALYR_LOOKUP_TRACE` | The songs file. Required. |
| `LUNALYR_LOOKUP_TRACE_OUT` | Output folder. Default: the songs file's folder. |
| `LUNALYR_LOOKUP_TRACE_ALL_SOURCES=1` | Run each song as a search: every source is asked and none is stopped early. The default is the app's automatic lookup, which stops sources once they can no longer change the pick. |
| `LUNALYR_LOOKUP_TRACE_SKIP=lyricsPlus,…` | Leave these sources out (by id), for example to see what shows when Lyrics+ is slow or down. |
| `LUNALYR_LOOKUP_TRACE_STOREFRONT=xx` | The iTunes storefront the original-title lookup starts from. Default: this Mac's region. |

Each run uses the real network and the app's default sources in its
recommended order, in Accuracy first mode: Lyrics+, AMLL, NetEase, LRCLIB, QQ
Music, Kugou, Kuwo, lyrics.ovh. Music's own cache and local files are left out,
as they are by default. The cache, the miss cache, artist aliases and original
titles all start empty in a throwaway folder, and nothing is pinned. The app's
own cache and settings are never read or written.

The songs run one after another, about one to three seconds each.

## Output

- `summary.md`: one row per song. Each source's cell shows its outcome, then
  the timing of what it offered. `**PICK**` is what would be shown, `#n` an
  accepted match at rank n, `rej …` a match that failed the recording check
  (with the reason), `none` no match, `stopped` not needed (the automatic
  lookup stopped it), `timeout`, or `error: …`. Timing is `word`, `mixed`,
  `line`, `phrase-timed` (a whole line or long phrases timed as words),
  `plain`, or `faulty` (lines stamped faster than they could be sung).
- `NN-title-artist.json`: the full trace for one song.

## Reading a song's JSON

Top level:

| Field | Meaning |
|---|---|
| `song` | The song as given. |
| `mode`, `sources` | The lookup mode and the sources, in order. |
| `storefront` | The iTunes storefront used for original titles. |
| `elapsedMs` | How long the whole lookup took. |
| `pick` | What would be shown: source, provenance, timing, the names the source reported, and whether it was found under the original title. Missing when nothing was found. |
| `digest` | One entry per source: its final `state` (as the source list in Settings would show it), a short `verdict`, `elapsedMs`, the query `variants` it searched, each `request` (status or error, time, address) and its `candidate`, if any. |
| `events` | Everything, in order. |

Each event has a `kind`, `at` (seconds since the lookup began), and the
`source` and `variant` it happened under (`variant` is missing for the query
as received). Kinds:

| Kind | What it records |
|---|---|
| `lookup` | The `query` as received, the mode and the `sources`. |
| `cache` | A pinned or saved choice used, sources skipped by the miss cache, and at the end whether the choice was saved. |
| `sourceStart`, `sourceEnd` | A source began; it ended with this state (`message`), after `elapsedMs`, with this `candidate`. "Cancelled" means the lookup stopped it. |
| `variant` | A query variant about to be searched, with its `query`: the listed title, `original title “…”` (from the iTunes JP storefront) or `artist alias “…”` (from MusicBrainz). They nest: `original title “夜に駆ける” + artist alias “…”`. |
| `variantResult` | What that variant gave (`candidate`) and what happened to it (`message`): kept, used, discarded and why, or why no further variant was tried. |
| `request` | One HTTP request: `url` (keys and tokens replaced by `REDACTED`), `status`, `bytes`, `ms`, the `timeout` it allowed, or an `error` (`timeout`, `cancelled`, …). |
| `results` | A search's size: how many results came back (`returned`) and how many were read further (`read`). |
| `hit` | One search result a source weighed before fetching lyrics: names, duration, score and similarities, and a `verdict` (read, not read, fails the recording check, title < 0.75, …). |
| `candidate` | A source's best offer, before ranking. |
| `publication` | Each time the lookup weighed what to show: the `step` (`wait`, `holdForUpgrade`, `publish`), the `best` match so far and its timing, whether it was `settled`, whether the grace and settle periods were over, what was shown before and after, the `revision` and the sources still `pending`. A `publish` with `shownBefore` set is a replacement. |
| `stop` | Sources stopped because their answer could no longer change the pick, and why. |
| `final` | The `ranked` list (accepted, best first, each with `rank`), the `rejected` list with reasons, the `states` of every source, and the pick (`source`). |
| `note` | Anything else worth knowing: a mirror resting, an exact LRCLIB match making the search unnecessary, an alias lookup's answer. |

A `candidate` (in `candidate`, `ranked` and `rejected`) carries:

| Field | Meaning |
|---|---|
| `title`, `artist`, `duration` | As the source returned them. |
| `titleSimilarity`, `artistSimilarity` | 0…1 against the query (`LyricQuery.similarity`). |
| `durationDelta` | Its length minus the query's, in seconds. |
| `confidence` | Name closeness plus length fit, the Accuracy-first tie-break. |
| `timing`, `timingFault`, `rankedTiming` | The timing tier, what is wrong with it, and how ranking counts it (2 word or mixed, 1 line, 0 plain). |
| `versionTags`, `versionMismatch` | Version qualifiers in its title (live, remix, english…) and those that differ from the playing song's. |
| `script`, `languageMismatch` | The script of its lyrics (latin, native, mixed) and whether that makes it another language's version. |
| `lead` | Its place ahead of the source order at its timing: 0 Music's own, 1 Apple's lyrics via Lyrics+, 2 LRCLIB word timing, 3 any other, 4 LRCLIB whose timing the others disagree with. |
| `agreement` | Other sources with the same words (`peers`), how many timed ones were compared, and whether its timing is the odd one out. |
| `verdict`, `reason` | accepted, or rejected with the reason the source list shows. |
| `viaOriginalTitle`, `appleRelay`, `provenance`, `lines`, `firstLine` | How it was found, whether Lyrics+ relayed Apple's lyrics, its label, its size and its first sung line. |

Ranking, in order: timed before plain text; sound timing before faulty; the
playing version before another version or language; word and mixed timing
before line timing; then `lead`; then, in Accuracy first, `agreement.peers`
(up to 2) and `confidence`; then the source order.

## Useful queries

```sh
# Per source, per song: verdict and state
jq -r '.song.title as $t | .digest[] | [$t, .source, .verdict, .state] | @tsv' out/*.json

# Every request that failed or did not return 200
jq -r '.events[] | select(.request and .request.status != 200) | [.source, .request.status, .request.error, .request.url] | @tsv' out/*.json

# Why each candidate was rejected
jq -r '.events[] | select(.kind == "final") | .rejected[]? | [.source, .reason, .title, .artist, .durationDelta] | @tsv' out/*.json
```
