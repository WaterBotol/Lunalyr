import SwiftUI

/// Text colour, strength, speed and the custom gradient.
@MainActor
struct LyricColourSection: View {
    @Model(\.colours) private var options

    var body: some View {
        ThemedSection("Colours") {
            ChoiceMenu("Colour", selection: $options.mode, selectedTitle: \.title) {
                ForEach(LyricColourMode.choices) { Text($0.title).tag($0) }
            }
            LyricColourSwatch()
            ValueSlider("Colour strength", value: $options.strength, in: LyricColourOptions.strengthRange,
                        step: 0.01, minimumLabel: "Neutral", maximumLabel: "Vivid", format: SettingsFormat.percent)
            InspectorCaption(options.mode.detail)
            RememberedStyleNote(aspect: .colour)
            if options.mode.drifts {
                ValueSlider("Colour speed", value: $options.speed, in: LyricColourOptions.speedRange,
                            minimumLabel: "Slow", maximumLabel: "Lively", format: SettingsFormat.percent)
                    .revealRow()
            }
            if options.mode == .customGradient {
                Group {
                    ForEach(Array(options.customHex.enumerated()), id: \.offset) { index, _ in
                        LabeledContent {
                            HStack(spacing: LunalyrDesign.Spacing.small) {
                                ColorPicker("Colour \(index + 1)", selection: Binding(
                                    get: { options.colour(at: index) },
                                    set: { options.setCustom(index, to: $0) }), supportsOpacity: false)
                                    .labelsHidden()
                                if options.customHex.count > 2 {
                                    Button { options.removeCustom(at: index) } label: {
                                        Image(systemName: "minus.circle")
                                            .frame(width: LunalyrDesign.Size.minimumHitTarget - 8,
                                                   height: LunalyrDesign.Size.minimumHitTarget - 8)
                                    }
                                    .buttonStyle(.borderless)
                                    .help("Remove this colour")
                                    .accessibilityLabel("Remove colour \(index + 1)")
                                }
                            }
                        } label: {
                            Text("Colour \(index + 1)")
                        }
                    }
                    LabeledContent {
                        if options.customHex.count < 4 {
                            Button("Add Colour") { options.addCustom() }
                                .help("Add a fourth colour to the gradient")
                        }
                    } label: {
                        CustomGradientSwatch(colours: options.customColours, strength: options.strength)
                            .frame(maxWidth: 180)
                    }
                }
                .revealRow()
            }
            InspectorCaption("Text backing keeps pastels readable on bright wallpapers.", more: "Automatic text backing helps the pastels stay visible on bright wallpapers. Colours fade between shades and hold almost still with Reduce Motion on.")
            // Space, not a rule, sets the highlight apart (docs/DESIGN.md: no decorative dividers).
            LyricHighlightColourControls()
                .padding(.top, LunalyrDesign.Studio.Layout.inspectorRowSpacing)
        }
        .lunalyrReveal(options.mode)
        .lunalyrReveal(options.customHex.count)
    }
}

/// The custom gradient as the lyrics will use it: blended in OKLab and
/// lightened for a dark wallpaper.
private struct CustomGradientSwatch: View {
    let colours: [OKLab]
    let strength: Double

    var body: some View {
        let ink = LyricInk(mode: .customGradient, stops: stops, stopCount: max(1, min(4, colours.count)))
        let samples = (0...24).map { (step: Int) -> Color in
            let slot: Double = Double(step) / 24 / 0.42 * 0.999
            return Perceptual.color(Perceptual.readable(ink.gradient(slot: slot), background: 0.08))
        }
        LinearGradient(colors: samples, startPoint: .leading, endPoint: .trailing)
            .frame(height: 18)
            .clipShape(RoundedRectangle(cornerRadius: LunalyrDesign.Radius.tag, style: .continuous))
            .overlay {
                RoundedRectangle(cornerRadius: LunalyrDesign.Radius.tag, style: .continuous).strokeBorder(.quaternary)
            }
            .accessibilityHidden(true)
    }

    private var stops: LyricInkDirector.Stops {
        func at(_ index: Int) -> OKLab {
            guard !colours.isEmpty else { return .white }
            return LyricInk.applyingStrength(LyricColourOptions.clampedStrength(strength),
                to: LyricInk.pastel(colours[index % colours.count]))
        }
        return (at(0), at(1), at(2), at(3))
    }
}

/// Remember the current layout and colour for the playing song or artist.
@MainActor
struct LyricStyleMemorySection: View {
    @Model(\.styleMemory) private var memory
    @Model(\.settings) private var settings
    @Model(\.colours) private var colours
    @Model(\.nowPlaying) private var playing
    @Environment(\.undoManager) private var undoManager
    @State private var confirmsForgetAll = false

    private var chosen: StyleMemory.Style {
        .init(composition: settings.lyricComposition?.rawValue ?? "automatic", colour: colours.mode.rawValue)
    }

    var body: some View {
        ThemedSection("Remember a style") {
            Toggle("Use remembered styles", isOn: $memory.enabled)
            if let track = playing.current {
                if let active = memory.remembered(for: track) {
                    LabeledContent("This \(active.scope.rawValue) uses") {
                        Text(Self.describe(active.style))
                    }
                    InspectorCaption("Changes apply to other music until you update this.", more: "Changes above apply to other music until you update or forget this.")
                    HStack(spacing: LunalyrDesign.Spacing.small) {
                        Button("Update with the Choices Above") { memory.remember(chosen, for: track, scope: active.scope) }
                        Button("Forget") {
                            let memory = memory
                            memory.changeUndoably("Forget Remembered Style", undoManager: undoManager) {
                                memory.forget(for: track, scope: active.scope)
                            }
                        }
                            .help("Use the choices above for this \(active.scope.rawValue) too")
                    }
                } else {
                    ViewThatFits(in: .horizontal) {
                        HStack(spacing: LunalyrDesign.Spacing.small) { rememberButtons(track) }
                        VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.small) { rememberButtons(track) }
                    }
                    if track.artist.isEmpty {
                        DisabledReason("This song names no artist, so its style can only be remembered for the song.")
                    }
                    InspectorCaption("Saves this layout and colour. A song’s own wins.", more: "Saves the layout and text colour chosen above. A song’s own style wins over its artist’s.")
                }
            } else {
                InspectorCaption("Play a song to remember a style for it.", more: "Play a song to remember a layout and colour for it or its artist.")
            }
            if !memory.songs.isEmpty || !memory.artists.isEmpty {
                LabeledContent {
                    Button("Forget All") { confirmsForgetAll = true }
                        .controlSize(.small)
                        .help("Forget every remembered style")
                        .confirmationDialog("Forget every remembered style?", isPresented: $confirmsForgetAll) {
                            Button("Forget All", role: .destructive) {
                                let memory = memory
                                memory.changeUndoably("Forget All Remembered Styles", undoManager: undoManager) {
                                    memory.forgetAll()
                                }
                            }
                        } message: {
                            Text("\(Self.rememberedCount(songs: memory.songs.count, artists: memory.artists.count)). Those songs and artists then use the choices above. Edit › Undo brings them back.")
                        }
                } label: {
                    Text(Self.rememberedCount(songs: memory.songs.count, artists: memory.artists.count))
                        .lunalyrText(.caption)
                }
            }
        }
    }

    @ViewBuilder private func rememberButtons(_ track: NowPlayingMonitor.Track) -> some View {
        Button("Remember for This Song") { memory.remember(chosen, for: track, scope: .song) }
        Button(track.artist.isEmpty ? "Remember for Artist" : "Remember for \(track.artist)") {
            memory.remember(chosen, for: track, scope: .artist)
        }
        .disabled(track.artist.isEmpty)
    }

    /// "1 song · 2 artists remembered".
    static func rememberedCount(songs: Int, artists: Int) -> String {
        "\(songs) \(songs == 1 ? "song" : "songs") · \(artists) \(artists == 1 ? "artist" : "artists") remembered"
    }

    static func describe(_ style: StyleMemory.Style) -> String {
        let layout = style.composition == "automatic" ? "Match the music" : style.layout?.title ?? "Match the music"
        return [layout, style.colourMode?.title].compactMap { $0 }.joined(separator: " · ")
    }
}

/// Under the Layout and Colour pickers: a style remembered for the playing
/// song or artist is what the wallpaper shows, so the picker would otherwise
/// look broken. Update saves the picker's choice there; Forget drops this
/// part of the memory.
@MainActor
struct RememberedStyleNote: View {
    enum Aspect { case layout, colour }
    let aspect: Aspect
    @Environment(\.undoManager) private var undoManager
    @Model(\.styleMemory) private var memory
    @Model(\.settings) private var settings
    @Model(\.colours) private var colours
    @Model(\.nowPlaying) private var playing

    var body: some View {
        if let track = playing.current, let active = memory.remembered(for: track),
           let name = Self.rememberedName(aspect, in: active.style) {
            VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.small) {
                Label("This \(active.scope.rawValue) uses \(name), remembered for it. The choice above applies to other music.",
                      systemImage: "bookmark.fill")
                    .labelStyle(CaptionLabelStyle())
                HStack(spacing: LunalyrDesign.Spacing.small) {
                    Button("Update") { memory.remember(updated(active.style), for: track, scope: active.scope) }
                        .help("Remember the choice above for this \(active.scope.rawValue) instead")
                    Button("Forget") { forget(track, active) }
                        .help("Use the choice above for this \(active.scope.rawValue) too")
                }
                .controlSize(.small)
                .padding(.leading, 20)
            }
            .accessibilityElement(children: .contain)
        }
    }

    /// The remembered part this note is about, by name, or nil if the
    /// memory leaves it to the general setting.
    static func rememberedName(_ aspect: Aspect, in style: StyleMemory.Style) -> String? {
        switch aspect {
        case .layout:
            guard let raw = style.composition else { return nil }
            return raw == "automatic" ? "Match the music" : style.layout?.title ?? "Match the music"
        case .colour:
            return style.colourMode?.title
        }
    }

    private func updated(_ style: StyleMemory.Style) -> StyleMemory.Style {
        var style = style
        switch aspect {
        case .layout: style.composition = settings.lyricComposition?.rawValue ?? "automatic"
        case .colour: style.colour = colours.mode.rawValue
        }
        return style
    }

    private func forget(_ track: NowPlayingMonitor.Track, _ active: (style: StyleMemory.Style, scope: StyleMemory.Scope)) {
        let memory = memory
        memory.changeUndoably("Forget Remembered Style", undoManager: undoManager) {
            forgetPart(track, active)
        }
    }

    private func forgetPart(_ track: NowPlayingMonitor.Track, _ active: (style: StyleMemory.Style, scope: StyleMemory.Scope)) {
        var style = active.style
        switch aspect {
        case .layout: style.composition = nil
        case .colour: style.colour = nil
        }
        if style.composition == nil && style.colour == nil {
            memory.forget(for: track, scope: active.scope)
        } else {
            memory.remember(style, for: track, scope: active.scope)
        }
    }
}

/// What a status row in Share says: sharing's relay state, or whether
/// Discord is there.
@MainActor
struct ShareStatusRow: Hashable {
    let text: String
    let symbol: String
    let status: LunalyrStatus

    /// Listen-along sharing, worded as the menu-bar panel words it. Nil is
    /// sharing switched on with nothing started yet.
    static func sharing(_ state: ListenAlong.SharingState?) -> ShareStatusRow {
        let state = state ?? .waitingForMusic
        let text = MenuBarListenAlong.sharingTitle(state), symbol = MenuBarListenAlong.sharingSymbol(state)
        switch state {
        case .waitingForMusic: return .init(text: text, symbol: symbol, status: .neutral)
        case .retrying: return .init(text: text, symbol: symbol, status: .warning)
        case .live: return .init(text: text, symbol: symbol, status: .success)
        }
    }

    /// Rich Presence's connection. Shown only while it is switched on, so
    /// `.off` is the moment before it starts.
    static func discord(_ status: DiscordPresence.ConnectionStatus) -> ShareStatusRow {
        switch status {
        case .off, .waitingForDiscord:
            return .init(text: "Waiting for Discord", symbol: "hourglass", status: .neutral)
        case .connected(let name?) where !name.trimmingCharacters(in: .whitespaces).isEmpty:
            return .init(text: "Connected as \(name)", symbol: "checkmark.circle.fill", status: .success)
        case .connected:
            return .init(text: "Connected", symbol: "checkmark.circle.fill", status: .success)
        }
    }

    /// Under the Discord row while Discord hasn't answered.
    static let waitingNote = "Open the Discord desktop app; Lunalyr connects once it\u{2019}s running."
}

/// A status row's chip, which cross-fades as the state changes.
private struct ShareStatusChip: View {
    let row: ShareStatusRow
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        StatusChip(row.status, row.text, symbol: row.symbol)
            .id(row)
            .transition(.opacity)
            .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion), value: row)
    }
}

/// Whether Discord is there for the presence.
@MainActor
struct DiscordConnectionStatus: View {
    let status: DiscordPresence.ConnectionStatus

    var body: some View {
        let row = ShareStatusRow.discord(status)
        LabeledContent {
            ShareStatusChip(row: row)
        } label: {
            Text("Connection")
        }
        if row.status != .success {
            InspectorCaption(ShareStatusRow.waitingNote)
                .revealRow()
        }
    }
}

/// Sharing's state on the relay -- waiting for music, retrying, or how many
/// follow the room -- with its link.
@MainActor
struct ListenSharingStatus: View {
    @ObservedObject var listen: ListenAlong

    var body: some View {
        LabeledContent {
            HStack(spacing: LunalyrDesign.Spacing.small) {
                ShareStatusChip(row: ShareStatusRow.sharing(listen.sharingState))
                if let link = listen.webPageURL {
                    Button("Copy Link") {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString(link.absoluteString, forType: .string)
                    }
                }
            }
        } label: {
            Text("Sharing")
        }
    }
}

/// Discord & lyric card: the places a song is shared from.
@MainActor
struct ShareInspector: View {
    @Model(\.extras) private var extras
    @Model(\.nowPlaying) private var playing

    var body: some View {
        ThemedForm {
            ThemedSection("Discord") {
                Toggle("Show what I'm listening to on Discord", isOn: $extras.showsDiscordPresence)
                InspectorCaption("Needs the Discord desktop app. Shows the line being sung.",
                                 more: "Needs the Discord desktop app. Your status shows what you choose below; your profile adds the song, artist, album, cover and progress, with Play this song and Get Lunalyr buttons for others (Discord never shows you your own). Nothing is shared while this is off, and it clears when you pause.")
                if extras.showsDiscordPresence {
                    Group {
                        DiscordConnectionStatus(status: extras.discordStatus)
                        ChoiceSegments("Status shows", selection: $extras.discordStatusText,
                                       cases: DiscordStatusText.allCases, label: \.title)
                        CheckboxList {
                            Toggle("Show the current lyric", isOn: $extras.discordShowsLyric)
                            Toggle(isOn: $extras.discordShowsCover) {
                                CheckboxLabel("Show the album cover", caption: "Looks the cover up by song and artist.",
                                              more: "The cover comes from Apple’s public search, which is sent only the song title and artist. Without a match, the Lunalyr icon is shown. The Play this song button uses the same search for Apple Music songs; with both off, nothing is sent to Apple.")
                            }
                            .accessibilityLabel("Show the album cover")
                            Toggle("Show the progress bar", isOn: $extras.discordShowsProgress)
                            Toggle(isOn: $extras.discordShowsSongButton) {
                                CheckboxLabel("Show a Play this song button", caption: "Opens the song on Spotify, Apple Music, YouTube Music and more.",
                                              more: "Links to the song’s song.link page, which lists it on every service. Found from Spotify’s own link, or, for Apple Music, from the same Apple search as the cover (sent only the song title and artist), and only when the result is exactly that song. Until you change it, this follows Show the album cover. Others see it; Discord never shows you your own buttons.")
                            }
                            .accessibilityLabel("Show a Play this song button")
                            Toggle("Show a Get Lunalyr button", isOn: $extras.discordShowsButton)
                            Toggle("Keep showing while paused", isOn: $extras.discordShowsWhenPaused)
                        }
                        ValueSlider("Lyric lead", value: $extras.discordLyricLead, in: LunalyrExtras.discordLeadRange, step: 0.5,
                                    minimumLabel: "With the music", maximumLabel: "Earlier",
                                    format: { String(format: "%.1f s earlier", $0) })
                        InspectorCaption("How far ahead of the music Discord shows the line.")
                    }
                    .revealRow()
                }
                if extras.listenAlongAvailable {
                    Group {
                        Toggle("Let friends listen along", isOn: $extras.listenSharing)
                        InspectorCaption("Friends with Lunalyr play what you play, in time, on their own Music or Spotify.",
                                         more: "Friends with Lunalyr play what you play, in time, on their own Music or Spotify (Music friends: songs in their library). While this is on, your relay is sent the song, artist, album, its Spotify ID, play and pause, the position, your Discord display name (while Discord is on) and, with Share lyrics with listeners, the lyric lines with their timing. Apple\u{2019}s ID is included only when the Discord cover or song button already looked the song up; sharing never sends titles to Apple. The relay keeps only the latest state, and anyone with the link or code can read it. Off when you turn this off or quit.")
                        if extras.listenSharing {
                            Group {
                                ListenSharingStatus(listen: .shared)
                                Toggle("Share lyrics with listeners", isOn: $extras.listenSharesLyrics)
                                InspectorCaption("Sends the lyric text with its timing, so friends and the web page can show it.",
                                                 more: "The lines you see, with their timing, go to your relay once per song. Anyone with the link or code can read them. Turn this off to share only the song.")
                                if extras.showsDiscordPresence {
                                    Toggle("Show a Join button on Discord", isOn: $extras.discordOffersJoin)
                                    InspectorCaption("Replaces your Discord buttons while you share.",
                                                     more: "Discord can\u{2019}t show a Join button and link buttons on the same profile. With this on, friends get Join (it opens their Lunalyr), but not Play this song or Get Lunalyr. With it off, they get Listen along (the web page) and Get Lunalyr; friends with Lunalyr can paste the link into its menu.")
                                }
                            }
                            .revealRow()
                        }
                    }
                    .revealRow()
                }
            }
            ThemedSection("Listen along") {
                HStack(spacing: 10) {
                    Text(Self.relayLabel)
                        .frame(width: 52, alignment: .leading)
                        .accessibilityHidden(true)
                    ListenRelayField(relay: $extras.listenRelayURL)
                        .accessibilityLabel(Self.relayLabel)
                }
                InspectorCaption("Listen along needs a relay you run. Leave it empty to turn listen-along off.",
                                 more: "The address of your Lunalyr listen-along relay (a small Cloudflare Worker). With none, listen-along is hidden and nothing connects.")
            }
            ThemedSection(Self.cardSection) {
                ChoiceMenu("Card layout", selection: $extras.lyricCardComposition,
                           selectedTitle: { $0?.title ?? "Follow wallpaper" }) {
                    Text("Follow wallpaper").tag(Optional<LyricComposition>.none)
                    LyricCompositionOptions()
                }
                LabeledContent {
                    Button(Self.openCardTitle) { extras.copyLyricCard() }
                        .controlSize(.small)
                        .disabled(playing.current == nil)
                } label: {
                    Text("Lyric card")
                }
                InspectorCaption("Pick lines, a shape and a format for a shareable card.", more: "Pick any lines from the song, the card’s shape and whether to make an MP4, GIF or PNG. Synced lyrics play in order.")
                if playing.current == nil {
                    DisabledReason("A card can be made while a song is playing.")
                        .revealRow()
                }
            }
        }
        // Discord's and listen-along's options glide in under their switches.
        .lunalyrReveal(extras.showsDiscordPresence)
        .lunalyrReveal(extras.discordStatus)
        .lunalyrReveal(extras.listenSharing)
        .lunalyrReveal(extras.listenAlongAvailable)
        .lunalyrReveal(playing.current == nil)
    }

    /// The relay field's label (it had none before).
    static let relayLabel = "Relay"
    /// The section for the lyric card (was "Share").
    static let cardSection = "Lyric card"
    /// The one name for the action, here, in the menu-bar panel and on the shortcut.
    static let openCardTitle = "Open Lyric Card\u{2026}"
}

/// Menu bar & shortcuts: hiding and pausing, the menu-bar line and global
/// shortcuts.
@MainActor
struct ExtrasInspector: View {
    @Model(\.extras) private var extras
    @Model(\.hotkeys) private var hotkeys
    @Model(\.nowPlaying) private var playing

    var body: some View {
        ThemedForm {
            ThemedSection("Lyrics on screen") {
                Toggle("Hide lyrics", isOn: $extras.lyricsHidden)
                Toggle("Hide lyrics while a display is mirrored", isOn: $extras.hidesWhileMirroring)
                InspectorCaption(Self.mirroringNote(mirroring: extras.mirroring, hides: extras.hidesWhileMirroring,
                                                    showsAnyway: extras.showsWhileMirrored))
                if extras.hiddenByMirroring {
                    Button(LunalyrExtras.Titles.showLyricsAnyway) { extras.showLyrics() }
                        .controlSize(.small)
                        .help("Shows the lyrics on every display until mirroring ends")
                        .revealRow()
                }
                Toggle("Pause wallpaper", isOn: $extras.wallpaperPaused)
                InspectorCaption("Freezes every wallpaper until you resume.", more: "Freezes every desktop wallpaper, lyrics included, until you resume it or Lunalyr next starts. The preview here keeps moving, so changes can still be judged.")
            }
            ThemedSection("Menu bar") {
                MenuBarLinePicture(shows: extras.showsMenuBarLine && !extras.lyricsHidden,
                                   length: Int(extras.menuBarLength.rounded()))
                Toggle("Show the current line in the menu bar", isOn: $extras.showsMenuBarLine)
                if extras.showsMenuBarLine {
                    Group {
                        ValueSlider("Longest line", value: $extras.menuBarLength, in: LunalyrExtras.menuBarLengthRange, step: 1,
                                    minimumLabel: "Short", maximumLabel: "Long",
                                    format: { "\(Int($0.rounded())) characters" })
                        InspectorCaption("Follows Hide lyrics and Pause.", more: "Hidden lyrics stay hidden there too, and a paused wallpaper says so.")
                    }
                    .revealRow()
                }
            }
            ThemedSection("Keyboard shortcuts") {
                Toggle("Use global shortcuts", isOn: $extras.shortcutsEnabled)
                ChoiceMenu("Keys", selection: $hotkeys.modifiers, selectedTitle: \.title) {
                    ForEach(GlobalHotkeys.Modifiers.allCases) { Text($0.title).tag($0) }
                }
                CheckboxList {
                    ForEach(GlobalHotkeys.shortcuts, id: \.action) { shortcut in
                        shortcutRow(shortcut.action)
                    }
                }
                InspectorCaption("They work from any app; switch off any you need elsewhere.", more: Self.shortcutsCaption)
                    .environment(\.captionFollowsControl, false)
                if !hotkeys.unavailable.isEmpty && extras.shortcutsEnabled {
                    InspectorCaption("A shortcut another app uses is left to that app.", more: "A shortcut another app already uses is left to that app. Switch it off there, or choose other keys above.")
                        .environment(\.captionFollowsControl, false)
                }
                if NSWorkspace.shared.isVoiceOverEnabled && hotkeys.modifiers == .controlOptionCommand {
                    StatusBanner(.warning, title: "VoiceOver uses these keys",
                                 message: "With Control-Option as its VO keys, ⌃⌥⌘ and a letter is also a VoiceOver command. Choose ⌃⇧⌘ above, or switch off the shortcuts you need for VoiceOver.")
                }
            }
        }
        .lunalyrReveal(extras.showsMenuBarLine)
        .lunalyrReveal(extras.hiddenByMirroring)
    }

    /// One shortcut: a checkbox with what it does, its keys right-aligned
    /// (ink while it works, ink2 while it doesn't), and a warning line
    /// under it while another app holds the keys.
    private func shortcutRow(_ action: GlobalHotkeys.Action) -> some View {
        let on = hotkeys.isSwitchedOn(action)
        let taken = extras.shortcutsEnabled && hotkeys.unavailable.contains(action)
        let live = extras.shortcutsEnabled && on && !taken
        return VStack(alignment: .leading, spacing: 2) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Toggle(action.title, isOn: Binding(get: { hotkeys.isSwitchedOn(action) },
                                                   set: { hotkeys.setSwitchedOn($0, for: action) }))
                    .accessibilityValue(on ? "On, \(hotkeys.label(for: action) ?? "")" : "Off")
                Text(hotkeys.label(for: action) ?? "")
                    .font(LunalyrDesign.Studio.TypeScale.row)
                    .foregroundStyle(live ? Color.studio(.ink) : Color.studio(.ink2))
                    .fixedSize()
                    .accessibilityHidden(true)
            }
            if taken {
                WarningCaption("In use by another app")
                    .padding(.leading, ThemedCheckboxBox.size + 8)
            }
        }
    }

    /// Under the shortcuts. The rows above show each combination as the
    /// keyboard in use types it (⌃⌥⌘Ü for ⌃⌥⌘[ on a German one), so this
    /// names no key of Lunalyr's: only the arrows it leaves alone. The card
    /// shortcut is named as its row names it (`GlobalHotkeys`), and does what
    /// the Lyric card section's button does.
    static let shortcutsCaption = "They work from any app; switch off any you need for something else. \(GlobalHotkeys.Action.copyCard.title) opens the lyric card window for the line playing, like Open Lyric Card\u{2026} in \(SettingsPage.share.title). Sync changes are saved for this song’s lyric source, like the song offset in \(SettingsPage.lyrics.title). None of them use the arrow keys, which window managers such as Rectangle use with ⌃⌥⌘ to move windows between displays."

    /// The caption under "Hide lyrics while a display is mirrored".
    static func mirroringNote(mirroring: Bool, hides: Bool, showsAnyway: Bool) -> String {
        guard mirroring else { return "For presenting: mirroring to a projector or TV hides the lyrics until it ends." }
        guard hides else { return "A display is mirrored now; the lyrics stay on every display." }
        return showsAnyway
            ? "A display is mirrored now, and the lyrics are shown anyway until mirroring ends."
            : "A display is mirrored now, so the lyrics are hidden on every display until mirroring ends."
    }
}

/// The menu bar as it will look: the sample line beside the clock, cut to
/// Longest line, or nothing while the line is off. A static drawing.
private struct MenuBarLinePicture: View {
    let shows: Bool
    let length: Int

    static let sample = SampleLyric.lines.map { $0.joined(separator: " ") }.joined(separator: " ")

    /// The sample cut to `length` characters, with an ellipsis when cut.
    static func line(length: Int) -> String {
        let text = sample
        guard text.count > length, length > 1 else { return text }
        return String(text.prefix(length - 1)).trimmingCharacters(in: .whitespaces) + "\u{2026}"
    }

    var body: some View {
        ZStack(alignment: .top) {
            SampleWallpaper()
            HStack(spacing: 10) {
                Image(systemName: "apple.logo").font(.system(size: 12))
                Text("Finder").font(.system(size: 12, weight: .bold))
                Spacer(minLength: 8)
                if shows {
                    Text(Self.line(length: length))
                        .font(.system(size: 12, weight: .medium))
                        .lineLimit(1)
                }
                Image(systemName: "wifi").font(.system(size: 11))
                Text("Fri 21:40").font(.system(size: 12, weight: .medium))
            }
            .foregroundStyle(.white)
            .padding(.horizontal, 12)
            .frame(height: 26)
            .background(Color.black.opacity(0.28))
        }
        .frame(maxWidth: ThemedPageMetrics.column)
        .frame(height: 62)
        .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(shows ? "The menu bar with the current line" : "The menu bar without the line")
    }
}

/// Shown on the Romanisation page.
@MainActor
struct TranslationSection: View {
    @Model(\.extras) private var extras

    var body: some View {
        ThemedSection("Translations") {
            Toggle("Show translations under lyrics", isOn: $extras.showsTranslations)
            InspectorCaption("Shown when the lyric source has a translation.", more: "Appears when the lyric source includes a translation for a line, such as bilingual LRC files. Lyrics are never sent anywhere to be translated.")
            #if canImport(Translation)
            if #available(macOS 15.0, *) { MachineTranslationControls() }
            #endif
        }
    }
}

/// Above the preview, what the desktop is doing that the preview does not
/// show by itself, with the way back: a paused wallpaper (the preview keeps
/// moving), or lyrics that mirroring hides on every display. One at a time,
/// a frozen desktop first.
@MainActor
struct WallpaperStateNote: View {
    @Model(\.extras) private var extras

    enum State: Equatable {
        case paused, hiddenByMirroring

        var symbol: String { self == .paused ? "pause.rectangle.fill" : "rectangle.on.rectangle" }
        var text: String { self == .paused ? "Wallpaper paused" : "Lyrics hidden while mirroring" }
        /// Short, beside the text; VoiceOver hears the whole action.
        var button: String { self == .paused ? "Resume" : "Show Anyway" }
        var action: String {
            self == .paused ? LunalyrExtras.Titles.resumeWallpaper : LunalyrExtras.Titles.showLyricsAnyway
        }
        var help: String {
            self == .paused ? "Let the desktop wallpaper move again"
                : "Show the lyrics on every display until mirroring ends"
        }
        /// A paused wallpaper is a mode the user chose; hidden lyrics a
        /// plain state.
        var status: LunalyrStatus { self == .paused ? .info : .neutral }
    }

    static func state(paused: Bool, hiddenByMirroring: Bool) -> State? {
        paused ? .paused : hiddenByMirroring ? .hiddenByMirroring : nil
    }

    var body: some View {
        if let state = Self.state(paused: extras.wallpaperPaused, hiddenByMirroring: extras.hiddenByMirroring) {
            StatusChip(state.status, state.text, symbol: state.symbol) {
                Button(state.button) {
                    if state == .paused { extras.toggleWallpaperPause() } else { extras.showLyrics() }
                }
                .help(state.help)
                .accessibilityLabel(state.action)
            }
        }
    }
}
