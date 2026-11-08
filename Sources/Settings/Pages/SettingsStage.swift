import AppKit
import SwiftUI

// MARK: - The stage
//
// The left of the Settings detail (or its top, in a narrow window): the live
// preview as large as the column allows, what the desktop is doing that the
// preview can't show (paused, hidden while mirroring) just above it, the
// song with its transport just below it, and Music's access banner when
// macOS refuses it. Every page but About shows it, so a change is judged
// where it is made.

@MainActor
struct SettingsStage: View {
    @ObservedObject var previewDisplay: SettingsPreviewDisplay
    /// Nil: the stage fills its column beside the inspector, its content
    /// centred. Set: it sits above the inspector, as tall as its content,
    /// within these limits.
    var stacked: SettingsStageLimits?
    @Model(\.extras) private var extras
    @Model(\.nowPlaying) private var playing
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private var chip: WallpaperStateNote.State? {
        WallpaperStateNote.state(paused: extras.wallpaperPaused, hiddenByMirroring: extras.hiddenByMirroring)
    }

    private var showsAccessBanner: Bool { AutomationStatusBanner.content(for: playing) != nil }

    var body: some View {
        SettingsStageLayout(desktop: previewDisplay.size, stacked: stacked) {
            if chip != nil {
                WallpaperStateNote()
                    .layoutValue(key: SettingsStageRole.self, value: .chip)
                    .transition(.opacity)
            }
            PreviewStage(desktop: previewDisplay.size) {
                SettingsLivePreview(desktopSize: previewDisplay.size, desktopPicture: previewDisplay.desktopPicture)
            }
            .layoutValue(key: SettingsStageRole.self, value: .preview)
            NowPlayingStripLayout {
                TrackSummary(artworkSize: LunalyrDesign.Size.artworkSmall, titleLines: 1)
                TransportControls(size: .small)
            }
            .layoutValue(key: SettingsStageRole.self, value: .strip)
            if showsAccessBanner {
                AutomationStatusBanner()
                    .layoutValue(key: SettingsStageRole.self, value: .banner)
                    .transition(.opacity)
            }
        }
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion), value: chip)
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion),
                   value: showsAccessBanner)
        .padding(LunalyrDesign.Spacing.window)
        .frame(maxWidth: .infinity, maxHeight: stacked == nil ? .infinity : nil)
        .background(Color.lunalyrCanvas(SettingsStyleStore.shared.style))
        .focusSection()
    }
}

/// How big a stage above the inspector may get: the preview at most
/// `preview` tall, the whole stage (margins included) at most `stage`, where
/// the preview can give way.
struct SettingsStageLimits: Equatable {
    var preview: CGFloat
    var stage: CGFloat

    /// For a detail `height` tall: the preview takes at most two fifths of it
    /// (three fifths with Larger Preview), the stage at most three quarters.
    init(detailHeight height: CGFloat, largePreview: Bool) {
        preview = height * (largePreview ? 0.6 : 0.4)
        stage = height * 0.75
    }

    init(preview: CGFloat, stage: CGFloat) {
        self.preview = preview
        self.stage = stage
    }
}

/// What each of the stage's views is, for its layout.
enum SettingsStageRole {
    /// "Wallpaper paused · Resume", above the preview.
    case chip
    case preview
    /// The song and its transport.
    case strip
    /// Music's access banner.
    case banner
}

extension SettingsStageRole: LayoutValueKey {
    static let defaultValue = SettingsStageRole.strip
}

/// Where the stage's views go. The preview keeps the desktop's shape and
/// takes whatever the column leaves once the chip, the strip and the banner
/// have their natural heights at the column's width; centred when that
/// leaves it narrower than the column.
struct SettingsStagePlan: Equatable {
    /// The preview's size, the desktop's shape.
    var preview: CGSize
    /// The strip and banner sit in a column to the right of the preview
    /// instead of below it (a stage above the inspector, when the preview
    /// leaves room beside it).
    var beside: Bool
    /// The whole group's height, from the chip's top to the banner's bottom.
    var height: CGFloat

    static let spacing: CGFloat = LunalyrDesign.Spacing.large
    static let chipSpacing: CGFloat = LunalyrDesign.Spacing.medium
    /// The narrowest a preview is drawn, whatever else the column holds.
    static let minimumPreviewWidth: CGFloat = 200
    /// What the strip and banner need to sit beside the preview.
    static let besideColumnMinimum: CGFloat = 170

    /// - Parameters:
    ///   - room: the stage's size inside its margins. A stacked stage only
    ///     uses the width; its limits say how tall it may get.
    ///   - aspect: the desktop's width over its height.
    ///   - stacked: nil for a stage that fills its column beside the
    ///     inspector; set for one stacked above it.
    ///   - chip, strip, banner: each one's height at a given width, or nil
    ///     when it isn't shown.
    static func make(room: CGSize, aspect rawAspect: CGFloat, stacked: SettingsStageLimits?,
                     chip: ((CGFloat) -> CGFloat)?, strip: ((CGFloat) -> CGFloat)?,
                     banner: ((CGFloat) -> CGFloat)?) -> SettingsStagePlan {
        let aspect = max(0.2, rawAspect.isFinite ? rawAspect : 16.0 / 10.0)
        let width = max(1, room.width)
        func fitted(height: CGFloat, width: CGFloat) -> CGSize {
            let fittedWidth = max(min(width, minimumPreviewWidth), min(width, max(1, height) * aspect))
            return CGSize(width: fittedWidth.rounded(.down), height: (fittedWidth / aspect).rounded(.down))
        }
        let above = chip.map { $0(width) + chipSpacing } ?? 0
        let below = (strip.map { spacing + $0(width) } ?? 0) + (banner.map { spacing + $0(width) } ?? 0)

        guard let stacked else {
            // Beside the inspector: the preview as large as the column allows.
            let preview = fitted(height: room.height - above - below, width: width)
            return SettingsStagePlan(preview: preview, beside: false, height: above + preview.height + below)
        }

        // Above the inspector: the song beside the preview where it fits.
        let besidePreview = fitted(height: stacked.preview, width: width)
        let column = width - besidePreview.width - spacing
        if column >= besideColumnMinimum, strip != nil || banner != nil {
            var side: CGFloat = strip?(column) ?? 0
            if let banner { side += (strip == nil ? 0 : spacing) + banner(column) }
            return SettingsStagePlan(preview: besidePreview, beside: true, height: above + max(besidePreview.height, side))
        }
        let budget = min(stacked.preview, stacked.stage - 2 * LunalyrDesign.Spacing.window - above - below)
        let preview = fitted(height: budget, width: width)
        return SettingsStagePlan(preview: preview, beside: false, height: above + preview.height + below)
    }
}

/// Lays the stage out by `SettingsStagePlan`: centred in its column beside
/// the inspector, or hugging its content above it.
struct SettingsStageLayout: Layout {
    var desktop: CGSize
    var stacked: SettingsStageLimits?

    private var aspect: CGFloat { desktop.width / max(1, desktop.height) }

    private func plan(_ room: CGSize, _ subviews: Subviews) -> SettingsStagePlan {
        func measure(_ role: SettingsStageRole) -> ((CGFloat) -> CGFloat)? {
            let matching = subviews.filter { $0[SettingsStageRole.self] == role }
            guard !matching.isEmpty else { return nil }
            return { width in
                matching.map { $0.sizeThatFits(ProposedViewSize(width: width, height: nil)).height }.max() ?? 0
            }
        }
        return SettingsStagePlan.make(room: room, aspect: aspect, stacked: stacked,
                                      chip: measure(.chip), strip: measure(.strip), banner: measure(.banner))
    }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let width = proposal.width ?? 640
        if stacked == nil {
            let height = proposal.height ?? plan(CGSize(width: width, height: 10_000), subviews).height
            return CGSize(width: width, height: height)
        }
        return CGSize(width: width, height: plan(CGSize(width: width, height: 0), subviews).height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        let plan = plan(bounds.size, subviews)
        var y = bounds.minY + max(0, ((bounds.height - plan.height) / 2).rounded(.down))
        let role = { (subview: LayoutSubview) in subview[SettingsStageRole.self] }
        let chips = subviews.filter { role($0) == .chip }
        let previews = subviews.filter { role($0) == .preview }
        let parts = subviews.filter { role($0) == .strip } + subviews.filter { role($0) == .banner }

        for chip in chips {
            let size = chip.sizeThatFits(ProposedViewSize(width: bounds.width, height: nil))
            chip.place(at: CGPoint(x: bounds.minX, y: y), anchor: .topLeading,
                       proposal: ProposedViewSize(width: min(size.width, bounds.width), height: size.height))
            y += size.height + SettingsStagePlan.chipSpacing
        }
        let previewX = plan.beside ? bounds.minX : bounds.minX + ((bounds.width - plan.preview.width) / 2).rounded(.down)
        for preview in previews {
            preview.place(at: CGPoint(x: previewX, y: y), anchor: .topLeading, proposal: ProposedViewSize(plan.preview))
        }
        if plan.beside {
            // The song and banner in a column to the right, centred on the preview.
            let columnX = bounds.minX + plan.preview.width + SettingsStagePlan.spacing
            let columnWidth = bounds.maxX - columnX
            let heights = parts.map { $0.sizeThatFits(ProposedViewSize(width: columnWidth, height: nil)).height }
            let total = heights.reduce(0, +) + SettingsStagePlan.spacing * CGFloat(max(0, parts.count - 1))
            var columnY = y + max(0, ((plan.preview.height - total) / 2).rounded(.down))
            for (part, height) in zip(parts, heights) {
                part.place(at: CGPoint(x: columnX, y: columnY), anchor: .topLeading,
                           proposal: ProposedViewSize(width: columnWidth, height: height))
                columnY += height + SettingsStagePlan.spacing
            }
            return
        }
        y += plan.preview.height
        for part in parts {
            let height = part.sizeThatFits(ProposedViewSize(width: bounds.width, height: nil)).height
            y += SettingsStagePlan.spacing
            part.place(at: CGPoint(x: bounds.minX, y: y), anchor: .topLeading,
                       proposal: ProposedViewSize(width: bounds.width, height: height))
            y += height
        }
    }
}

/// The song beside the preview: its summary, with the transport at the
/// trailing end, or under the summary where the column is too narrow for
/// both on one line.
struct NowPlayingStripLayout: Layout {
    /// Narrower than this, the transport goes under the summary.
    static let sideBySideMinimum: CGFloat = 300
    var spacing: CGFloat = LunalyrDesign.Spacing.medium

    private func sizes(_ width: CGFloat, _ subviews: Subviews) -> (summary: CGSize, transport: CGSize, sideBySide: Bool) {
        guard subviews.count == 2 else { return (.zero, .zero, true) }
        let transport = subviews[1].sizeThatFits(.unspecified)
        let sideBySide = width >= Self.sideBySideMinimum
        let summaryWidth = sideBySide ? max(0, width - transport.width - spacing) : width
        let summary = subviews[0].sizeThatFits(ProposedViewSize(width: summaryWidth, height: nil))
        return (CGSize(width: summaryWidth, height: summary.height), transport, sideBySide)
    }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let width = proposal.width ?? 360
        let (summary, transport, sideBySide) = sizes(width, subviews)
        let height = sideBySide ? max(summary.height, transport.height)
            : summary.height + LunalyrDesign.Spacing.xSmall + transport.height
        return CGSize(width: width, height: height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        guard subviews.count == 2 else {
            subviews.forEach { $0.place(at: bounds.origin, proposal: ProposedViewSize(bounds.size)) }
            return
        }
        let (summary, transport, sideBySide) = sizes(bounds.width, subviews)
        if sideBySide {
            subviews[0].place(at: CGPoint(x: bounds.minX, y: bounds.midY), anchor: .leading,
                              proposal: ProposedViewSize(summary))
            subviews[1].place(at: CGPoint(x: bounds.maxX, y: bounds.midY), anchor: .trailing,
                              proposal: ProposedViewSize(transport))
        } else {
            subviews[0].place(at: CGPoint(x: bounds.minX, y: bounds.minY), anchor: .topLeading,
                              proposal: ProposedViewSize(summary))
            // The transport lines up under the title, past the artwork.
            let indent = LunalyrDesign.Size.artworkSmall + LunalyrDesign.Spacing.medium - LunalyrDesign.Spacing.xSmall
            subviews[1].place(at: CGPoint(x: bounds.minX + indent, y: bounds.minY + summary.height + LunalyrDesign.Spacing.xSmall),
                              anchor: .topLeading, proposal: ProposedViewSize(transport))
        }
    }
}
