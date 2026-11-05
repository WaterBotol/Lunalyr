import SwiftUI

/// Screen dimensions in logical points, shared by the wallpaper and preview.
struct LyricViewportMetrics {
    let size: CGSize
    var baseSize: CGFloat { min(124, max(24, min(size.width, size.height * 16 / 9) * 0.046)) }
    var stageSize: CGSize { CGSize(width: size.width * 0.80, height: size.height * 0.72) }

    static func previewScale(desktop: CGSize, inside box: CGSize) -> CGFloat {
        guard desktop.width > 0, desktop.height > 0 else { return 0 }
        return min(box.width / desktop.width, box.height / desktop.height)
    }
}

enum LyricStageFit {
    static func scale(content: CGSize, available: CGSize, maximum: CGFloat = 1) -> CGFloat {
        guard content.width.isFinite, content.height.isFinite,
              available.width.isFinite, available.height.isFinite,
              content.width > 0, content.height > 0,
              available.width > 0, available.height > 0 else { return 1 }
        return min(maximum, available.width / content.width, available.height / content.height)
    }
    /// `neutral` is the share of the stage a line fills at size 1. Stages
    /// that already lay themselves out across the whole stage pass 1.
    static func presentationScale(content: CGSize, available: CGSize, userScale: CGFloat,
                                  neutral share: CGFloat = 0.75, maximumNaturalScale: CGFloat = 1) -> CGFloat {
        let neutral = scale(content: content,
            available: CGSize(width: available.width * share, height: available.height * share),
            maximum: maximumNaturalScale)
        let requested = neutral * min(1.5, max(0.7, userScale))
        guard content.width.isFinite, content.height.isFinite,
              available.width > 0, available.height > 0,
              content.width > 0, content.height > 0 else { return requested }
        return min(requested, available.width / content.width, available.height / content.height)
    }
}

/// A line and its reading share one fit, so scaling cannot push readings or
/// the outer constellation rows beyond a short/narrow display. No mask.
struct FittedLyricStage: ViewModifier {
    let available: CGSize
    var userScale: CGFloat = 1
    var neutral: CGFloat = 0.75
    var maximumNaturalScale: CGFloat = 1
    func body(content: Content) -> some View {
        content.visualEffect { ink, geometry in
            ink.scaleEffect(LyricStageFit.presentationScale(content: geometry.size,
                available: available, userScale: userScale, neutral: neutral, maximumNaturalScale: maximumNaturalScale))
        }
    }
}
