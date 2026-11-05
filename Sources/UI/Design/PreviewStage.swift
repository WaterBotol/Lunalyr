import SwiftUI

// MARK: - The preview stage
//
// The wallpaper preview is the centrepiece of Settings and of onboarding's
// "pick a look": a desktop in miniature, framed the same way wherever it
// appears. The frame keeps the desktop's shape, rounds it, lifts it off the
// stage with a soft shadow and lays a faint menu bar across its top so it
// reads as a screen, not a picture:
//
//     PreviewStage(desktop: previewDisplay.size) {
//         SettingsLivePreview(desktopSize: previewDisplay.size, desktopPicture: picture)
//     }
//     .padding(LunalyrDesign.Spacing.window)
//     .background(Color.lunalyrStage)
//
// In snapshots (and wherever `lunalyrRendersLiveContent` is off) put
// `LiveContentPlaceholder` inside instead of live content.

struct PreviewStage<Content: View>: View {
    /// The desktop's size in points; only its shape matters.
    let desktop: CGSize
    var cornerRadius: CGFloat = LunalyrDesign.Radius.preview
    /// The faint menu bar across the top.
    var showsMenuBar = true
    let content: Content

    init(desktop: CGSize, cornerRadius: CGFloat = LunalyrDesign.Radius.preview, showsMenuBar: Bool = true,
         @ViewBuilder content: () -> Content) {
        self.desktop = desktop
        self.cornerRadius = cornerRadius
        self.showsMenuBar = showsMenuBar
        self.content = content()
    }

    /// Behind the stage (`Color.lunalyrStage`).
    static var backdrop: Color { .lunalyrStage }

    var body: some View {
        content
            .aspectRatio(max(0.2, desktop.width / max(1, desktop.height)), contentMode: .fit)
            .overlay(alignment: .top) {
                if showsMenuBar {
                    GeometryReader { proxy in
                        // A real menu bar is 24-37 points on a 900-982 point screen.
                        Rectangle()
                            .fill(.white.opacity(0.14))
                            .frame(height: max(3, proxy.size.height * 0.03))
                    }
                    .allowsHitTesting(false)
                    .accessibilityHidden(true)
                }
            }
            .clipShape(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
            .overlay(
                RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                    .strokeBorder(.primary.opacity(0.12), lineWidth: 0.5)
            )
            .overlay(
                RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                    .strokeBorder(Color.lunalyrContrastBorder, lineWidth: 1)
            )
            .shadow(color: .black.opacity(0.22), radius: 14, y: 6)
    }
}

extension Color {
    /// Behind the preview stage: the system's under-page grey, set apart
    /// from the window and the inspector beside it.
    static var lunalyrStage: Color { Color(nsColor: .underPageBackgroundColor) }
}
