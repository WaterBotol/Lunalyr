# Handover: milestone 8 and the flaky tests

Written at the end of milestone 7 (`next/windows` e6193e5, Test Build 7). For a fresh session picking up the port.

## Where things are

- **Brief:** `%USERPROFILE%\Downloads\WINDOWS-PORT-PROMPT.md`. Read it whole; its hard rules come first.
- **Repo:** `C:\dev\Lunalyr-Windows-2`, integration branch `next/windows`. Work on branches (worktrees for parallel
  agents), merge into `next/windows`. Never push, publish, or create a GitHub repo or release without the owner's OK
  in chat. Small commits ending `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- **Mac reference (read-only):** `C:\dev\lunalyr-mac-ref` at `next/integration` 79b84db. Verso provenance: blame
  against `a99fd65`; never translate a Verso line (`docs/PROVENANCE.md`).
- **Rules for agents:** `docs/AGENT-RULES.md`. The ones that bite:
  - No sound, ever.
  - No GSMTC commands or media keys to real players.
  - **Never take the foreground or keyboard focus.** The owner types on this PC. Use `--quiet-windows`, PrintWindow
    (`tools/capture-window.ps1`), and no UI Automation scrolling or invoking.
  - Tests use temp settings folders and fakes, never the network.
- **Docs:** `docs/ARCHITECTURE.md` (stack), `docs/APP-SHELL.md` (shell behaviour and settings keys), `VALIDATION.md`,
  `docs/milestones/M1.md`…`M7.md`.
- **Test builds:** `C:\dev\Lunalyr Windows Test Builds\Lunalyr Windows Test Build N`, each with a README.txt. Test Build 7 is the
  latest. Publish with
  `dotnet publish src/Lunalyr.App/Lunalyr.App.csproj -c Release -p:Platform=x64` (output under
  `artifacts\bin\Lunalyr.App\x64\Release\…\win-x64\publish`), copy and drop the `.pdb` files.
- **Shell setup** (PowerShell, before `dotnet`):
  ```powershell
  $env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
  $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'
  ```
- **Tests:** `dotnet build Lunalyr.slnx`, then `dotnet test --solution Lunalyr.slnx --no-build` (no `-p:Platform` on
  the solution; projects take `-p:Platform=x64`) (xUnit v3 on Microsoft.Testing.Platform; `-- --filter-class <name>` for one class). 2,922 tests,
  1 opt-in skip.
- **Live checks:** `Lunalyr.exe --demo --data <temp folder> --quiet-windows --seconds N`.
  - Put `"wallpaperPaused": true` in that folder's `settings.json`, or the demo draws a wallpaper over the owner's
    desktop.
  - Capture flags: `--lyric-card`, `--ticker-capture`, `--hud-capture`, `--floating-capture`, `--settings-page`,
    `--theme`, and `--discord-pipe <own prefix>` (never Discord's pipes).
  - Check Event Viewer / WER after each launch.
- **The owner's own copy:** they run a test build with `%LOCALAPPDATA%\Lunalyr`. Don't touch that folder or that
  process. To quit it gracefully when they ask, post `WM_APP+2` (0x8002) to the window whose class starts with
  `Lunalyr.Host.`.

## Milestone 8: packaging, updates and polish

From the brief: "Packaging: MSIX and/or a signed installer, auto-update, launch at login"; "Onboarding, launch at
login, auto-update, crash-safe settings with atomic writes, and logs readable with a documented command"; "Code
signing and SmartScreen: prepare everything, but signing needs my certificate. Ask me."

- **Installer:** pick MSIX or an unpackaged installer. The app is Native AOT, WinUI 3 with the Windows App SDK 2.6.5
  (check how it is deployed today: self-contained or framework). Write the decision and why in `ARCHITECTURE.md`.
- **`lunalyr://` registration:** register the scheme as `Lunalyr.exe --link "%1"`. `ShellOptions` already treats
  everything after `--link` as links only, and second launches forward them over `WM_COPYDATA` (checked live).
  Registry writes go only under the app's own keys, done by the installer.
- **Launch at login:** a settings switch. The Mac has one; see the Mac's General page and `APP-SHELL.md`.
- **Updates:** check, download and verify. Never publish a feed or release without the owner's OK.
- **Onboarding:** the first-run flow, matching the Mac's, adapted.
- **Signing:** prepare the pipeline (signtool steps, a placeholder thumbprint), then ask the owner for the
  certificate. Don't read certificate stores.
- **Polish pass:**
  - Settings writes are atomic (check).
  - The logs command is documented in `docs/LOGGING.md`.
  - Narrator and high contrast.
  - Every failure state is visible.
  - Measured idle and playing cost for each wallpaper mode (the brief asks for numbers).
- Test Build 8 and a report with screenshots, as each milestone has.

## Flaky tests (each passed on rerun; make them reliable)

Seen during milestones 6–7, mostly under full-suite load:

- `ReduceMotionRenderTests` (mosaic)
- `MotionCoverTests` (two of them)
- `WallpaperTypesTests` "MatchTheMusic…"
- `SpreadSetupRenderTests` (portal)
- `MusicVideoLayerTests.ReduceMotionHoldsTheFirstFrameShown`
- `ScatteredFamilyRenderTests` (an allocation check)

Likely causes to look for:
- wall-clock timing instead of the `ManualTimeProvider` (`tests/Shared`);
- GPU work shared between parallel tests;
- allocation counts picking up other threads' work.

Fix the cause; don't add retries.

## Open questions for the owner (from milestone 7)

- **Discord application ID:** the Windows app uses the Mac's `1568919234367717506`. Keep it, or make a Windows one?
- **Discord activity:** it shows any media session (browsers, podcasts); the Mac shows only Music and Spotify.
  Filter it?
- **Lyric card song recording:** passive, or the Mac's seek-and-play? Keep the separate "Get the song's sound" button?
- Constellation layouts are narrow on portrait cards.
- Floating lyrics re-asserts topmost every 2 s, so it can sit above menus.
- Hide lyrics has no tray entry.

## Starting the owner's copy

Test Build 6.1 stopped at 19:46 on 2026-11-13 because the Claude desktop app updated itself, and the copy had been
started from an agent's shell inside it. Start the owner's copy through Explorer (`explorer.exe "…\Lunalyr.exe"`) so
it isn't tied to the agent, or let the owner start it. The installer and updater (this milestone) must close a
running Lunalyr gracefully before replacing it: post `WM_APP+2` to its `Lunalyr.Host.` window, or use Restart Manager.

## Milestone 8 done (14 Nov 2026)

Report: `docs/milestones/M8.md`. Test Build 8 has the installer (`Lunalyr Setup 0.8.0.exe`, unsigned) and the app in
`app\`. Packaging: `docs/PACKAGING.md`. Waiting on the owner: the signing certificate, where the update feed goes (and
the update key), whether to install over Test Build 7, and installer licence terms.
