# Contributing to Lunalyr

Thanks for helping improve Lunalyr. Bug reports, testing, documentation and code are all welcome. Everyone taking part is expected to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

- [Ways to contribute](#ways-to-contribute)
- [Development setup](#development-setup)
- [Running the tests](#running-the-tests)
- [Coding guidelines](#coding-guidelines)
- [Pull requests](#pull-requests)
- [Reporting bugs](#reporting-bugs)

## Ways to contribute

- **Report a bug or suggest a feature** with the [issue forms](https://github.com/WaterBotol/Lunalyr/issues/new/choose). Search existing issues first.
- **Test preview builds** from the [releases page](https://github.com/WaterBotol/Lunalyr/releases), especially on hardware we cannot easily cover: several displays, Intel Macs, or unusual audio routes.
- **Improve the documentation**, from typos in the README to the guides in [`docs/`](docs/README.md).
- **Send a pull request.** For anything larger than a small fix, open an issue first so we can agree on the approach before you spend time on it.

Report security problems privately, as described in [SECURITY.md](SECURITY.md), not in a public issue.

## Development setup

You need:

- macOS 14 or later
- Xcode 15 or later (the full app, not only the command-line tools) with its Metal toolchain. If `xcrun metal --version` fails, run `xcodebuild -downloadComponent MetalToolchain`.
- [XcodeGen](https://github.com/yonaskolb/XcodeGen): `brew install xcodegen`
- Python 3.9 or later, for the helper tests

Clone the repository and generate the Xcode project:

```bash
git clone https://github.com/WaterBotol/Lunalyr.git
cd Lunalyr
xcodegen generate --spec project.yml
open VersoCustom.xcodeproj
```

[`project.yml`](project.yml) is the source of truth. The generated `VersoCustom.xcodeproj` is ignored by Git, so change targets, settings or packages in `project.yml` and regenerate. Swift Package Manager resolves the two dependencies, Yams and Sparkle, at pinned versions. The project and target are still named `VersoCustom` for compatibility; the [README explains why](README.md#build-from-source).

Build and run the **VersoCustom** scheme. A few things to know when running from Xcode:

- Lunalyr is a menu-bar app, so look for its icon in the menu bar rather than the Dock.
- Quit any installed copy first. A development build uses the same bundle identifier, so it **shares preferences, cached lyrics and imported wallpapers** with your everyday copy, and two running copies both draw on the desktop.
- macOS may ask again for Automation or audio permission, because each development build is ad-hoc signed.

For a tour of the code, start with [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Running the tests

Run all three suites before opening a pull request. CI runs the same steps.

```bash
bash Scripts/test.sh
bash Scripts/build.sh
python3 -m unittest discover -s Tests/Python
```

| Suite | Location | Notes |
| --- | --- | --- |
| XCTest | `Tests/Swift/` | Hosted in the app. Run by `Scripts/test.sh`; log in `Build/test.log`. Includes real Metal rendering, so it needs a Mac GPU. |
| C | `Tests/*.c` | One suite per module in `Sources/Support/`, compiled with `-Wall -Wextra -Werror`. Run by `Scripts/build.sh`. |
| Python | `Tests/Python/` | Tests the update configuration and packaging helpers in `Scripts/`. |

`Scripts/build.sh` also produces a Release build in `Build/Lunalyr.app` and checks that it contains its Metal library and the Sparkle framework.

Add tests alongside your change. Swift logic belongs in `Tests/Swift/`. A new C module needs its own suite, added to `Scripts/build.sh`.

Some behaviour can only be checked in the real app on a Mac: live audio capture, permissions, multiple displays, Spaces, sleep and wake, and how things look and move. Say in your pull request what you checked by hand, and on what hardware.

## Coding guidelines

### General

- Match the style of the surrounding code. [`.editorconfig`](.editorconfig) sets the basics: UTF-8, LF line endings, four-space indentation (two for YAML, JSON and shell scripts), and a final newline.
- Keep pull requests focused. Avoid unrelated reformatting or renames in the same change.
- Write comments that explain *why*. The code already says *what*.
- User-facing text and documentation use British English: colour, visualiser, licence, romanisation.

### Swift

- Keep UI work on the main actor. Keep audio processing on its capture queue, and network and file work in `async` tasks or actors.
- The desktop renders every frame on every display, at up to 60 fps. Keep per-frame work, allocations and layout passes out of `NowPlayingView`'s timeline. Cache what does not change between frames, as `LyricTypography` and `ConnectionGeometryCache` do.
- Preferences live in `UserDefaults` under the `local.verso.custom` domain. Never rename or repurpose an existing key, because users would silently lose their settings. Give new keys the `custom.` prefix.
- Do not change the bundle identifier, product name or storage folders. Updates and existing installations depend on them.

### C and Metal

- C in `Sources/Support/` is C11 and must build cleanly with `-Wall -Wextra -Werror`. Keep the audio paths allocation-free and bounds-checked.
- `VizUniforms` in Swift and its Metal counterpart must keep the same layout. A test checks its size.

### Dependencies and third-party code

- Discuss a new dependency in an issue before adding it. Packages are added through `project.yml` with an exact version.
- Adapted third-party code must have a licence compatible with Lunalyr's (MIT, BSD or similar). Pin the upstream commit, keep the original files and a `SOURCE.md` manifest in `ThirdParty/<Project>/`, bundle the licence text in `Sources/Resources/`, and add a section to [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
- Do not add lyrics, album artwork, commercial fonts, or video or audio you do not have the rights to share.
- Never commit signing keys, tokens or other secrets.

## Pull requests

1. Fork the repository and create a branch from `main`.
2. Make your change, with tests where possible.
3. Run the three test suites above.
4. For user-visible changes, add a line under **Unreleased** in [CHANGELOG.md](CHANGELOG.md).
5. Open a pull request and fill in the template.

Write commit messages in the imperative mood, with a short summary line, for example *Fix lyric offset reset on track change*, and a body explaining why when it is not obvious.

For **visual changes**, attach a screenshot or short screen recording of the real app. For **performance changes**, compare the same song, composition, wallpaper, frame rate and displays in a Release build with the workspace window closed. [`Scripts/measure-performance.sh`](Scripts/measure-performance.sh) records CPU readings and a process sample on your Mac and uploads nothing.

## Reporting bugs

Use the [bug report form](https://github.com/WaterBotol/Lunalyr/issues/new?template=bug_report.yml). It asks for:

- your macOS version and Mac model;
- the Lunalyr version (**About** in the workspace);
- the lyric source shown in the menu-bar panel;
- the composition and wallpaper you were using;
- for audio problems, the text from **Copy audio diagnostics** under **Wallpaper → Music response**;
- for crashes, the newest `Lunalyr-*.ips` report from `~/Library/Logs/DiagnosticReports/`.

Remove personal information before posting, and never include signing keys or tokens.

## Licence

Lunalyr is licensed under GPL-3.0 with the Commons Clause (see [LICENSE](LICENSE)). By contributing, you agree that your contribution is licensed under the same terms.
