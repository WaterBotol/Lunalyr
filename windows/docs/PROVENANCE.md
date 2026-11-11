# Code provenance: keeping the Windows port free of Verso code

**Owner's decision (12 Nov 2026):** the Windows port must not use anything from Verso (AkkiCode06/verso), so that it
can be closed source. Verso is now only a small part of the Mac code, and none of its ideas are unique enough to
need copying. This replaces the original brief's "keep Verso's licence and attribution" rule for the Windows port.

## The rule

1. **Port only Lunalyr's own work.** Logic and tests written for Lunalyr (by the owner or their Claude sessions) may
   be re-implemented faithfully in C#, so both apps behave the same on the same inputs.
2. **Never translate Verso-derived code.** If a Mac file or function still contains lines that trace back to Verso's
   commits, don't translate it. Re-implement the *behaviour* from a short written description (what it does, its
   inputs and outputs, its edge cases), in our own structure and naming, and say so in the commit message.
3. **Generic ideas are free.** LRC/TTML parsing, timed-lyric display, desktop-wallpaper windows and public lyric
   APIs are common knowledge and documented formats, not Verso's. Use the format specs and public docs for these,
   not Verso's code.
4. **No Verso assets.** No Verso icons, images, strings, names or UI layouts.

## How the Verso-derived parts get identified (once the Mac bundle is on this PC)

- Find Verso's commits in the reference checkout: the initial import and any commits that came from Verso's
  repository.
- `git blame` every Swift file on `next/integration` and list the files and line ranges still attributed to those
  commits. Record the result below as the **do-not-translate list**.
- Every port agent gets this file and the list. Reviews check new code against it.

## Do-not-translate list

Summary from the Mac's LUNALYR session, measured at `next/integration` `d63068a` (12 Nov 2026). The full report is
`VERSO-PROVENANCE.md` (14,360 bytes, sha256 `af524bb4…f144528`). It goes into `C:\LunalyrKit\` once the user OKs the
upload. Verify it there, and re-run the check against the bundle once that's here too.

- **Method:** Verso is one commit, `9354e2f` ("Verso 0.1.0"). Lunalyr's history starts at `a99fd65`, a squashed
  import of Verso. A line counts as Verso when `git blame -w -M -C` puts it in `a99fd65` **and** its text also
  appears in `9354e2f`.
- **Extent:** 2,210 of 53,934 counted lines in `Sources/` (about 4.1%), 2,171 of them in 36 files. `Tests/` has 2
  incidental lines.
- **This is a minimum.** Verso code edited later, or Verso algorithms rewritten line by line, isn't counted. Treat every
  file listed below as describe-then-reimplement, whatever its percentage.

**Never port (byte-identical Verso, or almost all Verso):**
`FractalBackgroundView.swift`, `VersoPalette.swift`, `BehaviourSection.swift`, `TrackMood.swift` (93%), the Verso
assets (`Flower.png`, `Wordmark.png`, `Wordmark@2x.png`, the AppIcon `Contents.json`), and `LICENSE` /
`Verso-LICENSE.txt`.

**Re-implement from behaviour only (large Verso share; lines still Verso-attributed in brackets):**
`Settings.swift` (228), `ShaderBackgroundView.swift` (176), `LyricComposition.swift` (124),
`ScatteredLyricText.swift` (107), `KPoeLyricsProvider.swift` (99), `NowPlayingView.swift` (76), `VisualClock.swift`
(71), `MotionArtworkView.swift` (68), `AnimatedGradientView.swift` (64), `ArtworkProvider.swift` (60),
`LyricTypography.swift` (60), `NetEaseLyricsProvider.swift` (56), and `Sources/UI/Shaders.metal` (about 72%
Verso). For these, write a behaviour description first (inputs, outputs, edge cases) and port the parts that
Lunalyr's own commits added. The visualiser shaders in particular are written fresh in HLSL, not translated.

The remaining files of the 36 are listed in the full report.

## Third-party code and data

Third-party libraries and services stay under their own licences and are listed in `docs/ARCHITECTURE.md`
("Licence"). Microsoft's Windows App SDK licence allows closed-source apps.
