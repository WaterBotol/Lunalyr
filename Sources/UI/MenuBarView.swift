import AppKit
import SwiftUI

/// The menu-bar panel (gui/spec.md §3.3), top to bottom: a waiting update,
/// when the daily check found one; the song, its
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
    @Model(\.updates) private var updates
    @Environment(\.lunalyrModels) private var models
    /// Where system-audio capture is in a failure and its recovery. Followed
    /// on its own: the capture controller also publishes meter levels many
    /// times a second, which must not redraw the panel.
    @State private var audioPhase = MenuBarAudioStatus.Phase.hidden
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        let statuses = MenuBarStatusBanners.raised(playing: playing, settings: settings, extras: extras,
                                                   audioNeedsAttention: audioPhase != .hidden)
        // One scroll view at every height, scrolling only once the panel
        // outgrows its limit: switching between a plain stack and a scroll
        // view rebuilt the panel, and lost what was typed in it.
        ScrollView {
            panel(statuses)
        }
        .scrollBounceBehavior(.basedOnSize)
        .frame(width: LunalyrDesign.Layout.menuPanelWidth)
        .frame(maxHeight: Self.maximumPanelHeight)
        .fixedSize(horizontal: false, vertical: true)
        .styledCanvas()
        .lunalyrTint()
        .onReceive(models.resolved.audio.$state.removeDuplicates()) {
            audioPhase = MenuBarAudioStatus.phase(after: audioPhase, state: $0)
        }
        .task(id: audioPhase) {
            guard audioPhase == .recovered else { return }
            try? await Task.sleep(for: .seconds(MenuBarAudioStatus.recoveredHold))
            guard !Task.isCancelled else { return }
            audioPhase = .hidden
        }
    }

    private func panel(_ statuses: [MenuBarStatus]) -> some View {
        VStack(alignment: .leading, spacing: Self.groupSpacing) {
            if updates.badge.showsBadge {
                // First, as the dot on the icon is what was clicked.
                MenuBarUpdateBanner()
                    .focusSection()
                    .transition(.asymmetric(insertion: .opacity, removal: .identity))
            }
            MenuBarNowPlaying()
                .menuCard()
                .focusSection()
            if !statuses.isEmpty {
                MenuBarStatusBanners(statuses: statuses, audioPhase: audioPhase)
                    .focusSection()
                    .transition(.asymmetric(insertion: .opacity, removal: .identity))
            }
            MenuBarWallpaperControls()
                .menuCard()
                .focusSection()
            if extras.listenAlongAvailable {
                MenuBarListenAlong(listen: .shared)
                    .menuCard()
                    .focusSection()
            }
            MenuBarFooter()
                .focusSection()
        }
        .padding(LunalyrDesign.Spacing.panel)
        .frame(maxWidth: .infinity, alignment: .leading)
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion),
                   value: [statuses.isEmpty, updates.badge.showsBadge])
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
        badgedIcon(from: icon, paused: true, updateAvailable: false)
    }

    /// The menu-bar icon for the moment: plain, with the pause badge (lower
    /// right), with the update dot (upper right), or both.
    ///
    /// The update dot is a solid disc cut out of the mark by a clear ring,
    /// like a notification dot, and like the rest of the icon a template:
    /// a coloured dot can't follow the menu bar, which on recent macOS takes
    /// its light or dark from the wallpaper -- Lunalyr's own wallpaper,
    /// often dark album art behind a light-mode menu bar.
    static func badgedIcon(from icon: NSImage, paused: Bool, updateAvailable: Bool) -> NSImage {
        guard paused || updateAvailable else { return icon }
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
            let rect = CGRect(origin: .zero, size: icon.size)
            icon.draw(in: rect)
            if paused { drawBadge(in: rect, context: context.cgContext) }
            if updateAvailable { drawUpdateDot(in: rect, context: context.cgContext) }
            NSGraphicsContext.restoreGraphicsState()
            image.addRepresentation(bitmap)
        }
        image.isTemplate = true
        image.accessibilityDescription = accessibilityLabel(paused: paused, updateAvailable: updateAvailable)
        return image
    }

    /// What VoiceOver says for the status item.
    static func accessibilityLabel(paused: Bool, updateAvailable: Bool) -> String {
        var parts = ["Lunalyr"]
        if paused { parts.append("wallpaper paused") }
        if updateAvailable { parts.append("update available") }
        return parts.joined(separator: ", ")
    }

    /// A solid dot in the upper right corner, set apart by a clear ring.
    private static func drawUpdateDot(in rect: CGRect, context: CGContext) {
        let diameter = (rect.width * 0.42).rounded()
        let dot = CGRect(x: rect.maxX - diameter, y: rect.maxY - diameter, width: diameter, height: diameter)
        context.setBlendMode(.clear)
        context.fillEllipse(in: dot.insetBy(dx: -1.5, dy: -1.5))
        context.setBlendMode(.normal)
        context.setFillColor(NSColor.black.cgColor)
        context.fillEllipse(in: dot)
    }

    private static func drawBadge(in rect: CGRect, context: CGContext) {
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

extension View {
    /// A soft rounded card that groups one part of the menu-bar panel.
    func menuCard() -> some View {
        padding(LunalyrDesign.Spacing.medium + 2)
            .frame(maxWidth: .infinity, alignment: .leading)
            .styledSurface(radius: LunalyrDesign.Radius.preview)
    }
}
