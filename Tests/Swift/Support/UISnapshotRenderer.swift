import AppKit
import SwiftUI
@testable import VersoCustom

/// Draws a SwiftUI view, hosted in a real but never-shown window, into an
/// image: what `UISnapshotTests` writes to PNG.
///
/// `NSView.cacheDisplay` draws SwiftUI and AppKit controls faithfully, but
/// not what only the window server composites: Liquid Glass and other
/// materials, vibrancy, and Metal or WebKit layers. So the renderer:
///  - hides every `NSVisualEffectView` and paints a flat stand-in for its
///    material, and paints glass (`NSGlassEffectView`, the macOS 26 sidebar
///    and toolbar buttons) as a faint flat panel;
///  - repaints each glass panel that holds a list (the sidebar): the panel,
///    its selection as a neutral fill, then the list captured on its own on
///    top -- vibrant dark-mode text and symbols, drawn in dark greys for the
///    window server's colour matrix, inverted back to light (coloured icon
///    badges are left as they are);
///  - draws the window as the key window of an active app (see
///    `UISnapshotWindow`), so switches, sliders and prominent buttons show
///    their accent colours rather than an inactive window's grey;
///  - sets `lunalyrRendersLiveContent` false, so the live preview and other
///    Metal/WebKit content show `LiveContentPlaceholder`.
/// Selections in sidebars come out as a neutral fill. Everything else is the
/// app's own drawing at 2x.
@MainActor
enum UISnapshotRenderer {
    /// What surrounds the view.
    enum Chrome {
        /// A titled window, configured as the app configures it.
        case window(styleMask: NSWindow.StyleMask, configure: @MainActor (NSWindow) -> Void)
        /// The menu-bar panel: a rounded panel on a desktop-like backdrop,
        /// `margin` points from the image edge.
        case panel(cornerRadius: CGFloat, margin: CGFloat)
        /// The view alone on the window background.
        case none
    }

    /// How big to draw.
    enum Size {
        /// The window's frame, title bar included (what a person sees).
        case frame(CGSize)
        /// This content width (0: the view's own); as tall as the view
        /// wants, up to `maxHeight`.
        case fitting(width: CGFloat, maxHeight: CGFloat)
        /// A frame this wide and tall, then trimmed at the bottom where the
        /// last rows only repeat the background: a whole scrolling page.
        case fullPage(width: CGFloat, height: CGFloat)
    }

    enum Look: String, CaseIterable {
        case light, dark
        /// Accessibility › Display › Increase Contrast, as an appearance.
        case lightContrast = "light-contrast"
        case darkContrast = "dark-contrast"

        var appearanceName: NSAppearance.Name {
            switch self {
            case .light: return .aqua
            case .dark: return .darkAqua
            case .lightContrast: return .accessibilityHighContrastAqua
            case .darkContrast: return .accessibilityHighContrastDarkAqua
            }
        }

        var isDark: Bool { self == .dark || self == .darkContrast }
        var increasesContrast: Bool { self == .lightContrast || self == .darkContrast }
    }

    nonisolated static let scale: CGFloat = 2

    /// `pump` runs between layout passes: work the view started (a `.task`,
    /// a check sent to the fake players) gets done before the capture.
    static func render<Content: View>(_ content: Content, chrome: Chrome, size: Size, look: Look,
                                      pump: @MainActor () -> Void = {}) throws -> CGImage {
        guard let appearance = NSAppearance(named: look.appearanceName) else { throw SnapshotError.noAppearance(look.rawValue) }
        // Lunalyr's own dynamic colours follow Increase Contrast too.
        LunalyrAppearance.simulatedIncreaseContrast = look.increasesContrast ? true : nil
        defer { LunalyrAppearance.simulatedIncreaseContrast = nil }
        let rooted = content
            .environment(\.controlActiveState, .key)
            .environment(\.lunalyrRendersLiveContent, false)
        let root: AnyView
        if case .fitting(let width, _) = size, width > 0 {
            // The hosting view sizes the window to the view's ideal size;
            // pin the width so that is the one asked for.
            root = AnyView(rooted.frame(width: width, alignment: .topLeading))
        } else {
            root = AnyView(rooted)
        }
        let hosting = NSHostingView(rootView: root)
        let window: UISnapshotWindow
        switch chrome {
        case .window(let styleMask, let configure):
            window = UISnapshotWindow(contentRect: NSRect(x: -30_000, y: -30_000, width: 800, height: 600),
                                      styleMask: styleMask, backing: .buffered, defer: false)
            configure(window)
        case .panel:
            // The panel's own background is drawn around the capture.
            window = UISnapshotWindow(contentRect: NSRect(x: -30_000, y: -30_000, width: 400, height: 400),
                                      styleMask: [.borderless], backing: .buffered, defer: false)
            window.backgroundColor = .clear
            window.isOpaque = false
        case .none:
            window = UISnapshotWindow(contentRect: NSRect(x: -30_000, y: -30_000, width: 400, height: 400),
                                      styleMask: [.borderless], backing: .buffered, defer: false)
            window.backgroundColor = .windowBackgroundColor
            window.isOpaque = true
        }
        window.isReleasedWhenClosed = false
        window.appearance = appearance
        window.animationBehavior = .none
        window.contentView = hosting
        defer {
            window.contentView = nil
            window.close()
        }

        // Laid out once first: SwiftUI adds the toolbar as the view meets
        // the window, which changes the title bar's height.
        settle(window, pump: pump)
        for _ in 0..<2 {
            switch size {
            case .frame(let frame):
                window.setFrame(NSRect(origin: window.frame.origin, size: frame), display: false)
            case .fullPage(let width, let height):
                window.setFrame(NSRect(origin: window.frame.origin, size: CGSize(width: width, height: height)), display: false)
            case .fitting(let width, let maxHeight):
                // Again after appearing: some views size themselves then.
                let fitting = hosting.fittingSize
                window.setContentSize(CGSize(width: width > 0 ? width : max(1, fitting.width),
                                             height: min(maxHeight, max(1, fitting.height))))
            }
            settle(window, pump: pump)
        }

        if ProcessInfo.processInfo.environment["LUNALYR_UI_SNAPSHOT_DEBUG"] == "1" {
            print("[ui-snapshot-debug] frame \(window.frame.size) content \(window.contentView?.frame.size ?? .zero) layout \(window.contentLayoutRect.size) theme \(window.contentView?.superview?.bounds.size ?? .zero) sizing \(hosting.sizingOptions.rawValue) intrinsic \(hosting.intrinsicContentSize) fitting \(hosting.fittingSize)")
        }
        var image = try composite(window: window, appearance: appearance, dark: look.isDark)
        if case .fullPage = size { image = trimBottom(image) }
        if case .panel(let radius, let margin) = chrome {
            image = try panel(image, radius: radius, margin: margin, appearance: appearance)
        }
        return image
    }

    /// Lays out, draws and lets appear/task/onReceive work run, twice.
    private static func settle(_ window: NSWindow, pump: @MainActor () -> Void) {
        for _ in 0..<2 {
            window.layoutIfNeeded()
            window.contentView?.layoutSubtreeIfNeeded()
            window.displayIfNeeded()
            for _ in 0..<3 {
                pump()
                RunLoop.main.run(until: Date().addingTimeInterval(0.05))
            }
        }
    }

    // MARK: - Compositing

    private static func composite(window: NSWindow, appearance: NSAppearance, dark: Bool) throws -> CGImage {
        guard let frameView = window.contentView?.superview ?? window.contentView else { throw SnapshotError.noContent }
        let bounds = frameView.bounds
        guard bounds.width > 0, bounds.height > 0 else { throw SnapshotError.noContent }
        let glassClass: AnyClass? = NSClassFromString("NSGlassEffectView")
        let everything = descendants(of: frameView)
        let glasses = everything.filter { view in glassClass.map { view.isKind(of: $0) } ?? false }
        let inGlass = Set(glasses.flatMap { descendants(of: $0) }.map(ObjectIdentifier.init))
        let effects = everything.compactMap { $0 as? NSVisualEffectView }.filter { !$0.isHidden && $0.alphaValue > 0 }
        let debugging = ProcessInfo.processInfo.environment["LUNALYR_UI_SNAPSHOT_DEBUG"] == "1"
        if debugging {
            for effect in effects {
                print("[ui-snapshot-debug] effect \(effect.material.rawValue) \(effect.convert(effect.bounds, to: frameView)) in glass \(inGlass.contains(ObjectIdentifier(effect)))")
            }
            for glass in glasses { print("[ui-snapshot-debug] glass \(glass.convert(glass.bounds, to: frameView))") }
            for table in everything.compactMap({ $0 as? NSTableView }) {
                var chain: [String] = []
                var view: NSView? = table
                while let current = view { chain.append(String(describing: type(of: current)).prefix(28).description); view = current.superview }
                print("[ui-snapshot-debug] table chain \(chain.joined(separator: " < "))")
            }
        }
        // Materials draw as nothing (or black) here: hide them, paint stand-ins.
        effects.forEach { $0.isHidden = true }
        defer { effects.forEach { $0.isHidden = false } }
        frameView.displayIfNeeded()
        let window = capture(frameView)
        // A sidebar list in glass is drawn for the window server's vibrant
        // blend: captured with the window, dark-mode sidebar text comes out
        // dark, and nothing shows its selection. So each glass panel that
        // holds a list is painted over -- the panel, its selections -- and
        // the list captured on its own is drawn on top, vibrant dark content
        // inverted back to light. Other glass (toolbar buttons) draws right.
        typealias List = (image: CGImage, rect: CGRect)
        var sidebars: [(glass: NSView, lists: [List])] = []
        for glass in glasses where !glass.isHidden && glass.alphaValue > 0 {
            let tables = descendants(of: glass).compactMap { $0 as? NSTableView }.filter { !$0.isHidden }
            guard !tables.isEmpty else { continue }
            let lists: [List] = tables.compactMap { table in
                guard var image = capture(table) else { return nil }
                if isVibrantDark(table.effectiveAppearance) { image = invertGreys(image) ?? image }
                return (image, table.convert(table.bounds, to: frameView))
            }
            sidebars.append((glass, lists))
        }
        if debugging {
            for glass in glasses {
                print("[ui-snapshot-debug] glass \(glass.convert(glass.bounds, to: frameView)) appearance \(glass.effectiveAppearance.name.rawValue) lists \(sidebars.first { $0.glass === glass }?.lists.count ?? 0)")
            }
        }
        if let dir = ProcessInfo.processInfo.environment["LUNALYR_UI_SNAPSHOT_DEBUG_DIR"] {
            if let window { try? writePNG(window, to: URL(fileURLWithPath: dir).appendingPathComponent("debug-frame.png")) }
            for (index, list) in sidebars.flatMap(\.lists).enumerated() {
                try? writePNG(list.image, to: URL(fileURLWithPath: dir).appendingPathComponent("debug-list-\(index).png"))
            }
        }

        let width = Int((bounds.width * scale).rounded()), height = Int((bounds.height * scale).rounded())
        guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                      space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
        else { throw SnapshotError.noContext }
        context.scaleBy(x: scale, y: scale)
        let graphics = NSGraphicsContext(cgContext: context, flipped: false)
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = graphics
        defer { NSGraphicsContext.restoreGraphicsState() }

        // Back to front: materials outside glass, the window, glass panels;
        // then, over each sidebar, its panel, selections and list again.
        typealias Patch = (rect: CGRect, radius: CGFloat, material: NSVisualEffectView.Material?)
        func patch(_ effect: NSVisualEffectView) -> Patch {
            (effect.convert(effect.bounds, to: frameView), effect.material == .selection ? 6 : 0, effect.material)
        }
        func glassPatch(_ glass: NSView) -> Patch {
            (glass.convert(glass.bounds, to: frameView), (glass.value(forKey: "cornerRadius") as? CGFloat) ?? 0, nil)
        }
        func fill(_ patches: [Patch]) {
            appearance.performAsCurrentDrawingAppearance {
                for patch in patches {
                    // nil is glass: a faint panel over what is behind it.
                    let colour = patch.material.map { standIn(for: $0, dark: dark) }
                        ?? (dark ? NSColor(white: 1, alpha: 0.06) : NSColor(white: 0, alpha: 0.04))
                    colour.setFill()
                    NSBezierPath(roundedRect: patch.rect, xRadius: patch.radius, yRadius: patch.radius).fill()
                }
            }
        }
        if frameView.window?.isOpaque ?? true {
            appearance.performAsCurrentDrawingAppearance {
                NSColor.windowBackgroundColor.setFill()
                bounds.fill()
            }
        }
        fill(effects.filter { !inGlass.contains(ObjectIdentifier($0)) }.map(patch))
        if let window { draw(window, in: bounds) }
        fill(glasses.filter { glass in !glass.isHidden && !sidebars.contains { $0.glass === glass } }.map(glassPatch))
        for sidebar in sidebars {
            let panel = glassPatch(sidebar.glass)
            NSGraphicsContext.saveGraphicsState()
            NSBezierPath(roundedRect: panel.rect, xRadius: panel.radius, yRadius: panel.radius).addClip()
            fill([(panel.rect, panel.radius, .windowBackground), panel])
            let inside = Set(descendants(of: sidebar.glass).map(ObjectIdentifier.init))
            fill(effects.filter { inside.contains(ObjectIdentifier($0)) }.map(patch))
            for list in sidebar.lists { draw(list.image, in: list.rect) }
            NSGraphicsContext.restoreGraphicsState()
        }
        guard let image = context.makeImage() else { throw SnapshotError.noContext }
        return image
    }

    /// A flat colour for a material only the window server can draw.
    nonisolated private static func standIn(for material: NSVisualEffectView.Material, dark: Bool) -> NSColor {
        switch material {
        case .selection:
            return .unemphasizedSelectedContentBackgroundColor
        case .contentBackground:
            return .controlBackgroundColor
        case .underPageBackground:
            return .underPageBackgroundColor
        case .hudWindow, .fullScreenUI:
            return NSColor(white: dark ? 0.12 : 0.22, alpha: 0.92)
        case .sidebar:
            return NSColor.windowBackgroundColor.blended(withFraction: 0.04, of: dark ? .white : .black) ?? .windowBackgroundColor
        default:
            return .windowBackgroundColor
        }
    }

    private static func descendants(of view: NSView) -> [NSView] {
        [view] + view.subviews.flatMap(descendants)
    }

    private static func capture(_ view: NSView, rect: NSRect? = nil) -> CGImage? {
        let rect = rect ?? view.bounds
        guard rect.width > 0, rect.height > 0,
              let rep = view.bitmapImageRepForCachingDisplay(in: rect) else { return nil }
        view.cacheDisplay(in: rect, to: rep)
        return rep.cgImage
    }

    private static func draw(_ image: CGImage, in rect: CGRect) {
        NSGraphicsContext.current?.cgContext.draw(image, in: rect)
    }

    private static func isVibrantDark(_ appearance: NSAppearance) -> Bool {
        appearance.name == .vibrantDark || appearance.name == .accessibilityHighContrastVibrantDark
    }

    /// Vibrant dark-mode text and symbols as the window server would show
    /// them: the dark greys they are drawn in become light, alpha kept.
    /// Coloured pixels (icon badges) and light ones (their white glyphs) are
    /// drawn as they are, so they stay. Premultiplied, so an inverted channel
    /// becomes alpha less itself.
    private static func invertGreys(_ image: CGImage) -> CGImage? {
        guard var data = pixels(image) else { return nil }
        for offset in stride(from: 0, to: data.count, by: 4) {
            let alpha = Int(data[offset + 3])
            guard alpha > 0 else { continue }
            let channels = (0..<3).map { Int(data[offset + $0]) * 255 / alpha }
            let brightest = channels.max() ?? 0, chroma = brightest - (channels.min() ?? 0)
            guard chroma < 40, brightest < 150 else { continue }
            for channel in 0..<3 {
                data[offset + channel] = UInt8(alpha) - min(UInt8(alpha), data[offset + channel])
            }
        }
        return data.withUnsafeMutableBytes { bytes in
            CGContext(data: bytes.baseAddress, width: image.width, height: image.height, bitsPerComponent: 8,
                      bytesPerRow: image.width * 4, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)?.makeImage()
        }
    }

    // MARK: - Trimming and panels

    /// Drops bottom rows that only repeat the last row, keeping a margin.
    nonisolated static func trimBottom(_ image: CGImage, margin: Int = 24) -> CGImage {
        guard let data = pixels(image) else { return image }
        let width = image.width, height = image.height, stride = width * 4
        func row(_ y: Int) -> ArraySlice<UInt8> { data[(y * stride)..<((y + 1) * stride)] }
        let last = row(height - 1)
        var y = height - 1
        while y > 0 {
            let candidate = row(y)
            if zip(candidate, last).contains(where: { abs(Int($0) - Int($1)) > 3 }) { break }
            y -= 1
        }
        let keep = min(height, y + 1 + margin * Int(scale))
        return image.cropping(to: CGRect(x: 0, y: 0, width: width, height: keep)) ?? image
    }

    /// RGBA bytes, top row first.
    nonisolated static func pixels(_ image: CGImage) -> [UInt8]? {
        var data = [UInt8](repeating: 0, count: image.width * image.height * 4)
        guard let context = CGContext(data: &data, width: image.width, height: image.height, bitsPerComponent: 8,
                                      bytesPerRow: image.width * 4, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        return data
    }

    /// The menu-bar panel as it floats over the desktop: rounded, outlined,
    /// with a soft shadow, `margin` points in from a neutral backdrop.
    private static func panel(_ content: CGImage, radius: CGFloat, margin: CGFloat, appearance: NSAppearance) throws -> CGImage {
        let inset = margin * scale, r = radius * scale
        let width = content.width + Int(inset * 2), height = content.height + Int(inset * 2)
        guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                      space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
        else { throw SnapshotError.noContext }
        let dark = appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
        context.setFillColor(dark ? CGColor(red: 0.16, green: 0.18, blue: 0.23, alpha: 1)
                                  : CGColor(red: 0.80, green: 0.84, blue: 0.90, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        let frame = CGRect(x: inset, y: inset, width: CGFloat(content.width), height: CGFloat(content.height))
        let path = CGPath(roundedRect: frame, cornerWidth: r, cornerHeight: r, transform: nil)
        context.saveGState()
        context.setShadow(offset: CGSize(width: 0, height: -6 * scale), blur: 24 * scale,
                          color: CGColor(gray: 0, alpha: dark ? 0.6 : 0.28))
        context.addPath(path)
        context.setFillColor(CGColor(gray: dark ? 0.1 : 1, alpha: 1))
        context.fillPath()
        context.restoreGState()
        context.saveGState()
        context.addPath(path)
        context.clip()
        appearance.performAsCurrentDrawingAppearance {
            if let background = NSColor.windowBackgroundColor.usingColorSpace(.sRGB)?.cgColor {
                context.setFillColor(background)
                context.fill(frame)
            }
        }
        context.draw(content, in: frame)
        context.restoreGState()
        context.addPath(path)
        context.setStrokeColor(CGColor(gray: dark ? 1 : 0, alpha: dark ? 0.18 : 0.12))
        context.setLineWidth(1 * scale)
        context.strokePath()
        guard let image = context.makeImage() else { throw SnapshotError.noContext }
        return image
    }

    static func writePNG(_ image: CGImage, to url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let rep = NSBitmapImageRep(cgImage: image)
        rep.size = NSSize(width: CGFloat(image.width) / scale, height: CGFloat(image.height) / scale)
        guard let data = rep.representation(using: .png, properties: [:]) else { throw SnapshotError.encoding(url.lastPathComponent) }
        try data.write(to: url)
    }

    enum SnapshotError: Error, CustomStringConvertible {
        case noAppearance(String), noContent, noContext, encoding(String)
        var description: String {
            switch self {
            case .noAppearance(let name): return "No appearance named \(name)"
            case .noContent: return "The window has no content to draw"
            case .noContext: return "Couldn't make a bitmap context"
            case .encoding(let name): return "Couldn't encode \(name) as PNG"
            }
        }
    }
}

/// Draws as the key window of an active app, though the test host is
/// neither, and is never ordered on screen. The overrides of AppKit's
/// private key-state checks exist only in this test-only subclass; should a
/// macOS release rename them, snapshots merely look inactive.
final class UISnapshotWindow: NSWindow {
    override var isKeyWindow: Bool { true }
    override var isMainWindow: Bool { true }
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
    @objc(hasKeyAppearance) private func snapshotHasKeyAppearance() -> Bool { true }
    @objc(_hasKeyAppearance) private func snapshotPrivateHasKeyAppearance() -> Bool { true }
    @objc(_hasMainAppearance) private func snapshotHasMainAppearance() -> Bool { true }
    @objc(_hasActiveAppearance) private func snapshotHasActiveAppearance() -> Bool { true }
    @objc(_hasActiveAppearanceIgnoringKeyFocus) private func snapshotHasActiveAppearanceIgnoringKeyFocus() -> Bool { true }
    @objc(_hasActiveControls) private func snapshotHasActiveControls() -> Bool { true }
    @objc(_isKeyWindow) private func snapshotIsKeyWindow() -> Bool { true }
    @objc(_isKeyWindowIgnoringFocus) private func snapshotIsKeyWindowIgnoringFocus() -> Bool { true }
    @objc(isInKeyWindow) private func snapshotIsInKeyWindow() -> Bool { true }
}
