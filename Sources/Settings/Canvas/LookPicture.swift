import AppKit
import SwiftUI

// MARK: - The picture
//
// The desktop in miniature, drawn by the app's own renderers: the live
// preview while a song plays (`SettingsLivePreview`, the only live preview
// in the window), the sample wallpaper with the sample line set by
// `LyricLineView` while nothing plays. The selected object is drawn over
// it: an outline round the whole picture for the wallpaper and the covers,
// a box with handles round the lyric line (dragging a corner sets Lyric
// size), or round the floating lyrics' window in the Floating view.

@MainActor
struct LookPicture: View {
    let object: LookObject
    let select: (LookObject) -> Void
    @ObservedObject var previewDisplay: SettingsPreviewDisplay
    @ObservedObject var floating: FloatingLyricsOptions
    @Model(\.settings) private var settings
    @Model(\.nowPlaying) private var playing
    @Environment(\.undoManager) private var undoManager
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    /// Lyric size while a handle is dragged, so the box follows the pointer
    /// and the setting is written once, undoably, when the drag ends.
    @State private var draggedScale: Double?
    @State private var dragStartScale: Double?

    private var desktop: CGSize { previewDisplay.size }
    private var nothingPlaying: Bool { playing.current == nil }
    private var showsFloating: Bool { floating.isFloating && object != .covers }
    @AppStorage(OpeningTitle.settingKey) private var showsOpeningTitle = true
    @AppStorage(OpeningTitle.alwaysKey) private var keepsOpeningTitle = ShippedDefaults.openingTitleAlways

    var body: some View {
        PreviewStage(desktop: desktop, cornerRadius: 8, showsMenuBar: true) {
            ZStack(alignment: .topLeading) {
                if nothingPlaying {
                    SampleWallpaper()
                    if !showsFloating { SampleLyricScene(desktop: desktop) }
                } else {
                    SettingsLivePreview(desktopSize: desktop, desktopPicture: previewDisplay.desktopPicture)
                }
                if showsFloating {
                    FloatingScene(desktop: desktop, options: floating, sample: nothingPlaying)
                        .environment(\.lyricSurface, .floating)
                }
            }
        }
        .overlay { selection }
        .accessibilityElement(children: .contain)
        .accessibilityLabel(nothingPlaying ? "Picture of the desktop, with a sample line" : "Picture of the desktop")
    }

    // MARK: Selection

    @ViewBuilder private var selection: some View {
        GeometryReader { proxy in
            let size = proxy.size
            let scale = draggedScale ?? settings.lyricScale
            let box = LyricSizeDrag.box(in: size, scale: scale)
            ZStack(alignment: .topLeading) {
                // Clicks: the line selects Text (or keeps Animation), anywhere else the wallpaper.
                Color.clear
                    .contentShape(Rectangle())
                    .onTapGesture(coordinateSpace: .local) { point in
                        tapped(at: point, box: showsFloating ? floatingBox(in: size) : box)
                    }
                    .accessibilityHidden(true)
                switch object {
                case .wallpaper, .covers:
                    RoundedRectangle(cornerRadius: 8, style: .continuous)
                        .strokeBorder(Color.studio(.accent), lineWidth: 3)
                        .allowsHitTesting(false)
                        .accessibilityHidden(true)
                case .lyrics, .animation:
                    if showsFloating {
                        FloatingHandles(desktop: desktop, picture: size, options: floating)
                        if object == .lyrics, showsOpeningTitle, floating.showsTitleCard {
                            FloatingTitleHandles(desktop: desktop, picture: size, options: floating)
                        }
                    } else {
                        SelectionHandles(size: box.size)
                            .offset(x: box.minX, y: box.minY)
                        cornerGrips(box: box)
                        if object == .lyrics, showsOpeningTitle {
                            TitleCardHandles(desktop: desktop, picture: size,
                                             showsSample: nothingPlaying || !keepsOpeningTitle)
                        }
                    }
                }
                if nothingPlaying && !showsFloating {
                    Text("Sample")
                        .font(.system(size: 11, weight: .semibold))
                        .foregroundStyle(.white.opacity(0.85))
                        .shadow(color: .black.opacity(0.4), radius: 2)
                        .offset(x: box.minX + 2, y: max(4, box.minY - 18))
                        .allowsHitTesting(false)
                }
            }
            .frame(width: size.width, height: size.height, alignment: .topLeading)
        }
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion), value: object)
    }

    private func tapped(at point: CGPoint, box: CGRect) {
        if box.insetBy(dx: -6, dy: -6).contains(point) {
            // Text or Animation, whichever is showing; Text from elsewhere.
            select(object.isLyrics ? object : .lyrics)
        } else if object != .covers {
            select(.wallpaper)
        }
    }

    private func floatingBox(in size: CGSize) -> CGRect {
        FloatingScene.box(desktop: desktop, picture: size, options: floating)
    }

    /// Invisible grips over the four corner handles: dragging one sets Lyric size.
    private func cornerGrips(box: CGRect) -> some View {
        let corners = [CGPoint(x: box.minX, y: box.minY), CGPoint(x: box.maxX, y: box.minY),
                       CGPoint(x: box.minX, y: box.maxY), CGPoint(x: box.maxX, y: box.maxY)]
        return ForEach(Array(corners.enumerated()), id: \.offset) { _, corner in
            Color.clear
                .frame(width: 18, height: 18)
                .contentShape(Rectangle())
                .position(corner)
                .help("Drag to change Lyric size")
                .gesture(DragGesture(minimumDistance: 1)
                    .onChanged { drag in
                        let start = dragStartScale ?? settings.lyricScale
                        if dragStartScale == nil { dragStartScale = start }
                        let from = CGPoint(x: corner.x - box.midX, y: corner.y - box.midY)
                        draggedScale = LyricSizeDrag.scale(startScale: start, start: from, translation: drag.translation)
                    }
                    .onEnded { _ in finishDrag() })
                .accessibilityHidden(true)
        }
    }

    private func finishDrag() {
        defer {
            draggedScale = nil
            dragStartScale = nil
        }
        guard let before = dragStartScale, let after = draggedScale, abs(after - before) > 0.001 else { return }
        let settings = settings
        settings.lyricScale = after
        undoManager?.registerSettingsChange("Change Lyric Size",
                                            undo: { settings.lyricScale = before },
                                            redo: { settings.lyricScale = after })
    }
}

// MARK: - Nothing playing

/// The app's sample line, set by the real renderer at the desktop's size and
/// scaled down, in the chosen layout and at the chosen Lyric size.
@MainActor
struct SampleLyricScene: View {
    let desktop: CGSize
    @Model(\.settings) private var settings

    static let beat = 0.42
    static let words: [LyricsFetcher.Word] = {
        let texts = SampleLyric.lines.flatMap { $0 }
        return texts.enumerated().map { index, text in
            let start = 1 + Double(index) * beat
            return LyricsFetcher.Word(text: text, start: start, end: start + beat * 0.9)
        }
    }()

    var body: some View {
        GeometryReader { proxy in
            let metrics = LyricViewportMetrics(size: desktop)
            let stage = metrics.stageSize
            let composition = settings.lyricComposition ?? .scattered
            let words = Self.words
            let lineEnd = 1 + Double(words.count) * Self.beat
            let plain = [Bool](repeating: false, count: words.count)
            let glue = composition.hasConnections ? LyricRuns.splittingWords(words, glue: plain) : plain
            LyricLineView(words: words, time: lineEnd + 0.9, stageSize: stage, baseSize: metrics.baseSize,
                          composition: composition, seed: 3, lineEnd: lineEnd, exitStart: lineEnd + 1.2,
                          glue: glue, liftsBackingVocals: false)
                .modifier(FittedLyricStage(available: stage, userScale: CGFloat(settings.lyricScale)))
                .frame(width: stage.width, height: stage.height)
                .environment(\.lyricInk, LyricInk())
                .frame(width: desktop.width, height: desktop.height)
                .scaleEffect(LyricViewportMetrics.previewScale(desktop: desktop, inside: proxy.size))
                .frame(width: proxy.size.width, height: proxy.size.height)
                .environment(\.colorScheme, .dark)
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }
}

// MARK: - Floating view

/// Floating over apps: two app windows over the desktop, the Dock, and the
/// floating lyrics at their Place, with their shade.
@MainActor
struct FloatingScene: View {
    let desktop: CGSize
    @ObservedObject var options: FloatingLyricsOptions
    /// Nothing playing: the sample line.
    var sample: Bool
    @Environment(\.lunalyrModels) private var models
    @Model(\.nowPlaying) private var playing
    @AppStorage(OpeningTitle.settingKey) private var showsTitle = true
    @AppStorage(OpeningTitle.coverKey) private var showsCover = true

    /// The model desktop's menu bar and Dock, in points.
    static let menuBar: CGFloat = 24
    static let dock: CGFloat = 70

    /// The display the picture stands for: the first with floating
    /// lyrics, else the main one.
    @MainActor static func displayKey(_ options: FloatingLyricsOptions) -> String {
        let connected = FloatingFrame.connectedKeys()
        return options.floatingDisplays(among: connected).first ?? connected.first ?? ""
    }

    /// The area the floating window may use on the model desktop (AppKit
    /// coordinates, origin bottom left).
    static func area(desktop: CGSize) -> CGRect {
        CGRect(x: 0, y: dock, width: desktop.width, height: max(1, desktop.height - dock - menuBar))
    }

    /// The floating window's frame on the desktop, in AppKit coordinates.
    static func frame(desktop: CGSize, options: FloatingLyricsOptions) -> CGRect {
        let key = displayKey(options)
        return FloatingFrame.resolve(options.placement(for: key), custom: options.customFrame(for: key),
                                     savedSize: options.savedSize(for: key), textSize: CGFloat(options.textSize),
                                     screenFrame: CGRect(origin: .zero, size: desktop), area: area(desktop: desktop))
    }

    /// A desktop frame (origin bottom left) on a picture of `picture`'s size
    /// (origin top left).
    static func pictureRect(_ frame: CGRect, desktop: CGSize, picture: CGSize) -> CGRect {
        let scale = LyricViewportMetrics.previewScale(desktop: desktop, inside: picture)
        return CGRect(x: frame.minX * scale, y: (desktop.height - frame.maxY) * scale,
                      width: frame.width * scale, height: frame.height * scale)
    }

    static func box(desktop: CGSize, picture: CGSize, options: FloatingLyricsOptions) -> CGRect {
        pictureRect(frame(desktop: desktop, options: options), desktop: desktop, picture: picture)
    }

    /// The song title card's window on the desktop, in AppKit coordinates:
    /// where it was put, else beside the lyrics, as the window places it.
    static func titleFrame(desktop: CGSize, options: FloatingLyricsOptions, lyrics: CGRect? = nil) -> CGRect {
        let key = displayKey(options)
        return FloatingTitleFrame.resolve(custom: options.titleFrame(for: key), start: options.titleStart,
                                          box: lyrics ?? frame(desktop: desktop, options: options),
                                          textSize: CGFloat(options.textSize),
                                          screenFrame: CGRect(origin: .zero, size: desktop), area: area(desktop: desktop))
    }

    var body: some View {
        GeometryReader { proxy in
            let size = proxy.size
            let box = Self.box(desktop: desktop, picture: size, options: options)
            let unit = LyricViewportMetrics.previewScale(desktop: desktop, inside: size)
            ZStack(alignment: .topLeading) {
                appWindow(width: size.width * 0.5, height: size.height * 0.56, dark: false)
                    .offset(x: size.width * 0.05, y: size.height * 0.1)
                appWindow(width: size.width * 0.42, height: size.height * 0.48, dark: true)
                    .offset(x: size.width * 0.5, y: size.height * 0.16)
                RoundedRectangle(cornerRadius: 7, style: .continuous)
                    .fill(Color.white.opacity(0.28))
                    .frame(width: size.width * 0.36, height: Self.dock * unit * 0.8)
                    .offset(x: size.width * 0.32, y: size.height - Self.dock * unit * 0.9)
                lyrics(textSize: CGFloat(options.textSize) * unit)
                    .frame(width: box.width, height: box.height)
                    .background(RoundedRectangle(cornerRadius: 10 * max(0.4, unit * 2), style: .continuous)
                        .fill(Color.black.opacity(0.7 * options.backdrop)))
                    .offset(x: box.minX, y: box.minY)
                if showsTitle && options.showsTitleCard {
                    let frame = Self.titleFrame(desktop: desktop, options: options)
                    let card = Self.pictureRect(frame, desktop: desktop, picture: size)
                    titleCard(size: frame.size)
                        .frame(width: frame.width, height: frame.height)
                        .scaleEffect(unit, anchor: .topLeading)
                        .frame(width: card.width, height: card.height, alignment: .topLeading)
                        .offset(x: card.minX, y: card.minY)
                }
            }
            .frame(width: size.width, height: size.height, alignment: .topLeading)
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }

    /// The title card as its window draws it, settled, at its size on the
    /// desktop: stacked, or one row when it is small (`FloatingTitleFrame.layout`).
    private func titleCard(size: CGSize) -> some View {
        let track = playing.current
        return OpeningTitleCard(phase: OpeningTitle.Phase(age: 10, exit: 0, size: .full, moving: false),
                                title: track?.title ?? "Song Title", artist: track?.artist ?? "Artist",
                                album: track.flatMap { OpeningTitle.albumLine(album: $0.album ?? "", title: $0.title) },
                                reading: nil,
                                cover: OpeningTitle.cover(playing.currentTrackArtwork, enabled: showsCover),
                                baseSize: CGFloat(options.textSize),
                                mood: .neutral, window: true)
            .environment(\.colorScheme, .dark)
    }

    @ViewBuilder private func lyrics(textSize: CGFloat) -> some View {
        if sample {
            lines(SampleLyric.lines.map { $0.joined(separator: " ") }.joined(separator: " "), next: nil, size: textSize)
        } else {
            TimelineView(.periodic(from: .now, by: playing.isPlaying ? 0.25 : 30)) { _ in
                let resolved = models.resolved
                let now = resolved.lyricNow(resolved)
                lines(now.line ?? playing.current?.title ?? "", next: now.next, size: textSize)
            }
        }
    }

    private func lines(_ line: String, next: String?, size: CGFloat) -> some View {
        VStack(spacing: 2) {
            Text(line)
                .font(.system(size: max(4, size), weight: .bold))
                .foregroundStyle(.white)
            if let next {
                Text(next)
                    .font(.system(size: max(3, size * 0.65), weight: .semibold))
                    .foregroundStyle(.white.opacity(0.55))
            }
        }
        .lineLimit(1)
        .minimumScaleFactor(0.5)
        .multilineTextAlignment(.center)
        .padding(.horizontal, 6)
    }

    private func appWindow(width: CGFloat, height: CGFloat, dark: Bool) -> some View {
        VStack(spacing: 0) {
            HStack(spacing: 3) {
                Circle().fill(Color(red: 1, green: 0.37, blue: 0.34))
                Circle().fill(Color(red: 1, green: 0.74, blue: 0.18))
                Circle().fill(Color(red: 0.16, green: 0.79, blue: 0.25))
                Spacer()
            }
            .frame(width: 26, height: 5)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(6)
            VStack(alignment: .leading, spacing: 5) {
                ForEach(0..<6, id: \.self) { index in
                    Capsule().fill((dark ? Color.white : Color.black).opacity(0.12))
                        .frame(width: width * Self.bars[index], height: 4)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            .padding(.horizontal, 10)
            .padding(.top, 4)
        }
        .frame(width: width, height: height)
        .background(RoundedRectangle(cornerRadius: 6, style: .continuous)
            .fill(dark ? Color(white: 0.17) : Color(white: 0.97)))
        .shadow(color: .black.opacity(0.35), radius: 6, y: 3)
    }

    private static let bars: [CGFloat] = [0.7, 0.55, 0.62, 0.4, 0.66, 0.5]
}

/// The song's title card on the picture, on the Text object: a box with
/// handles where the card sits. Dragging the box moves the card; dragging
/// its top-right corner scales it from its lower-left corner. Saved as it
/// moves (`OpeningTitle.Placement`), so the live picture follows, and kept
/// on screen. With nothing playing (or when the card is not kept on
/// screen, so it may not be up), a settled sample card shows where it sits.
@MainActor
struct TitleCardHandles: View {
    let desktop: CGSize
    let picture: CGSize
    var showsSample = true
    @Model(\.nowPlaying) private var playing
    // Read so the box redraws when the placement changes (a Reset).
    @AppStorage(OpeningTitle.Placement.leftKey) private var left: Double?
    @AppStorage(OpeningTitle.Placement.liftKey) private var lift: Double?
    @AppStorage(OpeningTitle.Placement.scaleKey) private var scale: Double?
    @State private var start: (box: CGRect, placement: OpeningTitle.Placement)?

    private var baseSize: CGFloat { LyricViewportMetrics(size: desktop).baseSize }
    private var unit: CGFloat { LyricViewportMetrics.previewScale(desktop: desktop, inside: picture) }

    var body: some View {
        let placement = OpeningTitle.Placement.resolved(left: left, lift: lift, scale: scale)
        let frame = OpeningTitle.box(in: desktop, baseSize: baseSize, placement: placement)
        let box = CGRect(x: frame.minX * unit, y: frame.minY * unit, width: frame.width * unit, height: frame.height * unit)
        ZStack(alignment: .topLeading) {
            if showsSample { sample }
            Color.clear
                .frame(width: box.width, height: box.height)
                .contentShape(Rectangle())
                .offset(x: box.minX, y: box.minY)
                .help("Drag to move the song title; drag its top-right corner to resize it")
                .gesture(drag(resizing: false))
            SelectionHandles(size: box.size)
                .offset(x: box.minX, y: box.minY)
            Text("Song title")
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(.white.opacity(0.85))
                .shadow(color: .black.opacity(0.4), radius: 2)
                .offset(x: box.minX + 2, y: max(4, box.minY - 18))
                .allowsHitTesting(false)
            Color.clear
                .frame(width: 18, height: 18)
                .contentShape(Rectangle())
                .position(x: box.maxX, y: box.minY)
                .gesture(drag(resizing: true))
        }
        .frame(width: picture.width, height: picture.height, alignment: .topLeading)
        .accessibilityHidden(true)
    }

    /// The card as it settles, at the desktop's size, scaled down.
    private var sample: some View {
        let track = playing.current
        return OpeningTitleCard(phase: OpeningTitle.Phase(age: 10, exit: 0, size: .full, moving: false),
                                title: track?.title ?? "Song Title", artist: track?.artist ?? "Artist",
                                album: track.flatMap { OpeningTitle.albumLine(album: $0.album ?? "", title: $0.title) },
                                reading: nil,
                                cover: OpeningTitle.cover(playing.currentTrackArtwork, enabled: OpeningTitle.showsCover),
                                baseSize: baseSize,
                                mood: .neutral)
            .frame(width: desktop.width, height: desktop.height)
            .scaleEffect(unit, anchor: .topLeading)
            .frame(width: picture.width, height: picture.height, alignment: .topLeading)
            .environment(\.colorScheme, .dark)
            .allowsHitTesting(false)
    }

    private func drag(resizing: Bool) -> some Gesture {
        DragGesture(minimumDistance: 1)
            .onChanged { value in
                let origin = start ?? {
                    let placement = OpeningTitle.Placement.stored()
                    return (OpeningTitle.box(in: desktop, baseSize: baseSize, placement: placement), placement)
                }()
                if start == nil { start = origin }
                let delta = CGSize(width: value.translation.width / max(0.01, unit),
                                   height: value.translation.height / max(0.01, unit))
                Self.dragged(origin.box, placement: origin.placement, by: delta, resizing: resizing,
                             desktop: desktop, baseSize: baseSize).store()
            }
            .onEnded { _ in start = nil }
    }

    /// The placement after the box `box` (desktop points, origin top left)
    /// has been dragged by `delta`: moved, or scaled from its lower-left
    /// corner along its diagonal; kept on screen.
    static func dragged(_ box: CGRect, placement: OpeningTitle.Placement, by delta: CGSize, resizing: Bool,
                        desktop: CGSize, baseSize: CGFloat) -> OpeningTitle.Placement {
        if resizing {
            let diagonal = CGPoint(x: box.width, y: -box.height)
            let length = diagonal.x * diagonal.x + diagonal.y * diagonal.y
            guard length > 1 else { return placement }
            let along = ((diagonal.x + delta.width) * diagonal.x + (diagonal.y + delta.height) * diagonal.y) / length
            var scaled = OpeningTitle.placement(for: box, in: desktop, scale: placement.scale * Double(max(0.1, along)))
            // The lower-left corner stays put.
            scaled.corner = OpeningTitle.placement(for: box, in: desktop, scale: 1).corner
            return scaled
        }
        let moved = box.offsetBy(dx: delta.width, dy: delta.height)
        let wanted = OpeningTitle.placement(for: moved, in: desktop, scale: placement.scale)
        // Kept on screen, as the card itself is.
        let kept = OpeningTitle.box(in: desktop, baseSize: baseSize, placement: wanted)
        return OpeningTitle.placement(for: kept, in: desktop, scale: placement.scale)
    }
}

/// The floating song title card's window on the picture (Floating style,
/// Text): handles where it sits. Dragging it moves it anywhere on the
/// display; dragging its corner resizes it, and the card scales with it
/// (one row once it is small). Saved for the display when let go, as Edit
/// position saves it (`FloatingLyricsOptions.storeTitleFrame`).
@MainActor
struct FloatingTitleHandles: View {
    let desktop: CGSize
    let picture: CGSize
    @ObservedObject var options: FloatingLyricsOptions
    /// The frame while a drag is under way (desktop coordinates).
    @State private var dragged: CGRect?
    @State private var start: CGRect?

    var body: some View {
        let frame = dragged ?? FloatingScene.titleFrame(desktop: desktop, options: options)
        let box = FloatingScene.pictureRect(frame, desktop: desktop, picture: picture)
        // On a small picture the card is a few points tall: grab it by a
        // target of at least 48 x 28 round it.
        let grab = box.insetBy(dx: -max(0, (48 - box.width) / 2), dy: -max(0, (28 - box.height) / 2))
        ZStack(alignment: .topLeading) {
            Color.clear
                .frame(width: grab.width, height: grab.height)
                .contentShape(Rectangle())
                .offset(x: grab.minX, y: grab.minY)
                .help("Drag to move the song title anywhere; drag its corner to resize it")
                // It may sit over the lyrics' box: it, not the box, takes a drag that starts on it.
                .highPriorityGesture(drag(resizing: false))
            SelectionHandles(size: box.size)
                .offset(x: box.minX, y: box.minY)
            Text("Song title")
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(.white.opacity(0.85))
                .shadow(color: .black.opacity(0.4), radius: 2)
                .offset(x: box.minX + 2, y: max(4, box.minY - 18))
                .allowsHitTesting(false)
            Color.clear
                .frame(width: 18, height: 18)
                .contentShape(Rectangle())
                .position(x: box.maxX, y: box.maxY)
                .highPriorityGesture(drag(resizing: true))
        }
        .frame(width: picture.width, height: picture.height, alignment: .topLeading)
        .accessibilityHidden(true)
    }

    /// The frame after a drag on the picture by `translation` (picture
    /// points, y down) from `start` (desktop points, y up), at `scale`.
    static func dragged(_ start: CGRect, translation: CGSize, scale: CGFloat, resizing: Bool, area: CGRect) -> CGRect {
        let scale = max(0.01, scale)
        let delta = CGSize(width: translation.width / scale, height: -translation.height / scale)
        return FloatingFrame.dragged(start, by: delta, resizing: resizing, in: area, minimum: FloatingTitleFrame.minimumSize)
    }

    private func drag(resizing: Bool) -> some Gesture {
        DragGesture(minimumDistance: 1)
            .onChanged { value in
                let origin = start ?? FloatingScene.titleFrame(desktop: desktop, options: options)
                if start == nil { start = origin }
                dragged = Self.dragged(origin, translation: value.translation,
                                       scale: LyricViewportMetrics.previewScale(desktop: desktop, inside: picture),
                                       resizing: resizing, area: FloatingScene.area(desktop: desktop))
            }
            .onEnded { _ in
                if let dragged {
                    options.storeTitleFrame(dragged, for: FloatingScene.displayKey(options))
                }
                dragged = nil
                start = nil
            }
    }
}

/// Handles round the floating lyrics' window: dragging it moves it (Place:
/// Custom), dragging a corner resizes it, as Edit position does.
@MainActor
struct FloatingHandles: View {
    let desktop: CGSize
    let picture: CGSize
    @ObservedObject var options: FloatingLyricsOptions
    /// The frame while a drag is under way (desktop coordinates).
    @State private var dragged: CGRect?
    @State private var start: CGRect?

    var body: some View {
        let frame = dragged ?? FloatingScene.frame(desktop: desktop, options: options)
        let box = FloatingScene.pictureRect(frame, desktop: desktop, picture: picture)
        ZStack(alignment: .topLeading) {
            Color.clear
                .frame(width: box.width, height: box.height)
                .contentShape(Rectangle())
                .offset(x: box.minX, y: box.minY)
                .gesture(drag(resizing: false))
            SelectionHandles(size: box.size)
                .offset(x: box.minX, y: box.minY)
            Color.clear
                .frame(width: 18, height: 18)
                .contentShape(Rectangle())
                .position(x: box.maxX, y: box.maxY)
                .gesture(drag(resizing: true))
        }
        .frame(width: picture.width, height: picture.height, alignment: .topLeading)
        .accessibilityHidden(true)
    }

    private func drag(resizing: Bool) -> some Gesture {
        DragGesture(minimumDistance: 1)
            .onChanged { value in
                let origin = start ?? FloatingScene.frame(desktop: desktop, options: options)
                if start == nil { start = origin }
                let scale = max(0.01, LyricViewportMetrics.previewScale(desktop: desktop, inside: picture))
                // AppKit's y runs up.
                let delta = CGSize(width: value.translation.width / scale, height: -value.translation.height / scale)
                dragged = FloatingFrame.dragged(origin, by: delta, resizing: resizing,
                                                in: FloatingScene.area(desktop: desktop))
            }
            .onEnded { _ in
                if let dragged {
                    options.storeCustomFrame(dragged, for: FloatingScene.displayKey(options))
                }
                dragged = nil
                start = nil
            }
    }
}
