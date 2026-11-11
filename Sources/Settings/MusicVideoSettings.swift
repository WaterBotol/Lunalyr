import SwiftUI

/// Settings › Wallpaper › Music video: what is showing (or why not, quietly,
/// here rather than on the desktop), online lookups and what they send,
/// the fallback, this song's video timing, and dimming and blur.
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
        if controller.showingYouTubeID != nil {
            Button("Open video on YouTube") { controller.openOnYouTube() }
                .buttonStyle(.link)
                .help("Opens the video in your browser at the song’s position")
        }
        Toggle("Search YouTube for videos", isOn: $s.musicVideoOnline)
        InspectorCaption(Self.privacyCaption, more: Self.privacyDetail)
        ChoiceMenu("Without a video", selection: $s.musicVideoFallback, selectedTitle: \.title) {
            ForEach(Settings.BackgroundMode.musicVideoFallbacks) { Text($0.title).tag($0) }
        }
        PastedVideoLink(controller: controller)
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
                         more: Self.timingDetail)
        displayChoice
        ValueSlider("Video dimming", value: $s.musicVideoDim, in: Settings.musicVideoDimRange, step: 0.01,
                    format: SettingsFormat.percent)
        ValueSlider("Video blur", value: $s.musicVideoBlur, in: Settings.musicVideoBlurRange, step: 0.5,
                    format: { String(format: "%.0f pt", $0) })
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

    static let timingDetail = "Official videos often open with a scene, or are a different edit from the album track. A video longer than the song is guessed to have that much intro (Estimated); when searching, an official video as long as the song is preferred, as it needs no guess. Tap to sync: click it, then press any key (or Tap now) the moment the singing starts in the video; later in the song, tap when the next line is sung in the video. Tap again to refine: the taps are averaged. A tap, nudge or Align is kept for the video it was made on. Reset goes back to the estimate."
    static let privacyCaption = "Off by default. When on, the song’s title and artist are sent to YouTube to find its official video, which then streams from YouTube."
    static let privacyDetail = "Videos in your Music library are always tried first and need nothing sent. With this on, Lunalyr searches YouTube for “artist title official music video” and picks the artist’s own official upload. The video plays muted in YouTube’s privacy-enhanced player, and each song’s answer is remembered on this Mac so it is searched for once. A link you paste for a song plays even with this off; only that video is loaded from YouTube. Search YouTube… works with this off too: it sends only what is in its search box, when you search, and loads the results’ thumbnails."
}

/// A YouTube link for the playing song, for when search finds nothing or
/// picks the wrong video.
@MainActor
private struct PastedVideoLink: View {
    @ObservedObject var controller: MusicVideoController
    @State private var text = ""
    @State private var rejected = false

    var body: some View {
        LabeledContent("YouTube link") {
            HStack(spacing: LunalyrDesign.Spacing.small) {
                TextField("Paste a link for this song", text: $text)
                    .textFieldStyle(.roundedBorder)
                    .onSubmit(use)
                    .accessibilityLabel("YouTube link for this song")
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
            Button("−5") { nudge(-5) }.help("The video five seconds earlier in the song")
                .accessibilityLabel("Video 5 seconds earlier")
            Button("−0.5") { nudge(-0.5) }.help("The video half a second earlier in the song")
                .accessibilityLabel("Video half a second earlier")
            Text(MusicVideoTiming.format(offset))
                .lunalyrText(.value)
                .foregroundStyle(offset == 0 ? .secondary : .primary)
                .frame(minWidth: 56)
                .accessibilityHidden(true)
            Button("+0.5") { nudge(0.5) }.help("Skip half a second more of the video’s intro")
                .accessibilityLabel("Video half a second later")
            Button("+5") { nudge(5) }.help("Skip five seconds more of the video’s intro")
                .accessibilityLabel("Video 5 seconds later")
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
