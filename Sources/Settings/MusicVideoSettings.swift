import SwiftUI

/// Settings › Wallpaper › Music video: what is showing (or why not, quietly,
/// here rather than on the desktop), online lookups and what they send,
/// which displays show it and its dimming. The rest is under More options
/// (`MusicVideoMoreOptions`).
@MainActor
struct MusicVideoSection: View {
    @Model(\.settings) private var s
    @ObservedObject private var controller = MusicVideoController.shared
    @ObservedObject private var visualizer = VisualizerPreferences.shared
    @ObservedObject private var screens = MainDisplayMonitor.shared

    var body: some View {
        if let note = MusicVideoController.note(for: controller.status, online: s.musicVideoOnline,
                                                fallback: s.musicVideoFallback.title) {
            InspectorCaption(note)
        }
        Toggle("Search YouTube for videos", isOn: $s.musicVideoOnline)
        InspectorCaption(Self.privacyCaption, more: Self.privacyDetail)
        displayChoice
        ValueSlider("Video dimming", value: $s.musicVideoDim, in: Settings.musicVideoDimRange, step: 0.01,
                    format: SettingsFormat.percent)
    }

    /// "Show the video on", beside dimming and blur.
    @ViewBuilder private var displayChoice: some View {
        let heavy = visualizer.heavyScenesOnMainDisplayOnly
        let connected = screens.displays
        ChoiceMenu("Show the video on", selection: $s.musicVideoDisplays,
                   selectedTitle: { MusicVideoDisplays.title($0, heavyScenesOnMainDisplayOnly: heavy, connected: connected) }) {
            ForEach(MusicVideoDisplays.options(selected: s.musicVideoDisplays, connected: connected), id: \.self) { choice in
                Text(MusicVideoDisplays.title(choice, heavyScenesOnMainDisplayOnly: heavy, connected: connected)).tag(choice)
            }
        }
        if let caption = MusicVideoDisplays.caption(s.musicVideoDisplays, heavyScenesOnMainDisplayOnly: heavy,
                                                    connected: connected, fallback: s.musicVideoFallback.title) {
            InspectorCaption(caption)
        }
    }

    static let framingCaption = "Fill the screen crops the video’s edges. Show the whole video fits it inside the screen, with a blurred copy of the cover in the space around it."
    static let framingDetail = "Filled YouTube videos are zoomed a little further so YouTube’s title and logo stay off-screen. Shown whole, nothing is cut off: a wider video leaves space above and below, a squarer one (4:3) at the sides. To leave out YouTube’s own black bars, Lunalyr asks YouTube for the video’s shape (only its ID is sent). Shown whole, a YouTube video appears as soon as it plays; while YouTube draws its title and buttons over the first seconds, those strips are covered with the video’s blurred still."
    static let timingDetail = "Official videos often open with a scene, or are a different edit from the album track. A video longer than the song is guessed to have that much intro (Estimated); when searching, an official video as long as the song is preferred, as it needs no guess. Tap to sync: click it, then press any key (or Tap now) the moment the singing starts in the video; later in the song, tap when the next line is sung in the video. Tap again to refine: the taps are averaged. A tap, nudge or Align is kept for the video it was made on. A YouTube video’s captions, loaded unseen by its player, are matched with the song’s timed lyrics; when enough lines agree, that timing (From captions) is used over the estimate. Your own timing for the video (a tap, nudge or Align, whenever it was made) always wins over captions and is kept every time it plays; Reset forgets it, so the timing from captions applies again. Without either, the estimate is used."
    static let privacyCaption = "Off by default. When on, the song’s title and artist are sent to YouTube to find its official video, which then streams from YouTube."
    static let privacyDetail = "Videos in your Music library are always tried first and need nothing sent. With this on, Lunalyr searches YouTube for “artist title official music video”, then, if that finds nothing official, for “artist title” alone, and picks the artist’s own official upload. The video plays muted in YouTube’s privacy-enhanced player, and each song’s answer is remembered on this Mac so it is searched for once. A link you paste for a song plays even with this off; only that video is loaded from YouTube. Search YouTube… works with this off too: it sends only what is in its search box, when you search, and loads the results’ thumbnails."
}

/// Music video's rarer rows, under More options: a pasted link or a
/// search for this song's video, opening it on YouTube, this song's video
/// timing, what shows without a video, blur and framing.
@MainActor
struct MusicVideoMoreOptions: View {
    @Model(\.settings) private var s
    @ObservedObject private var controller = MusicVideoController.shared

    var body: some View {
        PastedVideoLink(controller: controller)
        if controller.showingYouTubeID != nil {
            Button("Open video on YouTube") { controller.openOnYouTube() }
                .buttonStyle(.link)
                .help("Opens the video in your browser at the song’s position")
        }
        LabeledContent("Video timing") {
            VStack(alignment: .trailing, spacing: 4) {
                VideoTimingNudge(offset: controller.currentOffset, enabled: controller.hasSong,
                                 manual: controller.offsetOrigin == .manual,
                                 nudge: { controller.nudgeOffset(by: $0) }, reset: { controller.resetOffset() })
                VideoTimingOrigin(label: MusicVideoController.timingLabel(origin: controller.offsetOrigin),
                                  tapTitle: MusicVideoTapSync.buttonTitle(controller.tapState),
                                  canTap: controller.canTapSync, tap: { controller.tapSyncButton() })
            }
        }
        if let note = MusicVideoTapSync.note(controller.tapState) {
            InspectorCaption(note)
        }
        InspectorCaption("For this song: how far into the video the song begins. Set it with Tap to sync, Align or the nudges.",
                         more: MusicVideoSection.timingDetail)
        ChoiceMenu("Without a video", selection: $s.musicVideoFallback, selectedTitle: \.title) {
            ForEach(Settings.BackgroundMode.musicVideoFallbacks) { Text($0.title).tag($0) }
        }
        ValueSlider("Video blur", value: $s.musicVideoBlur, in: Settings.musicVideoBlurRange, step: 0.5,
                    format: { String(format: "%.0f pt", $0) })
        ChoiceMenu("YouTube videos", selection: $s.musicVideoFraming, selectedTitle: \.title) {
            ForEach(MusicVideoFraming.allCases) { Text($0.title).tag($0) }
        }
        ChoiceMenu("Library videos", selection: $s.libraryVideoFraming, selectedTitle: \.title) {
            ForEach(MusicVideoFraming.allCases) { Text($0.title).tag($0) }
        }
        InspectorCaption(MusicVideoSection.framingCaption, more: MusicVideoSection.framingDetail)
    }
}

/// A YouTube link for the playing song, for when search finds nothing or
/// picks the wrong video.
@MainActor
private struct PastedVideoLink: View {
    @ObservedObject var controller: MusicVideoController
    @State private var text = ""
    @State private var rejected = false

    var body: some View {
        // Stacked: the field takes the panel's width and the buttons sit
        // under it, so the row never makes the panel wider than the window.
        VStack(alignment: .leading, spacing: 4) {
            Text("YouTube link")
            TextField("Paste a link for this song", text: $text)
                .textFieldStyle(.roundedBorder)
                .frame(maxWidth: .infinity)
                .onSubmit(use)
                .accessibilityLabel("YouTube link for this song")
            HStack(spacing: LunalyrDesign.Spacing.small) {
                Button("Use", action: use)
                    .disabled(text.trimmingCharacters(in: .whitespaces).isEmpty || !controller.hasSong)
                Button("Search…") { MusicVideoSearchWindowController.shared.show() }
                    .help("Search YouTube for this song and pick the video to use")
                    .accessibilityLabel("Search YouTube…")
                if controller.pastedLink != nil {
                    Button("Clear") { controller.clearPastedLink(); text = "" }
                        .help("Stop using the pasted link for this song")
                }
            }
            .controlSize(.small)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .disabled(!controller.hasSong)
        if rejected {
            DisabledReason("That isn’t a YouTube video link.")
        } else if let link = controller.pastedLink {
            InspectorCaption("This song uses \(link).")
            if controller.pastedLooksStill {
                InspectorCaption(MusicVideoController.pastedStillNote)
            }
        }
    }

    private func use() {
        rejected = !controller.usePastedLink(text)
        if !rejected { text = "" }
    }
}

/// Earlier and later for the playing song's video, in half seconds and
/// five-second steps (intros are long).
struct VideoTimingNudge: View {
    let offset: Double
    var enabled = true
    /// The offset is the user's own, so Reset has something to undo.
    var manual = true
    let nudge: (Double) -> Void
    let reset: () -> Void

    var body: some View {
        HStack(spacing: 4) {
            // A positive step brings the video's moments sooner, as the
            // menu bar's Video earlier does (`MusicVideoController.videoEarlier`).
            Button("−5") { nudge(-5) }.help("Video 5 seconds later: for a video that runs ahead of the music")
                .accessibilityLabel("Video 5 seconds later")
            Button("−0.5") { nudge(-0.5) }.help("Video half a second later: for a video that runs ahead of the music")
                .accessibilityLabel("Video half a second later")
            Text(MusicVideoTiming.format(offset))
                .lunalyrText(.value)
                .foregroundStyle(offset == 0 ? .secondary : .primary)
                .frame(minWidth: 56)
                .accessibilityHidden(true)
            Button("+0.5") { nudge(0.5) }.help("Video half a second earlier: skips more of its intro, for a video that lags the music")
                .accessibilityLabel("Video half a second earlier")
            Button("+5") { nudge(5) }.help("Video 5 seconds earlier: skips more of its intro, for a video that lags the music")
                .accessibilityLabel("Video 5 seconds earlier")
            Button(action: reset) { Image(systemName: "arrow.counterclockwise") }
                .help("Drop your nudge: back to the estimate")
                .accessibilityLabel("Reset video timing")
                .disabled(!manual)
        }
        .controlSize(.small)
        .disabled(!enabled)
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Video timing for this song")
        .accessibilityValue(MusicVideoTiming.format(offset))
    }
}

/// Where the video timing came from, and Tap to sync.
@MainActor
struct VideoTimingOrigin: View {
    let label: String?
    var tapTitle = "Tap to sync"
    var canTap = false
    var tap: () -> Void = {}

    var body: some View {
        HStack(spacing: 6) {
            if let label {
                Text(label).font(.caption).foregroundStyle(.secondary)
            }
            Button(tapTitle, action: tap)
                .controlSize(.small)
                .disabled(!canTap)
                .help("Click, then press any key the moment the singing starts in the video")
        }
    }
}
