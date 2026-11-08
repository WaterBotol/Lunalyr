# Lunalyr Settings: findability research and recommended information architecture

Method: fetched Apple HIG and NN/g pages directly (2026-11-08) and viewed real settings documentation in the built-in browser. Every claim has a URL. Items marked INFERENCE are my reasoning, not a sourced claim. Items marked NOT VERIFIED were not confirmed.

## 1. What the sources say

### 1.1 Apple HIG: macOS Settings window
Source: https://developer.apple.com/design/human-interface-guidelines/settings
- A macOS settings window "contains a toolbar ... for switching between views, called panes, that each contain a group of related settings."
- Use a non-customisable toolbar that stays visible and always shows the active button. Apple's reason: "People rely on a stable settings interface to help them find what they need."
- Update the window title to the visible pane's name.
- Restore the most recently viewed pane on reopen.
- Dim minimise and maximise, because the window sizes to the current pane.
- Minimise the number of settings. Too many "make it hard to find a particular setting."
- Give defaults that suit the most people. Do not ask for setup information you can detect (the app can detect Dark Mode).
- Put "general, infrequently changed settings" in the settings window. Task-specific options (show/hide parts of a view, reorder, filter) should be in the screen they affect. A separate settings area "disconnects it from its context ... and often hiding the results until people resume the task."
- Do not duplicate systemwide settings.

### 1.2 Apple HIG: sidebars, disclosure, search
- Sidebars, https://developer.apple.com/design/human-interface-guidelines/sidebars: "no more than two levels of hierarchy"; short descriptive group labels; SF Symbols as familiar icons; never put critical actions at the bottom of a sidebar, because windows get moved off-screen.
- Disclosure controls, https://developer.apple.com/design/human-interface-guidelines/disclosure-controls: put the most likely controls at the top, advanced hidden by default; label the control by what it hides ("Advanced Options"); at most one disclosure button per view.
- Searching, https://developer.apple.com/design/human-interface-guidelines/searching: "Aim to make your app's content searchable through a single location"; show the scope clearly.

### 1.3 macOS System Settings (Ventura and later)
- Apple's Mac guide describes a window with a sidebar of settings groups and the options for the selected item on the right. Its screenshot shows a search field at the top of the sidebar and grouped icon rows. Source and screenshot: https://support.apple.com/guide/mac-help/mh15217/mac (see img/layout-2.png).
- The same page says options are "organised into settings", for example highlight colour sits under Appearance.
- NOT VERIFIED: macOS 26 specifics. Apple's page lists no 26-only layout changes. Apple's separate "Find options in System Settings" article did not return its content.

### 1.4 NN/g evidence
- Two levels of disclosure is the practical limit; deeper designs "typically have low usability because users often get lost." Frequent items go on the initial display; the advanced button must be visible and labelled; analytics should be paired with usability testing. The print dialog is NN/g's model. The page reports no quantitative results. https://www.nngroup.com/articles/progressive-disclosure/
- Tabs: "The fewer tabs, the better." Avoid stacking tab rows: it is unclear which tab the selection belongs to, and moving the selected tab next to its panel breaks spatial memory. Labels one to two words. If there are no distinct groups, use one page with subheadings. https://www.nngroup.com/articles/tabs-used-right/
- Vertical left navigation scales: it holds many top-level categories without shrinking labels. Users look at the left half of the screen most of the time. Put less important items at the bottom. Specific top-level labels reduce clicks. Expert guidance with some cited studies, no new experiment. https://www.nngroup.com/articles/vertical-nav/
- Information scent: the label is the strongest cue. Labels must be specific, clear and free of jargon or brand terms. Evidence is illustrative, not experimental. https://www.nngroup.com/articles/information-scent/
- Recognition beats recall. A menu is the classic recognition interface. Settings are a recall problem when users must remember that an option exists and what it is called (the settings link is my inference, the article does not mention settings). https://www.nngroup.com/articles/recognition-and-recall/
- Card sorting reveals users' groupings. Open sorts surface the users' own category names. Card wording biases results. NN/g says to recruit at least 15 participants. https://www.nngroup.com/articles/card-sorting-definition/ and https://www.nngroup.com/articles/card-sorting-terminology-matches/
- Search is an "escape hatch" when users are stuck, and should be present on every page because users cannot predict where they get lost. Evidence is dated (2001): replacing a link with a search box raised search use by 91%. https://www.nngroup.com/articles/search-visible-and-simple/
- Page length: in a 2018 eye-tracking study of 120 participants, about 57% of viewing time was above the fold and 74% fell in the first two screenfuls (2160 px). Attention drops sharply after the fold, though people scroll when given a reason. This is for web pages, so applying it to settings panes is my inference. https://www.nngroup.com/articles/scrolling-and-attention/
- Tooltips and info icons are for brief supplementary help only. They are hard to discover. Do not put task-critical text in them. https://www.nngroup.com/articles/tooltip-guidelines/
- Task analysis: design starts from the user's goals and tasks, with frequency and cognitive demand mapped. The page does not compare task-based with object-based grouping. https://www.nngroup.com/articles/task-analysis/

### 1.5 Real settings screens viewed (img/)
These are screenshots of vendor documentation pages, not native app windows (no desktop-control tool was available). Where a UI image is shown, that is stated.

| File | Source | What it shows |
|---|---|---|
| img/layout-1.png | https://code.visualstudio.com/docs/configure/settings | VS Code Settings editor: search bar on top, User/Workspace scope tabs, a "Commonly Used" landing page, then a category tree. |
| img/layout-2.png | https://support.apple.com/guide/mac-help/mh15217/mac | macOS System Settings: search field at sidebar top, icon sidebar in groups, detail on the right with a back/forward title bar. |
| img/layout-3.png | https://manual.raycast.com/settings | Raycast v2 Settings: sidebar with General, Launcher, Shortcuts, Keyboard, Advanced, Organizations, About, then a second group of per-extension pages. Docs text: press Cmd-F in Settings to search "a setting, a command, an extension, or just a keyword"; Cmd-Shift-comma jumps to the selected item's settings. |
| img/layout-4.png | https://obsidian.md/help/settings | Docs only, no UI image: Settings is "Options" (General, Editor, Files and links, Appearance, Hotkeys), then "Core plugins", then "Community plugins". Shows structure as text. |
| img/layout-5.png | https://linear.app/changelog/2024-12-18-personalized-sidebar | Linear's settings redesign: left sidebar, a team page that summarises each feature's state with "Select the specific setting to view and update the full configuration." Low-resolution capture. |
| img/layout-6.png | https://ia.net/writer/support/basics/settings/settings-mac | Docs for iA Writer Settings: panes named General, Library, Editor, others; the doc lists General, Library and Editor. Text only, no UI image in the capture. |

Not covered: Arc, Things, Bear, Spotify, Craft, Sonos, Xcode. Searches did not surface a usable page for these (Things and Bear results gave only fragments, Xcode's settings URL returned 404). Fragments I did find: Things keeps Quick Entry shortcuts under Settings > Quick Entry (https://culturedcode.com/things/support/articles/2249437/); Bear's Preferences has a General pane (https://blog.bear.app/2018/10/write-your-way-with-beautiful-themes-and-bear-pro/). Treat these as unverified for layout purposes.

## 2. Patterns and the evidence behind them

| Question | Finding | Source |
|---|---|---|
| Navigation container | Use a sidebar, not tabs. Lunalyr has about 15 groups; Apple recommends a toolbar of panes for small sets and sidebars for more; NN/g says tabs should be few and that stacked rows confuse. Raycast, Obsidian, Linear, System Settings all use a sidebar. | https://www.nngroup.com/articles/tabs-used-right/ , https://www.nngroup.com/articles/vertical-nav/ , https://developer.apple.com/design/human-interface-guidelines/sidebars |
| Two stacked tab rows | Avoid. Selection ownership becomes unclear and spatial memory breaks. If Lunalyr currently has two tab rows, this is probably the "lost cat" cause (INFERENCE: I have not seen the current UI). | https://www.nngroup.com/articles/tabs-used-right/ |
| Task vs object grouping | No NN/g study found that tests this directly. Practical split used by the apps viewed: group by what the user wants to change in the product (Raycast: General, Shortcuts, Advanced, plus per-extension pages; VS Code: "Commonly Used" plus a tree). Verify with a card sort of 15+ people. | https://www.nngroup.com/articles/card-sorting-definition/ , https://code.visualstudio.com/docs/configure/settings , https://manual.raycast.com/settings |
| Settings search | Add one search field at the top of the sidebar (System Settings, VS Code, Raycast all do). Search is a stuck-user escape hatch and needs to be on every page. Result click jumps to the setting. | https://support.apple.com/guide/mac-help/mh15217/mac , https://manual.raycast.com/settings , https://www.nngroup.com/articles/search-visible-and-simple/ , https://developer.apple.com/design/human-interface-guidelines/searching |
| Progressive disclosure | One level of "Advanced" per page, labelled, collapsed by default; never nested. | https://www.nngroup.com/articles/progressive-disclosure/ , https://developer.apple.com/design/human-interface-guidelines/disclosure-controls |
| Info buttons | Use only for optional explanation. The macOS Automation permission explanation is task-critical, so show it as visible text plus a button, not a tooltip. | https://www.nngroup.com/articles/tooltip-guidelines/ |
| Page length | No published limit for settings panes. NN/g shows attention concentrated in the first two screenfuls on web pages. Working rule (INFERENCE): about 2 screenfuls and no more than 6 to 8 controls visible before a disclosure or subheading. | https://www.nngroup.com/articles/scrolling-and-attention/ |
| Labels | One to two nouns users would type: "Background", "Fonts", "Lyric timing", "Shortcuts", "Music player". Avoid internal names. | https://www.nngroup.com/articles/tabs-used-right/ , https://www.nngroup.com/articles/information-scent/ |
| Frequent defaults | Anything changed often should not live in Settings at all; put it in the app surface (menu-bar menu, lyric window). Apple: settings are for "general, infrequently changed" options; task options belong in the task. | https://developer.apple.com/design/human-interface-guidelines/settings |
| Live preview must stay visible | Apple's reasoning (separate settings hides the result) supports a persistent preview, or applying changes to the real lyric window. Layout idea (INFERENCE, no source): pin a preview strip above or beside the detail pane on pages that change appearance. | https://developer.apple.com/design/human-interface-guidelines/settings |
| Remember place | Reopen on the last pane; window title shows pane name. | https://developer.apple.com/design/human-interface-guidelines/settings |

## 3. Two recommended information architectures

Both use a left sidebar with a search field on top, sidebar items of two levels at most, icons plus 1 to 2 word labels, and the window title showing the current pane. The detail pane has a pinned live-preview strip on appearance pages (INFERENCE as above).

### Rule: never more than two clicks away
Two clicks = (1) pick the sidebar page, (2) the control is visible on that page without opening a disclosure. Search reaches anything in one query. Controls placed behind an "Advanced" disclosure are three actions and so must be limited to rarely changed items.

These are always one page click and visible, never under Advanced:
Wallpaper choice, lyric layout, font and colours, lyric timing offset, music player choice, launch at login, global shortcuts.

Rationale: these are the likely high-frequency or first-visit tasks (INFERENCE from the task list; confirm with usage data, as NN/g warns analytics alone can mislead: https://www.nngroup.com/articles/progressive-disclosure/).

### IA A: task-based, six pages (shortest sidebar)
```
[Search settings]
  Look
    Background      wallpaper/background choice; visualisers; music response; animated covers
    Lyrics          lyric layout (14 layouts as a visual grid, not a list); typography, fonts, colours
  Words
    Timing & sources  timing offset; lyric sources
    Translation       romanisation; translation
  App
    Music             music player choice; macOS Automation access (status + "Open System Settings")
    Menu bar & keys   menu-bar line; global shortcuts
    General           launch at login; updates; welcome; performance/power saving; Discord Rich Presence
    Share             lyric card export
    About             version, credits
```
Counts: 9 leaf pages in 3 groups. Pro: fewest pages. Con: "General" and "Background" are mixed bags (see risk below). Layout grid on Lyrics page replaces a 14-item list: 14 items in a flat list is slow to scan, a grid with thumbnails supports recognition (https://www.nngroup.com/articles/recognition-and-recall/).

### IA B: object-based, flatter, closer to how Raycast / System Settings read
```
[Search settings]
  Appearance
    Background        wallpaper; animated covers
    Visualisers       visualisers; music response
    Lyric layout      the 14 layouts, with live preview
    Text & colour     typography, fonts, colours
  Lyrics
    Timing & sources  timing offset; lyric sources
    Translation       romanisation; translation
  Integrations
    Music player      player choice; macOS Automation access
    Discord           Rich Presence
  System
    Menu bar          menu-bar line
    Shortcuts         global shortcuts
    Performance       performance/power saving
    General           launch at login; updates; welcome
  Share
    Lyric card        export
  About
```
Counts: 13 leaf pages in 5 groups. Pro: every page is one noun a user would search for (https://www.nngroup.com/articles/information-scent/), each page is short. Con: a longer sidebar; HIG says at most two levels (this has two: group headers are non-clickable, so effectively one navigable level) and recommends short labels (https://developer.apple.com/design/human-interface-guidelines/sidebars).

### Choosing between them
INFERENCE, not sourced: A wins if the sidebar must fit in a small settings window with no scroll; B wins if users search by noun. Run a 15-person open card sort on the ~15 settings groups to decide (NN/g threshold: https://www.nngroup.com/articles/card-sorting-definition/).

### Placement outside Settings (supports the two-click rule)
- Menu-bar menu (not Settings): the music player toggle, current layout switch, timing nudge. Apple: task-specific options belong where the task is (https://developer.apple.com/design/human-interface-guidelines/settings).
- Per-pane "Advanced" disclosure: performance caps, Discord detail fields, diagnostic toggles. One per page (https://developer.apple.com/design/human-interface-guidelines/disclosure-controls).

## 4. Gaps and caveats
- I did not see Lunalyr's current Settings UI; the "two stacked tab rows" cause is a hypothesis based on how the task was described.
- Screenshots 4 and 6 are documentation pages with text only, not UI shots; 5 is low resolution.
- No source found that proves task-based beats object-based grouping, nor a numeric page-length limit for settings.
- Page-length and attention numbers come from web pages, not settings panes.
