import CoreGraphics
import SwiftUI

/// Where the controls panel actually draws something. The panel is a fixed
/// rectangle in the middle of the desktop, but its content is a floating
/// cluster of artwork, type and buttons; everywhere else must let clicks,
/// rubber-band selections and drops reach Finder.
///
/// Built from the panel's own rendering, so it follows the real layout with
/// no knowledge of it. Faint shadows count, then the mask grows a little so
/// a button's edge is as easy to hit as its centre.
struct ControlHitMask: Equatable {
    let columns: Int
    let rows: Int
    /// Points per mask cell.
    let cell: CGFloat
    private let bits: [Bool]

    init?(image: CGImage, pointSize: CGSize, alphaThreshold: Double = 0.1, grow: CGFloat = 6) {
        let width = image.width, height = image.height
        guard width > 0, height > 0, pointSize.width > 0, pointSize.height > 0,
              width <= 4096, height <= 4096 else { return nil }
        var alpha = [UInt8](repeating: 0, count: width * height)
        let drawn = alpha.withUnsafeMutableBytes { buffer -> Bool in
            guard let context = CGContext(data: buffer.baseAddress, width: width, height: height,
                                          bitsPerComponent: 8, bytesPerRow: width,
                                          space: CGColorSpaceCreateDeviceGray(),
                                          bitmapInfo: CGImageAlphaInfo.alphaOnly.rawValue) else { return false }
            context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
            return true
        }
        guard drawn else { return nil }
        let threshold = UInt8(min(255, max(1, (alphaThreshold * 255).rounded())))
        let cell = pointSize.width / CGFloat(width)
        let radius = max(0, Int((grow / cell).rounded(.up)))
        // Memory rows run top to bottom; the mask is stored the same way.
        var marked = alpha.map { $0 >= threshold }
        if radius > 0 { marked = Self.grow(marked, columns: width, rows: height, radius: radius) }
        self.columns = width
        self.rows = height
        self.cell = cell
        self.bits = marked
    }

    /// `point` is panel-local in AppKit's orientation: origin bottom left.
    func contains(_ point: CGPoint) -> Bool {
        guard point.x.isFinite, point.y.isFinite, point.x >= 0, point.y >= 0 else { return false }
        let column = Int(point.x / cell)
        let fromBottom = Int(point.y / cell)
        guard column < columns, fromBottom < rows else { return false }
        return bits[(rows - 1 - fromBottom) * columns + column]
    }

    var isEmpty: Bool { !bits.contains(true) }

    /// Square dilation, separable: rows then columns.
    private static func grow(_ bits: [Bool], columns: Int, rows: Int, radius: Int) -> [Bool] {
        var horizontal = [Bool](repeating: false, count: bits.count)
        for row in 0..<rows {
            var last = -radius - 1
            // Distance to the nearest set bit on the left, then on the right.
            for column in 0..<columns {
                if bits[row * columns + column] { last = column }
                if column - last <= radius { horizontal[row * columns + column] = true }
            }
            last = columns + radius + 1
            for column in stride(from: columns - 1, through: 0, by: -1) {
                if bits[row * columns + column] { last = column }
                if last - column <= radius { horizontal[row * columns + column] = true }
            }
        }
        var grown = [Bool](repeating: false, count: bits.count)
        for column in 0..<columns {
            var last = -radius - 1
            for row in 0..<rows {
                if horizontal[row * columns + column] { last = row }
                if row - last <= radius { grown[row * columns + column] = true }
            }
            last = rows + radius + 1
            for row in stride(from: rows - 1, through: 0, by: -1) {
                if horizontal[row * columns + column] { last = row }
                if last - row <= radius { grown[row * columns + column] = true }
            }
        }
        return grown
    }

    /// Renders `content` at a coarse scale and keeps only its coverage.
    @MainActor
    static func render<Content: View>(_ content: Content, size: CGSize, scale: CGFloat = 0.5) -> ControlHitMask? {
        guard size.width >= 1, size.height >= 1 else { return nil }
        let renderer = ImageRenderer(content: content.frame(width: size.width, height: size.height))
        renderer.scale = scale
        renderer.isOpaque = false
        guard let image = renderer.cgImage else { return nil }
        return ControlHitMask(image: image, pointSize: size)
    }

    /// Whether a panel's mask must be rendered again. A usable mask follows
    /// what the panel draws, and is refreshed every couple of seconds; one
    /// that came out empty or failed is retried at most once a second, never
    /// on every hover poll.
    static func needsRender(usable: Bool?, stale: Bool, age: TimeInterval) -> Bool {
        guard let usable else { return true }
        return usable ? stale || age > 2 : age > 1
    }

    /// Whether a panel should take the pointer. While any button is held the
    /// previous answer stands, so a drag that started on the desktop stays
    /// with the desktop and a scrub that started on the controls stays there.
    static func acceptsMouse(active: Bool, overContent: Bool, buttonsHeld: Bool, previously: Bool) -> Bool {
        buttonsHeld ? previously : active && overContent
    }
}
