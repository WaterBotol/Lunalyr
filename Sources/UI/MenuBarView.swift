import AppKit
import SwiftUI

/// The menu-bar panel (gui/spec.md §3.3), top to bottom: the song, its
/// transport, the line being sung and this song's lyric timing; anything
/// that needs the person's attention, with its fix; the wallpaper's quick
/// controls; then Player, Settings…, Check for Updates… and Quit, like a menu.
/// Each piece is a shared component (Sources/UI/Design), so it reads exactly
/// as it does in Settings and onboarding. The pieces live in
/// Sources/UI/MenuBar.
struct MenuBarView: View {
    @Model(\.nowPlaying) private var playing
    @Model(\.settings) private var settings
    @Model(\.extras) private var extras
    @Environment(\.lunalyrModels) private var models
    /// Whether system-audio capture needs attention. Followed on its own:
    /// the capture controller also publishes meter levels many times a
    /// second, which must not redraw the panel.
    @State private var audioNeedsAttention = false

    var body: some View {
        let statuses = MenuBarStatusBanners.raised(playing: playing, settings: settings, extras: extras,
                                                   audioNeedsAttention: audioNeedsAttention)
        ViewThatFits(in: .vertical) {
            panel(statuses)
                .fixedSize(horizontal: false, vertical: true)
            ScrollView {
                panel(statuses)
            }
            .frame(height: Self.maximumPanelHeight)
        }
        .frame(width: LunalyrDesign.Layout.menuPanelWidth)
        .frame(maxHeight: Self.maximumPanelHeight)
        .lunalyrTint()
        .onReceive(models.resolved.audio.$state.map(\.needsAttention).removeDuplicates()) { audioNeedsAttention = $0 }
    }

    private func panel(_ statuses: [MenuBarStatus]) -> some View {
        VStack(alignment: .leading, spacing: Self.groupSpacing) {
            MenuBarNowPlaying()
                .focusSection()
            if !statuses.isEmpty {
                MenuBarStatusBanners(statuses: statuses)
                    .focusSection()
            }
            Divider()
            MenuBarWallpaperControls()
                .focusSection()
            Divider()
            MenuBarFooter()
                .focusSection()
        }
        .padding(LunalyrDesign.Spacing.panel)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    /// Leaves room for the menu bar and popover chrome on a 13-inch display.
    static let maximumPanelHeight: CGFloat = 680

    /// Between the panel's groups: a little under the spec's 14, which with
    /// a status showing took the panel past its 700-point budget.
    static let groupSpacing: CGFloat = 12

    // MARK: Kept for callers outside the panel

    /// "Play music" / "Pause music": named for the music, as the wallpaper
    /// has a Pause of its own.
    static func transportTitle(playing: Bool) -> String { TransportControls.title(playing: playing) }

    /// The artist, or what kind of thing is playing, or the idle prompt.
    static func subtitle(for track: NowPlayingMonitor.Track?, idlePrompt: String) -> String {
        TrackSummary<EmptyView>.subtitle(for: track, idlePrompt: idlePrompt)
    }

    /// Stands in for a missing cover (the desktop's hover controls use it too).
    static func placeholderSymbol(for track: NowPlayingMonitor.Track?) -> String {
        TrackSummary<EmptyView>.placeholderSymbol(for: track)
    }

    // MARK: The menu-bar icon

    /// Lunalyr's menu-bar icon with a pause badge in its lower right corner,
    /// shown while the wallpaper is paused: the one cue that is always in
    /// view, as the menu-bar line is off by default. Still a template, so
    /// the system colours it exactly like the plain icon.
    static func pausedIcon(from icon: NSImage) -> NSImage {
        let image = NSImage(size: icon.size)
        // Drawn once into bitmaps of its own, at 1x and 2x: the badge is cut
        // out with a clearing blend, which must never reach the menu bar.
        for scale in [1, 2] as [CGFloat] {
            guard let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(icon.size.width * scale),
                                                pixelsHigh: Int(icon.size.height * scale), bitsPerSample: 8,
                                                samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                                colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else { continue }
            // Sized in points before its context is made, so the context
            // draws in points at this scale.
            bitmap.size = icon.size
            guard let context = NSGraphicsContext(bitmapImageRep: bitmap) else { continue }
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = context
            drawBadge(over: icon, in: CGRect(origin: .zero, size: icon.size), context: context.cgContext)
            NSGraphicsContext.restoreGraphicsState()
            image.addRepresentation(bitmap)
        }
        image.isTemplate = true
        image.accessibilityDescription = "Lunalyr, wallpaper paused"
        return image
    }

    private static func drawBadge(over icon: NSImage, in rect: CGRect, context: CGContext) {
        icon.draw(in: rect)
        let diameter = (rect.width * 0.5).rounded()
        let badge = CGRect(x: rect.maxX - diameter, y: rect.minY, width: diameter, height: diameter)
        // A clear ring sets the badge apart from the mark beneath it.
        context.setBlendMode(.clear)
        context.fillEllipse(in: badge.insetBy(dx: -1.25, dy: -1.25))
        context.setBlendMode(.normal)
        context.setFillColor(NSColor.black.cgColor)
        context.fillEllipse(in: badge)
        // The pause bars, cut out of the disc.
        context.setBlendMode(.clear)
        let bar = CGSize(width: max(1, diameter * 0.16), height: diameter * 0.46)
        for centre in [badge.midX - diameter * 0.15, badge.midX + diameter * 0.15] {
            context.fill(CGRect(x: centre - bar.width / 2, y: badge.midY - bar.height / 2, width: bar.width, height: bar.height))
        }
        context.setBlendMode(.normal)
    }
}
