import SwiftUI

/// The visualiser's own rows in the Wallpaper inspector, under the scene
/// picker. Everything here reads `VisualizerPreferences`, so the shared
/// inspector only needs to place this view.
@MainActor
struct VisualizerSceneControls: View {
    let style: AudioVisualizerStyle
    @Model(\.visualizer) private var preferences
    @ObservedObject private var safety = VisualizerSafety.shared
    @ObservedObject private var diagnostics = WebVisualizerDiagnostics.shared
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private var calm: Bool { reduceMotion || safety.dimFlashingLights }

    var body: some View {
        if style == .starTrails {
            ChoiceMenu("Colours", selection: $preferences.starTrailsColours,
                       selectedTitle: { $0 == .album ? "Album cover" : "Original \(style.title)" }) {
                Text("Album cover").tag(VisualizerSceneColours.album)
                Text("Original \(style.title)").tag(VisualizerSceneColours.original)
            }
        }
        ValueSlider("Beat punch", value: $preferences.punch, in: VisualizerPreferences.punchRange,
                    minimumLabel: "Off", maximumLabel: "Strong",
                    format: { calm ? "Off" : SettingsFormat.percent($0) },
                    spoken: { calm ? "Off while Reduce Motion or Dim Flashing Lights is on" : SettingsFormat.percent($0) })
            .disabled(calm)
        Text(calm
            ? "Reduce Motion or Dim Flashing Lights is on, so beat flashes are off and brightness changes are limited."
            : "Visualisers can flash with the beat. Reduce Motion or Dim Flashing Lights in System Settings › Accessibility › Display removes beat flashes and limits brightness changes.")
            .font(.caption).foregroundStyle(.secondary)
        if let engine = style.webEngine {
            Toggle("Heavy scenes on main display only", isOn: $preferences.heavyScenesOnMainDisplayOnly)
            Text("Each display otherwise runs its own copy of this scene. With this on, other displays show \(engine.nativeStandIn(projection: .preset).title) instead.")
                .font(.caption).foregroundStyle(.secondary)
            if let summary = diagnostics.summary(for: engine) {
                Text(summary).font(.caption).foregroundStyle(.secondary)
            }
        }
    }
}

/// "Colours: Album cover / Original" for the web scenes that lay the album
/// palette over their own colours (Light 3D has its own setting).
@MainActor
struct VisualizerColoursPicker: View {
    let engine: WebVisualizerEngine
    @Model(\.visualizer) private var preferences

    var body: some View {
        if preferences.colours(for: engine) != nil {
            ChoiceMenu("Colours", selection: Binding(
                get: { preferences.colours(for: engine) ?? .album },
                set: { preferences.setColours($0, for: engine) }),
                       selectedTitle: { $0 == .album ? "Album cover" : "Original \(engine.title)" }) {
                Text("Album cover").tag(VisualizerSceneColours.album)
                Text("Original \(engine.title)").tag(VisualizerSceneColours.original)
            }
        }
    }
}
