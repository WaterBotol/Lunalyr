import SwiftUI

/// Performance: frame rate and power saving, and how the desktop behaves.
@MainActor
struct PerformancePage: View {
    @Model(\.settings) private var s

    var body: some View {
        ThemedForm {
            ThemedSection("Rendering and power") {
                ChoiceSegments("Frame rate", selection: $s.animationRate,
                               cases: Array(Settings.AnimationRate.allCases), label: \.title)
                if s.effectiveAnimationRate != s.animationRate {
                    PowerCaption("Power saving is holding animation at \(s.effectiveAnimationRate.title) for now.")
                }
                Toggle("Low power visuals", isOn: $s.lowPowerVisuals)
                InspectorCaption("Shows Line patterns at no more than 30 fps. Your full wallpaper returns when this is switched off.")
                Toggle("Follow macOS Low Power Mode", isOn: $s.respectLowPower)
                ChoiceMenu("On battery", selection: $s.onBattery, selectedTitle: \.title) {
                    ForEach(Settings.PowerBehaviour.allCases) { Text($0.title).tag($0) }
                }
                if let constraint = s.powerConstraint, constraint != .lowPowerVisuals {
                    PowerCaption("\(constraint.reason) now, so power saving is in effect.")
                }
                Toggle("Pause fully covered wallpapers", isOn: $s.pauseWhenCovered)
                if s.pauseWhenCovered {
                    // What "Pause fully covered wallpapers" can and cannot see.
                    InspectorCaption("Pause fully covered wallpapers goes by where windows are. Wallpapers pause behind app windows and full-screen apps, and keep moving behind terminals, which are often see-through, and under Stage Manager. If a wallpaper you can see through a window holds still, switch the pause off.")
                }
            }
            DesktopInteractionSection()
        }
        .formStyle(.grouped)
        .scrollContentBackground(.hidden)
    }
}

/// A caption about power saving, with its leaf.
private struct PowerCaption: View {
    let text: String

    init(_ text: String) { self.text = text }

    var body: some View {
        Label {
            Text(text)
        } icon: {
            Image(systemName: "leaf.fill").foregroundStyle(LunalyrStatus.success.text)
        }
        .labelStyle(CaptionLabelStyle())
        .accessibilityElement(children: .combine)
    }
}
