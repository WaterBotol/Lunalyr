import SwiftUI

/// How a background layer leaves. Each kind always leaves the same way, so
/// what is going (a credit, backing vocals) can be told by how it
/// goes, as well as by how it arrived.
enum LayerExit {
    /// Production credits lift away toward the edge they sit on, softening.
    case credit(towardTop: Bool)
    /// Backing vocals sink: a small settle downward and shrink, no blur.
    case backing

    /// What `progress` (0 while held ... 1 gone) does to the layer.
    func pose(_ progress: Double, size: CGFloat) -> (offset: CGFloat, scale: CGFloat, blur: CGFloat) {
        let p = CGFloat(min(1, max(0, progress)))
        switch self {
        case .credit(let towardTop): return ((towardTop ? -1 : 1) * p * size * 0.45, 1, p * size * 0.09)
        case .backing: return (p * size * 0.3, 1 - 0.06 * p, 0)
        }
    }
}

private struct LayerExitModifier: ViewModifier {
    let exit: LayerExit
    let progress: Double
    let size: CGFloat
    @Environment(\.lyricReduceMotion) private var reduceMotion

    func body(content: Content) -> some View {
        let pose = reduceMotion ? (offset: CGFloat(0), scale: CGFloat(1), blur: CGFloat(0)) : exit.pose(progress, size: size)
        content
            .scaleEffect(pose.scale)
            .blur(radius: pose.blur)
            .offset(y: pose.offset)
    }
}

extension View {
    /// Leaves as `exit` does; `progress` is 0 until it starts to go.
    func layerExit(_ exit: LayerExit, progress: Double, size: CGFloat) -> some View {
        modifier(LayerExitModifier(exit: exit, progress: progress, size: size))
    }
}
