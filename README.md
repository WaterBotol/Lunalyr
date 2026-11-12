<p align="center">
  <img src="Sources/Assets.xcassets/AppIcon.appiconset/icon_256.png" width="128" height="128" alt="Lunalyr app icon: a white crescent moon on violet with a highlighted lyric line across its opening and a small spark">
</p>

<h1 align="center">Lunalyr</h1>

<p align="center">
  <strong>The words to your music, sung across your Mac's desktop.</strong><br>
  Time-synced lyrics as a living wallpaper, word by word, in the colours of the song.
</p>

<p align="center">
  <a href="https://github.com/WaterBotol/Lunalyr/releases/latest"><img src="https://img.shields.io/github/v/release/WaterBotol/Lunalyr?label=download" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/macOS-14%2B-black?logo=apple" alt="Requires macOS 14 or later">
  <a href="LICENSE"><img src="https://img.shields.io/badge/licence-GPL--3.0%20%2B%20Commons%20Clause-blue" alt="Licence: GPL-3.0 with the Commons Clause"></a>
</p>

<p align="center">
  <a href="https://github.com/WaterBotol/Lunalyr/releases/latest"><strong>Download</strong></a> ·
  <a href="#install">Install</a> ·
  <a href="#features">Features</a> ·
  <a href="#faq-and-troubleshooting">Help</a> ·
  <a href="#privacy">Privacy</a> ·
  <a href="CHANGELOG.md">What's new</a>
</p>

[![Lunalyr on a Mac desktop: a song's lyrics arriving word by word over a background in the album's colours](docs/media/hero.gif)](docs/media/hero.mp4)

Play a song and Lunalyr puts its lyrics on your desktop, lighting each word as it is sung. It follows Apple Music, Spotify and more, sits quietly in the menu bar, and draws beneath your desktop icons, so there is no window to manage. Prefer to see the words while you work? Float them over your apps instead.

Lunalyr is free and open source. It started as a fork of [Verso](https://github.com/AkkiCode06/verso) by AkkiCode06.

> [!TIP]
> Click any animation on this page to watch it as a full-quality video.

## Features

- [Lyrics on your desktop](#lyrics-on-your-desktop)
- [Fourteen layouts](#fourteen-layouts)
- [Japanese, Chinese and Korean readings](#japanese-chinese-and-korean-readings)
- [Floating lyrics over your apps](#floating-lyrics-over-your-apps)
- [An opening title for every song](#an-opening-title-for-every-song)
- [Backgrounds](#backgrounds)
- [The desktop player](#the-desktop-player)
- [Lyric cards](#lyric-cards)
- [Where lyrics come from](#where-lyrics-come-from)
- [Works with the music apps you use](#works-with-the-music-apps-you-use)
- [Menu bar, keyboard shortcuts and Shortcuts](#menu-bar-keyboard-shortcuts-and-shortcuts)
- [Discord and listening along](#discord-and-listening-along)
- [Accessibility](#accessibility)

### Lyrics on your desktop

- **Every word in time.** Word- and syllable-timed lyrics light up as they are sung, with a karaoke sweep and held notes that swell. When a source only has line timing, Lunalyr shows whole lines rather than guessing at the words.
- **Backing vocals** written in brackets, like *(oh oh)*, drift in softly behind the lead.
- **Credits** such as "Produced by" and "Lyrics by" appear in the background instead of being mistaken for lyrics.
- **Instrumental breaks** fill with album-coloured ribbons and a cue for the next line.
- **One wallpaper per display**, with a choice of 15, 30 or 60 frames a second, a low-power mode, and pausing while displays sleep or the desktop is covered.

### Fourteen layouts

[![A tour of Lunalyr's layouts, switching through several of them on the same song](docs/media/layouts-tour.gif)](docs/media/layouts-tour.mp4)

Each layout sets the words in its own way, from calm and spacious to bold and playful. Pick one in **Settings › Lyric style**, or let **Match the music** choose one for each song. Every layout has a choice of word entrances (Float up, Pop, Orbit, Glide, Unfold and more), an optional faint preview of the next word, and an **Editorial** typography option. Use any Google Font, downloaded when you pick it, or one of the built-in typefaces. Lunalyr can remember the look you chose for a song or an artist.

<table>
  <tr>
    <td width="25%" align="center"><img src="docs/media/layout-scattered.png" alt="Scattered layout"><br><b>Scattered</b> · Calm</td>
    <td width="25%" align="center"><img src="docs/media/layout-constellation.png" alt="Constellation layout"><br><b>Constellation</b> · Calm</td>
    <td width="25%" align="center"><img src="docs/media/layout-constellationEditorial.png" alt="Constellation + Editorial layout"><br><b>Constellation + Editorial</b> · Calm</td>
    <td width="25%" align="center"><img src="docs/media/layout-editorial.png" alt="Editorial layout"><br><b>Editorial</b> · Calm</td>
  </tr>
  <tr>
    <td align="center"><img src="docs/media/layout-spoken.png" alt="Spoken layout"><br><b>Spoken</b> · Calm</td>
    <td align="center"><img src="docs/media/layout-echo.png" alt="Echo layout"><br><b>Echo</b> · Bold</td>
    <td align="center"><img src="docs/media/layout-cascade.png" alt="Cascade layout"><br><b>Cascade</b> · Bold</td>
    <td align="center"><img src="docs/media/layout-hinge.png" alt="Hinge layout"><br><b>Hinge</b> · Bold</td>
  </tr>
  <tr>
    <td align="center"><img src="docs/media/layout-broadside.png" alt="Broadside layout"><br><b>Broadside</b> · Bold</td>
    <td align="center"><img src="docs/media/layout-torque.png" alt="Torque layout"><br><b>Torque</b> · Bold</td>
    <td align="center"><img src="docs/media/layout-cutout.png" alt="Cutout layout"><br><b>Cutout</b> · Playful</td>
    <td align="center"><img src="docs/media/layout-portal.png" alt="Portal layout"><br><b>Portal</b> · Playful</td>
  </tr>
  <tr>
    <td align="center"><img src="docs/media/layout-mosaic.png" alt="Mosaic layout"><br><b>Mosaic</b> · Playful</td>
    <td align="center"><img src="docs/media/layout-fable.png" alt="Fable layout"><br><b>Fable</b> · Playful</td>
    <td></td>
    <td></td>
  </tr>
</table>

![The Lyric style page in Lunalyr's Settings, with a live preview of the lyrics beside the layout, typeface and word entrance choices](docs/media/settings-lyrics.png)

### Japanese, Chinese and Korean readings

[![A Japanese lyric with its romaji reading underneath, each syllable of the reading filling in as it is sung](docs/media/readings-japanese.gif)](docs/media/readings-japanese.mp4)

Show a reading under the lyrics, or instead of them: romaji for Japanese, pinyin for Mandarin (with or without tone marks), Revised Romanisation for Korean, and Latin letters for other scripts. Cantonese readings come from files you import. The reading fills in syllable by syllable with the word above it, in every layout.

Lyrics in a language you don't read can also be **translated on your Mac**, with Apple's on-device Translation (macOS 15 or later, off by default). A translation from the lyric source always comes first.

### Floating lyrics over your apps

[![Lyrics floating in a see-through window over an open app, fading as the pointer moves over them](docs/media/floating-lyrics.gif)](docs/media/floating-lyrics.mp4)

Choose **Floating over apps** (in **Settings › Lyric style**, or the **Floating** button in the menu bar) and the lyrics move into a see-through window above every app, full-screen ones included. Place them in a corner, the centre, under the menu bar or above the Dock, on the display you choose, and give them their own style, size and shade. Clicks pass straight through, the lyrics fade while the pointer is over them, and they hide when nothing is playing. **Edit position** lets you drag them anywhere.

### An opening title for every song

[![A new song starting: its title, artist and album appear low on the left beside the album cover, then make way for the first line](docs/media/opening-title.gif)](docs/media/opening-title.mp4)

As a song starts, its title, artist and album appear low on the left beside the cover, like the title card of a music video, with a reading under Japanese, Chinese or Korean titles. It leaves before the first line is sung, and the song's credits follow.

### Backgrounds

Choose what the lyrics sit on in **Settings › Wallpaper**:

- **Match the music** picks a look to suit each song's energy and feel.
- **GPU effects**: 27 moving scenes, from plasma and aurora to nebulae, drawn in the album's colours.
- **Line patterns** in the album's colours, gentle on power.
- **Colour only**: a moving gradient of the album's colours.
- **Visualisers** that move with your Mac's sound: Silk Ribbons, Spectral Aurora, Spectrum Tunnel, Aurora Curtains, Fosfora Aurora, Star Trails, Wavefield, Wavecore, Light 3D, Colour Strata, Overprint and more. The sound is analysed in memory on your Mac and never recorded.
- **Music video**: the song's own music video, muted and kept in time with the music. Lunalyr looks in your Music library first and, if you allow it, searches YouTube. You can pick a different video, paste a link, or line it up yourself by tapping or dragging.
- **Your wallpaper**: just the lyrics, over the desktop picture (or wallpaper app) you already have.
- **Image or video**: a picture or looping video of your own.

Apple Music's **animated album covers** can play behind the lyrics too.

<table>
  <tr>
    <td width="50%"><a href="docs/media/background-visualiser.mp4"><img src="docs/media/background-visualiser.gif" alt="A visualiser moving with the music behind the lyrics"></a></td>
    <td width="50%"><a href="docs/media/background-music-video.mp4"><img src="docs/media/background-music-video.gif" alt="The song's music video playing behind the lyrics"></a></td>
  </tr>
  <tr>
    <td align="center"><b>Visualiser</b></td>
    <td align="center"><b>Music video</b></td>
  </tr>
  <tr>
    <td width="50%"><img src="docs/media/background-wallpaper.png" alt="Lyrics drawn over an ordinary macOS desktop picture"></td>
    <td width="50%"><img src="docs/media/album-colours.png" alt="A background and lyrics tinted in the colours of the playing album's cover"></td>
  </tr>
  <tr>
    <td align="center"><b>Your wallpaper</b></td>
    <td align="center"><b>Album colours</b></td>
  </tr>
</table>

### The desktop player

<table>
  <tr>
    <td width="50%"><img src="docs/media/player-sleeve.png" alt="The Sleeve desktop player: a card in the album's colours with a large cover, the song title, a seek bar and a round play button"></td>
    <td width="50%"><img src="docs/media/player-queue.png" alt="The desktop player with its queue open beside the cover, listing what played, what's playing and what's next"></td>
  </tr>
</table>

Rest the pointer on an empty part of the desktop and **Sleeve**, the desktop player, comes up: one card washed in the album's colours, with a big cover, the song, a seek bar and the play controls. Search your library, open the queue, flick through covers, and switch between music apps. The seek bar shows the lyric line under the pointer and snaps to the start of lines. Once you click it, the keyboard works too: Space plays or pauses, the arrow keys move through the song or by lyric line, and ⌘F searches. Turn it on with **Show player on hover** in **Settings › Performance**.

### Lyric cards

![A lyric card: three lyric lines over the album's colours, with the Lunalyr mark in the corner](docs/media/lyric-card.png)

Pick lines from the playing song and make a card to share, as an MP4, GIF or PNG in 4:5, 1:1, 9:16 or 16:9. Put your own picture, GIF or video behind the words, and add sound: the song itself, the background video's sound or any audio file, each with its own level.

### Where lyrics come from

Lunalyr uses the Music app's own lyrics first, then asks other services and keeps the best timed match for the exact recording you are playing:

| Source | Timing |
| --- | --- |
| Apple Music (the Music app's own lyrics) | Word or line |
| Lyrics+ (KPoe) | Word or syllable |
| AMLL TTML database | Word or syllable |
| NetEase Cloud Music | Word or line |
| LRCLIB | Word or line |
| QQ Music | Line |
| Kugou Music | Word or line |
| Kuwo Music | Line |
| lyrics.ovh | Plain text, when nothing timed is found |
| Your own exported files ([Lyrimuse](https://github.com/Yudaotor/lyrimuse) LRC, TTML and YRC) | Word or line, with readings |

By default, **Accuracy first** rejects results whose title, artist or length do not match, prefers lyrics that several services agree on, and ranks lyrics for a live or remixed version below the version you are playing. In **Settings › Timing & sources** you can reorder or turn off sources, pick a different result for a song, search by hand, or import an LRC or TTML file.

Lyrics early or late? Nudge them for that song, or use **Tap to Sync Lyrics** in the menu bar: press any key as each line starts, and after four taps that agree Lunalyr sets the timing for you.

![The Timing & sources page in Settings, listing lyric sources in priority order](docs/media/settings-sources.png)

### Works with the music apps you use

- **Apple Music** and **Spotify**.
- **YouTube Music, SoundCloud and Amazon Music** playing in Safari, Chrome, Arc, Brave or Edge (off by default; see [Privacy](#privacy)).
- **VLC, Swinsian and VOX**, for music files on your Mac (off by default).

Lunalyr only reads a player that is already open; it never opens one itself.

### Menu bar, keyboard shortcuts and Shortcuts

![The Lunalyr menu-bar panel, showing the playing song, quick toggles for the wallpaper and lyric timing controls](docs/media/menu-bar.png)

- **The menu-bar panel** shows what is playing and where its lyrics came from, with quick switches for the wallpaper, floating lyrics, the visualiser scene and lyric timing.
- **The current line in the menu bar**, if you like.
- **Global keyboard shortcuts** (off until you turn them on in **Settings › Menu bar & shortcuts**) to show or hide the lyrics, nudge their timing, change layout, open a lyric card, pause the wallpaper, tap to sync, toggle the music video and change visualiser.
- **Shortcuts actions** for the same jobs, so you can use them in your own shortcuts or with Siri.
- **`lunalyr://` links**: `lunalyr://toggle`, `lunalyr://floating`, `lunalyr://card` and `lunalyr://settings`.

When you use one of these while the wallpaper is hidden, a small confirmation appears under the menu bar.

### Discord and listening along

- **Discord**: show the line being sung, or just the song, on your Discord profile, with the album cover and a progress bar (off by default).
- **Listen along**: let friends follow what you play, in time, on their own Spotify or Music, or in a web page with the lyrics lit line by line. This needs a small relay server that you deploy yourself for free; [`relay/README.md`](relay/README.md) explains how. Until you add one, listen-along stays hidden and nothing connects.

### Accessibility

- **Reduce Motion.** When **Reduce motion** is on in **System Settings › Accessibility**, most word entrances become simple fades and layouts dissolve between steps instead of moving words around. Visualisers slow to about 15% speed and stop reacting to beats. The desktop player drops its bending and sliding for plain fades. For a calmer background, choose Your wallpaper, Colour only or a line pattern.
- **Light and motion sensitivity.** Visualisers move continuously, and their light and colour change with the music. Flashes, camera jolts and flashing stars are turned off, but if motion or changing light affects you, use one of the calmer backgrounds above.
- **Reduce Transparency and Increase Contrast** are followed by the desktop player and Settings.
- **Text size.** **Lyric size** in Lyric style scales every layout, after each line is fitted to the screen.

## Install

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

### Getting started

1. Lunalyr lives in the menu bar: look for its crescent icon near the clock. A short welcome opens the first time.
2. Play a song, and allow Lunalyr to control your music app when macOS asks. The lyrics appear on your desktop.
3. Click the menu-bar icon for quick controls, or choose **Settings…** to pick a layout, background and typeface, with a live preview beside the options.

### Permissions

- **Automation** is required. Allow it when macOS asks whether Lunalyr may control Music (or Spotify, or the other player you use). This is how it reads the current song and playback position, and how its playback buttons work. You can change it later in **System Settings → Privacy & Security → Automation**.
- **System audio** is only needed for visualisers. macOS asks the first time you turn on **System audio** in **Settings › Wallpaper**. The **Screen recording compatibility** capture method needs screen recording permission instead. **Open Privacy Settings…** next to the audio controls takes you to **Privacy & Security → Screen & System Audio Recording**.

### Updating and uninstalling

Click **Check for Updates…** in the menu-bar panel or in **Settings › General**. Stable releases install from there; preview builds are published as GitHub pre-releases and installed by hand.

To uninstall, quit Lunalyr and move it to the Bin (Trash). To remove its data as well, delete these folders and reset its preferences:

- `~/Library/Application Support/Verso Custom/Lyrics` (saved and cached lyrics)
- `~/Library/Application Support/Verso/Fonts` (downloaded fonts)
- `~/Library/Application Support/Lunalyr/Wallpapers` (imported images and videos)

```bash
defaults delete local.verso.custom
```

## Requirements

- macOS 14 Sonoma or later.
- A music app Lunalyr can follow: Music, Spotify, a supported web player, VLC, Swinsian or VOX.
- Visualisers use system-audio capture on macOS 14.2 or later, with a screen-recording fallback on earlier versions.
- Translating lyrics on your Mac needs macOS 15 or later.
- Discord status needs the Discord desktop app. Listen-along needs a relay you deploy yourself.

## FAQ and troubleshooting

<details>
<summary><b>macOS says Lunalyr can't be opened, or that it is damaged</b></summary>

Releases are not notarised, so macOS blocks the first launch; follow the [first-launch steps](#first-launch). If macOS says the app is damaged, unzip the download again with Finder, since some third-party unarchivers break the app's signature. Then move it to Applications and, if needed, clear the quarantine flag with the `xattr` command shown there.

</details>

<details>
<summary><b>I can't find the menu-bar icon</b></summary>

Lunalyr has no Dock icon while Settings is closed. On a MacBook with a notch, menu-bar icons can disappear behind the notch when the menu bar is crowded: quit a few other menu-bar apps, or use a menu-bar manager. You can always open Settings by opening Lunalyr again from Applications or Spotlight.

</details>

<details>
<summary><b>No lyrics appear</b></summary>

- Make sure a song is playing in a player Lunalyr follows, and that the player is turned on in **Settings › Music players**.
- Check that Lunalyr may control your player, in **System Settings → Privacy & Security → Automation → Lunalyr**. If you declined the prompt and Lunalyr is not listed, run this in Terminal and open Lunalyr again to be asked once more:

  ```bash
  tccutil reset AppleEvents local.verso.custom
  ```

- The menu-bar panel shows which source supplied the lyrics. If none did, use **Find lyrics** in **Settings › Timing & sources** to search by hand, turn on more sources, or import an LRC or TTML file.

</details>

<details>
<summary><b>Lyrics are early or late</b></summary>

Use **Earlier** and **Later** under **Settings › Timing & sources › This song**, or **Tap to Sync Lyrics** in the menu bar. The timing is saved for that recording. **Advanced timing** on the same page has an offset for every song.

</details>

<details>
<summary><b>The wrong lyrics, or the wrong music video, show</b></summary>

For lyrics, pick another result for the song in **Settings › Timing & sources**. For a music video, choose **Search YouTube…** in the menu bar to pick a different one, or paste a link in **Settings › Wallpaper › Music video**.

</details>

<details>
<summary><b>The visualiser doesn't react to music</b></summary>

- Turn on **System audio** in **Settings › Wallpaper › Music response**, and look for *Receiving audio* with moving meters.
- Click **Open Privacy Settings…** and check that Lunalyr is allowed under **Screen & System Audio Recording**. Releases are ad-hoc signed, so macOS can forget this permission, and the Automation permission, after an update. Turn Lunalyr off and on again in that list, or remove and re-add it, then click **Reconnect**.
- Try the other capture method: **System audio** or **Screen recording compatibility**.
- If you report a bug, include the text from **Copy Audio Diagnostics**.

</details>

<details>
<summary><b>Check for Updates doesn't work</b></summary>

Make sure Lunalyr is in your Applications folder and was opened from there. An app opened from Downloads runs from a temporary, read-only copy that the updater cannot replace. Move it, open it again and check once more. You can also download the [latest release](https://github.com/WaterBotol/Lunalyr/releases/latest) by hand.

</details>

<details>
<summary><b>Lunalyr uses a lot of CPU, GPU or battery</b></summary>

In **Settings › Performance**, choose 30 fps, turn on **Low power visuals**, and consider **Pause fully covered wallpapers**. Visualisers, music videos and GPU effects cost more than line patterns, Colour only or Your wallpaper. Close Settings when you are not using it, because its live preview renders too.

</details>

Still stuck? [Open a bug report](https://github.com/WaterBotol/Lunalyr/issues/new/choose).

## Privacy

Lunalyr has no accounts, analytics or telemetry and does not access your Apple Account. It reads the current track from Music, Spotify, VLC, Swinsian or VOX through AppleScript, only while that player is already open (it never opens one). For VLC, Swinsian and VOX, a file without tags is named from its file name, and that name is what is sent to lyric sources as the title and artist. Audio for the visualisers is analysed in memory on your Mac: nothing is recorded, saved or uploaded, and the microphone is never used.

The desktop player (**Show player on hover**) also works with your Music library, on your Mac: it searches the library for what you type, reads the covers of the songs in its strip from the library, and shows the playing playlist around the current song. When you click a song in the strip, it makes a playlist called "Lunalyr Queue" with that song and the ones after it, copied from your library, and plays it. It replaces only the "Lunalyr Queue" playlist it made itself; it never deletes or changes anything else in your library, and never adds songs to it. Songs from other sources, such as Apple Music playlists or radio, simply play.

With **Follow web players in browsers** on (off by default), Lunalyr also reads YouTube Music, SoundCloud and Amazon Music tabs in Safari, Chrome, Arc, Brave or Edge. It does this by running a small script in those music tabs through AppleScript, which needs the browser's **Allow JavaScript from Apple Events** setting; Lunalyr explains how to turn it on and never changes it itself. The script reads the song, artist, album, position and cover address from the page, and play, pause, next, previous and seek go through the page's own player controls. To find music tabs, Lunalyr reads the addresses of the browser's open tabs; nothing else in other tabs is read or run. The song's details are then used like any other player's, and the cover is loaded from the address the page gives.

The music-video wallpaper (**Music video**, off by default) first looks for a music video in your Music library on your Mac.

Lunalyr contacts these services:

- **Lyric sources you have enabled** receive the song's title and artist; some also receive the album and duration. With **Search under other names** on, MusicBrainz is asked once per artist for their Chinese, Japanese or Korean name, remembered for 30 days.
- **Apple's iTunes Search API** receives the artist with the album or song title for each new album you play, so Lunalyr can look for an animated cover. If it finds the album, Lunalyr also loads its public Apple Music page. This happens even when **Use animated covers** is off. The API is also used to find artwork that Music does not provide, and to find a song's Japanese title when Apple Music lists it under an English one. Animated covers stream from Apple's servers while they are shown.
- **Apple's iTunes Search API** also receives, from the desktop player, the words you type in its search box (for Apple Music songs and albums), and the title and artist of a song in the strip whose cover is not in your library.
- **Discord**, only when **Show what I'm listening to on Discord** is on, receives through its desktop app on your Mac the song, artist and album, the line being sung (or what you chose to show) and the progress. These appear on your profile for others. With **Show the album cover**, the cover is found on Apple's iTunes Search API, which receives the song's title and artist. With **Show a Play this song button**, your profile shows a public song.link address for the song: built from Spotify's own track ID, or, for Apple Music, from the same Apple search (only when the result is exactly that song). With both the cover and the button off, nothing is sent to Apple for Discord or for listen-along. Turning Discord off also stops your display name being sent to a listen-along relay. With **Show a Join button on Discord**, the Join invite carries your listen-along room code and your Discord display name. song.link is not contacted by Lunalyr, only by whoever clicks the button.
- **YouTube**, for the music-video wallpaper only. With **Search YouTube for videos** on (off by default), YouTube's search page (www.youtube.com) receives the song's title and artist with "official music video"; the answer for each song is remembered on your Mac. **Search YouTube…** works with this off too: it sends only what is in its search box, when you search, and loads the results' thumbnails. Any YouTube video shown, found or pasted, plays muted in YouTube's embedded player, which loads its script from www.youtube.com and the video from youtube-nocookie.com in a private web view that keeps no cookies. A video from your library plays on your Mac and contacts nothing. **Tap to sync** reads nothing but the key you press while Lunalyr's own window or panel is in front.
- **Your listen-along relay**, only after you enter its address in **Settings › Discord & lyric card** (empty by default, which hides listen-along and connects nothing). The relay is a small server you deploy yourself (see [`relay/README.md`](relay/README.md)); addresses must use https, except a relay on this Mac.
  - With **Let friends listen along** on (off by default), the relay receives the song, artist, album, duration, its Spotify track ID, whether it is playing, and the position; your Discord display name while **Show what I'm listening to on Discord** is on; and, with **Share lyrics with listeners** (on by default), the lyric lines you see with their timing, once per song. Apple's catalogue ID is included only when Discord's cover or Play this song button already looked the song up; sharing never sends anything to Apple itself. The relay keeps only the latest state, and deletes it when you stop sharing or 10 minutes after Lunalyr disconnects. Anyone with the room's link or code can read that state, on the relay's web page or from its `/rooms/<code>/state` address.
  - Following a friend (from Discord's **Join** or a code in the menu bar) connects to your relay only after you accept "Listen along with …?". Lunalyr then plays their songs on your Music (songs in your library, found by searching it on your Mac) or Spotify, and turns shuffle off until you stop. Pausing or changing the music yourself pauses following until you click **Resume**, and a song is never started while your friend is paused.
  - The relay's web listen-along page, opened by people without Lunalyr, asks Apple's iTunes Search API for the song's cover and links, and loads Spotify's or Apple Music's embedded player only when the visitor chooses to listen. Those requests come from the visitor's browser, not from your Mac.
- **Apple**, for translation languages only: with **Translations** on, lyrics are translated on your Mac, and macOS downloads a language only when you turn it on or press **Download**.
- **Google Fonts** supplies the default interface and lyric typeface, Manrope, and any Google Font you choose. Downloaded fonts are kept on your Mac.
- **GitHub** serves update information, only when you click **Check for Updates…**. Automatic checks are off, and no system profile is sent.

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
- Lyric data from Apple Music, [Lyrics+](https://github.com/ibratabian17/LyricsPlus), the [AMLL TTML database](https://github.com/amll-dev/amll-ttml-db), [LRCLIB](https://lrclib.net), NetEase Cloud Music, QQ Music, Kugou, Kuwo and lyrics.ovh, and the [Lyrimuse](https://github.com/Yudaotor/lyrimuse) export formats. Lunalyr is not affiliated with any of these services.
- The [Cotodama Lyric Speaker](https://lyric-speaker.com/), which inspired Verso.

Full notices are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md), and the licence texts are bundled with the app.

## Licence

Lunalyr is licensed under the GNU General Public License v3.0 with the Commons Clause licence condition, unchanged from Verso. See [LICENSE](LICENSE). The GPL lets you use, study, share and modify the code. The Commons Clause adds that you may not sell the software, or a product or service whose value derives substantially from it. The LICENSE file is the authoritative text.

Apple Music and Music are trademarks of Apple Inc. Lunalyr is not affiliated with or endorsed by Apple.
