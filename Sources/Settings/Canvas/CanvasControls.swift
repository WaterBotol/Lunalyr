import SwiftUI

// MARK: - Controls of the picture pages
//
// The object switch, the view switch, the choice tiles and the inspector's
// title, drawn to SPEC.md §2–3 with the redesign's colour roles
// (`Color.studio`). Each is a real control: keyboard focus, VoiceOver
// labels and the selected trait.

/// A segmented switch: a track with the chosen segment raised.
struct CanvasSegments<Value: Hashable>: View {
    let title: String
    @Binding var selection: Value
    let options: [(value: Value, label: String)]
    /// The view switch under the picture: 12 pt, 22 high.
    var small = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.colorScheme) private var scheme
    @Namespace private var segment

    var body: some View {
        HStack(spacing: 2) {
            ForEach(options, id: \.value) { option in
                let chosen = option.value == selection
                Button {
                    selection = option.value
                } label: {
                    Text(option.label)
                        .font(.system(size: small ? 12 : 13, weight: chosen ? .semibold : .regular))
                        .foregroundStyle(chosen ? Color.studio(.ink) : Color.studio(.ink2))
                        .lineLimit(1)
                        .fixedSize()
                        .padding(.horizontal, small ? 10 : 14)
                        .frame(height: small ? 22 : 26)
                        .background {
                            if chosen {
                                RoundedRectangle(cornerRadius: 6, style: .continuous)
                                    .fill(Color.studio(.segmentSelected))
                                    .shadow(color: .black.opacity(scheme == .dark ? 0.3 : 0.12), radius: 1.5, y: 0.5)
                                    .matchedGeometryEffect(id: "chosen", in: segment)
                            }
                        }
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel(option.label)
                .accessibilityAddTraits(chosen ? .isSelected : [])
            }
        }
        .padding(2)
        .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.studio(.controlOnCanvas)))
        .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous)
            .strokeBorder(Color.lunalyrContrastBorder, lineWidth: 1))
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion), value: selection)
        .accessibilityElement(children: .contain)
        .accessibilityLabel(title)
    }
}

/// The chosen ring round a tile: 2.5 pt accent, 4 outside it.
struct ChosenRing: ViewModifier {
    let chosen: Bool
    var radius: CGFloat = 6

    func body(content: Content) -> some View {
        content.overlay {
            RoundedRectangle(cornerRadius: radius + 3, style: .continuous)
                .strokeBorder(chosen ? Color.studio(.accent) : .clear, lineWidth: 2.5)
                .padding(-4)
                .allowsHitTesting(false)
        }
    }
}

/// A choice drawn as a picture, with its name under it (or only as a
/// VoiceOver label and help tag when `showsName` is off).
struct ChoiceTile<Picture: View>: View {
    let name: String
    let chosen: Bool
    let size: CGSize
    var radius: CGFloat = 6
    var showsName = true
    var nameFont: CGFloat = 11
    var hint: String?
    /// What VoiceOver says, when it differs from the name ("Matched, edited").
    var spokenName: String?
    let action: () -> Void
    @ViewBuilder let picture: Picture

    var body: some View {
        Button(action: action) {
            VStack(spacing: 6) {
                picture
                    .frame(width: size.width, height: size.height)
                    .clipShape(RoundedRectangle(cornerRadius: radius, style: .continuous))
                    .overlay(RoundedRectangle(cornerRadius: radius, style: .continuous)
                        .strokeBorder(Color.lunalyrContrastBorder, lineWidth: 1))
                    .modifier(ChosenRing(chosen: chosen, radius: radius))
                if showsName {
                    Text(name)
                        .font(.system(size: nameFont, weight: chosen ? .semibold : .regular))
                        .foregroundStyle(chosen ? Color.studio(.ink) : Color.studio(.ink2))
                        .lineLimit(1)
                        .minimumScaleFactor(0.85)
                        .frame(width: size.width)
                }
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(ChoiceTilePressStyle())
        .help(hint.map { "\(name): \($0)" } ?? name)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(spokenName ?? name)
        .accessibilityHint(hint ?? "")
        .accessibilityAddTraits(chosen ? [.isButton, .isSelected] : .isButton)
    }
}

/// A tile dims a little while pressed, so the click registers.
struct ChoiceTilePressStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label.brightness(configuration.isPressed ? -0.08 : 0)
    }
}

/// "Wallpaper type  Match the music": a choice grid's name and its value.
struct ChoiceHeader: View {
    let title: String
    let value: String

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Text(title).font(.system(size: 13, weight: .semibold)).foregroundStyle(Color.studio(.ink))
                .accessibilityAddTraits(.isHeader)
            Text(value).font(.system(size: 13)).foregroundStyle(Color.studio(.ink2))
                .lineLimit(1)
                .truncationMode(.tail)
                .contentTransition(.opacity)
        }
        .accessibilityElement(children: .combine)
    }
}

/// The selected object's name at the top of its settings, with an optional
/// caption: what the inspector below edits.
struct InspectorTitle: View {
    let title: String
    var subtitle: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title)
                .font(LunalyrDesign.Studio.TypeScale.title)
                .foregroundStyle(Color.studio(.ink))
                .accessibilityAddTraits(.isHeader)
            if let subtitle {
                Text(subtitle)
                    .font(LunalyrDesign.Studio.TypeScale.caption)
                    .foregroundStyle(Color.studio(.ink2))
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// The eight handles round a selected line or window.
struct SelectionHandles: View {
    let size: CGSize

    static func points(in size: CGSize) -> [CGPoint] {
        [
            .zero, CGPoint(x: size.width / 2, y: 0), CGPoint(x: size.width, y: 0),
            CGPoint(x: 0, y: size.height / 2), CGPoint(x: size.width, y: size.height / 2),
            CGPoint(x: 0, y: size.height), CGPoint(x: size.width / 2, y: size.height),
            CGPoint(x: size.width, y: size.height),
        ]
    }

    var body: some View {
        ZStack(alignment: .topLeading) {
            Rectangle().stroke(Color.studio(.accent), lineWidth: 1.5)
                .frame(width: size.width, height: size.height)
            ForEach(Array(Self.points(in: size).enumerated()), id: \.offset) { _, point in
                Rectangle()
                    .fill(Color.white)
                    .overlay(Rectangle().strokeBorder(Color.studio(.accent), lineWidth: 1.5))
                    .frame(width: 8, height: 8)
                    .offset(x: point.x - 4, y: point.y - 4)
            }
        }
        .frame(width: size.width, height: size.height, alignment: .topLeading)
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }
}

/// A section of the inspector: its title (and value, at the right), then
/// its rows, 9 apart. No card: the inspector's fill and the space between
/// sections set them apart (spec §1).
struct InspectorSection<Content: View>: View {
    let title: String
    var value: String?
    let content: Content

    init(_ title: String, value: String? = nil, @ViewBuilder content: () -> Content) {
        self.title = title
        self.value = value
        self.content = content()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: LunalyrDesign.Studio.Layout.inspectorRowSpacing) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(title)
                    .font(LunalyrDesign.Studio.TypeScale.section)
                    .foregroundStyle(Color.studio(.ink))
                    .accessibilityAddTraits(.isHeader)
                Spacer(minLength: 0)
                if let value {
                    Text(value)
                        .font(.system(size: 12))
                        .foregroundStyle(Color.studio(.ink2))
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
            }
            content
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// A checkbox (spec §4): 15 × 15, radius 3.5, accent with a white tick when
/// on, 8 points to its label; for lists of related options.
struct StudioCheckboxStyle: ToggleStyle {
    func makeBody(configuration: Configuration) -> some View {
        StudioCheckbox(configuration: configuration)
    }
}

private struct StudioCheckbox: View {
    let configuration: ToggleStyleConfiguration
    @Environment(\.isEnabled) private var enabled

    var body: some View {
        Button {
            configuration.isOn.toggle()
        } label: {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                ZStack {
                    RoundedRectangle(cornerRadius: 3.5, style: .continuous)
                        .fill(configuration.isOn ? Color.studio(.accent) : Color.studio(.checkboxOff))
                    if configuration.isOn {
                        Image(systemName: "checkmark")
                            .font(.system(size: 9, weight: .bold))
                            .foregroundStyle(.white)
                    } else {
                        RoundedRectangle(cornerRadius: 3.5, style: .continuous)
                            .strokeBorder(Color.studio(.checkboxEdge), lineWidth: 1)
                    }
                }
                .frame(width: 15, height: 15)
                .alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 4.5 }
                configuration.label
                    .font(LunalyrDesign.Studio.TypeScale.row)
                    .foregroundStyle(Color.studio(.ink))
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .opacity(enabled ? 1 : 0.4)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        // VoiceOver hears a real checkbox: its label and on or off.
        .accessibilityRepresentation {
            Toggle(isOn: configuration.$isOn) { configuration.label }
        }
    }
}
