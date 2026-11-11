# Lunalyr for Windows

A native Windows 10/11 port of Lunalyr, the macOS lyric-wallpaper app. Work in progress: see
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the stack and [VALIDATION.md](VALIDATION.md) for what works.

## Build and test

Requires the .NET 10 SDK (see `global.json`) and, for the app and Native AOT, the Visual Studio Build Tools with
the C++ workload and a Windows 11 SDK.

```
dotnet build Lunalyr.slnx
dotnet test --solution Lunalyr.slnx
```

Build output goes to `artifacts/` (per project), so separate git worktrees never share outputs.

## Layout

| Folder | What |
|---|---|
| `src/Lunalyr.Core` | Platform-independent logic ported from the Mac app (lyrics, ranking, timing, motion maths) |
| `src/Lunalyr.Platform.Windows` | Windows services without UI: now playing (GSMTC), settings store, logging, desktop attach |
| `tests/` | xUnit v3 tests. They never touch real settings, the network, media sessions or permission prompts |
| `spikes/` | The milestone 1 stack spikes, frozen as measured |
| `tools/` | Measurement and screenshot scripts |
| `docs/` | Architecture, spike spec, measurements, notes |

## Rules for anyone (or any agent) working here

- **No sound, ever.** Never play audio or video, never send media keys or GSMTC playback commands to real players.
  Tests use silent fakes; the app is launched for checks with `--no-playback`.
- Never push, publish or create releases without the owner's OK. Commit locally on branches; commit messages end
  with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Don't read credential stores, browser profiles, secret files or signing certificates; don't change Windows
  settings or the registry outside the app's own keys.
- Don't work around YouTube's caption, bot or PO-token protection.

## Licence

Lunalyr derives from Verso (AkkiCode06/verso), GPLv3 with the Commons Clause. The full licence text and attribution
will be added from the Mac reference repo.
