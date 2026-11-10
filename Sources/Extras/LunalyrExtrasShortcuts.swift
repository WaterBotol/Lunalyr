import Foundation

/// Wallpaper actions the global shortcuts (and nothing else yet) run. Each
/// says what it did, like the other shortcuts.
extension LunalyrExtras {
    func toggleMusicVideo() { toggleMusicVideo(settings: .shared) }

    /// Music video on, or back to what it falls back to.
    func toggleMusicVideo(settings: Settings) {
        if settings.backgroundMode == .musicVideo {
            settings.backgroundMode = settings.musicVideoFallback
            post("Music video off · \(settings.backgroundMode.title)", symbol: "film")
        } else {
            settings.backgroundMode = .musicVideo
            post("Music video on", symbol: "film")
        }
    }

    func nextVisualizer() { nextVisualizer(settings: .shared) }

    /// The next visualiser. With another background showing, the first
    /// press switches to the visualisers, showing the one chosen last.
    func nextVisualizer(settings: Settings) {
        if settings.backgroundMode != .visualizer {
            settings.backgroundMode = .visualizer
        } else {
            settings.audioVisualizerStyle = Self.visualizer(after: settings.audioVisualizerStyle)
        }
        post("Visualiser: \(settings.audioVisualizerStyle.title)", symbol: "waveform")
    }

    static func visualizer(after style: AudioVisualizerStyle) -> AudioVisualizerStyle {
        let all = AudioVisualizerStyle.allCases
        let index = all.firstIndex(of: style) ?? -1
        return all[(index + 1) % all.count]
    }
}
