// Lunalyr's original vector mark: a crescent moon with a highlighted lyric
// line across it -- the tile that presses behind each sung word -- and a
// second, quieter line and a spark. No upstream logo pixels are used.
// Run: swift Scripts/draw-brand.swift
//
// Geometry is in a 1024 square with the origin at the top left. The same
// shapes are in Sources/Extras/LunalyrMark.swift for the lyric card.
import AppKit
import Foundation

let root = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)

func draw(size: Int, template: Bool, destination: String) throws {
    // Exactly `size` pixels square, whatever the screen's scale.
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                               bytesPerRow: 0, bitsPerPixel: 0)!
    rep.size = NSSize(width: size, height: size)
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let flip = NSAffineTransform()
    flip.translateX(by: 0, yBy: CGFloat(size))
    flip.scaleX(by: CGFloat(size) / 1024, yBy: -CGFloat(size) / 1024)
    flip.concat()

    if !template {
        let background = NSBezierPath(roundedRect: NSRect(x: 48, y: 48, width: 928, height: 928), xRadius: 216, yRadius: 216)
        NSGradient(starting: NSColor(red: 0.42, green: 0.34, blue: 0.91, alpha: 1),
                   ending: NSColor(red: 0.13, green: 0.08, blue: 0.40, alpha: 1))!.draw(in: background, angle: -50)
    }
    let ink = template ? NSColor.black : NSColor(red: 0.97, green: 0.95, blue: 1, alpha: 1)

    // The crescent: a disc with a second disc cut out of its upper right. Drawn
    // on its own layer so the cut is real (it stays clear in the menu-bar icon).
    let layer = NSImage(size: NSSize(width: 1024, height: 1024))
    layer.lockFocus()
    ink.setFill()
    NSBezierPath(ovalIn: NSRect(x: 168, y: 232, width: 600, height: 600)).fill()
    NSGraphicsContext.current?.compositingOperation = .destinationOut
    NSBezierPath(ovalIn: NSRect(x: 340, y: 150, width: 560, height: 560)).fill()
    layer.unlockFocus()
    layer.draw(in: NSRect(x: 0, y: 0, width: 1024, height: 1024), from: .zero, operation: .sourceOver, fraction: 1)

    // The highlighted line: a tile with the word on it knocked out.
    let tile = template ? NSColor.black : NSColor(red: 1, green: 0.50, blue: 0.42, alpha: 1)
    tile.setFill()
    NSBezierPath(roundedRect: NSRect(x: 430, y: 470, width: 410, height: 104), xRadius: 30, yRadius: 30).fill()
    if !template {
        NSColor(red: 0.17, green: 0.08, blue: 0.30, alpha: 1).setFill()
        NSBezierPath(roundedRect: NSRect(x: 470, y: 505, width: 230, height: 34), xRadius: 17, yRadius: 17).fill()
        ink.withAlphaComponent(0.55).setFill()
        NSBezierPath(roundedRect: NSRect(x: 470, y: 640, width: 290, height: 38), xRadius: 19, yRadius: 19).fill()
        // A spark off the moon's tip.
        ink.setFill()
        let star = NSBezierPath()
        let centre = CGPoint(x: 790, y: 290), outer: CGFloat = 78, inner: CGFloat = 20
        for i in 0..<8 {
            let angle = Double(i) * .pi / 4 - .pi / 2
            let r = i % 2 == 0 ? outer : inner
            let point = NSPoint(x: centre.x + CGFloat(cos(angle)) * r, y: centre.y + CGFloat(sin(angle)) * r)
            if i == 0 { star.move(to: point) } else { star.line(to: point) }
        }
        star.close(); star.fill()
    }
    NSGraphicsContext.restoreGraphicsState()
    try rep.representation(using: .png, properties: [:])!.write(to: root.appendingPathComponent(destination))
}

for size in [16, 32, 64, 128, 256, 512, 1024] {
    try draw(size: size, template: false, destination: "Sources/Assets.xcassets/AppIcon.appiconset/icon_\(size).png")
}
try draw(size: 32, template: true, destination: "Sources/Resources/MenuBarIconTemplate.png")
try draw(size: 64, template: true, destination: "Sources/Resources/MenuBarIconTemplate@2x.png")
