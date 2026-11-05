<p align="center">
  <img src="Sources/Assets.xcassets/AppIcon.appiconset/icon_256.png" width="128" height="128" alt="Lunalyr app icon: a white crescent moon with a line of text across its dark centre">
</p>

<h1 align="center">Lunalyr</h1>

<p align="center">
  <strong>Word-timed Apple Music lyrics, set across your Mac's desktop.</strong><br>
  Animated typography over ambient GPU scenes, album colours or live audio visualisers.
</p>

<p align="center">
  <a href="https://github.com/WaterBotol/Lunalyr/actions/workflows/macos.yml"><img src="https://github.com/WaterBotol/Lunalyr/actions/workflows/macos.yml/badge.svg?branch=main" alt="Build status"></a>
  <a href="https://github.com/WaterBotol/Lunalyr/releases/latest"><img src="https://img.shields.io/github/v/release/WaterBotol/Lunalyr?label=release" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/macOS-14%2B-black?logo=apple" alt="Requires macOS 14 or later">
  <a href="LICENSE"><img src="https://img.shields.io/badge/licence-GPL--3.0%20%2B%20Commons%20Clause-blue" alt="Licence: GPL-3.0 with the Commons Clause"></a>
</p>

<p align="center">
  <a href="https://github.com/WaterBotol/Lunalyr/releases/latest"><strong>Download</strong></a> ·
  <a href="#install">Install</a> ·
  <a href="#quick-start">Quick start</a> ·
  <a href="#troubleshooting">Troubleshooting</a> ·
  <a href="CHANGELOG.md">Changelog</a> ·
  <a href="CONTRIBUTING.md">Contributing</a>
</p>

<p align="center">
  <img src="docs/images/hero.webp" width="860" alt="Lunalyr's Echo composition: a lyric line set across a dark desktop, with fainter repeats above and below">
</p>

Lunalyr turns your desktop into a live lyric display for Apple Music. It follows what the Music app is playing and sets each word on your wallpaper as it is sung: over ambient GPU scenes coloured from the album art, over your own desktop picture, or over visualisers that move with your Mac's audio. It lives in the menu bar and draws beneath your desktop icons, so there is no window to manage.

Lunalyr is free to use. It started as a fork of [Verso](https://github.com/AkkiCode06/verso) by AkkiCode06.

## Screenshots

<table>
  <tr>
    <td width="50%"><img src="docs/images/composition-constellation.webp" alt="Constellation composition: lyric words linked by hairlines over a teal halftone wallpaper"></td>
    <td width="50%"><img src="docs/images/visualiser-aurora-curtains.webp" alt="Aurora Curtains visualiser: red curtains of light moving behind the lyrics"></td>
  </tr>
  <tr>
    <td align="center"><b>Constellation</b></td>
    <td align="center"><b>Aurora Curtains visualiser</b></td>
  </tr>
  <tr>
    <td width="50%"><img src="docs/images/your-wallpaper.webp" alt="Your wallpaper mode: lyrics drawn over an existing desktop wallpaper"></td>
    <td width="50%"><img src="docs/images/workspace.png" alt="The Lunalyr settings window on the Wallpaper page"></td>
  </tr>
  <tr>
    <td align="center"><b>Your wallpaper</b>: lyrics over the desktop you already have</td>
    <td align="center"><b>Settings</b></td>
  </tr>
</table>

## Features

- **Words in time with the singer.** Word- and syllable-timed lyrics light up as they are sung, with a karaoke sweep and swelling held notes. When a source only has line timing, Lunalyr shows whole lines rather than inventing word timing.
- **Eight compositions.** Scattered, Constellation, Constellation + Editorial, Echo, Editorial, Spoken, Cascade and Cutout. Each has its own word entrance and an optional faint preview of the next word.
- **Wallpapers that match the song.** 27 Metal GPU effects and 18 line patterns tinted from the album art, moving album-colour gradients, Apple Music animated album covers, or an image or looping video of your own. Choose **Your wallpaper** to draw only the lyrics over your existing desktop picture.
- **Audio-reactive visualisers.** Silk Ribbons, Spectral Aurora, Spectrum Tunnel, Aurora Curtains and Fosfora Aurora respond to your Mac's sound output, analysed on your Mac.
- **Lyrics from many sources.** Lyrics+ (KPoe), NetEase Cloud Music, LRCLIB, QQ Music, Kugou, Kuwo, lyrics.ovh and local lyric exports, picked by an *Accuracy first* match. Per-song timing offsets, manual search and LRC/TTML import cover the rest.
- **Readings.** Romanisation below or instead of the lyric: Japanese readings, Mandarin pinyin with tone options, Korean and other scripts, plus Cantonese readings from exported files.
- **Instrumental interludes.** Album-coloured ribbons and a next-vocal cue fill instrumental breaks.
- **Typefaces.** Any Google Font, downloaded on demand, or SF Pro, with Editorial pairings.
- **At home on the desktop.** One wallpaper per display, playback controls that appear when you hover over exposed desktop, a menu-bar panel, launch at login, a choice of 15, 30 or 60 fps, a low-power mode, and pausing while displays sleep.
- **Built-in updates.** Install new releases from the menu bar. Update archives are signed, and the updater verifies them before installing.

## Install

You need macOS 14 Sonoma or later and the Music app. Visualisers use Core Audio system-audio capture on macOS 14.2 or later, with a ScreenCaptureKit fallback.

1. Download `Lunalyr-<version>.zip` from the [latest release](https://github.com/WaterBotol/Lunalyr/releases/latest).
2. Unzip it in Finder and move **Lunalyr.app** to your **Applications** folder *before* opening it. An app opened straight from Downloads runs from a temporary, read-only copy, and the built-in updater cannot replace it.
3. Open Lunalyr from Applications and follow the first-launch steps below.

### First launch

Releases are ad-hoc signed but not notarised by Apple, so macOS blocks the first launch. You only need to allow it once.

On **macOS 15 Sequoia or later**:

1. Open Lunalyr. When macOS says it could not verify the app, click **Done**.
2. Open **System Settings → Privacy & Security** and scroll down to **Security**.
3. Next to the message about Lunalyr, click **Open Anyway**. Confirm with your password or Touch ID, then click **Open Anyway** again.

On **macOS 14 Sonoma**, Control-click **Lunalyr.app** in Finder, choose **Open**, then click **Open** in the dialog.

If you prefer Terminal, you can remove the download quarantine from a copy you got from this repository's releases page:

```bash
xattr -dr com.apple.quarantine /Applications/Lunalyr.app
```

### Permissions

- **Automation (Music)** is required. Allow it when macOS asks whether Lunalyr may control Music. This is how it reads the current song and playback position, and how its playback buttons work. You can change it later in **System Settings → Privacy & Security → Automation**.
- **System audio** is only needed for visualisers. macOS asks the first time you turn on **System audio** in Lunalyr's Wallpaper settings. The **Screen recording compatibility** capture method needs screen recording permission instead. The **Permissions…** button next to the audio controls opens **Privacy & Security → Screen & System Audio Recording**.

### Updating and uninstalling

Click **Check for updates…** in the menu-bar panel or under **Settings → General**. Stable releases install from there; preview builds are published as GitHub pre-releases and installed manually.

To uninstall, quit Lunalyr and move it to the Bin (Trash). To remove its data as well, delete these folders and reset its preferences:

- `~/Library/Application Support/Verso Custom/Lyrics` (saved and cached lyrics)
- `~/Library/Application Support/Verso/Fonts` (downloaded fonts)
- `~/Library/Application Support/Lunalyr/Wallpapers` (imported images and videos)

```bash
defaults delete local.verso.custom
```

## Quick start

1. Open Lunalyr. It lives in the menu bar: look for its crescent icon near the clock. There is no main window, and a Dock icon appears only while the workspace is open. The workspace opens by itself the first time. To bring it back later, click the menu-bar icon and choose **Open Lunalyr**, or open Lunalyr again from Applications.
2. Play a song in Music and allow Lunalyr to control Music when asked. The lyrics appear on your desktop.
3. Click the menu-bar icon, then **Open Lunalyr**. The workspace shows a live preview of your desktop next to the settings.
4. In **Wallpaper**, choose a kind: Match the music, GPU effects, Line patterns, Colour only, Visualisers, Your wallpaper, or Imported image or video.
5. In **Typography**, choose a composition, typeface and word entrance.
6. For a visualiser, choose **Wallpaper → Visualisers**, pick a scene and turn on **System audio**. You should see *Receiving audio* and moving meters. If not, use **Permissions…** or **Reconnect**.
7. Lyrics early or late? In **Lyrics → This song**, move the offset; it is saved for that recording. Wrong lyrics? Use **Find lyrics** to search, pick another candidate, or import an LRC or TTML file.
8. In **Performance**, 30 fps is a good balance; 60 fps uses more CPU and GPU. **Low power visuals** and **Launch at login** are there too.

## Troubleshooting

<details>
<summary><b>macOS says Lunalyr can't be opened, or that it is damaged</b></summary>

Releases are not notarised, so macOS blocks the first launch; follow the [first-launch steps](#first-launch). If macOS says the app is damaged, unzip the download again with Finder, since some third-party unarchivers break the app's signature. Then move it to Applications and, if needed, clear the quarantine flag with the `xattr` command shown there.

</details>

<details>
<summary><b>I can't find the menu-bar icon</b></summary>

Lunalyr has no Dock icon while its workspace is closed. On a MacBook with a notch, menu-bar icons can disappear behind the notch when the menu bar is crowded: quit a few other menu-bar apps, or use a menu-bar manager. You can always open the workspace by opening Lunalyr again from Applications or Spotlight.

</details>

<details>
<summary><b>No lyrics appear</b></summary>

- Lunalyr follows the Music app. Make sure a song is playing there.
- Check that Lunalyr may control Music, in **System Settings → Privacy & Security → Automation → Lunalyr → Music**. If you declined the prompt and Lunalyr is not listed, run this in Terminal and open Lunalyr again to be asked once more:

  ```bash
  tccutil reset AppleEvents local.verso.custom
  ```

- The menu-bar panel shows which source supplied the lyrics. If none did, use **Find lyrics** in the Lyrics settings to search by hand, turn on more sources, or import an LRC or TTML file.

</details>

<details>
<summary><b>Lyrics are early or late</b></summary>

Use **Earlier** and **Later** under **Lyrics → This song**. The offset is saved for that recording. **Lyrics → Advanced timing** has a global offset that applies to every song.

</details>

<details>
<summary><b>The visualiser doesn't react to music</b></summary>

- Turn on **System audio** in **Wallpaper → Music response**, and look for *Receiving audio* with moving meters.
- Click **Permissions…** and check that Lunalyr is allowed under **Screen & System Audio Recording**. Releases are ad-hoc signed, so macOS can forget this permission, and the Automation permission, after an update. Turn Lunalyr off and on again in that list, or remove and re-add it, then click **Reconnect**.
- Try the other capture method: **System audio** or **Screen recording compatibility**.
- If you report a bug, include the text from **Copy audio diagnostics**.

</details>

<details>
<summary><b>Check for updates doesn't work</b></summary>

Make sure Lunalyr is in your Applications folder and was opened from there. An app opened from Downloads runs from a temporary, read-only copy that the updater cannot replace. Move it, open it again and check once more. You can also download the [latest release](https://github.com/WaterBotol/Lunalyr/releases/latest) by hand.

</details>

<details>
<summary><b>Lunalyr uses a lot of CPU, GPU or battery</b></summary>

In **Performance**, choose 30 fps, turn on **Low power visuals**, and consider **Pause fully covered wallpapers**. Visualisers and GPU effects cost more than line patterns, colour gradients or Your wallpaper. Close the workspace window when you are not using it, because its live preview renders too.

</details>

Still stuck? [Open a bug report](https://github.com/WaterBotol/Lunalyr/issues/new/choose).

## Accessibility

- **Reduce Motion.** When **Reduce motion** is on in **System Settings → Accessibility**, most word entrances become simple fades, Spoken, Cascade and Cutout step from word to word instead of gliding, and interlude graphics stay still. Except for word-timed lines in Spoken, Cascade and Cutout, each line still fades in and out with a slight rise and a small change in size, and blurs slightly as it leaves. Visualisers slow to about 15% speed and stop reacting to beats; other wallpapers keep moving as usual. For a calmer background, choose Your wallpaper, Colour only or a line pattern, and lower **Generated motion speed**.
- **Light and motion sensitivity.** Visualisers move continuously, and their light and colour change with the music. GPU effects also move continuously but do not react to audio. Flashes, camera jolts and flashing stars are turned off, but if motion or changing light affects you, avoid the visualisers and use one of the calmer backgrounds above.
- **Text size.** **Lyric size** in Typography scales every composition, after Lunalyr fits each line to the screen.

## Lyrics sources and privacy

### Where lyrics come from

| Source | Timing | Notes |
| --- | --- | --- |
| Local exports | Word or line, with readings | LRC, `.roma.lrc`, TTML and YRC files exported by [Lyrimuse](https://github.com/Yudaotor/lyrimuse) into a folder you choose. Read-only; off by default. |
| Lyrics+ (KPoe) | Word or syllable | |
| NetEase Cloud Music | Word or line | |
| LRCLIB | Word or line | |
| QQ Music | Line | |
| Kugou Music | Line | |
| Kuwo Music | Line | |
| lyrics.ovh | Plain text | Fallback when nothing timed is found. |

By default, **Accuracy first** rejects results whose title, artist or duration do not match the recording. It then keeps the best timing available: complete word timing, then mixed, line timing and plain text. Among equally timed results, a word-timed LRCLIB result is preferred, then the closest title and artist match, then the source order above. In **Lyrics** you can reorder or disable sources, pin a particular result to a song, or choose by **Source order**. For a strict source order, also turn off **Prefer real word timing across sources** and **Prefer LRCLIB with real word timing**. Most of these services are unofficial, so availability varies. Lunalyr does not use speech recognition or vocal separation.

### What stays on your Mac

Lunalyr has no accounts, analytics or telemetry. It reads the current track from Music through AppleScript and does not access your library or Apple Account. Audio for the visualisers is analysed in memory on your Mac: nothing is recorded, saved or uploaded, and the microphone is never used.

Lunalyr contacts these services:

- **Lyric sources you have enabled** receive the song's title and artist; some also receive the album and duration.
- **Apple's iTunes Search API** receives the artist with the album or song title for each new album you play, so Lunalyr can look for an animated cover. If it finds the album, Lunalyr also loads its public Apple Music page. This happens even when **Use animated covers** is off. The API is also used to find artwork that Music does not provide. Animated covers stream from Apple's servers while they are shown.
- **Google Fonts** supplies the default interface and lyric typeface, Manrope, and any Google Font you choose. Downloaded fonts are kept on your Mac.
- **GitHub** serves update information, only when you click **Check for updates…**. Automatic checks are off, and no system profile is sent.

Preferences are stored in the `local.verso.custom` defaults domain. Lyrics, fonts and imported wallpapers are stored in the folders listed under [Updating and uninstalling](#updating-and-uninstalling).

## Build from source

You need Xcode 16 or later with its Metal toolchain, [XcodeGen](https://github.com/yonaskolb/XcodeGen) and Python 3.9 or later. The build needs the macOS 15 SDK that ships with Xcode 16, which in turn needs macOS 14.5 or later. The app itself still runs on macOS 14.

```bash
git clone https://github.com/WaterBotol/Lunalyr.git
cd Lunalyr
brew install xcodegen
bash Scripts/test.sh
bash Scripts/build.sh
python3 -m unittest discover -s Tests/Python
```

`Scripts/test.sh` generates the Xcode project and runs the XCTest suite. `Scripts/build.sh` runs the C test suites, builds the Release app into `Build/Lunalyr.app` and checks its Metal library and updater framework. The Python tests cover the update and packaging helpers. If the Metal compiler is missing, run `xcodebuild -downloadComponent MetalToolchain`.

To work in Xcode, run `xcodegen generate --spec project.yml` and open `VersoCustom.xcodeproj`. The project is generated from [`project.yml`](project.yml) and is not committed.

> [!NOTE]
> **Why `VersoCustom`?** The Xcode project, target, scheme and Swift module are still called `VersoCustom`, and the bundle identifier is still `local.verso.custom`. These names date from the Verso fork. They are kept so existing installations keep their preferences and saved data and continue to receive updates, which is also why some storage folders keep their old names.

More documentation lives in [`docs/`](docs/README.md): the [architecture overview](docs/ARCHITECTURE.md), the [release process](docs/RELEASING.md), [validation records](docs/VALIDATION.md) and [external formats and system APIs](docs/INTEROPERABILITY.md).

## Contributing

Bug reports, ideas and pull requests are welcome. Start with [CONTRIBUTING.md](CONTRIBUTING.md), and please follow the [Code of Conduct](CODE_OF_CONDUCT.md). Report security problems privately, as described in [SECURITY.md](SECURITY.md).

## Acknowledgements

Lunalyr is built on [Verso](https://github.com/AkkiCode06/verso) by Akki ([AkkiCode06](https://github.com/AkkiCode06)), which provides its lyric engine, compositions and GPU effects. Verso's original README is kept in [docs/upstream](docs/upstream/README.md).

Lunalyr also relies on:

- [Synesthia](https://github.com/jonjaques/synesthia) by Jon Jaques (MIT) for the FFT analyser and the aurora and tunnel shaders behind Silk Ribbons, Spectral Aurora and Spectrum Tunnel.
- [Fosfora](https://github.com/kevinraymond/fosfora) by Kevin Raymond (MIT) for the Aurora shader behind Fosfora Aurora.
- [Sparkle](https://sparkle-project.org) (MIT) for updates.
- [Yams](https://github.com/jpsim/Yams) and libyaml (MIT) for LRCLIB's YAML lyric files.
- [XcodeGen](https://github.com/yonaskolb/XcodeGen) for project generation, and [Google Fonts](https://fonts.google.com) for typefaces.
- Lyric data from [Lyrics+](https://github.com/ibratabian17/LyricsPlus), [LRCLIB](https://lrclib.net), NetEase Cloud Music, QQ Music, Kugou, Kuwo and lyrics.ovh, and the [Lyrimuse](https://github.com/Yudaotor/lyrimuse) export formats. Lunalyr is not affiliated with any of these services.
- The [Cotodama Lyric Speaker](https://lyric-speaker.com/), which inspired Verso.

Full notices are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md), and the licence texts are bundled with the app.

## Licence

Lunalyr is licensed under the GNU General Public License v3.0 with the Commons Clause licence condition, unchanged from Verso. See [LICENSE](LICENSE). The GPL lets you use, study, share and modify the code. The Commons Clause adds that you may not sell the software, or a product or service whose value derives substantially from it. The LICENSE file is the authoritative text.

Apple Music and Music are trademarks of Apple Inc. Lunalyr is not affiliated with or endorsed by Apple.
