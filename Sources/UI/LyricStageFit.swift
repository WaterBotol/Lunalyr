import SwiftUI

enum LyricStageFit {
    static func scale(content: CGSize, available: CGSize) -> CGFloat {
        guard content.width.isFinite, content.height.isFinite,
              available.width.isFinite, available.height.isFinite,
              content.width > 0, content.height > 0,
              available.width > 0, available.height > 0 else { return 1 }
        return min(1, available.width / content.width, available.height / content.height)
    }
}

/// A line and its reading share one fit, so scaling cannot push readings or
/// the outer constellation rows beyond a short/narrow display. No mask.
struct FittedLyricStage: ViewModifier {
    let available: CGSize
    func body(content: Content) -> some View {
        content.visualEffect { ink, geometry in
            ink.scaleEffect(LyricStageFit.scale(content: geometry.size, available: available))
        }
    }
}
