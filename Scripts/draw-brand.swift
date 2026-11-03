// Lunalyr's original vector mark: a crescent carrying three lyric strokes.
// No upstream logo pixels are used. Run: swift Scripts/draw-brand.swift
import AppKit
import Foundation
let root = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
func draw(size: Int, template: Bool, destination: String) throws {
    let image = NSImage(size: NSSize(width: size, height: size))
    image.lockFocus()
    let scale = CGFloat(size) / 1024
    NSAffineTransform(transform: AffineTransform(scale: scale)).concat()
    if !template {
        let background = NSBezierPath(roundedRect: NSRect(x: 48, y: 48, width: 928, height: 928), xRadius: 216, yRadius: 216)
        NSGradient(starting: NSColor(red: 0.17, green: 0.12, blue: 0.36, alpha: 1), ending: NSColor(red: 0.05, green: 0.25, blue: 0.32, alpha: 1))!.draw(in: background, angle: 40)
    }
    let moon = NSBezierPath()
    moon.appendOval(in: NSRect(x: 212, y: 218, width: 590, height: 590))
    moon.appendOval(in: NSRect(x: 375, y: 345, width: 530, height: 530))
    moon.windingRule = .evenOdd
    NSColor(white: template ? 0 : 0.97, alpha: 1).setFill(); moon.fill()
    for (i, width) in [270.0, 205.0, 138.0].enumerated() {
        NSBezierPath(roundedRect: NSRect(x: 466, y: 420 - Double(i) * 88, width: width, height: 38), xRadius: 19, yRadius: 19).fill()
    }
    image.unlockFocus()
    let rep = NSBitmapImageRep(data: image.tiffRepresentation!)!
    try rep.representation(using: .png, properties: [:])!.write(to: root.appendingPathComponent(destination))
}
for size in [16, 32, 64, 128, 256, 512, 1024] {
    try draw(size: size, template: false, destination: "Sources/Assets.xcassets/AppIcon.appiconset/icon_\(size).png")
}
try draw(size: 32, template: true, destination: "Sources/Resources/MenuBarIconTemplate.png")
try draw(size: 64, template: true, destination: "Sources/Resources/MenuBarIconTemplate@2x.png")
