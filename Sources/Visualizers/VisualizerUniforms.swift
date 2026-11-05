// Adapted from Synesthia by Jon Jaques, MIT licence.
// Pinned source and local changes: ThirdParty/Synesthia/SOURCE.md.
// Copyright (c) 2026 Jon Jaques. See Resources/Synesthia-LICENSE.txt.

import Foundation
import simd

struct VizUniforms {
    /// Drawable size in pixels; shaders use it to correct for aspect ratio.
    var resolution = SIMD2<Float>(0, 0)
    /// Seconds since app launch — the animation clock.
    var time: Float = 0
    /// Seconds since the previous frame (clamped; see MetalVisualizerView).
    var dt: Float = 0
    // Audio features copied from AudioSnapshot (see AudioAnalyzer.swift for
    // what each one means). All roughly 0...1.
    var bass: Float = 0
    var mid: Float = 0
    var treble: Float = 0
    var level: Float = 0
    var beat: Float = 0
    // User tuning from the options popover.
    var sensitivity: Float = 1
    var speed: Float = 1
    /// Palette index as a float (uniform blocks hold floats; the shader
    /// rounds it back to an int).
    var palette: Float = 0
    // The visualizer's own options, in declaration order — 16 consecutive
    // floats, which is byte-identical to the `float p[16]` the shaders index.
    // Read them through `subscript(parameter:)` rather than by name.
    var p0: Float = 0
    var p1: Float = 0
    var p2: Float = 0
    var p3: Float = 0
    var p4: Float = 0
    var p5: Float = 0
    var p6: Float = 0
    var p7: Float = 0
    var p8: Float = 0
    var p9: Float = 0
    var p10: Float = 0
    var p11: Float = 0
    var p12: Float = 0
    var p13: Float = 0
    var p14: Float = 0
    var p15: Float = 0
    // Finer-grained audio features (named sub-bands and transients).
    var subBass: Float = 0
    var lowMid: Float = 0
    var highMid: Float = 0
    var presence: Float = 0
    var air: Float = 0
    var trebleBeat: Float = 0
    var flux: Float = 0
    var centroid: Float = 0
    // Per-frame state the host derives once so no visualizer has to keep it
    // itself. `beat`/`trebleBeat` are *envelopes* that decay smoothly, so
    // "a new hit landed this frame" is not something a shader can see; these
    // two are the rising edges, 1 on the frame of the hit and 0 otherwise —
    // exactly what a simulation kernel needs to apply an impulse (a GPU
    // kernel has no memory of the previous frame's envelope).
    var beatHit: Float = 0
    var trebleHit: Float = 0
    /// Kicks counted since the audio source started: `kickOnsets`, so it
    /// restarts with a new source. A free clock for anything that wants to
    /// advance per beat rather than per second (and the seed of the
    /// beat-synced scene changes in docs/roadmap.md).
    var beatCount: Float = 0
    /// Frames rendered since launch, wrapped well inside float precision.
    /// Useful as a per-frame seed for hashes and temporal dithering.
    var frame: Float = 0
    /// 1 when the system Reduce Motion setting is on. The host already damps
    /// the transient features (see `applyReduceMotionIfNeeded`), but some
    /// effects can only be toned down structurally — a particle sim's
    /// turbulence, a strobe — so they get told outright.
    var reduceMotion: Float = 0
    /// Ramps 0 → 1 over the first moments after this visualizer was built,
    /// so a switch can fade in rather than pop. Groundwork for the crossfade
    /// half of beat-synced scene changes.
    var intro: Float = 1
    // Sound types (SoundTypeDetector), evaluated at the displayed time. The
    // six envelopes are 0...1. The transients (kick, snare, hat) are already
    // scaled by Punch; under Reduce Motion and Dim Flashing Lights they are
    // read with slow shapes at half Punch instead (at most half strength, 0
    // when Punch is off). The sustained ones are never scaled. Classes are
    // not exclusive: a kick and a snare on one beat both fire. While the
    // scene is idle nothing new flares and the counters hold.
    var soundKick: Float = 0
    var soundSnare: Float = 0
    var soundHat: Float = 0
    /// The bass line, kick tails held out.
    var soundBass: Float = 0
    /// The lead voice or melodic lead: partials that move together in pitch.
    var soundVocal: Float = 0
    /// The sustained harmonic bed: chords, pads, strings.
    var soundPad: Float = 0
    /// Onsets counted since the source started, as exact integers (wrapped
    /// at 2^24); only a new source restarts them. `uint(u.kickOnsets)` seeds
    /// a fresh random subset per hit; crossfade from the previous subset over
    /// the first ~50 ms of the age so membership never pops.
    var kickOnsets: Float = 0
    var snareOnsets: Float = 0
    var hatOnsets: Float = 0
    /// Real seconds since the newest onset at the displayed time, 0...60
    /// (60 = none yet). Not scaled by Speed or Reduce Motion.
    var kickAge: Float = 60
    var snareAge: Float = 60
    var hatAge: Float = 60

    /// The option slots in descriptor order. They are stored properties
    /// rather than a Swift array because the layout has to match MSL's
    /// `float p[16]` byte for byte, and this table is how the host fills them
    /// without a sixteen-case switch.
    private static let parameterKeyPaths: [WritableKeyPath<VizUniforms, Float>] = [
        \.p0, \.p1, \.p2, \.p3, \.p4, \.p5, \.p6, \.p7,
        \.p8, \.p9, \.p10, \.p11, \.p12, \.p13, \.p14, \.p15,
    ]

    /// How many options a visualizer may declare — one number, derived from
    /// the struct itself, that the host, the docs, and the tests all use.
    static var parameterCount: Int { parameterKeyPaths.count }

    subscript(parameter index: Int) -> Float {
        get { self[keyPath: Self.parameterKeyPaths[index]] }
        set { self[keyPath: Self.parameterKeyPaths[index]] = newValue }
    }

    /// Bounds-checked `subscript(parameter:)`: a descriptor that declares more
    /// options than there are slots quietly loses the extras rather than
    /// trapping in a release build.
    mutating func setParameter(_ index: Int, to value: Float) {
        guard Self.parameterKeyPaths.indices.contains(index) else { return }
        self[parameter: index] = value
    }
}
