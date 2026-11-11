import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Renders the floating lyrics window's content over app-like screens to
/// PNGs: Small, Medium and Large at the default place, two other presets,
/// edit mode, Reduce Transparency, and the Settings location picker.
/// Preview only: nothing plays, nothing is fetched, no window opens.
/// Skipped unless LUNALYR_FLOATING_PREVIEWS names a folder (pass it to
/// `xcodebuild test` as TEST_RUNNER_LUNALYR_FLOATING_PREVIEWS=<dir>).
@MainActor
final class FloatingLyricsPreviewTests: XCTestCase {
    static let screen = CGSize(width: 1440, height: 900)
    static let menuBar: CGFloat = 30
    static let dock: CGFloat = 64
    /// The usable area in AppKit's coordinates (origin bottom left).
    static var area: CGRect { CGRect(x: 0, y: dock, width: screen.width, height: screen.height - dock - menuBar) }
    static let grid: [SIMD3<Double>] = [
        SIMD3(0.92, 0.55, 0.85), SIMD3(0.95, 0.45, 0.75), SIMD3(0.62, 0.50, 0.70),
        SIMD3(0.58, 0.60, 0.55), SIMD3(0.90, 0.65, 0.90), SIMD3(0.08, 0.50, 0.95),
        SIMD3(0.66, 0.70, 0.40), SIMD3(0.60, 0.40, 0.60), SIMD3(0.05, 0.40, 0.80),
    ]

    struct Shot {
        let name: String
        let caption: String
        var textSize: Double = FloatingLyricsOptions.SizePreset.medium.rawValue
        var placement: FloatingPlacement = .bottom
        var dark = false
        var editing = false
        var reduceTransparency = false
        var backdrop = FloatingLyricsOptions.defaultBackdrop
    }

    static let shots: [Shot] = [
        Shot(name: "01-small-bottom-light", caption: "Small (30 pt), above the Dock, over a light app",
             textSize: 30),
        Shot(name: "02-medium-bottom-light", caption: "Medium (44 pt), above the Dock, over a light app"),
        Shot(name: "03-large-bottom-light", caption: "Large (64 pt), above the Dock, over a light app", textSize: 64),
        Shot(name: "04-medium-bottom-dark", caption: "Medium, above the Dock, over a dark app", dark: true),
        Shot(name: "05-medium-top-right-light", caption: "Medium, Top right preset", placement: .topRight),
        Shot(name: "06-small-top-dark", caption: "Small, Top preset (below the menu bar), dark app", textSize: 30,
             placement: .top, dark: true),
        Shot(name: "07-medium-centre-edit", caption: "Edit position: drag to move, corner to resize",
             placement: .centre, editing: true),
        Shot(name: "08-medium-reduce-transparency", caption: "Reduce Transparency: a solid panel instead of the shade",
             reduceTransparency: true),
        Shot(name: "09-medium-no-shade-light", caption: "Shade behind lyrics at 0: shadow only", backdrop: 0),
    ]

    func testRenderFloatingLyricsPreviews() throws {
        let environment = ProcessInfo.processInfo.environment
        guard let path = environment["LUNALYR_FLOATING_PREVIEWS"] ?? environment["TEST_RUNNER_LUNALYR_FLOATING_PREVIEWS"],
              !path.isEmpty else {
            throw XCTSkip("Set TEST_RUNNER_LUNALYR_FLOATING_PREVIEWS=<dir> to render the floating lyrics previews")
        }
        let folder = URL(fileURLWithPath: path)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)

        let music = FakeMusicApp()
        music.queue = [FakeMusicApp.Song(id: "00000000000000F1", name: "Floating Probe", artist: "Render Harness",
                                         album: "Previews")]
        music.position = 20
        let harness = PlayerHarness(music: music)
        harness.start()
        let model = LyricsViewModel.shared, settings = VersoCustom.Settings.shared
        let extras = LunalyrExtras.shared, readings = RomanizationOptions.shared
        let saved = (lines: model.lines, plain: model.plainLines, notFound: model.notFound, loading: model.isLoading,
                     composition: settings.lyricComposition, hidden: extras.lyricsHidden, display: readings.display)
        defer {
            model.lines = saved.lines
            model.plainLines = saved.plain
            model.notFound = saved.notFound
            model.isLoading = saved.loading
            settings.lyricComposition = saved.composition
            extras.lyricsHidden = saved.hidden
            if readings.display != saved.display { readings.display = saved.display }
        }
        model.plainLines = []
        model.notFound = false
        model.isLoading = false
        settings.lyricComposition = .editorial
        extras.lyricsHidden = false
        if readings.display != .below { readings.display = .below }
        model.lines = LyricHighlightRenderTests.lines
        let deadline = Date().addingTimeInterval(20)
        while model.romanized.count < 3, Date() < deadline {
            RunLoop.main.run(until: Date().addingTimeInterval(0.05))
        }

        var wall = Date().timeIntervalSinceReferenceDate + 30
        for shot in Self.shots {
            // A Latin line mid-word, and a Japanese one with its reading.
            for (suffix, time) in [("", 19.4), ("-reading", 31.2)] where suffix.isEmpty || shot.name.hasPrefix("02")
                || shot.name.hasPrefix("03") {
                let renderer = ImageRenderer(content: scene(shot, time: time - 1.2, wall: wall, monitor: harness.monitor))
                renderer.scale = 1
                // Let entrances and colours settle as the clock runs.
                for step in 0..<24 {
                    wall += 0.05
                    renderer.content = scene(shot, time: time - 1.2 + Double(step) * 0.05, wall: wall, monitor: harness.monitor)
                    renderer.render { _, _ in }
                }
                let image = try XCTUnwrap(renderer.cgImage, shot.name)
                try DesktopPlayerPreviewTests.write(image, to: folder.appendingPathComponent("\(shot.name)\(suffix).png"))
            }
        }

        let picker = ImageRenderer(content: PickerSheet())
        picker.scale = 2
        try DesktopPlayerPreviewTests.write(try XCTUnwrap(picker.cgImage),
                                            to: folder.appendingPathComponent("10-settings-location-picker.png"))
    }

    func scene(_ shot: Shot, time: Double, wall: Double, monitor: NowPlayingMonitor) -> PreviewScene {
        PreviewScene(shot: shot, time: time, wall: wall, monitor: monitor)
    }

    /// A screen with a menu bar, an app window and the Dock, and the
    /// floating lyrics where the placement puts them.
    struct PreviewScene: View {
        let shot: Shot
        let time: Double
        let wall: Double
        let monitor: NowPlayingMonitor

        var body: some View {
            let screen = FloatingLyricsPreviewTests.screen
            let area = FloatingLyricsPreviewTests.area
            let frame = FloatingFrame.resolve(shot.placement, custom: nil, savedSize: nil, textSize: CGFloat(shot.textSize),
                                              screenFrame: CGRect(origin: .zero, size: screen), area: area)
            ZStack(alignment: .topLeading) {
                FakeDesktop(dark: shot.dark)
                window(size: frame.size)
                    .offset(x: frame.minX, y: screen.height - frame.maxY)
                Text(shot.caption)
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(.white)
                    .padding(.horizontal, 10).padding(.vertical, 5)
                    .background(Capsule().fill(Color.black.opacity(0.7)))
                    .offset(x: 16, y: FloatingLyricsPreviewTests.menuBar + 12)
            }
            .frame(width: screen.width, height: screen.height, alignment: .topLeading)
            .environment(\.colorScheme, shot.dark ? .dark : .light)
        }

        private func window(size: CGSize) -> some View {
            let look = FloatingLyricsLook(grid: FloatingLyricsPreviewTests.grid, track: monitor.current, wordsPerSecond: 2)
            let metrics = LyricViewportMetrics(size: size, textSize: CGFloat(shot.textSize))
            return ZStack {
                FloatingLyricsFrame(backdrop: shot.backdrop, reduceTransparency: shot.reduceTransparency,
                                    albumAccent: look.albumAccent) {
                    LyricLayer(time: time, baseSize: metrics.baseSize, maxWidth: metrics.stageSize.width,
                               maxHeight: metrics.stageSize.height, mood: .neutral, accent: look.accent,
                               trackSeed: 4242, wall: wall, palette: look.cover.artworkStops)
                        .frame(width: size.width, height: size.height)
                        .environmentObject(monitor)
                }
                if shot.editing {
                    FloatingEditChrome(placeholder: false, onDrag: { _, _ in }, onDone: {})
                }
            }
            .frame(width: size.width, height: size.height)
            .clipped()
        }
    }

    /// A stand-in desktop: a menu bar, a document window and the Dock.
    struct FakeDesktop: View {
        let dark: Bool

        var body: some View {
            let screen = FloatingLyricsPreviewTests.screen
            let page = dark ? Color(white: 0.13) : Color(white: 0.985)
            let chrome = dark ? Color(white: 0.2) : Color(white: 0.93)
            let ink = dark ? Color(white: 0.75) : Color(white: 0.25)
            ZStack(alignment: .topLeading) {
                LinearGradient(colors: [Color(red: 0.25, green: 0.35, blue: 0.55), Color(red: 0.55, green: 0.4, blue: 0.5)],
                               startPoint: .topLeading, endPoint: .bottomTrailing)
                // The app's window fills most of the screen.
                VStack(spacing: 0) {
                    HStack(spacing: 8) {
                        ForEach([Color.red, .yellow, .green], id: \.self) { Circle().fill($0.opacity(0.85)).frame(width: 12) }
                        Spacer()
                        Text("Quarterly notes.txt").font(.system(size: 13, weight: .semibold)).foregroundStyle(ink)
                        Spacer()
                    }
                    .padding(.horizontal, 14)
                    .frame(height: 38)
                    .background(chrome)
                    HStack(alignment: .top, spacing: 0) {
                        VStack(alignment: .leading, spacing: 12) {
                            ForEach(0..<12, id: \.self) { row in
                                RoundedRectangle(cornerRadius: 3).fill(ink.opacity(0.25))
                                    .frame(width: CGFloat(90 + (row * 37) % 70), height: 10)
                            }
                        }
                        .padding(18)
                        .frame(width: 220, alignment: .topLeading)
                        .frame(maxHeight: .infinity, alignment: .top)
                        .background(chrome.opacity(0.7))
                        VStack(alignment: .leading, spacing: 14) {
                            Text("Planning for the spring release")
                                .font(.system(size: 26, weight: .bold)).foregroundStyle(ink)
                            ForEach(0..<20, id: \.self) { row in
                                RoundedRectangle(cornerRadius: 3).fill(ink.opacity(row % 6 == 5 ? 0 : 0.32))
                                    .frame(width: CGFloat(520 + (row * 131) % 560), height: 11)
                            }
                            RoundedRectangle(cornerRadius: 8)
                                .fill(LinearGradient(colors: [.orange, .pink, .purple], startPoint: .leading, endPoint: .trailing))
                                .frame(width: 520, height: 150)
                        }
                        .padding(28)
                        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
                        .background(page)
                    }
                }
                .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
                .frame(width: screen.width - 80, height: screen.height - FloatingLyricsPreviewTests.menuBar - 40)
                .offset(x: 40, y: FloatingLyricsPreviewTests.menuBar + 10)
                // Menu bar.
                HStack(spacing: 18) {
                    Image(systemName: "apple.logo")
                    Text("Notes").bold()
                    Text("File"); Text("Edit"); Text("View")
                    Spacer()
                    Text("Tue 11 Nov 22:40")
                }
                .font(.system(size: 13))
                .foregroundStyle(dark ? .white : .black)
                .padding(.horizontal, 16)
                .frame(width: screen.width, height: FloatingLyricsPreviewTests.menuBar)
                .background(dark ? Color.black.opacity(0.55) : Color.white.opacity(0.65))
                // Dock.
                RoundedRectangle(cornerRadius: 16, style: .continuous)
                    .fill(Color.white.opacity(0.3))
                    .overlay(HStack(spacing: 10) {
                        ForEach(0..<9, id: \.self) { index in
                            RoundedRectangle(cornerRadius: 10)
                                .fill(Color(hue: Double(index) / 9, saturation: 0.55, brightness: 0.85))
                                .frame(width: 44, height: 44)
                        }
                    })
                    .frame(width: 520, height: FloatingLyricsPreviewTests.dock - 8)
                    .offset(x: (screen.width - 520) / 2, y: screen.height - FloatingLyricsPreviewTests.dock + 4)
            }
            .frame(width: screen.width, height: screen.height, alignment: .topLeading)
        }
    }

    /// The location picker as Settings shows it, in each scheme.
    struct PickerSheet: View {
        var body: some View {
            HStack(spacing: 24) {
                ForEach([(ColorScheme.light, FloatingPlacement.bottom), (.dark, .topRight)], id: \.1) { scheme, placement in
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Place").font(.system(size: 13, weight: .semibold))
                        FloatingLocationPicker(placement: .constant(placement))
                        Text(placement.title).font(.system(size: 11)).foregroundStyle(.secondary)
                    }
                    .padding(16)
                    .background(scheme == .dark ? Color(white: 0.16) : Color(white: 0.96))
                    .environment(\.colorScheme, scheme)
                    .foregroundStyle(scheme == .dark ? Color.white : Color.black)
                }
            }
            .padding(16)
            .background(Color(white: 0.5))
        }
    }
}
