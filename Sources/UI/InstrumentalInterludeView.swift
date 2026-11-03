import SwiftUI

extension VCVocalGap {
    func presence(at time: Double) -> Double {
        let enter = Easing.smoothstep((time - start) / 0.7)
        let leave = nextVocal >= 0 ? 1 - Easing.smoothstep((time - end + 0.65) / 0.65) : 1
        return min(enter, leave)
    }
}

/// A quiet passage with continuous ambient ribbons and a next-vocal cue.
/// Its motion follows lyric timing rather than reacting to captured audio.
struct InstrumentalInterludeView: View {
    let time: Double
    let gap: VCVocalGap
    let baseSize: CGFloat
    let accent: Color
    var nextWord: String? = nil
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    private var remaining: Double { gap.nextVocal >= 0 ? max(0, gap.end - time) : .infinity }
    private var returnCue: Double { 1 - Easing.smoothstep((remaining - 0.7) / 3.6) }

    var body: some View {
        ZStack {
            Canvas { context, size in
                drawRibbons(context: &context, size: size)
            }
            VStack(spacing: baseSize * 0.23) {
                HStack(spacing: baseSize * 0.045) {
                    ForEach(Array("INSTRUMENTAL".enumerated()), id: \.offset) { index, letter in
                        let phase = max(0, time - gap.start) * 0.65 - Double(index) * 0.27
                        Text(String(letter))
                            .font(.system(size: max(10, baseSize * 0.145), weight: .medium))
                            .foregroundStyle(.white.opacity(0.40 + (reduceMotion ? 0 : 0.12 * sin(phase))))
                            .offset(y: reduceMotion ? 0 : CGFloat(sin(phase)) * baseSize * 0.025)
                    }
                }
                Spacer().frame(height: baseSize * 1.1)
                if let nextWord, gap.nextVocal >= 0 {
                    Text(nextWord)
                        .font(.system(size: baseSize * 0.42, weight: .semibold, design: .rounded))
                        .foregroundStyle(.white.opacity(0.25))
                        .blur(radius: reduceMotion ? 0 : (1 - returnCue) * 5)
                        .offset(y: reduceMotion ? 0 : CGFloat(1 - returnCue) * baseSize * 0.25)
                        .opacity(returnCue)
                }
                if gap.nextVocal >= 0 {
                    HStack(spacing: 7) {
                        ForEach(0..<3) { dot in
                            let cue = Easing.smoothstep((Double(3 - dot) - remaining) / 0.45)
                            Capsule().fill(accent.opacity(0.16 + 0.64 * cue))
                                .frame(width: CGFloat(7 + 11 * cue), height: 3)
                        }
                    }
                    .opacity(returnCue)
                    .accessibilityLabel("Vocals return in \(Int(ceil(remaining))) seconds")
                }
            }
        }
        .frame(height: baseSize * 4.4)
        .opacity(gap.presence(at: time))
        .scaleEffect(reduceMotion ? 1 : 0.97 + gap.presence(at: time) * 0.03)
        .allowsHitTesting(false)
    }

    private func drawRibbons(context: inout GraphicsContext, size: CGSize) {
        let elapsed: Double = reduceMotion ? 0 : max(0, time - gap.start)
        let low: Double = 0
        let middle: Double = 0
        let hit: Double = 0
        let glowRadius: CGFloat = CGFloat(7.0 + low * 6.0)
        let glowWidth: CGFloat = CGFloat(5.0 + low * 5.0)
        let inkWidth: CGFloat = CGFloat(0.8 + low * 1.5)
        let glowOpacity: Double = 0.22 + hit * 0.15
        let inkOpacity: Double = 0.32 + middle * 0.28
        let colours: [Color] = [.clear, accent.opacity(0.40), .white.opacity(inkOpacity),
                                accent.opacity(0.40), .clear]
        let paint = GraphicsContext.Shading.linearGradient(Gradient(colors: colours),
            startPoint: .zero, endPoint: CGPoint(x: size.width, y: 0))

        for ribbon in 0..<3 {
            let path = ribbonPath(size: size, elapsed: elapsed, low: low, middle: middle, ribbon: ribbon)
            var glow = context
            glow.addFilter(.blur(radius: glowRadius))
            glow.stroke(path, with: .color(accent.opacity(glowOpacity)),
                        style: StrokeStyle(lineWidth: glowWidth, lineCap: .round))
            context.stroke(path, with: paint, style: StrokeStyle(lineWidth: inkWidth, lineCap: .round))
        }
    }

    /// Explicit scalar types keep Swift's overloaded numeric operators out of
    /// the view builder's constraint graph. The visual equations are unchanged.
    private func ribbonPath(size: CGSize, elapsed: Double, low: Double, middle: Double, ribbon: Int) -> Path {
        let phase: Double = Double(ribbon) * 1.9
        let travelRate: Double = 0.40 + middle * 0.22
        let travel: Double = elapsed * travelRate
        let counterTravel: Double = elapsed * 0.29
        let amplitude: Double = 0.12 + low * 0.10
        let laneOffset: Double = (Double(ribbon) - 1.0) * 0.045
        var path = Path()

        for step in 0...56 {
            let u: Double = Double(step) / 56.0
            let envelope: Double = sin(u * Double.pi)
            let firstPhase: Double = u * 6.8 + travel + phase
            let secondPhase: Double = u * 12.0 - counterTravel + phase
            let wave: Double = sin(firstPhase) + sin(secondPhase) * 0.22
            let displacement: Double = wave * envelope * amplitude
            let xFraction: Double = 0.08 + u * 0.84
            let yFraction: Double = 0.55 + displacement + laneOffset
            let x: CGFloat = size.width * CGFloat(xFraction)
            let y: CGFloat = size.height * CGFloat(yFraction)
            let point = CGPoint(x: x, y: y)
            if step == 0 { path.move(to: point) } else { path.addLine(to: point) }
        }
        return path
    }
}
