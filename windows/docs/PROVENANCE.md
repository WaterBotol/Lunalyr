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

_Pending: needs the Mac bundle (`C:\LunalyrKit\lunalyr-mac.bundle`)._

## Third-party code and data

Third-party libraries and services stay under their own licences and are listed in `docs/ARCHITECTURE.md`
("Licence"). Microsoft's Windows App SDK licence allows closed-source apps.
