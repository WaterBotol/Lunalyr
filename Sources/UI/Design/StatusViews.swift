import SwiftUI

// MARK: - Status
//
// What Lunalyr is doing that the user should know, with the way to change it
// right there. One component for every surface, so a refusal, a failing
// capture or power saving look and read the same in Settings, the menu-bar
// panel and onboarding:
//
//     StatusBanner(.warning, title: "Lunalyr can’t read Music",
//                  message: "Turn on Music for Lunalyr in System Settings › Privacy & Security › Automation.") {
//         Button("Open Privacy Settings") { navigation.openSystemSettings(.automation) }
//         Button("Follow Spotify Only") { players.choice = .spotify }
//     }
//
//     StatusChip(.info, "Wallpaper paused", symbol: "pause.rectangle.fill") {
//         Button("Resume") { extras.toggleWallpaperPause() }
//             .accessibilityLabel("Resume Wallpaper")
//     }
//
// `StatusBanner` sits in a page or panel; `.compact` (menu-bar panel) uses
// less padding and smaller buttons. Actions lay out side by side when they
// fit, stacked when they don't. Never open System Settings or ask for a
// permission from a banner appearing: only from its buttons.

/// How tightly a shared component packs itself.
enum PanelDensity {
    /// Settings pages and onboarding.
    case regular
    /// The menu-bar panel.
    case compact
}

/// A status with a title, an explanation and up to three actions.
struct StatusBanner<Actions: View>: View {
    typealias Density = PanelDensity

    let status: LunalyrStatus
    let title: String
    var message: String?
    /// Defaults to the status's own symbol.
    var symbol: String?
    var density: Density = .regular
    let actions: Actions
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    init(_ status: LunalyrStatus, title: String, message: String? = nil, symbol: String? = nil,
         density: Density = .regular, @ViewBuilder actions: () -> Actions) {
        self.status = status
        self.title = title
        self.message = message
        self.symbol = symbol
        self.density = density
        self.actions = actions()
    }

    private var padding: CGFloat { density == .compact ? 10 : 12 }

    var body: some View {
        VStack(alignment: .leading, spacing: density == .compact ? 6 : 8) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Image(systemName: symbol ?? status.symbol)
                    .font(.system(size: 13))
                    .foregroundStyle(StatusGlyph.color(status))
                    .contentTransition(LunalyrDesign.Motion.symbolSwap(reduceMotion: reduceMotion))
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 2) {
                    Text(title)
                        .font(.system(size: density == .compact ? 12.5 : 13, weight: .semibold))
                        .foregroundStyle(Color.studio(.ink))
                        .fixedSize(horizontal: false, vertical: true)
                        .contentTransition(.opacity)
                    if let message {
                        Text(message)
                            .font(LunalyrDesign.Studio.TypeScale.caption)
                            .foregroundStyle(Color.studio(.ink2))
                            .fixedSize(horizontal: false, vertical: true)
                            .contentTransition(.opacity)
                    }
                }
                .accessibilityElement(children: .combine)
                .accessibilityLabel(spokenSummary)
            }
            if Actions.self != EmptyView.self {
                ViewThatFits(in: .horizontal) {
                    HStack(spacing: LunalyrDesign.Spacing.small) { actions }
                    VStack(alignment: .leading, spacing: 6) { actions }
                }
                .controlSize(.small)
                .buttonStyle(ThemedButtonStyle())
                .padding(.leading, symbolIndent)
            }
        }
        .padding(padding)
        .frame(maxWidth: .infinity, alignment: .leading)
        // A fill, not a border (SPEC §1: no cards): the control fill, a
        // canvas-grey step on the canvas; Bold's outline and shadow come
        // from `ThemedSurface`, Increase Contrast's edge too.
        .modifier(ThemedSurface(radius: 8, selected: false, state: .normal, isCard: false))
        // A banner that changes its state (a reconnect, a refusal answered)
        // cross-fades rather than cutting.
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion),
                   value: [status.rawValue, title, message ?? "", symbol ?? ""])
        .accessibilityElement(children: .contain)
    }

    /// Lines the actions up under the title, past the symbol.
    private var symbolIndent: CGFloat { 21 }

    private var spokenSummary: String {
        [status.spokenName, title, message].compactMap { $0 }.joined(separator: ". ")
    }
}

extension StatusBanner where Actions == EmptyView {
    init(_ status: LunalyrStatus, title: String, message: String? = nil, symbol: String? = nil,
         density: Density = .regular) {
        self.init(status, title: title, message: message, symbol: symbol, density: density) { EmptyView() }
    }
}

/// A state in a capsule, with an optional action beside it: for a page
/// header, a toolbar or a panel's top ("Wallpaper paused · Resume").
struct StatusChip<Action: View>: View {
    let status: LunalyrStatus
    let text: String
    var symbol: String?
    let action: Action

    init(_ status: LunalyrStatus, _ text: String, symbol: String? = nil, @ViewBuilder action: () -> Action) {
        self.status = status
        self.text = text
        self.symbol = symbol
        self.action = action()
    }

    var body: some View {
        // A glyph and the words, no capsule: the glyph carries the status
        // colour, the words stay ink2 (SPEC §2: status colours never text).
        HStack(spacing: 5) {
            StatusGlyph(status: status, symbol: symbol)
            Text(text)
                .foregroundStyle(Color.studio(.ink2))
                .lineLimit(1)
                .accessibilityLabel([status.spokenName, text].compactMap { $0 }.joined(separator: ": "))
            if Action.self != EmptyView.self {
                action
                    .controlSize(.small)
                    .buttonStyle(.plain)
                    .foregroundStyle(Color.studio(.accentText))
                    .padding(.leading, 4)
            }
        }
        .font(LunalyrDesign.Studio.TypeScale.row)
        .fixedSize()
        .accessibilityElement(children: .contain)
    }
}

extension StatusChip where Action == EmptyView {
    init(_ status: LunalyrStatus, _ text: String, symbol: String? = nil) {
        self.init(status, text, symbol: symbol) { EmptyView() }
    }
}
