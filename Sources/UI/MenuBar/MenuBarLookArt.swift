import SwiftUI

/// A built-in Look drawn as the sung line on its wallpaper, in its layout
/// and typeface (direction 2's art, SPEC.md section 6): a few shapes and
/// words, no live rendering, so six of them cost nothing per frame. Lyric
/// fonts here are the Look's own (DESIGN.md's single-typeface rule is for
/// chrome). Static by design; the sample words are the mock-ups' line.
struct MenuBarLookArt: View {
    enum Art: CaseIterable { case matched, quiet, nightDrive, poster, starChart, collage }

    let art: Art

    /// The drawing for a built-in Look; nil for the person's own.
    static func art(for id: Look.ID) -> Art? {
        switch id {
        case "builtin.matched": return .matched
        case "builtin.quiet": return .quiet
        case "builtin.nightDrive": return .nightDrive
        case "builtin.poster": return .poster
        case "builtin.starChart": return .starChart
        case "builtin.collage": return .collage
        default: return nil
        }
    }

    private static let orange = Color(red: 0.99, green: 0.56, blue: 0.32)
    private static let pink = Color(red: 0.88, green: 0.27, blue: 0.52)
    private static let deep = Color(red: 0.07, green: 0.05, blue: 0.15)

    var body: some View {
        GeometryReader { proxy in
            let w = proxy.size.width
            let h = proxy.size.height
            ZStack(alignment: .topLeading) {
                switch art {
                case .matched: matched(w, h)
                case .quiet: quiet(w, h)
                case .nightDrive: nightDrive(w, h)
                case .poster: poster(w, h)
                case .starChart: starChart(w, h)
                case .collage: collage(w, h)
                }
            }
            .frame(width: w, height: h)
            .clipped()
        }
        .accessibilityHidden(true)
    }

    private func matched(_ w: CGFloat, _ h: CGFloat) -> some View {
        ZStack(alignment: .bottomLeading) {
            SampleWallpaper()
            VStack(alignment: .leading, spacing: h * 0.01) {
                Text(verbatim: "Static on the radio,")
                Text(verbatim: "gold across the bay")
            }
            .font(.system(size: h * 0.105, weight: .semibold))
            .foregroundStyle(.white)
            .padding(.leading, w * 0.08)
            .padding(.bottom, h * 0.14)
        }
    }

    private func quiet(_ w: CGFloat, _ h: CGFloat) -> some View {
        ZStack(alignment: .bottom) {
            LinearGradient(colors: [Color(red: 0.56, green: 0.64, blue: 0.75), Color(red: 0.86, green: 0.76, blue: 0.70)],
                           startPoint: .top, endPoint: .bottom)
            Ellipse().fill(Color(red: 0.24, green: 0.30, blue: 0.37))
                .frame(width: w * 1.3, height: h * 0.7).offset(x: -w * 0.25, y: h * 0.42)
            Ellipse().fill(Color(red: 0.12, green: 0.16, blue: 0.21))
                .frame(width: w * 1.2, height: h * 0.6).offset(x: w * 0.3, y: h * 0.40)
            HStack(spacing: w * 0.012) {
                Text(verbatim: "Static on the radio,").foregroundStyle(.white.opacity(0.6))
                Text(verbatim: "gold").foregroundStyle(.white)
            }
            .font(.system(size: h * 0.09, weight: .medium))
            .padding(.bottom, h * 0.11)
        }
    }

    private func nightDrive(_ w: CGFloat, _ h: CGFloat) -> some View {
        let heights: [CGFloat] = [0.22, 0.34, 0.18, 0.4, 0.28, 0.5, 0.36, 0.24, 0.44, 0.3, 0.2, 0.38,
                                  0.26, 0.46, 0.32, 0.2, 0.3, 0.42, 0.24, 0.34]
        return ZStack(alignment: .bottomLeading) {
            Self.deep
            HStack(alignment: .bottom, spacing: w * 0.012) {
                ForEach(heights.indices, id: \.self) { index in
                    RoundedRectangle(cornerRadius: w * 0.006)
                        .fill(index % 3 == 0 ? Self.pink : Self.orange)
                        .opacity(0.75)
                        .frame(height: h * heights[index] * 0.7)
                }
            }
            .frame(width: w, height: h, alignment: .bottom)
            VStack(alignment: .leading, spacing: 0) {
                Text(verbatim: "on the radio,").foregroundStyle(.white.opacity(0.6))
                Text(verbatim: "gold").foregroundStyle(Self.orange)
                    .font(.custom("Avenir Next", size: h * 0.2).weight(.bold))
                Text(verbatim: "across the bay").foregroundStyle(.white)
            }
            .font(.custom("Avenir Next", size: h * 0.1).weight(.semibold))
            .padding(.leading, w * 0.1)
            .frame(maxHeight: .infinity, alignment: .center)
            .offset(y: -h * 0.08)
        }
    }

    private func poster(_ w: CGFloat, _ h: CGFloat) -> some View {
        ZStack(alignment: .leading) {
            Color(red: 0.40, green: 0.09, blue: 0.27)
            VStack(alignment: .leading, spacing: h * 0.01) {
                Text(verbatim: "static on the radio,")
                    .font(.custom("Futura", size: h * 0.075).weight(.medium))
                    .foregroundStyle(Self.orange)
                    .padding(.leading, w * 0.08)
                Text(verbatim: "gold across")
                    .font(.custom("Futura", size: h * 0.21).weight(.bold))
                    .foregroundStyle(.white)
                    .fixedSize()
                    .frame(width: w, alignment: .leading)
                    .offset(x: w * 0.07)
            }
            .frame(width: w, height: h, alignment: .leading)
        }
    }

    private func starChart(_ w: CGFloat, _ h: CGFloat) -> some View {
        let points: [(String, CGFloat, CGFloat, CGFloat)] = [
            ("Static", 0.14, 0.26, 0.08), ("on the radio,", 0.5, 0.18, 0.08),
            ("gold", 0.3, 0.55, 0.14), ("across the bay", 0.62, 0.72, 0.08),
        ]
        return ZStack(alignment: .topLeading) {
            Color(red: 0.06, green: 0.07, blue: 0.17)
            Path { path in
                var x: CGFloat = -h
                while x < w {
                    path.move(to: CGPoint(x: x, y: h))
                    path.addLine(to: CGPoint(x: x + h, y: 0))
                    x += max(6, w / 16)
                }
            }
            .stroke(.white.opacity(0.08), lineWidth: max(0.5, h * 0.004))
            Path { path in
                for (index, point) in points.enumerated() {
                    let anchor = CGPoint(x: w * point.1 + w * 0.02, y: h * point.2 + h * point.3 * 0.6)
                    if index == 0 { path.move(to: anchor) } else { path.addLine(to: anchor) }
                }
            }
            .stroke(.white.opacity(0.35), lineWidth: max(0.5, h * 0.004))
            ForEach(points.indices, id: \.self) { index in
                let point = points[index]
                Text(point.0)
                    .font(.system(size: h * point.3, weight: index == 2 ? .semibold : .regular, design: .serif))
                    .foregroundStyle(.white)
                    .fixedSize()
                    .position(x: w * point.1 + w * 0.02, y: h * point.2)
                    .offset(x: w * 0.06)
            }
        }
    }

    private func collage(_ w: CGFloat, _ h: CGFloat) -> some View {
        let unit = max(w, h)
        return ZStack(alignment: .leading) {
            Self.deep
            RadialGradient(colors: [Color(red: 0.12, green: 0.55, blue: 0.60), .clear],
                           center: UnitPoint(x: 0.85, y: 0.2), startRadius: 0, endRadius: unit * 0.55)
            RadialGradient(colors: [Self.pink.opacity(0.9), .clear],
                           center: UnitPoint(x: 0.2, y: 0.9), startRadius: 0, endRadius: unit * 0.5)
            RadialGradient(colors: [Self.orange.opacity(0.7), .clear],
                           center: UnitPoint(x: 0.6, y: 0.65), startRadius: 0, endRadius: unit * 0.3)
            VStack(alignment: .leading, spacing: h * 0.02) {
                HStack(spacing: w * 0.02) {
                    Text(verbatim: "Static")
                        .foregroundStyle(.black)
                        .padding(.horizontal, w * 0.015)
                        .background(Self.orange)
                    Text(verbatim: "on the").foregroundStyle(.white)
                }
                .font(.custom("Futura", size: h * 0.09).weight(.bold))
                Text(verbatim: "radio,")
                    .font(.custom("Futura", size: h * 0.24).weight(.bold))
                    .foregroundStyle(.white)
                HStack(spacing: w * 0.02) {
                    Text(verbatim: "gold")
                        .foregroundStyle(.white)
                        .padding(.horizontal, w * 0.015)
                        .background(Color(red: 0.62, green: 0.10, blue: 0.36))
                    Text(verbatim: "across the bay").foregroundStyle(.white)
                }
                .font(.custom("Futura", size: h * 0.09).weight(.medium))
            }
            .padding(.leading, w * 0.09)
        }
    }
}
