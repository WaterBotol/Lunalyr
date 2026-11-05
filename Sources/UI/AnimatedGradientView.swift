import SwiftUI

/// The colour field, built the way Apple Music builds its animated artwork
/// background: a handful of large, very soft colour blobs drawn from the
/// cover, each drifting along its own slow path, blurred together so they
/// read as pigment suspended in water rather than distinct shapes.
///
/// Colour masses drift continuously through a varied album-derived palette.
///
/// This is the base of every pattern and colour-only wallpaper, of Match the
/// music for most tracks and of every power-saving fallback, and it redraws
/// on every frame of the wallpaper clock. Drawn at native pixels, its blur
/// cost an offscreen pass of 6 MP on a 14-inch MacBook Pro and 15 MP on a 5K
/// display, every frame. It is drawn at a bounded pixel budget instead and
/// stretched over the screen by the compositor: the field is a few blobs
/// under a blur of 7.5% of the short side, so the upscale cannot be seen.
struct AnimatedGradientView: View {
    let palette: AlbumWallpaperPalette
    /// Accumulated phase from `VisualClock`.
    let time: Double
    /// Colour richness, applied inside the small pass rather than to the
    /// full-size result.
    var vividness: Double = 1
    /// Spend fewer pixels while power is being saved.
    var economical = false
    /// Replaces the pixel budget; `.infinity` draws at native resolution.
    var pixelBudget: Double?
    @Environment(\.displayScale) private var displayScale

    var body: some View {
        GeometryReader { geometry in
            let size = geometry.size
            let canvas = ColourFieldBudget.canvas(points: size, scale: displayScale,
                budget: pixelBudget ?? ColourFieldBudget.pixels(economical: economical))
            field(size: canvas.size)
                .frame(width: canvas.size.width, height: canvas.size.height)
                // Flattens the whole stack into one GPU pass -- several large
                // blurred gradients composited every frame is otherwise
                // costly -- at the canvas size...
                .drawingGroup()
                // ...which the compositor stretches over the screen.
                .scaleEffect(x: canvas.stretch.width, y: canvas.stretch.height)
                .frame(width: size.width, height: size.height)
        }
        .ignoresSafeArea()
    }

    private func field(size: CGSize) -> some View {
        ZStack {
            baseColor
            ZStack {
                ForEach(palette.stops.indices, id: \.self) { slot in
                    blob(slot: slot, size: size)
                }
            }
            // Diffuses the blobs into one another. Scaled up first so the
            // blur's soft edge falls outside the screen instead of
            // darkening the border.
            .blur(radius: min(size.width, size.height) * 0.075)
            .scaleEffect(1.2)
        }
        .saturation(vividness)
    }

    // MARK: - Layers

    private var baseColor: Color {
        AlbumWallpaperPalette.color(palette.average * 0.45)
    }

    private func blob(slot: Int, size: CGSize) -> some View {
        let spec = specification(slot: slot)
        let shortest = min(size.width, size.height)
        let turn = 2 * Double.pi

        // Different periods on each axis trace a Lissajous path, so blobs
        // wander without ever retracing the same loop.
        let angleX = time / spec.periodX * turn + spec.phase
        let angleY = time / spec.periodY * turn + spec.phase * 1.3
        let x = size.width / 2 + (CGFloat(cos(angleX)) * spec.orbitX) * size.width
        let y = size.height / 2 + (CGFloat(sin(angleY)) * spec.orbitY) * size.height

        let breathe = 1 + 0.05 * sin(time / spec.periodRadius * turn + spec.phase)
        let radius = shortest * spec.radius * CGFloat(breathe)

        let colour = AlbumWallpaperPalette.color(palette.stops[slot])

        return RadialGradient(
            gradient: Gradient(stops: [
                .init(color: colour.opacity(0.85), location: 0.0),
                .init(color: colour.opacity(0.42), location: 0.42),
                .init(color: colour.opacity(0.0), location: 1.0),
            ]),
            center: .center,
            startRadius: 0,
            endRadius: radius
        )
        .frame(width: radius * 2, height: radius * 2)
        .position(x: x, y: y)
    }

    // MARK: - Blob definitions

    private struct BlobSpec {
        var periodX: Double
        var periodY: Double
        var periodRadius: Double
        var orbitX: CGFloat
        var orbitY: CGFloat
        var radius: CGFloat
        var phase: Double
    }

    /// Long, mutually prime-ish periods so the combined arrangement takes
    /// many minutes to come back around.
    private func specification(slot: Int) -> BlobSpec {
        let periodsX: [Double] = [37, 43, 53, 61, 71]
        let periodsY: [Double] = [47, 59, 41, 67, 79]
        let periodsR: [Double] = [23, 29, 31, 19, 26]
        let orbitsX: [CGFloat] = [0.26, 0.30, 0.20, 0.32, 0.16]
        let orbitsY: [CGFloat] = [0.22, 0.16, 0.28, 0.20, 0.30]
        let radii: [CGFloat] = [0.72, 0.62, 0.80, 0.58, 0.68]

        return BlobSpec(
            periodX: periodsX[slot],
            periodY: periodsY[slot],
            periodRadius: periodsR[slot],
            orbitX: orbitsX[slot],
            orbitY: orbitsY[slot],
            radius: radii[slot],
            phase: Double(slot) * 1.27
        )
    }
}

/// How many pixels the colour field may draw per frame.
enum ColourFieldBudget {
    /// A quarter of a megapixel: a 14-inch MacBook Pro's 6 MP and a 5K
    /// display's 15 MP both come down to it, with no visible difference.
    static let standard = 250_000.0

    static func pixels(economical: Bool) -> Double {
        economical ? standard * 0.6 : standard
    }

    /// Where the field is drawn and how far it is stretched to fill `points`.
    struct Canvas: Equatable {
        /// In points, a whole number of pixels at the display scale.
        var size: CGSize
        /// Per axis; 1 when the field is drawn natively.
        var stretch: CGSize
        /// The pixels drawn per frame.
        func pixels(scale: CGFloat) -> Double {
            Double(size.width * scale) * Double(size.height * scale)
        }
    }

    /// The share of each side the field is drawn at: native when `points`
    /// at `scale` fits the budget, otherwise the same aspect scaled down.
    static func factor(points: CGSize, scale: CGFloat, budget: Double) -> CGFloat {
        guard points.width > 0, points.height > 0, points.width.isFinite, points.height.isFinite else { return 1 }
        let scale = scale.isFinite && scale > 0 ? scale : 2
        let native = Double(points.width * scale) * Double(points.height * scale)
        guard budget.isFinite else { return 1 }
        return CGFloat(min(1, sqrt(max(1, budget) / native)))
    }

    /// A canvas of whole pixels, so nothing is rounded off it, stretched to
    /// reach one of its pixels past each edge of the screen: an offscreen
    /// pass rounded down by a pixel would otherwise leave a seam there.
    static func canvas(points: CGSize, scale: CGFloat, budget: Double) -> Canvas {
        let factor = factor(points: points, scale: scale, budget: budget)
        guard factor < 1 else { return Canvas(size: points, stretch: CGSize(width: 1, height: 1)) }
        let scale = scale.isFinite && scale > 0 ? scale : 2
        let pixels = CGSize(width: max(4, (points.width * factor * scale).rounded(.down)),
                            height: max(4, (points.height * factor * scale).rounded(.down)))
        let size = CGSize(width: pixels.width / scale, height: pixels.height / scale)
        return Canvas(size: size, stretch: CGSize(width: points.width / size.width * (pixels.width + 2) / pixels.width,
                                                  height: points.height / size.height * (pixels.height + 2) / pixels.height))
    }
}
