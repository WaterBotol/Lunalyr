# Apple Music's own lyrics on Windows

On the Mac, the `AppleMusicCache` source reads the lyrics Music has already downloaded. They sit in Music's CFNetwork URL
cache, `~/Library/Caches/com.apple.Music/Cache.db` plus `fsCachedData/`, as `amp-api.music.apple.com` song responses
that include `syllable-lyrics` (TTML). This note records whether Apple Music for Windows keeps anything similar that
Lunalyr could read.

**Result:** nothing usable. `AppleMusicWindowsSource` (`src/Lunalyr.Platform.Windows/Lyrics/`) reports the source as
unavailable on this PC, gives the reason, and never returns lyrics.

## What was looked at (12 Nov 2026, read-only)

Apple Music for Windows is installed here as the packaged app `AppleInc.AppleMusicWin_nzyj5cx40ttqa`. Its data is under
`%LOCALAPPDATA%\Packages\AppleInc.AppleMusicWin_nzyj5cx40ttqa\`. Only directory names, file extensions, sizes and the
first 16 bytes of a few non-personal cache files were read. Account, credential and token files were not opened, no
database was queried, and the WebView2 browser profile was not entered.

| Location (under the package folder) | Contents | Lyrics? |
|---|---|---|
| `LocalCache\Local\Apple Computer\Cache.db` | SQLite (`SQLite format 3` header), 53 KB, last written 5 Nov. No `fsCachedData` folder beside it. | Probably a small CFNetwork URL cache like the Mac's, but it is tiny and was not updated while Music was used later. Not queried: it is a response cache that may hold personal data. |
| `LocalCache\Local\Apple\AMPLibraryAgent\` | `Radio_2`/`Store_1` `.sqlite` (32 KB each), a `SubscriptionPlayCache` `.xml` and an encrypted `.m4p`. | No. These are library and store databases and DRM-protected audio. Not opened. |
| `LocalCache\Local\Apple\StoreCache\` | One `.plist` (238 B). | No. |
| `LocalState\Apple\CS\AppleMusic\` | `.hds` files (PlayReady data stores, header `02 00 0A 00 …`), a `bplist00` file, `Cache\*.bla`. | No. This is DRM state. |
| `LocalState\EBWebView\` | The WebView2 (Chromium) profile that runs Music's interface: `Default\Cache\Cache_Data`, Local Storage, and so on. | Most likely, because the interface fetches lyrics from `amp-api` and Chromium caches the replies. But it is a browser profile, which the project rules put off limits, and Chromium's cache format is internal and undocumented. **Not used.** |
| `LocalState\Logs`, `LocalCache\Local\Logs`, `Settings`, `SystemAppData` | ETW traces, logs, package settings. | No. |

## Why not the alternatives

- **Reading the WebView2 cache:** this is a browser profile, which the rules forbid. It would also mean parsing
  Chromium's private cache format, which changes between versions.
- **Asking Apple's catalogue API (`amp-api.music.apple.com`) directly:** that needs a developer token and the user's
  Music user token. That is authentication we would have to obtain or imitate, so it was not attempted.
- **The `Lyrics+` source:** this already delivers Apple-sourced syllable lyrics for many songs (its `metadata.source`
  says `Apple`). It is the practical substitute on Windows.

## Open questions for the owner

1. Is a schema-only look at `LocalCache\Local\Apple Computer\Cache.db` acceptable? That means table names and a count of
   rows whose `request_key` mentions `syllable-lyrics`, without reading any bodies. It would settle whether the native
   side ever caches lyric responses. The file's size and age suggest it doesn't.
2. Should the source list show Apple Music on Windows at all, greyed out with the reason, or hide it? The source
   reports `Availability.IsAvailable == false` with a reason either way.

## Mac tests not ported

`AppleMusicCachedLyricsTests` was not ported (0 of 3 tests). There is no readable cache to point the reader at. The
matching rules those tests check, `matchLevel`, `albumCore` and ambiguity, also depend on `VersionTags` and TTML
parsing, which belong to other workstreams. If a readable source of `amp-api` song JSON ever turns up, port
`AppleMusicCachedLyrics.candidate(query, songs:)` together with those tests.
