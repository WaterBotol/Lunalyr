import SwiftUI

// MARK: - Hide (a candidate action, `WordAction.candidateList`; owner's idea, 25 Nov 2026)
//
// A hole opens in the ground under the word; the word crouches, hops and
// drops into it, label and all, cut off at the hole's near rim; a beat later
// it pops back up on a spring (a little past its place, then settles) and
// the hole closes behind it. The whole word moves as one, so macOS 14 shows
// it in full. Not Sink (a slow sink through water with bubbles) nor Dip (a
// fall and recovery in place).

extension LivelyAction {
    /// How open the hole is, 0...1: opens over the first 0.3 s, closes over
    /// the last 0.28 s.
    static func hideHole(_ t: Double, duration D: Double) -> Double {
        guard t > 0, t < D else { return 0 }
        return ss5(t / 0.3) * (1 - ss5((t - (D - 0.3)) / 0.28))
    }

    /// How far down the word is, in ems (negative is up): a hop, a fall that
    /// gathers speed, hidden, then a springy return from `D − 0.85`.
    static func hideDepth(_ t: Double, duration D: Double, intensity A: Double) -> Double {
        guard t > 0, t < D else { return 0 }
        let depth: Double = 1.75 * max(0.6, A)
        let hop: Double = -0.22 * max(0.6, A) * bump(t - 0.16, 0.32)
        let u: Double = clamp01((t - 0.34) / 0.3)
        let fall: Double = u * u
        let rise: Double = spring(t - (D - 0.85), peak: 0.32, overshoot: 0.14)
        let down: Double = depth * fall * (1 - rise)
        return (hop + down) * rest(t, D)
    }
}

extension LivelyPose {
    /// Hide's whole-word part (all of it; macOS 14 draws the same).
    static func hide(t: Double, duration D: Double, intensity A: Double, hold: Double) -> LivelyPose {
        guard t > 0, t < D else { return .identity }
        typealias L = LivelyAction
        var pose = LivelyPose()
        pose.dy = L.hideDepth(t, duration: D, intensity: A)
        // A crouch before the hop, and a little squash as it pops back up.
        let crouch: Double = L.bump(t - 0.02, 0.24)
        let land: Double = L.bump(t - (D - 0.5), 0.34)
        pose.sy = 1 - 0.12 * A * crouch + 0.06 * A * land
        pose.sx = 1 + 0.06 * A * crouch - 0.03 * A * land
        pose.anchorY = 1
        return pose
    }
}

/// Hide's hole and ground, fixed where the word stands (applied after the
/// word's travel): everything of the word below the hole's near rim is cut
/// away, and the hole is drawn behind it.
struct LivelyHole: ViewModifier {
    /// Nil: none.
    let open: Double?
    let size: CGFloat

    /// The hole's centre sits this far below the word's frame, in ems.
    static let ground: CGFloat = 0.12
    /// Its half-height when open, in ems.
    static let depth: CGFloat = 0.17
    /// How far past the word it reaches on each side, in ems.
    static let reach: CGFloat = 0.4

    func body(content: Content) -> some View {
        if let open {
            content
                .mask {
                    Canvas { context, canvas in
                        let frame = CGRect(x: size * 2, y: size * 2, width: canvas.width - size * 4, height: canvas.height - size * 4)
                        let cy = frame.maxY + size * Self.ground
                        context.fill(Path(CGRect(x: 0, y: 0, width: canvas.width, height: cy)), with: .color(.black))
                        let rim = Self.rim(frame: frame, open: open, size: size)
                        if rim.width > 1 { context.fill(Path(ellipseIn: rim), with: .color(.black)) }
                    }
                    .padding(-size * 2)
                }
                .background {
                    Canvas { context, canvas in
                        let frame = CGRect(x: size * 2, y: size * 2, width: canvas.width - size * 4, height: canvas.height - size * 4)
                        let rim = Self.rim(frame: frame, open: open, size: size)
                        guard rim.width > 1 else { return }
                        let earth = Color(red: 0.03, green: 0.02, blue: 0.03)
                        var shadow = context
                        shadow.addFilter(.blur(radius: size * 0.08))
                        shadow.fill(Path(ellipseIn: rim.insetBy(dx: -size * 0.06, dy: -size * 0.03)),
                                    with: .color(.black.opacity(0.35 * open)))
                        context.fill(Path(ellipseIn: rim), with: .radialGradient(
                            Gradient(stops: [.init(color: earth.opacity(0.97), location: 0),
                                             .init(color: earth.opacity(0.9), location: 0.75),
                                             .init(color: Color(red: 0.16, green: 0.12, blue: 0.1).opacity(0.85), location: 1)]),
                            center: CGPoint(x: rim.midX, y: rim.midY - rim.height * 0.15),
                            startRadius: 0, endRadius: rim.width / 2))
                        // A pale lip along the near edge.
                        var lip = Path()
                        lip.addArc(center: CGPoint(x: rim.midX, y: rim.midY), radius: rim.width / 2,
                                   startAngle: .degrees(15), endAngle: .degrees(165), clockwise: false,
                                   transform: CGAffineTransform(translationX: rim.midX, y: rim.midY)
                                       .scaledBy(x: 1, y: rim.height / rim.width)
                                       .translatedBy(x: -rim.midX, y: -rim.midY))
                        context.stroke(lip, with: .color(.white.opacity(0.18 * open)), lineWidth: max(1, size * 0.02))
                    }
                    .padding(-size * 2)
                    .allowsHitTesting(false)
                }
        } else {
            content
        }
    }

    /// The hole's ellipse for a word `frame`, `open` 0...1.
    static func rim(frame: CGRect, open: Double, size: CGFloat) -> CGRect {
        let o = CGFloat(open)
        let rx = (frame.width / 2 + size * reach) * o
        let ry = size * depth * o
        let cy = frame.maxY + size * ground
        return CGRect(x: frame.midX - rx, y: cy - ry, width: 2 * rx, height: 2 * ry)
    }
}
