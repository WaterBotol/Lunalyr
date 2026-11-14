import SwiftUI

/// "Aa" in a typeface choice's own face.
enum TypefaceSpecimen {
    static func font(_ face: Settings.FontChoice, size: CGFloat) -> Font {
        switch face {
        case .auto, .appleMusic, .google: return .system(size: size, weight: .bold)
        case .manrope: return .custom("Manrope", size: size).weight(.bold)
        case .sans: return .custom("Avenir Next", size: size).weight(.semibold)
        case .serif: return .custom("Charter", size: size).weight(.bold)
        case .slab: return .custom("Rockwell", size: size).weight(.bold)
        case .soft: return .system(size: size, weight: .semibold, design: .rounded)
        }
    }
}

/// Typeface as eight specimen tiles, four to a row: Match, the built-in
/// faces, and Other… (a Google Fonts or installed family). Selecting one is
/// the old Typeface menu's choice (`selectFontChoice`).
@MainActor
struct TypefaceTiles: View {
    @Model(\.settings) private var settings
    static let columns = 4
    static let gap: CGFloat = 10

    var body: some View {
        GeometryReader { proxy in
            let width = ((proxy.size.width - Self.gap * 3) / 4).rounded(.down)
            VStack(alignment: .leading, spacing: Self.gap) {
                ForEach(Array(ChoiceGridMetrics.rows(LookChoices.typefaces, count: Self.columns).enumerated()), id: \.offset) { _, row in
                    HStack(alignment: .top, spacing: Self.gap) {
                        ForEach(row) { face in tile(face, width: width) }
                    }
                }
            }
            .padding(.top, 4)
        }
        .frame(height: 2 * (38 + 19) + Self.gap + 4)
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Typeface")
    }

    private func tile(_ face: Settings.FontChoice, width: CGFloat) -> some View {
        let chosen = settings.fontChoice == face
        return Button {
            settings.selectFontChoice(face)
        } label: {
            VStack(spacing: 4) {
                ZStack {
                    RoundedRectangle(cornerRadius: 7, style: .continuous).fill(Color.studio(.control))
                    specimen(face).foregroundStyle(Color.studio(.ink))
                }
                .frame(width: width, height: 38)
                .overlay(RoundedRectangle(cornerRadius: 7, style: .continuous)
                    .strokeBorder(Color.lunalyrContrastBorder, lineWidth: 1))
                .modifier(ChosenRing(chosen: chosen, radius: 7))
                Text(LookChoices.shortName(face))
                    .font(.system(size: 10.5, weight: chosen ? .semibold : .regular))
                    .foregroundStyle(chosen ? Color.studio(.ink) : Color.studio(.ink2))
                    .lineLimit(1)
                    .minimumScaleFactor(0.85)
                    .frame(width: width)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(ChoiceTilePressStyle())
        .help(face.title)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(face.title)
        .accessibilityAddTraits(chosen ? [.isButton, .isSelected] : .isButton)
    }

    @ViewBuilder private func specimen(_ face: Settings.FontChoice) -> some View {
        switch face {
        case .auto: Image(systemName: "wand.and.stars").font(.system(size: 15, weight: .medium))
        case .google: Image(systemName: "ellipsis").font(.system(size: 15, weight: .semibold))
        default: Text("Aa").font(TypefaceSpecimen.font(face, size: 18))
        }
    }
}
