import AppKit
import SwiftUI

/// The menu-bar panel: direction 2's short menu (work/ui-redesign-mockups/
/// final/SPEC.md section 7). Top to bottom: a waiting update, when the daily
/// check found one; the song, its transport and progress; the line being
/// sung; anything that needs the person's attention, with its fix; the Look
/// switcher; Lyric timing, Tap to sync…, Wallpaper ›, Player ›; the Pause,
/// Hide lyrics and Floating switches; Open Lyric Card…; listen-along once a
/// relay is set; then Settings…, Check for Updates… and Quit, like a menu.
/// Rows 24 high on frosted glass (opaque under Reduce Transparency), height
/// to content, scrolling only past the screen. Esc closes it; ⌘, opens
/// Settings. The pieces live in Sources/UI/MenuBar.
struct MenuBarView: View {
    @Model(\.nowPlaying) private var playing
    @Model(\.settings) private var settings
    @Model(\.extras) private var extras
    @Model(\.updates) private var updates
    @Environment(\.lunalyrModels) private var models
    @Environment(\.lunalyrLookStore) private var lookStore
    /// Where system-audio capture is in a failure and its recovery. Followed
    /// on its own: the capture controller also publishes meter levels many
    /// times a second, which must not redraw the panel.
    @State private var audioPhase = MenuBarAudioStatus.Phase.hidden
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        let statuses = MenuBarStatusBanners.raised(playing: playing, settings: settings, extras: extras,
                                                   audioNeedsAttention: audioPhase != .hidden)
        // One scroll view at every height, scrolling only once the panel
        // outgrows the screen: switching between a plain stack and a scroll
        // view rebuilt the panel, and lost what was typed in it.
        ScrollView {
            panel(statuses)
        }
        .scrollBounceBehavior(.basedOnSize)
        .frame(width: LunalyrDesign.Layout.menuPanelWidth)
        .frame(maxHeight: Self.maximumPanelHeight())
        .fixedSize(horizontal: false, vertical: true)
        .scrollContentBackground(.hidden)
        .background(MenuPanelGround())
        .tint(Color.studio(.accent))
        .background {
            // Esc closes the panel, like a menu.
            Button("Close") { LunalyrNavigation.dismissMenuBarPanel() }
                .keyboardShortcut(.cancelAction)
                .focusable(false)
                .frame(width: 0, height: 0)
                .opacity(0)
                .accessibilityHidden(true)
        }
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
        VStack(alignment: .leading, spacing: 0) {
            if updates.badge.showsBadge {
                // First, as the dot on the icon is what was clicked.
                MenuBarUpdateBanner()
                    .padding(.top, 6)
                    .focusSection()
                    .transition(.asymmetric(insertion: .opacity, removal: .identity))
                MenuPanelSeparator()
            }
            MenuBarNowPlaying()
                .focusSection()
            if !statuses.isEmpty {
                MenuPanelSeparator()
                MenuBarStatusBanners(statuses: statuses, audioPhase: audioPhase)
                    .focusSection()
                    .transition(.asymmetric(insertion: .opacity, removal: .identity))
            }
            MenuPanelSeparator()
            MenuBarLookSwitcher(store: MenuBarLooks.store(injected: lookStore, models: models.resolved))
                .focusSection()
            MenuPanelSeparator()
            VStack(alignment: .leading, spacing: 0) {
                MenuBarLyricTiming()
                MenuBarWallpaperControls()
            }
            .focusSection()
            MenuPanelSeparator()
            MenuBarToggles()
                .focusSection()
            MenuPanelSeparator()
            MenuBarLyricCardRow()
            if extras.listenAlongAvailable {
                MenuPanelSeparator()
                MenuBarListenAlong(listen: .shared)
                    .padding(.horizontal, MenuPanel.inset)
                    .padding(.vertical, 2)
                    .focusSection()
            }
            MenuPanelSeparator()
            MenuBarFooter()
                .focusSection()
        }
        .padding(.vertical, MenuPanel.verticalPadding)
        .frame(maxWidth: .infinity, alignment: .leading)
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion),
                   value: [statuses.isEmpty, updates.badge.showsBadge])
    }

    /// The panel's limit: the smallest screen's visible height, less room
    /// for the menu bar's gap and the window's shadow. 680 when no screen is
    /// known (the 13-inch Air's budget).
    static func maximumPanelHeight(screens: [NSRect]? = nil) -> CGFloat {
        let frames = screens ?? NSScreen.screens.map(\.visibleFrame)
        guard let smallest = frames.map(\.height).min(), smallest > 0 else { return fallbackPanelHeight }
        return max(320, smallest - 24)
    }

    static let fallbackPanelHeight: CGFloat = 680

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

    /// How the icon says an update is waiting. One line picks it:
    /// `updateIconStyle`.
    enum UpdateIconStyle: CaseIterable {
        /// The moon with a small template dot in its upper right corner.
        case dot
        /// A different icon altogether: a violet disc with a bite out of
        /// its upper right, like the moon, and a white arrow pointing down.
        /// In colour, so it isn't a template.
        case arrowMoon
    }

    static let updateIconStyle: UpdateIconStyle = .arrowMoon

    /// The menu-bar icon for the moment: plain, with the pause badge (lower
    /// right), with an update waiting (`style`), or both.
    ///
    /// `.dot` stays a template, so the system colours it like the plain
    /// icon. `.arrowMoon` is in colour, not a template: a saturated violet
    /// with white on it reads on a light and a dark menu bar alike, which
    /// matters as recent macOS takes the bar's light or dark from the
    /// wallpaper. Drawn at `scales` (1x and 2x for the menu bar).
    static func badgedIcon(from icon: NSImage, paused: Bool, updateAvailable: Bool,
                           style: UpdateIconStyle = updateIconStyle, scales: [CGFloat] = [1, 2]) -> NSImage {
        guard paused || updateAvailable else { return icon }
        let coloured = updateAvailable && style == .arrowMoon
        let image = NSImage(size: icon.size)
        // Drawn once into bitmaps of its own: the badges are cut out with a
        // clearing blend, which must never reach the menu bar.
        for scale in scales {
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
            let cg = context.cgContext
            if coloured {
                drawArrowMoon(in: rect, paused: paused, context: cg)
                if paused { drawBadge(in: rect, colour: updateColour, context: cg) }
            } else {
                icon.draw(in: rect)
                if paused { drawBadge(in: rect, colour: nil, context: cg) }
                if updateAvailable { drawUpdateDot(in: rect, context: cg) }
            }
            NSGraphicsContext.restoreGraphicsState()
            image.addRepresentation(bitmap)
        }
        image.isTemplate = !coloured
        image.accessibilityDescription = accessibilityLabel(paused: paused, updateAvailable: updateAvailable)
        return image
    }

    /// Lunalyr's violet, as it fills under white in dark mode: light enough
    /// to stand out on a dark menu bar, deep enough for white on it and to
    /// show on a light one.
    static let updateColour: CGColor = (LunalyrAccent.fill(dark: true, increaseContrast: false)
        .usingColorSpace(.sRGB) ?? .systemPurple).cgColor

    /// The `.arrowMoon` icon: the disc, its moon bite and the arrow. Smaller
    /// and up to the left with the pause badge, which takes the lower right.
    private static func drawArrowMoon(in rect: CGRect, paused: Bool, context: CGContext) {
        let unit = rect.width / 18
        let radius = (paused ? 6.6 : 7.6) * unit
        let centre = paused ? CGPoint(x: rect.minX + 7.4 * unit, y: rect.minY + 10.4 * unit)
                            : CGPoint(x: rect.midX - 0.4 * unit, y: rect.midY - 0.4 * unit)
        context.setFillColor(updateColour)
        context.fillEllipse(in: CGRect(x: centre.x - radius, y: centre.y - radius, width: radius * 2, height: radius * 2))
        // The bite, as the crescent's.
        let bite = radius * 0.6, offset = radius * 0.92
        context.setBlendMode(.clear)
        context.fillEllipse(in: CGRect(x: centre.x + offset - bite, y: centre.y + offset - bite, width: bite * 2, height: bite * 2))
        context.setBlendMode(.normal)
        // The arrow, a little left of centre to clear the bite.
        let x = centre.x - radius * 0.06, y = centre.y - radius * 0.02
        context.setFillColor(CGColor(gray: 1, alpha: 1))
        let shaft = radius * 0.15
        context.fill(CGRect(x: x - shaft, y: y - radius * 0.02, width: shaft * 2, height: radius * 0.6))
        context.beginPath()
        context.move(to: CGPoint(x: x - radius * 0.46, y: y + radius * 0.04))
        context.addLine(to: CGPoint(x: x + radius * 0.46, y: y + radius * 0.04))
        context.addLine(to: CGPoint(x: x, y: y - radius * 0.56))
        context.closePath()
        context.fillPath()
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

    /// The pause badge: a disc set apart by a clear ring, with the pause
    /// bars cut out of it (template), or white on `colour`.
    private static func drawBadge(in rect: CGRect, colour: CGColor?, context: CGContext) {
        let diameter = (rect.width * (colour == nil ? 0.5 : 0.46)).rounded()
        let badge = CGRect(x: rect.maxX - diameter, y: rect.minY, width: diameter, height: diameter)
        // A clear ring sets the badge apart from the mark beneath it.
        context.setBlendMode(.clear)
        context.fillEllipse(in: badge.insetBy(dx: -1.25, dy: -1.25))
        context.setBlendMode(.normal)
        context.setFillColor(colour ?? NSColor.black.cgColor)
        context.fillEllipse(in: badge)
        // The pause bars.
        if colour == nil {
            context.setBlendMode(.clear)
        } else {
            context.setFillColor(CGColor(gray: 1, alpha: 1))
        }
        let bar = CGSize(width: max(1, diameter * 0.16), height: diameter * 0.46)
        for centre in [badge.midX - diameter * 0.15, badge.midX + diameter * 0.15] {
            context.fill(CGRect(x: centre - bar.width / 2, y: badge.midY - bar.height / 2, width: bar.width, height: bar.height))
        }
        context.setBlendMode(.normal)
    }
}
