import AppKit
import SwiftUI

/// Where the lyrics show, at the top of Lyric style: on the desktop or
/// floating over the apps. The style controls below edit the chosen one's
/// own style, and this says which.
@MainActor
struct LyricSurfaceSection: View {
    @ObservedObject var options: FloatingLyricsOptions = .shared

    var body: some View {
        ThemedSection("Show lyrics") {
            ChoiceSegments("Show lyrics", selection: $options.surface, cases: LyricsSurface.allCases, label: \.title)
            InspectorCaption(Self.caption(options.surface))
                .id(options.surface)
                .transition(.opacity)
        }
    }

    static func caption(_ surface: LyricsSurface) -> String {
        switch surface {
        case .desktop:
            return "Editing the Desktop style. The lyrics sit on the wallpaper, under your windows."
        case .floating:
            return "Editing the Floating style. The desktop keeps its own and shows no lyrics while they float."
        }
    }
}

/// A short reminder, on pages whose settings belong to a lyric style
/// (readings, translations), of which style they change.
struct LyricStyleEditingNote: View {
    @ObservedObject var options: FloatingLyricsOptions = .shared

    var body: some View {
        if options.isFloating {
            InspectorCaption("These settings change the Floating style; the desktop keeps its own.")
        }
    }
}

/// The floating window's own settings: size, shade, place and display.
@MainActor
struct FloatingLyricsSection: View {
    @ObservedObject var options: FloatingLyricsOptions = .shared
    @ObservedObject private var editing = FloatingLyricsController.shared.state
    @State private var screens = FloatingDisplayChoice.connected()

    var body: some View {
        ThemedSection("Floating lyrics") {
            ChoiceSegments("Size", selection: Binding(
                get: { FloatingLyricsOptions.SizePreset(rawValue: options.textSize) },
                set: { if let preset = $0 { options.textSize = preset.rawValue } }),
                           cases: FloatingLyricsOptions.SizePreset.allCases.map(Optional.some),
                           label: { $0?.title ?? "Custom" })
            ValueSlider("Text size", value: $options.textSize, in: FloatingLyricsOptions.textSizeRange, step: 1,
                        format: { "\(Int($0.rounded())) pt" })
            ValueSlider("Shade behind lyrics", value: $options.backdrop, in: 0...1, step: 0.01,
                        format: SettingsFormat.percent)
            InspectorCaption("A soft shade and shadow keep the words readable over any app.")
            Toggle("Fade when the pointer is over them", isOn: $options.fadesUnderPointer)
            LabeledContent("Place") {
                FloatingLocationPicker(placement: $options.placement)
            }
            Text(options.placement.title)
                .lunalyrText(.caption)
                .accessibilityHidden(true)
            if screens.count > 1 {
                ChoiceMenu("Display", selection: $options.display,
                           selectedTitle: { key in screens.first { $0.key == key }?.name ?? "Main display" }) {
                    Text("Main display").tag("")
                    ForEach(screens, id: \.key) { Text($0.name).tag($0.key) }
                }
            }
            HStack(spacing: LunalyrDesign.Spacing.small) {
                Button(editing.editing ? "Done" : "Edit position…") {
                    FloatingLyricsController.shared.toggleEditing()
                }
                .help(editing.editing ? "Locks the floating lyrics in place"
                      : "Shows the floating lyrics with handles: drag to move, drag the corner to resize")
                Button("Reset size") { options.resetFrames() }
                    .help("Forgets sizes and places set by hand; the window follows the text size again")
            }
            InspectorCaption("Clicks pass through the lyrics. Edit position lets you drag them; Done locks them again.")
        }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didChangeScreenParametersNotification)) { _ in
            screens = FloatingDisplayChoice.connected()
        }
    }
}

/// A display the floating lyrics can go on.
struct FloatingDisplayChoice {
    let key: String
    let name: String

    @MainActor
    static func connected() -> [FloatingDisplayChoice] {
        NSScreen.screens.compactMap { screen in
            screen.versoDisplayID.map { FloatingDisplayChoice(key: FloatingFrame.displayKey($0), name: screen.localizedName) }
        }
    }
}

/// A small screen with its menu bar and Dock, and a spot for each preset:
/// click one to put the lyrics there. Custom is the place set by dragging.
struct FloatingLocationPicker: View {
    @Binding var placement: FloatingPlacement
    static let size = CGSize(width: 196, height: 124)

    var body: some View {
        VStack(alignment: .trailing, spacing: LunalyrDesign.Spacing.xSmall) {
            ZStack {
                RoundedRectangle(cornerRadius: 8, style: .continuous)
                    .fill(Color.primary.opacity(0.06))
                    .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous)
                        .strokeBorder(Color.lunalyrContrastBorder, lineWidth: 1))
                VStack(spacing: 0) {
                    // The menu bar and the Dock, which the presets keep clear of.
                    UnevenRoundedRectangle(topLeadingRadius: 8, topTrailingRadius: 8, style: .continuous)
                        .fill(Color.primary.opacity(0.16))
                        .frame(height: 9)
                    Spacer(minLength: 0)
                    Capsule().fill(Color.primary.opacity(0.18))
                        .frame(width: 74, height: 8)
                        .padding(.bottom, 4)
                }
                Grid(horizontalSpacing: 6, verticalSpacing: 6) {
                    ForEach(0..<3, id: \.self) { row in
                        GridRow {
                            ForEach(0..<3, id: \.self) { column in
                                spot(FloatingPlacement.presets.first { $0.cell?.column == column && $0.cell?.row == row })
                            }
                        }
                    }
                }
                .padding(.top, 15)
                .padding(.bottom, 16)
                .padding(.horizontal, 8)
            }
            .frame(width: Self.size.width, height: Self.size.height)
            Button {
                placement = .custom
            } label: {
                Label("Custom", systemImage: placement == .custom ? "hand.draw.fill" : "hand.draw")
                    .font(.caption.weight(.medium))
                    .padding(.horizontal, 8)
                    .padding(.vertical, 3)
                    .background(Capsule().fill(placement == .custom ? Color.lunalyrAccent : Color.primary.opacity(0.1)))
                    .foregroundStyle(placement == .custom ? Color.white : Color.primary)
                    .contentShape(Capsule())
            }
            .buttonStyle(.plain)
            .help("The place and size you set with Edit position")
            .accessibilityLabel(FloatingPlacement.custom.title)
            .accessibilityAddTraits(placement == .custom ? .isSelected : [])
        }
    }

    @ViewBuilder
    private func spot(_ preset: FloatingPlacement?) -> some View {
        if let preset {
            let selected = placement == preset
            Button { placement = preset } label: {
                RoundedRectangle(cornerRadius: 3, style: .continuous)
                    .fill(selected ? Color.lunalyrAccent : Color.primary.opacity(0.22))
                    .frame(width: 50, height: 14)
                    .overlay(RoundedRectangle(cornerRadius: 3, style: .continuous)
                        .strokeBorder(selected ? Color.clear : Color.lunalyrContrastBorder, lineWidth: 1))
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help(preset.title)
            .accessibilityLabel(preset.title)
            .accessibilityAddTraits(selected ? .isSelected : [])
        } else {
            Color.clear.frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }
}
