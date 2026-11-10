# Changelog

All notable changes to Lunalyr are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Each release has a marketing version (`0.5.0`) and an increasing build number (`18`).

Lunalyr is a fork of [Verso](https://github.com/AkkiCode06/verso) at upstream commit `9354e2f6703fda43e819284563c257e9bb4b07f8`. The original licence is unchanged. The [upstream modification record](docs/upstream/MODIFICATIONS.md) lists files changed in early versions, and [docs/VALIDATION.md](docs/VALIDATION.md) holds the test evidence for each release.

## [Unreleased]

<!--
Add entries for merged, unreleased work here under Added, Changed, Deprecated,
Removed, Fixed or Security. Several branches may add entries at the same time.
When merging, keep every entry and combine duplicate headings.
-->

### Added

- Music video: line the video up by dragging it. The film button beside the desktop player's seek bar (or Align… next to Video earlier/later in the menu bar) turns the seek bar into the video's own bar, with the song shown inside it. Drag while the song plays until the picture matches the music; let go to keep it for that video. Drag down (or hold ⌥) for tenths of a second, ← → move it 0.1 s (⇧ 1 s), Esc puts it back, Automatic forgets it. Video earlier/later and Tap to sync change the same timing.
- Lyrics for another version of the song (Live, Remix, Acoustic, Sped Up, Edit, Instrumental and so on, in English, Chinese, Japanese or Korean) now rank below timed lyrics for the version playing. They are never rejected, so they still show when nothing else is found, and the source list says "other version (Live)". Remasters, credits and deluxe editions count as the same recording, and a tag the album carries is not held against a match.
- When several services give the same words, Accuracy first prefers that match among the other sources, and the source list says how many agree. LRCLIB's word timing loses its lead only when two other services agree on timing it disagrees with.
- NetEase, QQ, Kugou and Kuwo search again under an artist's Chinese, Japanese or Korean name (from MusicBrainz) when the library's name finds nothing. One request per artist, remembered for 30 days. Timing & sources > "Search under artists' other names" turns it off.
- Music's own lyrics are found for more songs: when no album is reported, or the album is a single, EP, deluxe or remastered release. The exact album still wins, and live albums and other lengths still do not match.

- The desktop player has a seek bar again, under the covers. Click or drag anywhere on it; it thickens while you use it, and the seek happens once, when you let go (Esc cancels). Hovering shows the time and the lyric line at the pointer, with small ticks at line starts and gaps between sections. Dragging snaps gently to the start of a line (hold ⌘ to turn that off), and moving the pointer away from the bar while dragging slows it down for fine scrubbing (or hold ⌥). The wallpaper's lyrics follow the drag on that display. Clicking the time on the right switches between time left and the song's length. Radio shows LIVE, adverts and songs that can't be scrubbed show a plain bar.
- Keys for the desktop player after clicking it: Space plays or pauses, ← and → move 5 s, ⌥← and ⌥→ move by lyric line, ⌘← and ⌘→ change song, 0–9 jump through the song, ⌘F or / search, ↑ ↓ and Return pick a search result, Esc goes back.
- On a Force Touch trackpad, a light tap is felt when the seek bar snaps to a line or reaches either end.
- Double-click the playing cover in the desktop player to bring its app forward.

- A small confirmation under the menu bar, on the screen with the pointer, for actions taken where the wallpaper can't be seen: global shortcuts, Shortcuts actions, `lunalyr://` links, listen-along joining and ending, and anything done while the wallpaper is paused or covered. It never takes focus or clicks and is left out of screenshots. Shortcuts actions now say what they did.
- Listen along: the menu says "Reconnecting to …" while a dropped connection is retried, keeps a note when following ends (with Dismiss), and says when sharing is waiting for music or can't reach the relay. Copy Link confirms with "Copied".
- `lunalyr://` links: `lunalyr://join/<room>` listens along with a room (only with a relay set, and only after you say yes), `lunalyr://toggle` shows or hides the lyrics, `lunalyr://card` makes a lyric card, `lunalyr://settings` opens Settings. Anything else is ignored. The web listen-along page has an "Open in Lunalyr" link on wider screens.
- Shortcuts actions: show or hide lyrics, lyrics earlier or later, next layout, make a lyric card, pause or resume the wallpaper, and open Settings.
- Tap to sync for lyrics: choose Tap to Sync Lyrics in the menu-bar menu (or the Lyric timing section of the menu-bar panel), then press any key each time a lyric line starts. After four taps that agree, Lunalyr sets this song's lyric timing for its lyric source, says so on screen, and offers Undo. Stray taps are ignored, Esc stops, and it gives up on its own when the taps stop.
- New global shortcuts, off until switched on in Settings > Keyboard shortcuts: Tap to sync lyrics (T), Music video on or off (V) and Next visualiser (N), with the same modifier keys as the others.
- Translate lyrics on this Mac (Settings › Romanisation › Translations, macOS 15 or later, off by default). When a song’s lyrics have no translation and are in a language you don’t read, Apple’s on-device Translation translates them into your language (or one you pick), shown in the usual translation band. Each song is translated once and cached; a lyric source’s own translation always wins. Language downloads are asked for only when you turn it on or press Download.
- Listen-along relay (`relay/`): a Cloudflare Worker with one Durable Object per room that passes the host's song, position and lyric timing to up to 50 listeners, following `relay/PROTOCOL.md`. It runs on Cloudflare's free plan; `relay/README.md` explains how to deploy your own and where its URL goes in Lunalyr.
- A web listen-along page at `/listen/<room>` on the relay: the host's song, cover and lyrics with the current line lit, and a "Start listening" button that plays the song in Spotify's embedded player in time with the host. It links to Apple Music, YouTube Music and song.link as alternatives, and works on phones in light and dark.
- More music sources. YouTube Music, SoundCloud and Amazon Music playing in Safari, Chrome, Arc, Brave or Edge, read by running a small script in the music tab (Settings > Music players > Follow web players in browsers, off by default). This needs the browser's "Allow JavaScript from Apple Events"; when it is off, Lunalyr says how to turn it on in that browser and never changes it itself. Only tabs on the services' own addresses are read (music.youtube.com, soundcloud.com and Amazon Music's country sites, over HTTPS); a tab that only mentions one in its address is never sent a script. Play/pause, next, previous and seeking work through the page's own buttons, in the tab being followed (found again by its id, and checked to still be that service), and nowhere else. With several music tabs, the one playing is followed, and stays followed while another starts; a tab that stops answering is skipped without holding up the others.
- VLC, Swinsian and VOX for music files on this Mac (Settings > Music players > Follow VLC, Swinsian and VOX, off by default and shown only when one is installed; each can also be chosen alone). Lyrics are looked up from the title, artist and length as for the others; VLC's title line ("Artist - Title") or the file name is split into title and artist. Leading track numbers ("01 Song", "1-03 Song") are dropped, while titles that start with a number, such as "99 Luftballons", keep it.
- The desktop player's switch shows only the players that are installed, open or playing, and a web player under the service's name. Players without a queue or search show just the song playing.
- Listen along: with a listen-along relay set (Settings › Share, empty by
  default, which hides all of it; https only, except a relay on this Mac),
  "Let friends listen along" shares what you play: the song, play and pause,
  the position, your Discord display name while Discord is on, and, with
  "Share lyrics with listeners" (on by default), the lyric lines with their
  timing. Anyone with the link or code can read these, as Settings says.
  Sharing never sends titles to Apple. Friends with Lunalyr follow on their
  own Spotify (any song) or Music (songs in their library), in time, and get
  their shuffle back when they stop. Joining from Discord or with a code or
  link in the menu-bar panel first asks "Listen along with …?"; nothing
  connects or plays until they accept. Pausing, a media key, unplugging
  headphones, sleep, another song or another player pauses following
  ("Paused · Resume" in the menu-bar panel) and nothing is resumed without a
  click; a player opened after joining waits for Play; a friend's song is
  never started while they are paused. A song that can't be played offers
  its song.link page instead, and a full room says so.
- Discord: while sharing, the profile shows a Listen along button (the web
  page) beside Get Lunalyr. "Show a Join button on Discord" swaps the buttons
  for a Join party instead, since Discord refuses both at once.
- A new wallpaper type, Music video: the playing song's music video behind the lyrics, muted, filled to the screen and kept in time with the song (play, pause and seeks are followed; drift is corrected past about a third of a second). A video in your Music library is used first. With "Search YouTube for videos" on (off by default; sends the title and artist to YouTube), the official video is found on YouTube's search page and played in YouTube's privacy-enhanced player; each song's answer is remembered. A YouTube link can be pasted for any song. Songs without a video show the type chosen before Music video (or one picked in Settings), with a short note in Settings only. Per-song video timing for intros, plus Video dimming and Video blur. "Heavy scenes on main display only" keeps the video to the main display, and the video pauses whenever the wallpaper is covered or paused.
- Music video timing is set automatically, once per song and video, and remembered on your Mac. For a YouTube video (with "Search YouTube for videos" on), Lunalyr reads the video's caption track and matches it to the song's timed lyrics; it needs enough matching lines that agree, and otherwise sets nothing. YouTube currently withholds caption text from requests outside its own player, so for most videos this finds nothing yet; then a video 2 to 40 seconds longer than the song is taken to have that much intro, shown as "Estimated". For a video in your Music library, the video's soundtrack is decoded (never played) and compared with the song as it sounds, only while the visualisers' system audio is already on and receiving; this is checked once more later in the song. A nudge of yours always wins. Settings shows "Synced automatically", "Estimated" or "Offset set by you" beside the nudge buttons, with Re-sync to look again; Reset drops your nudge.
- Brief listening for library music videos. When a video from your Music library starts for a song with no timing yet (none set by you and none found before), and music is playing, Lunalyr turns the visualisers' system-audio capture on for up to 25 seconds to line the video's soundtrack up with the song, then off again, once per song and video. If music was paused when the video started, it is tried once when playback resumes. It only does this when System audio is switched on and has already worked on this Mac (or Screen Recording is already allowed, for compatibility capture), so it never asks for a permission, and not while power settings are sparing the Mac. The audio is analysed in memory on your Mac; nothing is recorded, kept or sent. Settings shows "Listening to line up the video…" meanwhile.
- Tap to sync, for YouTube and library videos: in Settings › Music video beside Re-sync, and in the menu-bar panel while a music video shows. Click it, then press any key (or click Tap now) the moment the singing starts in the video; armed later in the song, tap when the next line is sung in the video. The offset is the video's position at the tap, less 0.2 s for reaction time, minus that lyric line's time in the song, and becomes your own ("Offset set by you"), so it beats automatic timing. Tap again to refine: the taps are averaged. Settings says the result, such as "Video starts 8.4 s ahead". Esc cancels. Only plain keys count, and only while Lunalyr's own window or panel is in front (no new permission is needed): not while typing in a text field, and not Command, Control or Option shortcuts or modifier keys alone.
- Music video: Lunalyr now lines a YouTube video's cuts up with the beat. While System audio's capture runs (or may run briefly, as for library videos), it looks at a few tiny frames a second of the muted video for up to 50 seconds, once per video, and fine-tunes the timing by the few tenths of a second it is off. It keeps the result only when one timing clearly fits; an even beat that fits several ways changes nothing. Nothing is downloaded or recorded.
- Music video: "Video earlier" and "Video later" in the menu-bar panel, next to Tap to sync, move the video a quarter second at a time, remembered for that video.

### Changed

- The desktop player only comes up when the pointer slows down or rests in the middle of the desktop, not when it passes through; it stays up while you use it, and goes at once when the pointer moves onto a window. It arrives with a soft spring and leaves with a quicker fade.
- Desktop player buttons are bigger, light up as you press them, and play/pause, next (with shuffle off in Music) and seeks show their result at once. The "shuffle is on" note shows once after shuffle or the playlist changes, not every time. Flicked covers settle on a cover; a paused song's cover sits back a little.
- The desktop player follows Reduce Motion (no bending, depth or sliding: plain fades), Reduce Transparency (solid backgrounds) and Increase Contrast.
- A song change now crossfades the background once, after the new song's cover colours and lyrics are in (at most 2.5 s), instead of two or three times; a lyric upgrade later in the song no longer switches the background.
- Background crossfades no longer dim the screen halfway, and a second change arriving mid-fade carries on smoothly instead of cutting.
- The wallpaper fades in at launch or on a new display instead of popping in over black; visualisers, web scenes and imported pictures now fade in once they have a first frame, not into black first.
- Music video: a song change shows the cover's colours while the next video is found, instead of flashing the fallback wallpaper (it falls back after 3 s).
- Moving a web visualiser between the live scene and its stand-in (main display changed) now crossfades. Under Reduce Motion, a new album's still cover crossfades in.
- The text backing no longer steps darker when lyrics arrive, and the album tint fades with the colours.
- Song changes while the wallpaper is paused show the new background and colours together, without a jump on resume.
- The menu-bar line keeps one width per song, so the status items to its left no longer shift with every line; a line too wide is shown in two halves. It wakes the Mac about once a line instead of four times a second, dims while the music is paused, and waits 2 seconds before leaving between songs.
- The menu-bar panel no longer jumps in height when a song starts or stops. Covers, titles, play/pause, quick toggles, the timing value and status banners now change with short fades instead of cutting; play/pause answers the click at once, and the audio banner stays in place through Reconnect and says when it worked.
- The "Copy a lyric card" shortcut is now called "Open lyric card", which is what it does.
- Music video: pausing the song now leaves the video paused on its current frame instead of switching to the album-colour gradient. A YouTube video is shown as a picture of its last frame (YouTube's own pause button never shows), per display, and plays again from there on resume. After about ten minutes paused, the YouTube player is closed to save power and reopened on resume, with the frame kept on screen meanwhile. Covering or pausing the wallpaper, Reduce Motion, buffering, and times before or after the video still show the gradient.
- Smoother music video sync. A library video eases small drift away by playing up to 3% faster or slower instead of jumping, and seeks only past 1.5 s. A YouTube video seeks only for drift over a second that lasts, or a jump, and each seek is led by how long that player takes to land one, measured as it plays.
- The Player picker lists only installed players. Automatic follows browsers only with web players turned on, and VLC, Swinsian and VOX only with local players turned on. Onboarding names only the players installed on this Mac.
- Clicking a song in the desktop player builds the "Lunalyr Queue" in short steps: from the library in one copy, from a playlist about 20 songs at a time, so lyrics and progress keep updating while it is built.
- Fable is rewritten. Each line is set in one of five shapes (staircase, arch, zigzag, wave or ramp) and marks the word being sung with one of five accents (underline, ring, tile, star or corner brackets). The next line always has a different shape and accent, and a line looks the same each time it is drawn. Words land where they will stay and then hold still. Its place in the picker, word entrances, Editorial typography and Cutout · Highlight are unchanged.
- Lyric card MP4s with a video background over 2 seconds now last as long as the video (Clip length › Match background, the default). The lines play on their own timing inside it: if they finish early the last line stays up, fading out over the last half second when more than 3 seconds remain; if they run past the end they are cut there and the card window says how many lines won't fit. Match lyrics keeps the old length, looping the video with a 0.3 s crossfade at each seam (its sound loops with it) or cutting it. Pictures, GIFs and clips of 2 seconds or less always match the lyrics. A new Max 60 s option (off by default) caps any MP4. Every MP4's sound now fades out over its last 0.3 s, and the song fades over 0.4 s where it is cut or stops early. GIF and PNG exports are unchanged.
- The song in a lyric card MP4 is no longer dropped when its recording doesn't cover every selected line: the missing parts are silent and the export still says to record it again.
- Uses less battery while idle: a song paused for over a minute is checked every 10 seconds instead of every 2 (play, pause and track changes still show at once), and the hover check slows down while the pointer rests.
- Uses less memory over a long day: cover art kept for replays is capped, and large library covers are decoded once, at the size shown, away from the wallpaper's drawing.
- Uses less battery while the displays sleep or the screen is locked: the music player is re-checked every 10 seconds instead of every 2, and at once when the screens wake.
- Smoother stage layouts (Hinge, Broadside, Portal, Mosaic, Torque, Fable). Words spring in from rest, the strip and stacks glide on from word to word without a jolt, the word being sung hands its look to the next one over a moment instead of in one frame, and stacks no longer jump as a word is added. Lines leave at the same speed as in every other layout.
- With Reduce Motion, the stage layouts dissolve between steps instead of moving words around, and Hinge now fades out at the end of a line.
- Word entrances (Float up, Pop, Orbit, Glide, Unfold and the Cutouts) move the same in Torque and Fable as in every other layout.
- Settings controls move: switches slide, sliders glide on arrow keys and Reset (and keep your grip when dragged), segmented choices and the Calm/Bold ring animate, and pressed and keyboard-focus states show.
- Settings options that appear under a switch or choice now fade and slide in, and the sections below move smoothly instead of jumping.
- Settings pages reopen where you left them, and a page opened from elsewhere at a section no longer jumps back there on later visits.
- Hiding the Settings preview slides it away instead of rearranging the window mid-animation.
- Settings sidebar: arrow keys move between pages, ⌘F goes to search, and the selected page stays in view.
- Reset Adjustments, Forget and Forget All (remembered styles), Restore Recommended Order and Allow (excluded albums) can be undone with Edit › Undo. Forget All asks first.
- Clearer feedback in Settings: importing a wallpaper from the tiles shows progress, Google Fonts downloads show a spinner and a warning when they fail, failed lyric and reading imports look like warnings, and a failed translation offers Try Again.
- Timing & sources has Tap to Sync Lyrics… and its Undo, and the song offset slider stretches to ±30 s when an offset needs it.
- The welcome's sample lyric plays twice and then rests, and the word being sung is the brightest. Onboarding's buttons and the Pick a look status fade instead of popping, and VoiceOver moves to each step's title once it has settled.
- The colour preview in Lyric style redraws less often, and not at all under Reduce Motion or while Settings is in the background.
- Discord & lyric card shows whether Discord is connected ("Waiting for Discord" or "Connected as …") and whether listen-along sharing is waiting for music, can't reach the relay, or live with its listeners.
- Lyric style marks the word entrances a layout doesn't use on word-timed lyrics ("whole lines only"), with a note saying why; such an entrance no longer locks Reveal sung words.
- The shortcuts note in Menu bar & shortcuts now says what Open lyric card does, and searching Settings for "lyric card" finds that page too.

### Fixed

- Music video: on a song change the old YouTube video now holds its last frame while it fades out, instead of dropping to the album-colour gradient. Pausing the wallpaper (or covering it) also keeps the frame.
- Music video: a YouTube video correcting its sync no longer blinks to the gradient; a short wait covers most seeks, and a longer one shows the frame from just before it. Videos now fade in and out with eased curves.
- Music video: a library video no longer goes through black before its first frame, and fades in smoothly.
- Music video with Reduce Motion: a YouTube video now shows its own still (faded in) instead of only the gradient.
- Desktop player: the time and lyric shown over the seek bar no longer sit on top of the song titles under the covers; the titles step aside while they show.
- Desktop player: with Reduce Motion, the wallpaper lyrics behind the player only fade, without blurring.
- Shortcut confirmations on the wallpaper stay sharp while the desktop player is up, and are no longer shown twice when the small confirmation under the menu bar already showed them.
- Romanisation set to "Below lyrics" now shows under the words in the Portal, Hinge, Broadside, Mosaic and Torque layouts too (only Fable showed it before). Each reading moves, scales and fades with its word, takes the highlight colour with the word being sung, and is counted when a layout shrinks to fit the screen.
- Imported wallpaper pictures are decoded off the main thread, so a large photo no longer stalls the wallpaper.
- Torque: long lines no longer sink and run off the bottom of the stage.
- Hinge: the last words of a line are no longer cut off half-way through folding away.
- Stage layouts: in Chinese, Japanese and Korean lines, each syllable fades in as it is sung instead of popping in. Mosaic no longer shows a word at two sizes at once as it shrinks into its stack.
- Tap to sync for lyrics: a tap pressed just after the timing is set no longer reaches the app you were in (Space there could pause the song); the keyboard comes back when the result closes, or at once with Esc or Return.
- Tap to sync for lyrics stops if a different lyric source replaces the lyrics mid-session, instead of saving timing for lyrics no longer shown.
- Tap to sync for lyrics: stopping on its own now says why (the song changed, it paused, or other lyrics loaded) instead of "Tap to sync stopped", and keeps your next tap from reaching the app behind. Past four taps it says how many more it will take.
- Tap to sync for lyrics: the panel fades in and out, each tap pops a dot and nudges the hand, the result shows a check mark or warning sign without the text jumping, and VoiceOver counts the taps. The result stays while the pointer is over it, and for 8 seconds when Undo is offered.
- A Shortcuts action that opens Lunalyr waits until Lunalyr has started before it runs.
- Lyrics from Kugou's word-timed files and from Kuwo no longer lose a line, or show a stray timestamp like "<01:60.00>", when a line or word starts within a few milliseconds of a whole minute.
- LRCLIB, QQ Music, Kugou and Kuwo no longer offer an edition of the song with the wrong length (which was then rejected) when one with the right length was also found.
- A song LRCLIB lists as instrumental no longer shows the lyrics of a sung edition with the same name and length.
- Accuracy first now picks the same lyrics whichever source answers first, so the choice (and the song's timing offset for it) no longer changes from one play to the next.
- LRC files that put a space between a line's timestamps ("[00:12.00] [00:45.00]Chorus") now show the line at every timestamp instead of the second one as text.
- After changing lyric sources, the lyrics on screen change once, between lines, to the best match, instead of possibly switching to a lesser one first.
- First launch: resting the pointer on the desktop while the welcome was open could make macOS ask whether Lunalyr may control Music before the welcome had explained why. The desktop player now appears on hover only once the welcome is finished.
- Launch at login: a warning from a failed attempt no longer stays on screen after one that worked. When Lunalyr is switched off in System Settings › Login Items, Settings now says so with an Open Login Items button instead of showing the switch as done, and the switch follows changes made in System Settings while Lunalyr is open.
- Imported wallpaper: importing another picture or video now deletes the copy Lunalyr kept of the previous one, instead of keeping every import in Application Support. If the kept copy goes missing, the wallpaper falls back to a generated one instead of black, and Settings says the file is missing and asks for it again.
- Lyrics from a broken source with an impossibly late line time no longer crash Lunalyr when that line or the break before it is shown.
- Song titles and credits: brackets such as "(Remix)" or "(feat. …)" now stay in the title shown as a song starts, instead of being drawn small and apart like a backing vocal.
- Hinge, Broadside, Portal, Mosaic and Torque: lyrics whose word times run out of order no longer crash Lunalyr; their words now appear in reading order.
- Echo: Lunalyr could crash as a song started when the layout (or the backing-vocal style) was Echo, while drawing the song's title in the background. Credits now also look the same on every launch.
- Desktop player: song and search text containing line breaks or other hidden control characters no longer breaks the Music script it is sent in.
- Desktop player: while you type a search with two displays, the player stays on the display you typed on instead of showing on both.
- Desktop player: a song that played in Spotify or a browser no longer shows in the Music strip's history, where clicking it could only fail.
- Desktop player: a "Lunalyr Queue" left behind when Music or Lunalyr quit part-way through starting a song is now removed the next time you pick a song, instead of staying as a second "Lunalyr Queue".
- Music video: after a song change, the old video now holds still while it fades out instead of jumping to the new song's position (which could flash its opening frame).
- Music video: a YouTube player kept hidden for about ten minutes (wallpaper covered, hidden or paused, Reduce Motion) is now closed so it uses nothing, and reopened when the video plays again. A hidden wallpaper whose video has not loaded no longer keeps checking it four times a second.
- Music video: Japanese and Korean video titles are now recognised, such as 「Title」 and 『Title』, 'Title' M/V, and the artist's name written in their own script next to the title.
- Music video: a library video of a song with the same name by another band (such as "The Who" and "The Kinks") is no longer picked just because the names share a word, and a YouTube channel only counts as the artist's when it is named after them, not when a short name like "IU" happens to be inside another channel's name.
- Music video: a YouTube video that loads but never starts playing (a sign-in or bot check, or a block YouTube reports no error for) now falls back to the normal wallpaper after 20 seconds, and is tried once more later, instead of leaving the colour gradient up for the whole song.
- Music video: when YouTube refuses to play a found video outside its site (age-restricted, not embeddable, removed), the song's next official video is tried, up to three, before the normal wallpaper is shown for it.
- Music video: the YouTube player is checked and muted again on every sync check, in case anything unmuted it.
- Music video: videos no longer run about 0.16 s ahead of the music. The video followed the lyrics' clock, which runs slightly early on purpose for the highlight.
- Music video: a YouTube video that ended up a fraction of a second to a second off now gets put back in step after a few seconds, instead of staying off for the rest of the song, and a small change of timing (a nudge, a tap) shows at once instead of only after the next big correction.
- Music video: the player's position is compared with the song's as of when it was read, not when the answer arrived, so YouTube videos no longer settle slightly ahead after each correction.
- Music video: your tap or nudge is now kept for the video you made it on. Another upload of the same song (a pasted link, the next official video) starts from its own timing instead of inheriting it.
- Music video: when searching YouTube, an official video as long as the song is preferred over a longer cut with scenes before or after the music, as it needs no guessing.
- Lyric cards: saving a second card into the same folder no longer fails to make the MP4; the new files are named "Lunalyr Lyric Card 2" and so on, and nothing is overwritten.
- Lyric cards: cancelling no longer leaves a half-made card (such as an MP4 without its sound) in the folder or the temporary folder.
- Lyric cards: recordings of the song are deleted when they are replaced, when the song changes and on the next launch, instead of piling up in the temporary folder.
- Lyric cards: a background video whose sound stops before its picture now repeats its sound in step with the picture, and a song that stops almost at once fades out instead of jumping quieter.
- Lyric cards: the progress bar no longer comes back after Cancel or after a card is made (which left Copy and Save greyed out).
- Lyric cards: the preview no longer flashes "Pick lines to preview" while you drag a slider or change an option.
- Lyric cards: opening the card window again (shortcut, menu, `lunalyr://card`) no longer throws away the lines you picked, the song recording or a card being made. A new song still starts afresh, and better lyrics arriving keep the lines you picked.
- Lyric cards: mixing an MP4's sound no longer freezes the card window, and Cancel now stops it. The status says what is happening ("Mixing the sound…", "Encoding the GIF…"), and the progress bar moves steadily through every format instead of jumping or stalling.
- Lyric cards: the bottom of the card window no longer jumps: progress (with a percentage and Cancel, which Esc also presses) and the result share one fixed line. A made card shows a check mark and Show in Finder, a failure says so with a warning sign, and any notes sit under Details. The result clears when you change an option.
- Lyric cards: while a card is made or the song is recorded, the lines and options are dimmed and locked (Cancel stays usable), so nothing changes underneath. Recording the song shows a spinner while it starts, then a bar and a countdown.
- Lyric cards: the preview cross-fades to each new version and dims with a spinner while it catches up; dropping a file on it shows a highlight; the Shape and Make tiles answer hover and clicks; option rows fade in and out instead of popping.
- Discord: the song now shows when Discord is opened after Lunalyr, or restarted, without waiting for the next song.
- Discord: updates stay within Discord's limit of five every 20 seconds, with room kept for a new song or a pause, so they are no longer dropped by Discord.
- Discord: long lines in scripts such as Thai, or with emoji, and a bad button link no longer make Discord reject the whole status.
- Discord: while paused or with nothing playing, Lunalyr checks for changes every 5 seconds instead of twice a second.
- Fable: a line ending in one very long word no longer shrinks when it fits.
- Fable: long lines no longer run off the stage or under the menu bar, and earlier words no longer fade away before the line has been read. Lines are wrapped, then shrunk only if they still do not fit.
- Fable: Arabic and other joined scripts are no longer split into reversed, unjoined letters, and right-to-left lines read from the right.
- Fable: with romanisation set to Below, each word's reading now shows under it. Fable used to drop readings that were split by word.
- Fable: Reduce Motion now stops the swoop-in, the letter spreading and the echo trails as well as the floating; words only fade in.
- Fable: words no longer drift on a loop the whole time a line is held, the colour and glow no longer snap off the previous word when the next one starts, and a chosen word entrance is now used.
- Lyric card MP4s: the Song level, Background video and sound file sliders now make an audible difference. Each sound is first brought to the same loudness (about -16 LUFS, measured on the part actually used), so a mastered song no longer drowns out a quieter video or phone recording at the same setting. The sliders work in decibels and show them: 100% is level with the others, 50% is -12 dB, 25% is -24 dB, 150% is +12 dB and 0 is off. The mix is rendered by Lunalyr itself, so levels above 100% are real gain, and a look-ahead limiter keeps peaks at -1 dBFS so nothing distorts. Measured on test tones, a song 20 dB louder than the video used to stay 14 dB above it at 50%; now it sits 12 dB below it. A new "Duck the song under the video's sound" option lowers the song by 12 dB while the background video's own sound plays.

- Listen along: when your copy of the song ends a moment before the host's, following no longer pauses as if you had skipped, and a song the host plays again on repeat is played again for you too.
- Listen along: quickly skipping through songs as host no longer leaves friends on the wrong song for up to ten seconds.
- Listen along: when the host plays from Apple Music, Spotify listeners are told the song can't be found on Spotify from here, instead of that it isn't on their Spotify.
- Web listen-along page: a paused song no longer shows the wrong time for late joiners. For songs the host plays from Apple Music, the page now offers Apple Music's own player (a preview) and plain Spotify, Apple Music, YouTube Music and song.link links; it no longer asks song.link's API, which has stopped working.
- Visualisers: a silent source with a tiny constant offset in its audio no longer shows a stuck bass glow and keeps the idle scene away.
- Visualisers with Reduce Motion or Dim Flashing Lights on: the first frames of a scene no longer show at full brightness before the brightness limiter is ready, and a display switching graphics chips no longer jumps to full brightness.
- Visualisers: switching from a heavy scene such as Spectrum Tunnel to a lighter one no longer leaves the new scene at reduced resolution.
- Lyrics: a word sung more than about nine seconds into a long line now appears, and a long held last word is no longer faded out while it is still being sung.
- Lyrics: lines that leave a moment after their last word now leave at the full frame rate instead of stepping.
- Lyrics: scrubbing or seeking within a song cross-fades smoothly instead of popping, and the line on screen no longer dims and blurs for a moment after a seek.
- Lyrics: a better lyric source arriving at the start of a song no longer makes the previous song's fading lyrics vanish; one that shows a different line cross-fades instead of cutting.
- Lyrics: the loading spinner and the "Nothing playing" notice fade out instead of disappearing in one frame.
- Hinge: a line's last words now finish folding away instead of being cut off part-way.
- Spoken, Cascade and Cutout with Reduce Motion: the column of words now dissolves to its next place at each word instead of jumping.
- Lyrics without word timing: the line being replaced no longer brightens for a moment as the next line appears, and the previewed first word of a line stays in place instead of blinking out and jumping when its line starts.
- Lyrics: a line now starts leaving a little earlier and lifts away as the next one arrives, so two lines no longer sit on top of each other in the middle of the screen at every change.
- Lyrics: words in very fast lines stay on screen for a moment instead of flashing, and after two very short lines the line before them finishes leaving instead of being cut off.
- Hinge, Broadside, Portal, Mosaic, Torque and Fable: lyrics without word timing now bring the line's words in together from its start, instead of building the line on guessed word times.
- Wallpaper while lyrics load: no more system spinner flashing on every song. A lookup that takes longer than a moment fades in the song's name instead, and looking the lyrics up again for a song already showing "no lyrics" no longer brings a spinner back mid-song.
- Wallpaper: when lyrics can't be found because the Mac is offline, it says so instead of "Lyrics unavailable".
- Wallpaper: the "can't read this player" notice names the same player as the menu bar and hover controls, and replaces "Nothing playing" with a cross-fade instead of the notice dropping and rising again.
- Lyrics: skipping a song during an instrumental intro or outro fades the interlude dots out instead of cutting them, they leave more gently, and credits on the old song no longer switch to the new song's title while they fade.
- Wallpaper: "Wallpaper paused" now shows when you pause the wallpaper with a shortcut, and pressing a shortcut again while its note is showing (cycling layouts, say) changes the words in place instead of blinking the note away and back.
- Lyrics: backing vocals keep to the other edge of the screen while a credit is showing, instead of landing on it.
- Lyrics without word timing: the last line of a song, or a line before a long break, now lights its words over the time it takes to sing them instead of spreading the highlight across the whole outro or break. Line start times are unchanged.

## [1.0.0] - 2026-11-08

Build 21 for macOS 14 and later.

### Added

- Production credits ("Produced by:", "Lyrics by:", 作词 and the like) are shown in the background instead of being hidden or mistaken for lyrics, at full size and brightness along the top and bottom edges, for about four times longer than before. Credits are recognised in lyrics from every source, including Apple Music's TTML, and bracketed section labels like [Verse 1] are removed.
- Every song opens once with "Title - Artist", with the artist lit word by word using the Cutout · Highlight tile. Titles and artists repeated in the lyric file are not shown twice.
- Credits, the title and backing vocals each fade out their own way: credits lift off and soften, the title swells and dissolves, backing vocals sink.
- Six new lyric layouts: Hinge (text on two faces of a cube corner), Broadside, Portal (varied per line, with hand-drawn outlines), Mosaic, Torque, Fable (a different shape and accent every line). Torque follows whichever word entrance is chosen, and Editorial typography now applies to all of them.
- Layout pickers are grouped as Calm, Bold and Playful.
- Lyric cards can use a picture, GIF or video as the background, and an MP4 can carry sound: the background video's, any audio file, or the song itself, recorded as the selected lines play. Apple Music carries on from where the recording ended.
- A new Lunalyr mark: a crescent moon with a highlighted lyric line. It is the app icon, the menu-bar icon and the credit on lyric cards.
- Star Trails: each sung word lights a handful of vocal stars, the spot following the word's place in its line, and bass lights large stars low in the sky.
- Discord Rich Presence (off by default, in Settings > Share > Discord): the status shows the current lyric, or the song or artist, with the album cover, a progress bar, a "Get Lunalyr" button and "Paused" while paused. Lyrics can run ahead of the music (2.5 s by default) and breaks show "♪ Instrumental".
- Lyric cards: the MP4 can mix several sounds (the song, a background video's own sound and any number of audio files), each with its own level, and a Text size slider applies to every layout. The line picker follows the song when it changes.
- A faint shade under the menu bar, so a bright wallpaper does not whiten it (Wallpaper > Legibility, on by default).

### Changed

- Settings has a sidebar with search, grouped into Look, Words and App, and two looks, Calm and Bold, with live previews, shorter captions with an info button for the rest, and a new Music players page.
- Torque centres its stack by weight, so longer lines no longer sit low.
- The Lyric Card window scrolls its options, keeps Copy and Save in view, and returns to the front when copying or recording finishes.
- Stage layouts clear a line shortly after its last word instead of holding it until the next line starts.

### Fixed

- A line no longer flashes before or after the real line when a source supplies the same line with both line and word timing.

## [0.6.0] - 2026-11-07

Build 20 for macOS 14 and later.

### Added

- Lyric Card window: pick any lines from the playing song, choose a 4:5, 1:1, 9:16 or 16:9 card whose layout fits its shape, and make an MP4, GIF, PNG or any mix. MP4 is 30 frames a second H.264; PNG is a crisp still of up to three lines. Copy puts each format on the clipboard as an attachment, and Save… writes them to a folder.
- The AMLL TTML database as a lyric source: community word- and syllable-timed lyrics, found through NetEase's song id.
- Kugou lyrics now ask for word-timed KRC first and fall back to line timing.
- Soft and Clay looks for Settings, the menu-bar panel and the welcome, chosen on first launch or in Settings > App. Both keep full-contrast text and selection and drop their shadows under Increase Contrast.
- Match the music can change the lyric layout with every song. Choose which layouts take part and how many songs in a row each one gets.
- Backing vocals written in brackets, like (oh oh), animate word by word as small, soft lyrics behind the lead, in a layout you choose, and stay on screen a little longer.

### Changed

- Settings uses Look, Lyrics and App tabs with page chips instead of a sidebar, in a new violet accent with rounder cards. The menu-bar panel is grouped into cards, its Settings and Options buttons close it, and it is wider.
- Animated lyric GIFs export at the card's full size.
- Songs without a genre take their energy from the cover's colour and the lyrics' pace, so they no longer all land on the same look.
- Lyric recordings that closely match the playing song's length rank slightly higher among equal matches. Cached automatic lyric choices are looked up again.
- Lyric colours keep a tint on moderately bright wallpapers, and Constellation links each word to the previous one.

## [0.5.1] - 2026-11-06

Build 19 for macOS 14 and later.

### Changed

- Star Trails restores the original planes and their pink and lime trails. Kicks and distinct bass attacks pulse the glow along both trails. Beat Punch controls the strength, and the sky can still take its colours from the album cover.
- Album lyric colours favour hues present in the artwork, with contrasting highlights and a colour fallback for monochrome covers.
- The Mac and Windows apps share the rules that check lyric recording matches and remove duplicate cues in mixed-timing lyrics.

## [0.5.0] - 2026-11-06

Build 18 for macOS 14 and later.

### Added

- Copy animated lyric excerpts as GIFs. Choose the lyric layout and up to five lines on either side of the current line in Settings under **Menu bar & shortcuts → Share**. The card puts the cover, song title and artist credits below the lyrics.
- Add a menu-bar lyric ticker, configurable keyboard shortcuts, and per-song or per-artist style memory.
- Add Star Trails, Wavefield variants, Wavecore, Light 3D, Colour Strata and Overprint visualisers. Star Trails can blend its nebula from the album art.
- Add a lyric colour strength control, a white-to-colour sung sweep, custom highlight colours and a nine-stop animated album gradient.
- Add a **Use Default Folder** control for local Lyrimuse lyrics.

### Changed

- Redesign Settings, the menu-bar panel and first-run onboarding with clearer controls and live previews.
- Expand romanisation across scripts, refine album colour extraction and show instrumental cues over your own wallpaper.
- Reorganised the repository for contributors. The README is rewritten with screenshots, this changelog replaces `CHANGES.md`, and long-form documentation now lives in [`docs/`](docs/README.md), including a new [architecture overview](docs/ARCHITECTURE.md).
- Rewrote the contributing guide and pull request template, replaced the issue templates with issue forms, and added a code of conduct, a security policy, `.editorconfig` and `.gitattributes`.
- Moved the build and test entry points from the repository root into `Scripts/`. `Build.command` is now `Scripts/build.sh` and `Run-Tests.command` is now `Scripts/test.sh`. The performance helper is run directly as `Scripts/measure-performance.sh`.

### Removed

- The owner-only `Push-to-GitHub.command` upload helper. Use Git or GitHub Desktop.
- `DEVELOPMENT_BACKLOG.md`, internal notes from earlier AI-assisted development sessions.

### Fixed

- Preserve word timing within mixed-timing songs, suppress duplicate lyrics and prevent a full phrase from flashing during next-word previews or playback corrections.
- Keep Cutout Highlight active through the end of the line and improve GIF text fitting for long and short excerpts.

## [0.4.10] - 2026-11-04

Stable release, build 17. Copies that already include the updater can install it with **Check for Updates…**.

### Added

- **Fosfora Aurora** visualiser: seven luminous frequency ribbons, ported to native Metal from the MIT-licensed [Fosfora](https://github.com/kevinraymond/fosfora) shader at commit `2e85d3babca5b7f27426e211e58fe3c831fb667f`. Its licence is bundled and the original shader, descriptor and provenance are kept in `ThirdParty/Fosfora`. Global onset/beat flashes and shimmer are left out.

### Changed

- **Aurora Curtains** maps its full width to the 64-band logarithmic spectrum, with bass on the left and treble on the right. Each of its five lines favours a different register and phase, so local frequencies bend, widen and light their own part of the curtain instead of shifting the whole wallpaper.
- Silk Ribbons and Spectral Aurora have more local spectral curvature and a modest geometric response to transients. Spectrum Tunnel no longer hard-clamps its high-gain angular lanes and ridge geometry. Global flashes and camera jolts stay disabled.
- **Your wallpaper** now draws only the lyrics, in a transparent desktop window over your existing macOS wallpaper or third-party wallpaper app. No file selection, copy or screen capture is needed, the mode survives the low-power fallback, and the settings preview uses a neutral backdrop.
- The earlier file import is now labelled **Imported image or video**. Saved files and placement are kept.

### Fixed

- Interlude graphics no longer appear in the lyrics-only overlay.
- Live audio meters no longer rebuild the wallpaper and capture pickers while a menu is open.

_Validation: 90 native tests and 9 Python tests passed locally, including narrow FFT-peak spatial response and existing-wallpaper low-power coverage._

## [0.4.9] - 2026-11-04

Preview release, build 16.

### Changed

- Visualiser smoothing follows note attacks sooner and releases between phrases. The extra two-pole envelope's attack/release times drop from 180/600 ms to 55/220 ms; the FFT's existing filtering is unchanged, so attacks come through without jagged, waveform-driven geometry.
- Aurora Curtains has a stronger, spectrum-shaped sway and more thickness variation.

### Fixed

- The wallpaper keeps its size and position when lines are long or the lyric size changes. Lyrics are now placed over an independently sized wallpaper viewport.
- Aurora Curtains keeps its bass and mid variation at high sensitivity. A soft knee replaces the previous hard clamp.

_Validation: 89 native tests passed locally, including AppKit/SwiftUI viewport-layout and high-sensitivity Metal rendering regressions, plus 9 Python tests. Live system-audio capture was confirmed in the running Release build._

## [0.4.8] - 2026-11-04

Preview release, build 15.

### Added

- **Your wallpaper** (renamed **Imported image or video** in 0.4.10): import a local image or a silent looping video. Lunalyr validates the file and keeps its own copy, with Fill, Fit and top/bottom placement and orientation-aware video sizing. Low power mode still uses a lightweight wallpaper at 30 fps.
- **Aurora Curtains**, a dedicated audio visualiser built on the ambient GPU Aurora's material. Music shapes its layered curtains; the ambient original stays separate.
- Audio capture shows its startup stage, live meters and a packet counter, gives up after a startup deadline, and reports clearer tap and fallback errors.

### Changed

- Visualisers use the full sensitivity range (up to 3×) and respond to the Colour richness control.
- Silk Ribbons uses a smoother energy curve, and wallpaper controls that do not apply are hidden.

### Fixed

- The lyric size slider is applied after automatic fitting, so fitting can no longer cancel it. This covers word-timed, whole-line and plain-text lyrics.
- Editorial words keep their typeface as rolling lyric windows move.
- Changing an unrelated setting no longer restarts audio capture.
- A battery guard no longer stops capture while a visualiser is active.

_Validation: 87 native tests and 9 Python tests passed locally; the Release build was exercised with image import and rendering. Live capture was not verified for this build (macOS denied permission to the test copy). Synthetic GPU input is not evidence of live capture._

## [0.4.7] - 2026-11-04

Preview release, build 14.

### Added

- A native settings workspace with one live desktop preview and seven inspectors: Wallpaper, Typography, Lyrics, Readings, Album covers, Performance and About. The menu-bar panel is rebuilt to match.
- Lyric candidate cards with separate source, timing and reading labels; editable metadata search; LRC and TTML import; a clear way to reset a saved choice.
- **Kuwo** (line-synced) and **lyrics.ovh** (plain-text fallback) lyric sources. Existing installations can enable them in the Lyrics inspector's source list. Neither needs an account.
- **Restore recommended source order** in Lyrics.

### Changed

- **Accuracy first** is the default lyric selection for new installations. It rejects poor title, artist and duration matches, then prefers complete word timing, mixed timing, line timing and plain text, in that order. Source order breaks ties. This is a transparent heuristic, not a measured accuracy benchmark, and catalogue size is never used.
- The recommended source order is local exports, Lyrics+/KPoe, NetEase, LRCLIB, QQ, Kugou, Kuwo, then lyrics.ovh. Existing custom orders and enabled sources are kept.
- Valid word timing from any source is preferred over line-only results, even in Source order mode. Turn this preference off for strict source order.
- Song timing, source selection and readings are now separate. Pronunciation readings are labelled separately from translations, and line-only results are labelled **Line-synced**.
- Word coverage and timing are validated once when a result is applied, never in the frame loop. Punctuation variants are kept.
- Automatic lyric caches use a new policy fingerprint, so results chosen under the old policy are looked up again.

### Removed

- The previous settings panes, sample renderers and custom pane chrome.

### Fixed

- Line-only results no longer hide available word-timed matches from other sources.
- Results for a different recording of the same song are rejected.

_Validation: 83 native tests passed locally with no failures or skips, plus 9 Python helper tests. The Release build and new workspace were inspected on a Mac. Physical multi-monitor and controlled performance coverage remain outstanding._

## [0.4.6] - 2026-11-04

Preview release, build 13.

### Added

- Per-recording lyric offsets, stored separately from the global offset.
- A manual low power mode that also follows macOS Low Power Mode, stops unused capture and switches to lightweight wallpapers at 30 fps.
- Editorial typefaces on every composition.
- Real nested word timing from LRCLIB's Lyricsfile YAML, decoded with [Yams](https://github.com/jpsim/Yams) 6.2.2.

### Changed

- The app is rebranded as **Lunalyr**: name, app bundle, icon, menu and settings. The bundle identifier (`local.verso.custom`) and existing storage locations are unchanged.
- Settings use native, resizable navigation with a live desktop renderer preview instead of the fixed custom sidebar.
- When real word timing is missing, complete lines and readings are shown instead of invented word entrances.
- The LRCLIB word-timing preference is now opt-out. Explicit saved choices and strict priority are kept when it is turned off.
- Wider ribbon cores and continuous spectrum curvature; the original GPU Aurora curtains gain a smoothed musical sway.

### Removed

- The narrated walkthrough's narration source and speech resource.

### Fixed

- LRCLIB requests retry short 503/429 back-offs and fall back to search after an exact-match failure. Invalid word timing is treated as line-only.
- Lyric source settings now take effect outside the Sources pane.
- The low-power fallback is applied before animated artwork is chosen.

Apple Music lyric extraction was investigated and deferred; no Accessibility-based reading was added.

## [0.4.5] - 2026-11-04

Stable release, build 12. The first release published on the stable update feed.

### Added

- Native Metal **Spectral Aurora** and **Spectrum Tunnel** visualisers and an FFT analyser, adapted from the MIT-licensed [Synesthia](https://github.com/jonjaques/synesthia) at commit `05144bf0f03eea5cc4af8135e9d71553f1ac9202`. Its licence, attribution and an adaptation manifest are kept.
- **Silk Ribbons**, a quiet aurora variation and the default visualiser. Visualisers are complete wallpapers: audio shapes their own material and geometry.
- The stable update feed and public updater key are configured, so copies with the updater can use **Check for Updates**. Private signing material stays outside Git, and native test results are kept as CI artifacts. Preview publication is gated on native tests and a Release build; stable candidates stay as drafts until reviewed.

### Changed

- Audio now drives only the dedicated visualisers. Capture still uses the system tap with the compatibility fallback and bounded PCM health diagnostics.
- Supported PCM formats are decoded into independent left and right FFTs, so opposite-phase channels cannot cancel each other out. The spectrum is smoothed in time and frequency.
- Desktop defaults are calmer: slower motion, adaptive GPU resolution and a bounded number of frames in flight.
- Smaller words are larger and size contrast is lower across all eight compositions. Each complete line is fitted to the screen together with its reading, and rolling words are fitted to their measured, rotated glyph bounds.
- Constellation links light continuously, following the same vocal envelope as the words. Fixed graph slots and geometry caching are kept.
- Near-monochrome artwork gets a coordinated fan of hues, and the default gradient palette is broader. Ambient wallpaper motion no longer depends on live audio.

### Removed

- Audio-driven shader-domain deformation, colour-field distortion, lyric and background pulses, and the global beat-response controls.
- Kick-driven camera jumps and flashing stars.

### Fixed

- Text wrapping no longer reuses a stale width, and oversized words are reported instead of silently overflowing.
- `NSImage` is created on the main actor from validated image bytes, removing a Swift concurrency diagnostic.

_Validation: 70 native tests, including real Metal rendering, passed locally, as did 9 Python tests and four C suites built with sanitizers._

## 0.4.4 - 2026-11-04

Development build 11 and the first commit in this repository. Not published as a release.

### Fixed

- Release builds failed at `InstrumentalInterludeView.swift:40` because the compiler could not type-check a mixed numeric expression in reasonable time. Ribbon drawing and path construction now live outside the view builder, with explicitly typed `Double` and `CGFloat` calculations. The visual equations and settings are unchanged.

_Validation: the fix was made in source; a native rebuild had not yet been confirmed._

## 0.4.3

Development build; not published. Date not recorded.

### Added

- Automatic capture falls back to compatibility capture after a setup failure, missing or stalled callbacks, unsupported PCM, or eight seconds of silence while Music is playing. Explicit method choices are honoured, and compatibility capture never loops back into recovery. Output-device changes and display wake reconnect the session.
- Richer damped word entrances, held-note growth and drift, and line exits for the existing compositions. Word motion seeds are cached with the static typography, and Constellation keeps its fixed graph slots and envelopes.
- **Copy audio status**, plus an explanation when the response intensity is zero.

### Changed

- System-audio capture clocks its private aggregate device from the current output, waits for the device to be ready, disables physical input channels and analyses only the trailing tap channels, including interleaved layouts.
- Real audio-band energy is compressed so quieter playback still responds, with faster attacks, smooth releases and bounded intensity. Bass, mid and high deformation applies inside all 27 GPU materials, with stronger Tidal and Light Trails behaviour and responsive colour-field movement. (Replaced by the dedicated visualisers in 0.4.5.)
- In Automatic background mode, a generated GPU wallpaper is used while live audio is on, so animated album video cannot hide the response.
- Rolling typography uses a larger free stage with natural word bounds, onset-driven handoffs, distinct hero, column and collage paths, and fades before the stage edges. Long held words stay in focus; cadence, duration and real energy shape the motion, and seeking stays deterministic.
- The interlude spinner is replaced by flowing album-coloured ribbons, moving type and a next-vocal cue. Each composition's next-word preview switch is respected, and no countdown is shown when the return time is unknown.
- Pausing when the wallpaper is covered is now opt-in for new installations, because desktop occlusion can report a visible wallpaper as covered. Display-sleep pausing and existing explicit preferences are kept.
- The GitHub upload helper derives a missing repository-local Git author from the signed-in GitHub account, keeps existing identities and never force-pushes. The browser sign-in and workflow scope are documented.

### Fixed

- Compatibility capture consumes and discards its tiny 1 Hz screen stream so the stream starts reliably. No microphone output is requested and nothing is recorded.

_Validation: the 0.4.2 build passed all 53 Debug native tests on a Mac. This version adds five audio recovery and channel-selection tests (58 total), a portable motion regression suite and quiet, loud and noise audio checks. Native compilation and live checks were still pending._

## 0.4.2

Development build 9; not published. Date not recorded.

### Added

- [Sparkle](https://sparkle-project.org) 2.10.0 with **Check for Updates** in the menu and Behaviour settings. No feed is invented and no private key is bundled. Includes synchronised update configuration, signature-format and packaging checks, and signed-update preparation helpers.
- GitHub Actions workflows that test and build the Mac app and, on request, prepare a signed draft release. Normal build jobs are read-only; only the draft job can write releases. Signing material comes from Actions secrets and is removed from the runner afterwards.
- `Measure-Performance.command`, an owner-run helper that samples CPU locally and uploads nothing (now `Scripts/measure-performance.sh`).
- `Push-to-GitHub.command`, an owner-run upload command that never force-pushes.
- Bundled licence notices for upstream Verso and Sparkle.

### Changed

- Static scattered and rolling typography is cached, including font identity, glyph strings, held-word metadata and layout styling. The cache is invalidated by font loading, reading or source changes, and composition, spread and size changes.
- Unchanged words sit behind an `Equatable` glyph boundary; per-frame state (preview, karaoke, held-word lighting, seeking) still derives from playback time.
- Word measurements and flow rows use SwiftUI `Layout` caches.
- Line starts are prepared for binary search, and density is computed once per lyric result. Duplicate-timestamp and malformed-import behaviour is preserved.
- The lyric fallback is evaluated lazily, only when live audio is not running.

### Removed

- Hidden scrubber schedules and unnecessary battery lookups.

_Validation: 13 new native test methods (53 total) were added but not run in that environment; 9 Python helper tests passed. Native build, real performance and installed updates were not yet verified._

## 0.4.1

Development build 8; not published. Date not recorded.

### Added

- Frame-rate choices of 15, 30 or 60 fps in Behaviour, applied to the settings previews too. The default is 30 fps and the walkthrough is capped at 30 fps.
- Per-window occlusion and display-sleep handling: covered wallpapers pause their schedules and album videos independently, with a pause-when-covered preference.

### Changed

- The wallpaper no longer subscribes to the audio meter; it reads the latest response on its existing scheduled frame.
- The C connection graph is memoised per canvas, including empty graphs, and invalidated when geometry, viewport, count or clearance change. A caller asking for a prefix still gets the full cached graph, and the cache never publishes changes while drawing.
- Line-wide word filtering, cadence and density are computed once per lyric render.

### Fixed

- Settings and walkthrough hosting views are torn down after their windows close, including via the walkthrough's title-bar close button, without deleting a window that was reopened before cleanup ran.

_Validation: the C suite gained cache reuse, output equivalence, invalidation, independent-display, partial-output, empty-result and invalid-input checks; the 40 existing native tests were not run in that environment. This work responded to a reported 154.5% CPU reading; the reduction had not been measured on a Mac at the time._

## 0.4.0

Development build 7; not published. Date not recorded.

### Added

- **Spoken**, **Cascade** and **Cutout** compositions, built around individual word arrival, with stable word identities and timestamp-based scrolling across line changes and seeking.
- An optional faint next word (and its romanisation) for every composition, remembered per style, with a continuous handoff at the word's timestamp. Later words stay hidden.
- **Glide** and **Unfold** word entrances, smoother settling and Reduce Motion support.
- Album-coloured **Tidal** and **Light Trails** GPU effects, whose fluid fields and ribbons are shaped by the music, bringing the total to 27.
- System-audio analysis through private, unmuted Core Audio process taps on macOS 14.2 and later, with a selectable ScreenCaptureKit compatibility path and **Reconnect**. Floating-point and integer PCM are decoded instead of assuming Float32, unsupported formats are reported, and input health is judged from the actual samples.
- Interlude visuals during timed instrumental gaps, intros and outros: an album-coloured breathing ring with a next-vocal countdown when the return time is known, and a Behaviour toggle. Overlapping vocals are merged, short breaths ignored, unknown vocal boundaries in line-only lyrics left unknown, and explicit no-lyrics choices respected.

### Changed

- Constellation links stay anchored to fixed ink and entrance slots and are drawn behind the glyphs, with room reserved for the new entrances.
- An explicitly chosen GPU effect takes priority over animated album video; videos remain available in Automatic mode.
- Shared bloom and pulse are reduced, the flow clock is smoother, and analysis sensitivity is separate from visual intensity.

### Removed

- The global camera deformation.

_Validation: portable tests were extended for PCM formats, bounds, smoothing, the preview handoff curve, seeking and gap detection. Ten new XCTest methods (40 total) were added but not run in that environment, and no natively tested binary was produced for this version._

## 0.3.3

Development build 6; not published. Date not recorded.

### Fixed

- Controls panels no longer hide when the app deactivates: `NSPanel.hidesOnDeactivate` is off, app hiding cannot hide them, and they stay at their desktop level while the app is inactive. An unexpectedly hidden panel is shown again without activating the app.
- Hover detection asks AppKit which window is under the pointer and checks only that window's layer. This replaces an all-window scan that unrelated helper windows could confuse.
- Display-specific hover state, window positions, app-window blocking and same-display linger from 0.3.2 are kept.

### Added

- The build script reminds you to quit the old app before opening the new build.

_Validation: seven native metadata-classification tests were added, and the portable hit-classification suite was updated._

## 0.3.2

Development build 5; not published. Date not recorded.

### Fixed

- Hover state is tracked per display instead of with one global flag, and is read separately by the wallpaper and controls views.
- Each display gets its own controls panel; inactive panels let clicks through.
- Controls appear only over exposed desktop: normal app windows cancel the reveal. AppKit pointer coordinates are matched to Quartz window coordinates using the primary display's origin.
- Hover linger stays within one display, keeps the controls while you reach for a button, and clears when you switch screens or enter an app.
- Windows are initialised relative to their screen, then given explicit global frames. Wallpaper and control panels are rebuilt on display changes, and obsolete windows are closed.

### Added

- A portable desktop-interaction implementation with regression tests.

## 0.3.1

Development build 4; not published. Date not recorded.

### Fixed

- The Japanese tokenizer locale builds a `CFLocaleIdentifier` explicitly, fixing a compiler error.
- Asynchronous audio callbacks capture their controller weakly.
- Display-change notifications hop to the main actor, and the shared style picker is marked as main-actor UI.

Earlier history belongs to upstream Verso; see [docs/upstream](docs/upstream/README.md).

[Unreleased]: https://github.com/WaterBotol/Lunalyr/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/WaterBotol/Lunalyr/releases/tag/v1.0.0
[0.6.0]: https://github.com/WaterBotol/Lunalyr/releases/tag/v0.6.0
[0.5.1]: https://github.com/WaterBotol/Lunalyr/releases/tag/v0.5.1
[0.5.0]: https://github.com/WaterBotol/Lunalyr/releases/tag/v0.5.0
[0.4.10]: https://github.com/WaterBotol/Lunalyr/releases/tag/v0.4.10
[0.4.9]: https://github.com/WaterBotol/Lunalyr/releases/tag/v0.4.9-preview
[0.4.8]: https://github.com/WaterBotol/Lunalyr/releases/tag/v0.4.8-preview
[0.4.7]: https://github.com/WaterBotol/Lunalyr/releases/tag/v0.4.7-preview
[0.4.6]: https://github.com/WaterBotol/Lunalyr/releases/tag/v0.4.6-preview
[0.4.5]: https://github.com/WaterBotol/Lunalyr/releases/tag/v0.4.5
