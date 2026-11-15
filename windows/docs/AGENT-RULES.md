# Rules for agents working on Lunalyr for Windows

Read this whole file before doing anything. These rules come from the owner and are not negotiable.

## Hard rules

- **No sound, ever.** The owner listens to music while you work. Never play audio or video, never send media keys,
  never call GSMTC playback commands (`TryPlayAsync` and friends) on real sessions, never open YouTube, Spotify or
  other player pages. Tests that need a media session use the silent fake.
- **Git:** work only in your own worktree and branch. Small, focused commits whose messages end with a blank line then
  `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never push, never merge, never touch other worktrees,
  `main`, `next/windows`, or the reference checkout.
- **Private things:** don't read credential stores, browser profiles, `.env`/secret files or certificates. Don't change
  Windows settings or the registry.
- **Tests never touch** the owner's real settings or data folders, the real network, media sessions or permission
  prompts. Use temp folders, fakes and recorded fixtures checked into the repo.
- **Network while developing:** you may make a *few* real HTTP requests to public lyric APIs only to record fixtures,
  with a generic User-Agent (`Lunalyr/0.1 (Windows port; dev)`), never including any personal data. The only
  YouTube request allowed is a *few* GETs of `https://www.youtube.com/results?search_query=…` to record search
  fixtures (the owner chose the Mac's page search, 13 Nov 2026), sent as the app sends them. Never open YouTube in a
  browser or WebView, never load a player or embed, and never work around any service's bot, consent, caption or
  PO-token protection: a wall means "unavailable".
- **No Verso code (closed-source requirement).** Read `C:\dev\Lunalyr-Windows-2\docs\PROVENANCE.md`.
  Before translating any Swift file, run in the reference checkout:
  `git -C C:\dev\lunalyr-mac-ref blame -w -M -C --line-porcelain <file> | Select-String '^[0-9a-f]{40} '` and check
  for lines attributed to commit `a99fd65` (the squashed Verso import). Lines from `a99fd65` must not be translated:
  re-implement that behaviour from a description in your own structure. Lunalyr's own lines may be ported
  faithfully. Mention any re-implemented-from-behaviour parts in your commit messages and report.
- **Lyric priority** (identical to the Mac): word or mixed timing first, Apple Music > LRCLIB > others; then line
  timing, Apple Music > others. LRCLIB loses its lead when two other sources agree against its timing. Lyrics with
  broken timing (crammed words, bunched lines) rank below sound timing but are still used when nothing else is timed.

## The reference

- Read-only Mac checkout: `C:\dev\lunalyr-mac-ref` (branch `next/integration`, Swift). Never commit there (a hook
  blocks it). Its `docs/ARCHITECTURE.md` maps the code.
- Port **behaviour and tests**, not Swift idioms: idiomatic, modern C# (records, spans where it matters), Native-AOT
  safe (no reflection-based serialization: use System.Text.Json source generation; no `dynamic`).
- **Port the tests**: for every Mac test you port, keep the same inputs and expected outputs so both apps agree.
  Name the C# test after the Swift one (comment `// Ported from <File>.swift: <testName>`). If a Mac test can't apply
  on Windows, list it in your report with the reason.

## The repo

- Solution: `Lunalyr.slnx`. `src/Lunalyr.Core` (no Windows APIs; `net10.0`), `src/Lunalyr.Platform.Windows`
  (`net10.0-windows10.0.26100.0`, WinRT allowed), tests in `tests/Lunalyr.Core.Tests` and
  `tests/Lunalyr.Platform.Windows.Tests` (xUnit v3 on Microsoft.Testing.Platform; `using Xunit` is global).
  Shared test helpers: `tests/Shared/` (e.g. `ManualTimeProvider`).
- Shared lyric types are in `src/Lunalyr.Core/Lyrics/LyricModels.cs`. Don't change their existing members; if you need
  more, add them in your own file (`LyricLine` and `LyricQuery` are partial) and say so in your report.
- Logging: `Lunalyr.Core.Logging.Logger` (categories include `Lyrics`, `Sync`). Use `Logger.Null` in tests.
- Each PowerShell call starts with a fresh PATH: begin with
  the PATH setup in `docs/HANDOVER-M8.md` ("Shell setup").
- Build/test from your worktree root: `dotnet build Lunalyr.slnx` then `dotnet test --solution Lunalyr.slnx`.
  `TreatWarningsAsErrors` is on. Output goes to your worktree's own `artifacts/`.
- No new NuGet packages unless essential. If you add one, add its version to `Directory.Packages.props`, make sure it
  is AOT-compatible, and give its licence in your report (MIT/BSD/Apache only).

## Done means

Build clean, all tests pass (run the suite twice), everything committed on your branch, `git status` clean.
Report back briefly: what's ported (files → C# types), how many Mac tests you ported and how many pass, Mac tests not
ported and why, anything re-implemented from behaviour because of Verso lines, open questions, and commit hashes.
Don't paste large code.
