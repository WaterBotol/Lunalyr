import AppKit
import SwiftUI

/// The floating window's size and place, in Text › Size and place while
/// the Floating view shows: the size preset, the place on screen and the
/// display; the exact text size, Edit position and Reset size under More
/// options.
@MainActor
struct FloatingSizeAndPlace: View {
    @ObservedObject var options: FloatingLyricsOptions = .shared
    @ObservedObject private var editing = FloatingLyricsController.shared.state

    var body: some View {
        ThemedSegments("Size", selection: Binding(
            get: { FloatingLyricsOptions.SizePreset(rawValue: options.textSize) },
            set: { if let preset = $0 { options.textSize = preset.rawValue } }),
                       options: FloatingLyricsOptions.SizePreset.allCases.map { (Optional($0), $0.title) })
        LabeledContent("Place") {
            FloatingPlaceGrid(placement: $options.placement)
        }
        InspectorCaption("Clicks pass through the lyrics. Edit position lets you drag them; Done locks them again.")
        MoreOptions {
            ValueSlider("Text size", value: $options.textSize, in: FloatingLyricsOptions.textSizeRange, step: 1,
                        format: { "\(Int($0.rounded())) pt" })
            HStack(spacing: 8) {
                Button(editing.editing ? "Done" : "Edit position…") {
                    FloatingLyricsController.shared.toggleEditing()
                }
                .help(editing.editing ? "Locks the floating lyrics in place"
                      : "Shows the floating lyrics with handles: drag to move, drag the corner to resize")
                Button("Reset size") { options.resetFrames() }
                    .help("Forgets sizes and places set by hand; the window follows the text size again")
            }
            .controlSize(.small)
        }
    }
}

/// What each display shows: a menu per connected display, listed by name.
@MainActor
struct DisplayLyricsSection: View {
    @ObservedObject var options: FloatingLyricsOptions = .shared
    @State private var screens = FloatingDisplayChoice.connected()

    var body: some View {
        InspectorSection("Displays") {
            ForEach(screens, id: \.key) { screen in
                ChoiceMenu(screens.count > 1 ? screen.name : "This display", selection: Binding(
                    get: { options.choice(for: screen.key) },
                    set: { options.setChoice($0, for: screen.key) }), selectedTitle: \.title, oneLine: true) {
                    ForEach(DisplayLyrics.allCases) { Text($0.title).tag($0) }
                }
                .help("What \(screen.name) shows: lyrics on its desktop, floating over its apps, both, or the wallpaper alone")
            }
            InspectorCaption("Each monitor has its own look: pick one at the top of the picture. Its desktop lyrics use its Desktop style and its floating lyrics its Floating style.",
                             more: "Each display can show either, both, or no lyrics at all. A display you plug in for the first time shows Desktop lyrics and starts with the main display's look.")
        }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didChangeScreenParametersNotification)) { _ in
            screens = FloatingDisplayChoice.connected()
        }
    }
}

/// How the floating lyrics stay readable over apps, under Text ›
/// Readability's More options while the Floating view shows.
@MainActor
struct FloatingReadabilityRows: View {
    @ObservedObject var options: FloatingLyricsOptions = .shared

    var body: some View {
        ValueSlider("Shade behind lyrics", value: $options.backdrop, in: 0...1, step: 0.01,
                    format: SettingsFormat.percent)
            .help("A soft shade and shadow keep the words readable over any app.")
        Toggle("Fade when the pointer is over them", isOn: $options.fadesUnderPointer)
            .toggleStyle(StudioCheckboxStyle())
    }
}

/// Place as a small picture of the screen: nine spots in a 3 × 3 grid, the
/// chosen one lit (none while the place is Custom, set by dragging).
struct FloatingPlaceGrid: View {
    @Binding var placement: FloatingPlacement

    var body: some View {
        VStack(spacing: 4) {
            ForEach(0..<3, id: \.self) { row in
                HStack(spacing: 4) {
                    ForEach(0..<3, id: \.self) { column in
                        spot(FloatingPlacement.presets.first { $0.cell?.column == column && $0.cell?.row == row })
                    }
                }
            }
        }
        .padding(5)
        .background(RoundedRectangle(cornerRadius: 6, style: .continuous).fill(Color.studio(.track).opacity(0.5)))
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Place")
        .accessibilityValue(placement.title)
    }

    @ViewBuilder private func spot(_ preset: FloatingPlacement?) -> some View {
        if let preset {
            let chosen = placement == preset
            Button { placement = preset } label: {
                RoundedRectangle(cornerRadius: 2, style: .continuous)
                    .fill(chosen ? Color.studio(.accent) : Color.studio(.control))
                    .overlay(RoundedRectangle(cornerRadius: 2, style: .continuous)
                        .strokeBorder(Color.lunalyrContrastBorder, lineWidth: 1))
                    .frame(width: 26, height: 15)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help(preset.title)
            .accessibilityLabel(preset.title)
            .accessibilityAddTraits(chosen ? [.isButton, .isSelected] : .isButton)
        } else {
            Color.clear.frame(width: 26, height: 15)
        }
    }
}

/// A display the floating lyrics can go on.
struct FloatingDisplayChoice {
    let key: String
    let name: String

    /// The connected displays, the main one first; two displays of the
    /// same name are numbered.
    @MainActor
    static func connected() -> [FloatingDisplayChoice] {
        // In the test host, one display whatever this Mac has, so Settings
        // draws the same everywhere.
        if AppRuntime.isUnitTesting { return [FloatingDisplayChoice(key: "test-display", name: "Built-in Display")] }
        return named(NSScreen.screens.compactMap { screen in
            screen.versoDisplayID.map { (FloatingFrame.cachedKey($0), screen.localizedName) }
        })
    }

    static func named(_ displays: [(key: String, name: String)]) -> [FloatingDisplayChoice] {
        var seen: [String: Int] = [:]
        let repeated = Set(Dictionary(grouping: displays.map(\.name), by: { $0 }).filter { $0.value.count > 1 }.keys)
        return displays.map { display in
            guard repeated.contains(display.name) else { return FloatingDisplayChoice(key: display.key, name: display.name) }
            let number = (seen[display.name] ?? 0) + 1
            seen[display.name] = number
            return FloatingDisplayChoice(key: display.key, name: "\(display.name) \(number)")
        }
    }
}
