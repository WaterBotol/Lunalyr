import AppKit
import CoreImage
import Observation
import SwiftUI

/// The ambient glow around a video shown whole (`MusicVideoFraming.ambient`):
/// about once a second a small copy of the playing picture is taken, blurred
/// heavily, and cross-faded in behind the video, stretched to fill the
/// screen. The glow takes the scene's colours without a second decode; it
/// lags a cut a little, which reads as light spilling rather than flicker.
@MainActor @Observable
final class MusicVideoAmbience {
    /// How often the picture is sampled.
    static let interval: TimeInterval = 1
    /// Low Power Mode: half as often.
    static let lowPowerInterval: TimeInterval = 2
    /// The cross-fade from one sample to the next.
    static let fade: TimeInterval = 1.2
    /// Reduce Motion: a slower, gentler cross-fade.
    static let reducedFade: TimeInterval = 2.4
    /// The width a sample is taken and blurred at.
    static let sampleWidth: CGFloat = 160
    /// The blur's radius at that width: heavy, so only colour and light are left.
    static let blurRadius: Double = 12
    /// The glow's own darkening, under Settings' dimming.
    static let darkening: Double = 0.3
    /// A little more colour, so the glow does not look grey once blurred.
    static let saturation: Double = 1.25

    /// The newest blurred sample, and how many have been shown (the
    /// cross-fade's identity).
    private(set) var frame: CGImage?
    private(set) var generation = 0

    /// Takes the current picture (nil when there is none to take).
    @ObservationIgnored var capture: ((@escaping @MainActor (CGImage?) -> Void) -> Void)?
    @ObservationIgnored private var timer: Timer?
    @ObservationIgnored private var busy = false

    /// Samples only while the glow shows and the video moves.
    func setActive(_ active: Bool) {
        if active {
            guard timer == nil else { return }
            let interval = ProcessInfo.processInfo.isLowPowerModeEnabled ? Self.lowPowerInterval : Self.interval
            let timer = Timer(timeInterval: interval, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { self?.sample() }
            }
            timer.tolerance = interval * 0.25
            RunLoop.main.add(timer, forMode: .common)
            self.timer = timer
            sample()
        } else {
            timer?.invalidate()
            timer = nil
        }
    }

    /// A new video: the old one's glow goes.
    func reset() {
        frame = nil
        generation += 1
    }

    func stop() {
        setActive(false)
        capture = nil
    }

    private func sample() {
        guard !busy, let capture else { return }
        busy = true
        capture { [weak self] picture in
            guard let self else { return }
            guard let picture else { self.busy = false; return }
            Task { [weak self] in
                let blurred = await Task.detached(priority: .utility) { MusicVideoAmbience.glow(from: picture) }.value
                guard let self else { return }
                self.busy = false
                guard let blurred, self.timer != nil else { return }
                self.frame = blurred
                self.generation += 1
            }
        }
    }

    /// The glow drawn from `image`: reduced, blurred with its edges kept,
    /// and a little richer in colour.
    nonisolated static func glow(from image: CGImage) -> CGImage? {
        guard image.width > 0, image.height > 0 else { return nil }
        let scale = min(1, sampleWidth / CGFloat(image.width))
        var picture = CIImage(cgImage: image).transformed(by: CGAffineTransform(scaleX: scale, y: scale))
        let extent = picture.extent
        if let colour = CIFilter(name: "CIColorControls") {
            colour.setValue(picture, forKey: kCIInputImageKey)
            colour.setValue(saturation, forKey: kCIInputSaturationKey)
            picture = colour.outputImage ?? picture
        }
        guard let blur = CIFilter(name: "CIGaussianBlur") else { return nil }
        blur.setValue(picture.clampedToExtent(), forKey: kCIInputImageKey)
        blur.setValue(blurRadius, forKey: kCIInputRadiusKey)
        guard let output = blur.outputImage?.cropped(to: extent) else { return nil }
        return CIContext(options: [.useSoftwareRenderer: false, .allowLowPower: true]).createCGImage(output, from: extent)
    }
}

/// The glow itself: each sample stretched over the screen, cross-faded in
/// over the last. Clear until the first sample, so the still-based backdrop
/// under it shows.
struct MusicVideoAmbientGlow: View {
    let ambience: MusicVideoAmbience
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        ZStack {
            if let frame = ambience.frame {
                Image(decorative: frame, scale: 1).resizable().interpolation(.medium)
                    .aspectRatio(contentMode: .fill)
                    .id(ambience.generation)
                    .transition(.opacity)
                Color.black.opacity(MusicVideoAmbience.darkening)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .clipped()
        .ignoresSafeArea()
        .animation(.easeInOut(duration: reduceMotion ? MusicVideoAmbience.reducedFade : MusicVideoAmbience.fade),
                   value: ambience.generation)
    }
}
