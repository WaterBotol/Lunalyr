import SwiftUI

// MARK: - 4. The typeface

/// The typefaces the welcome offers: the faces on every Mac, so choosing
/// one downloads nothing, and Manrope (a Google font) only once it has
/// already loaded for the app's own type. Match, Google Fonts and Installed
/// font stay in Settings › Look › Text.
@MainActor
enum OnboardingTypefaces {
    static func offered(manropeLoaded: Bool? = nil) -> [Settings.FontChoice] {
        let manrope = manropeLoaded ?? (AppFont.regular != nil)
        return [.appleMusic] + (manrope ? [.manrope] : []) + [.sans, .serif, .slab, .soft]
    }

    /// The tile's name, shorter than `FontChoice.title`.
    static func name(_ choice: Settings.FontChoice) -> String {
        switch choice {
        case .appleMusic: return "SF Pro"
        case .manrope: return "Manrope"
        case .sans: return "Avenir Next Condensed"
        case .serif: return "Charter"
        case .slab: return "Rockwell"
        case .soft: return "SF Rounded"
        default: return choice.title
        }
    }

    /// The sung word's face, as the lyrics draw it (`Settings.lyricFont`, hero).
    static func font(_ choice: Settings.FontChoice, size: CGFloat) -> Font {
        switch choice {
        case .appleMusic: return .system(size: size, weight: .heavy)
        case .manrope:
            if let name = AppFont.bold ?? AppFont.regular { return .custom(name, fixedSize: size) }
            return .system(size: size, weight: .heavy)
        case .sans: return LyricFace.sans.font(size: size, hero: true)
        case .serif: return LyricFace.serif.font(size: size, hero: true)
        case .slab: return LyricFace.slab.font(size: size, hero: true)
        case .soft: return LyricFace.soft.font(size: size, hero: true)
        case .auto, .google, .installed: return .system(size: size, weight: .heavy)
        }
    }
}

/// Picks the lyrics' typeface from tiles that each set a line of the sample
/// song in their face, over the sample wallpaper. The choice goes to both
/// lyric styles (`OnboardingFlow.chooseTypeface`).
struct TypefaceStep: View {
    @ObservedObject var flow: OnboardingFlow
    var focus: OnboardingTitleFocus
    @Model(\.settings) private var settings
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    static let columns = 3
    static let tile = CGSize(width: 128, height: 72)
    static let gap = LunalyrDesign.Spacing.medium

    var body: some View {
        let faces = OnboardingTypefaces.offered()
        let current = settings.fontChoice
        OnboardingPage(step: .typeface, title: OnboardingCopy.typefaceTitle, message: OnboardingCopy.typeface,
                       focus: focus) {
            VStack(spacing: Self.gap) {
                ForEach(Array(stride(from: 0, to: faces.count, by: Self.columns)), id: \.self) { start in
                    HStack(alignment: .top, spacing: Self.gap) {
                        ForEach(faces[start..<min(start + Self.columns, faces.count)]) { face in
                            tile(face, chosen: face == current)
                        }
                    }
                }
            }
            .accessibilityElement(children: .contain)
            .accessibilityLabel("Typeface")
            Text(OnboardingCopy.typefaceLater)
                .onboardingCaption()
                .multilineTextAlignment(.center)
                .padding(.top, LunalyrDesign.Spacing.large)
        }
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion), value: current)
    }

    private func tile(_ face: Settings.FontChoice, chosen: Bool) -> some View {
        Button {
            flow.chooseTypeface(face)
        } label: {
            VStack(spacing: LunalyrDesign.Spacing.xSmall) {
                ZStack {
                    SampleWallpaper()
                    VStack(spacing: 0) {
                        Text("Static on")
                        Text("the radio,")
                    }
                    .font(OnboardingTypefaces.font(face, size: 19))
                    .foregroundStyle(.white)
                    .lineLimit(1)
                    .minimumScaleFactor(0.6)
                    .padding(.horizontal, LunalyrDesign.Spacing.small)
                }
                .frame(width: Self.tile.width, height: Self.tile.height)
                .clipShape(RoundedRectangle(cornerRadius: LunalyrDesign.Radius.artwork, style: .continuous))
                .environment(\.colorScheme, .dark)
                .modifier(OnboardingChosenRing(chosen: chosen, radius: LunalyrDesign.Radius.artwork))
                Text(OnboardingTypefaces.name(face))
                    .font(LunalyrDesign.Studio.TypeScale.caption)
                    .fontWeight(chosen ? .semibold : .regular)
                    .foregroundStyle(chosen ? Color.studio(.ink) : Color.studio(.ink2))
                    .multilineTextAlignment(.center)
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
            }
            .frame(width: Self.tile.width)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(OnboardingTypefaces.name(face))
        .accessibilityHint("Sets the lyrics in \(OnboardingTypefaces.name(face)).")
        .accessibilityAddTraits(chosen ? [.isButton, .isSelected] : .isButton)
        .help(face.title)
    }
}
