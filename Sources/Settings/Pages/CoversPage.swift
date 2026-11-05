import SwiftUI

/// Animated covers: Apple Music's motion artwork behind the lyrics, where it
/// plays, how it is framed, and the albums left out.
@MainActor
struct CoversPage: View {
    @Model(\.settings) private var s
    @Model(\.nowPlaying) private var playing
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    /// What covers are doing for the playing song, or why they aren't.
    private var coverStatus: String {
        guard s.useMotionArtwork else { return "Animated covers are off." }
        guard s.backgroundMode == .auto else { return "Animated covers play in Match the music wallpaper mode." }
        guard s.effectiveBackgroundMode == .auto else {
            return "\(s.powerConstraint?.reason ?? "Power saving is on"), so animated covers are paused."
        }
        guard let track = playing.current else { return WorkspaceCopy.coverIdlePrompt(followed: playing.followedPlayers) }
        if let skipped = WorkspaceCopy.coverLookupSkipped(track: track, player: playing.activePlayer) { return skipped }
        guard !s.excludesMotion(artist: track.artist, album: track.album) else { return "This album is excluded." }
        if playing.isLookingUpMotion { return "Checking Apple Music for an animated cover…" }
        return playing.motionArtwork == nil ? "No animated cover found for this album." : "Animated cover available."
    }

    /// The playing song, when an animated cover is looked up for it: only
    /// then is there an album to exclude.
    private var lookedUpTrack: NowPlayingMonitor.Track? {
        guard let track = playing.current,
              WorkspaceCopy.coverLookupSkipped(track: track, player: playing.activePlayer) == nil else { return nil }
        return track
    }

    var body: some View {
        Form {
            Section("Animated covers") {
                Toggle("Use animated covers", isOn: $s.useMotionArtwork)
                if s.useMotionArtwork && s.backgroundMode != .auto {
                    StatusBanner(.info, title: "Covers play with Match the music",
                                 message: "The wallpaper is \(s.backgroundMode.title) now, so animated covers don’t show.",
                                 symbol: "play.rectangle.fill") {
                        Button("Use Match the music") { s.backgroundMode = .auto }
                            .accessibilityLabel("Use Match the music wallpaper")
                            .help("Switch the wallpaper to Match the music, where animated covers play")
                    }
                } else {
                    Label(coverStatus, systemImage: "play.rectangle")
                        .labelStyle(CaptionLabelStyle())
                        .accessibilityElement(children: .combine)
                }
                if reduceMotion && s.useMotionArtwork {
                    InspectorCaption(WorkspaceCopy.reduceMotionCovers)
                }
            }
            Section("Framing") {
                ChoiceMenu("Placement", selection: $s.motionPlacement, selectedTitle: \.title) {
                    ForEach(Settings.MotionPlacement.allCases) { Text($0.title).tag($0) }
                }
                InspectorCaption(s.motionPlacement.detail)
                ValueSlider("Softening", value: $s.motionBlur, in: 0...25, step: 0.5,
                            format: { $0 == 0 ? "None" : SettingsFormat.percent($0 / 25) })
                if let track = lookedUpTrack {
                    Toggle("Exclude this album", isOn: Binding(
                        get: { s.excludesMotion(artist: track.artist, album: track.album) },
                        set: { _ in
                            s.setMotionExcluded(!s.excludesMotion(artist: track.artist, album: track.album),
                                                artist: track.artist, album: track.album)
                        }))
                    // Named as the excluded list below names it.
                    InspectorCaption([track.artist, track.album ?? ""].filter { !$0.isEmpty }.joined(separator: " · "))
                }
            }
            if !s.motionExclusions.isEmpty {
                Section("Excluded albums") {
                    ForEach(s.motionExclusions, id: \.self) { name in
                        let album = name.replacingOccurrences(of: "|", with: " · ")
                        LabeledContent {
                            Button("Allow") { s.motionExclusions.removeAll { $0 == name } }
                                .controlSize(.small)
                                .help("Allow animated covers for \(album) again")
                                .accessibilityLabel("Allow \(album)")
                                .accessibilityHint("Lets this album’s animated cover play again.")
                        } label: {
                            Text(album).lineLimit(2)
                        }
                    }
                }
            }
        }
        .formStyle(.grouped)
    }
}
