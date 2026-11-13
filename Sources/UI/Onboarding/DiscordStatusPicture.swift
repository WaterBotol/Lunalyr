import AppKit
import SwiftUI

/// The Discord status as it looks in a member list: a recording of the
/// real thing (`DiscordStatus.gif`, the line changing as the song plays) on
/// Discord's dark list. Still on its first frame under Reduce Motion, in a
/// snapshot and in tests.
struct DiscordStatusPicture: View {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.lunalyrRendersLiveContent) private var live

    var body: some View {
        DiscordStatusRecording(animates: live && !reduceMotion && !AppRuntime.isUnitTesting)
            .frame(width: LunalyrDesign.Onboarding.discordStatus.width, height: LunalyrDesign.Onboarding.discordStatus.height)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(RoundedRectangle(cornerRadius: LunalyrDesign.Radius.control, style: .continuous)
                .fill(LunalyrDesign.Onboarding.discordBacking))
            .overlay(RoundedRectangle(cornerRadius: LunalyrDesign.Radius.control, style: .continuous)
                .strokeBorder(Color.lunalyrContrastBorder, lineWidth: 1))
    }
}

/// The recording in an image view that plays GIFs and takes no clicks.
struct DiscordStatusRecording: NSViewRepresentable {
    var animates: Bool

    static let resource = "DiscordStatus"
    @MainActor static let image: NSImage? = Bundle.main.url(forResource: resource, withExtension: "gif")
        .flatMap(NSImage.init(contentsOf:))

    func makeNSView(context: Context) -> PassiveImageView {
        let view = PassiveImageView()
        view.image = Self.image
        view.imageScaling = .scaleProportionallyUpOrDown
        view.isEditable = false
        view.setContentHuggingPriority(.defaultLow, for: .horizontal)
        view.setContentHuggingPriority(.defaultLow, for: .vertical)
        view.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        view.setContentCompressionResistancePriority(.defaultLow, for: .vertical)
        view.animates = animates
        return view
    }

    func updateNSView(_ view: PassiveImageView, context: Context) {
        if view.animates != animates { view.animates = animates }
    }
}

/// An image view the pointer passes through.
final class PassiveImageView: NSImageView {
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
}
