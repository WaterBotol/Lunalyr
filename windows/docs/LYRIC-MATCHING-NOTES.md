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
