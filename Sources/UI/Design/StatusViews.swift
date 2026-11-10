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

    private var padding: CGFloat { density == .compact ? LunalyrDesign.Spacing.small + 2 : LunalyrDesign.Spacing.medium }

    var body: some View {
        VStack(alignment: .leading, spacing: density == .compact ? 6 : LunalyrDesign.Spacing.small) {
            HStack(alignment: .firstTextBaseline, spacing: LunalyrDesign.Spacing.small) {
                Image(systemName: symbol ?? status.symbol)
                    .foregroundStyle(status.text)
                    .imageScale(.medium)
                    .contentTransition(LunalyrDesign.Motion.symbolSwap(reduceMotion: reduceMotion))
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 3) {
                    Text(title)
                        .font(density == .compact ? .callout.weight(.semibold) : .body.weight(.semibold))
                        .fixedSize(horizontal: false, vertical: true)
                        .contentTransition(.opacity)
                    if let message {
                        Text(message).lunalyrText(.caption)
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
                .controlSize(density == .compact ? .small : .regular)
                .padding(.leading, symbolIndent)
            }
        }
        .padding(padding)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: LunalyrDesign.Radius.card, style: .continuous)
                .fill(status.fill)
        )
        .overlay(
            RoundedRectangle(cornerRadius: LunalyrDesign.Radius.card, style: .continuous)
                .strokeBorder(status.border, lineWidth: 1)
        )
        // A banner that changes its state (a reconnect, a refusal answered)
        // cross-fades rather than cutting.
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion),
                   value: [status.rawValue, title, message ?? "", symbol ?? ""])
        .accessibilityElement(children: .contain)
    }

    /// Lines the actions up under the title, past the symbol.
    private var symbolIndent: CGFloat { 22 }

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
        HStack(spacing: 6) {
            Image(systemName: symbol ?? status.symbol)
                .foregroundStyle(status.text)
                .accessibilityHidden(true)
            Text(text)
                .lineLimit(1)
                .accessibilityLabel([status.spokenName, text].compactMap { $0 }.joined(separator: ": "))
            if Action.self != EmptyView.self {
                action
                    .controlSize(.small)
                    .buttonStyle(.borderless)
                    .fontWeight(.semibold)
                    .foregroundStyle(Color.lunalyrAccentText)
            }
        }
        .font(.callout)
        .padding(.leading, 10)
        .padding(.trailing, Action.self == EmptyView.self ? 10 : 8)
        .padding(.vertical, 4)
        .background(Capsule(style: .continuous).fill(status.fill))
        .overlay(Capsule(style: .continuous).strokeBorder(status.border, lineWidth: 1))
        .fixedSize()
        .accessibilityElement(children: .contain)
    }
}

extension StatusChip where Action == EmptyView {
    init(_ status: LunalyrStatus, _ text: String, symbol: String? = nil) {
        self.init(status, text, symbol: symbol) { EmptyView() }
    }
}
