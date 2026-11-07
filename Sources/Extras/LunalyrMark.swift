import SwiftUI

/// Lunalyr's mark: a crescent moon with a highlighted lyric line across it,
/// a quieter second line and a spark. The same shapes `Scripts/draw-brand.swift`
/// draws for the app icon, in its 1024 square.
struct LunalyrMark: View {
    var size: CGFloat = 40
    var tile = Color(red: 1, green: 0.5, blue: 0.42)
    var ink = Color(red: 0.97, green: 0.95, blue: 1)
    var word = Color(red: 0.17, green: 0.08, blue: 0.30)

    var body: some View {
        Canvas { context, canvas in
            // The artwork fills about x 168...870, y 150...832 of the 1024 square.
            let scale = canvas.width / 760
            context.translateBy(x: -140 * scale, y: -110 * scale)
            context.scaleBy(x: scale, y: scale)

            let moon = Path(ellipseIn: CGRect(x: 168, y: 232, width: 600, height: 600))
                .subtracting(Path(ellipseIn: CGRect(x: 340, y: 150, width: 560, height: 560)))
            context.fill(moon, with: .color(ink))
            context.fill(Path(roundedRect: CGRect(x: 430, y: 470, width: 410, height: 104), cornerRadius: 30), with: .color(tile))
            context.fill(Path(roundedRect: CGRect(x: 470, y: 505, width: 230, height: 34), cornerRadius: 17), with: .color(word))
            context.fill(Path(roundedRect: CGRect(x: 470, y: 640, width: 290, height: 38), cornerRadius: 19), with: .color(ink.opacity(0.55)))
            context.fill(Star4().path(in: CGRect(x: 790 - 78, y: 290 - 78, width: 156, height: 156)), with: .color(ink))
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }
}
