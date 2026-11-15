import SwiftUI

/// Performance: frame rate and power saving (low power visuals and pausing
/// covered wallpapers under More options), and Troubleshooting for system
/// audio, all under More options.
@MainActor
struct PerformancePage: View {
    @Model(\.settings) private var s

    var body: some View {
        ThemedForm {
            ThemedSection("Power") {
                ChoiceSegments("Frame rate", selection: $s.animationRate,
                               cases: Array(Settings.AnimationRate.allCases), label: \.title)
                if s.effectiveAnimationRate != s.animationRate {
                    PowerCaption("Power saving is holding animation at \(s.effectiveAnimationRate.title) for now.")
                }
                ChoiceMenu("On battery", selection: $s.onBattery, selectedTitle: \.title) {
                    ForEach(Settings.PowerBehaviour.allCases) { Text($0.title).tag($0) }
                }
                Toggle("Follow macOS Low Power Mode", isOn: $s.respectLowPower)
                if let constraint = s.powerConstraint, constraint != .lowPowerVisuals {
                    PowerCaption("\(constraint.reason) now, so power saving is in effect.")
                }
                MoreOptions {
                    Toggle("Low power visuals", isOn: $s.lowPowerVisuals)
                    InspectorCaption("Caps Line patterns at 30 fps while on.", more: "Shows Line patterns at no more than 30 fps. Your full wallpaper returns when this is switched off.")
                    Toggle("Pause fully covered wallpapers", isOn: $s.pauseWhenCovered)
                    if s.pauseWhenCovered {
                        // What "Pause fully covered wallpapers" can and cannot see.
                        InspectorCaption("Goes by where windows are.", more: "Pause fully covered wallpapers goes by where windows are. Wallpapers pause behind app windows and full-screen apps, and keep moving behind terminals, which are often see-through, and under Stage Manager. If a wallpaper you can see through a window holds still, switch the pause off.")
                            .revealRow()
                    }
                }
            }
            ThemedSection("Troubleshooting") {
                InspectorCaption("System audio for the visualisers: its levels, fixes and a report for bug reports.")
                    .environment(\.captionFollowsControl, false)
                MoreOptions {
                    AudioTroubleshooting()
                }
            }
        }
        .lunalyrReveal(s.pauseWhenCovered)
    }
}

/// System audio's diagnostics: what capture is doing, the live levels
/// (drawn only while More options is open), the fixes and the report. The
/// fixes are offered as on Wallpaper › Music response's status: Reconnect
/// while capture is on, compatibility capture while the tap needs attention.
@MainActor
private struct AudioTroubleshooting: View {
    @Environment(\.lunalyrModels) private var models
    // Filled from the controller as the view appears; not observed at page
    // level, as the meters publish many times a second.
    @State private var status = AudioReactiveController.offMessage
    @State private var state = AudioCaptureState.off
    @State private var usingTap = false

    var body: some View {
        let audio = models.resolved.audio
        // Read, so a new state or capture route redraws the fixes' enabling.
        let _ = (state, usingTap)
        Group {
            Label {
                Text(status)
            } icon: {
                Image(systemName: "waveform")
            }
            .labelStyle(CaptionLabelStyle())
            .accessibilityElement(children: .combine)
            AudioMeters()
            HStack {
                Button("Reconnect") { audio.reconnect() }
                    .disabled(!audio.offersReconnect)
                Button("Try Compatibility Capture") { audio.tryCompatibilityCapture() }
                    .disabled(!audio.offersCompatibilityCapture)
                    .help("Uses Screen Recording permission instead of System Audio Recording.")
            }
            .controlSize(.small)
            HStack {
                // The same action, and name, as beside a failing status.
                Button("Open Privacy Settings…") { audio.openPrivacySettings() }
                    .help("Opens the Privacy & Security pane that lets Lunalyr hear system audio")
                Button("Copy Audio Diagnostics") { audio.copyDiagnostics() }
                    .help("Copies a report of how audio capture is set up, to paste into a bug report")
            }
            .controlSize(.small)
        }
        .onReceive(audio.$status.removeDuplicates()) { status = $0 }
        .onReceive(audio.$state.removeDuplicates()) { state = $0 }
        .onReceive(audio.$usingTap.removeDuplicates()) { usingTap = $0 }
    }
}

/// A caption about power saving, with its leaf in the success colour.
private struct PowerCaption: View {
    let text: String
    @Environment(\.formDensity) private var density

    init(_ text: String) { self.text = text }

    var body: some View {
        Label {
            Text(text)
        } icon: {
            Image(systemName: "leaf.fill").foregroundStyle(Color.studio(.success))
        }
        .labelStyle(CaptionLabelStyle())
        .padding(.top, LunalyrDesign.Studio.Layout.captionGap - density.rowSpacing)
        .accessibilityElement(children: .combine)
    }
}
