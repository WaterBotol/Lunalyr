import AppKit
import QuartzCore
import SwiftUI

// MARK: - Motion tokens
//
// One timing vocabulary for every surface. `DesignTokens.swift` holds the four
// original SwiftUI tokens (`quick`, `standard`, `step`, `reduced`) and the
// `animation(_:reduceMotion:)` / `transition(_:reduceMotion:)` helpers; this
// file adds the rest without touching them:
//
//   - SwiftUI chrome: `exit`, `emphasised`, `celebrate`, `reveal`, `symbolSwap`
//     and the `lunalyrReveal(_:)` modifier.
//   - `Hold`: how long feedback stays on screen, and delays before it appears.
//   - `CA`: AppKit / Core Animation equivalents (status item, panels, HUD, layers).
//   - `WallpaperMotion`: time-driven durations for the wallpaper's TimelineView.
//
// Rules (POLISH-AUDIT 2026-11-10, section 3):
//   1. Arrivals settle (vc_settle or a bounce-0 spring); symmetric cross-fades
//      use smoothstep; linear only for karaoke fills and progress.
//   2. Interruptible: a new target starts from the current presented value.
//   3. One-shot animations only; no repeatForever on surfaces that may be hidden.
//   4. Reduce Motion: movement, scale and blur become a 0.18 s opacity dissolve;
//      springs become critically damped; symbol bounces are dropped.
//   5. Every action answers within 100 ms; async work shows nothing under 0.8 s.

extension LunalyrDesign.Motion {
    // MARK: SwiftUI (UI chrome). Springs with bounce 0; exits shorter than entrances.

    /// Removals and dismissals: about 75% of `standard`.
    static let exit = Animation.smooth(duration: 0.2)
    /// Larger moments: the onboarding hero, a card being made. Below the 500 ms "drag" line.
    static let emphasised = Animation.smooth(duration: 0.45)
    /// The only bouncy token: one-shot celebrations (card made, tap sync applied).
    /// Never on frequent UI; use `animation(_:reduceMotion:)` so Reduce Motion drops the bounce.
    static let celebrate = Animation.snappy(duration: 0.35, extraBounce: 0.05)

    /// The vertical settle, in points, that `reveal` adds to a row's fade-in.
    static func revealOffset(reduceMotion: Bool) -> CGFloat {
        reduceMotion ? 0 : -4
    }

    /// Rows appearing or disappearing in a form: opacity plus a 4 pt settle;
    /// opacity only under Reduce Motion.
    static func reveal(reduceMotion: Bool) -> AnyTransition {
        reduceMotion ? .opacity
            : .asymmetric(insertion: .opacity.combined(with: .offset(y: revealOffset(reduceMotion: false))),
                          removal: .opacity)
    }

    /// Symbol swaps (play/pause, status icons): the symbol replace effect, or a
    /// plain cross-fade under Reduce Motion.
    static func symbolSwap(reduceMotion: Bool) -> ContentTransition {
        reduceMotion ? .opacity : .symbolEffect(.replace)
    }

    // MARK: Holds and delays (feedback timing)

    enum Hold {
        /// "Copied", "Saved", "Applied" confirmations.
        static let confirm: TimeInterval = 1.5
        /// The action HUD on screen.
        static let hud: TimeInterval = 1.4
        /// Before a "no artwork" glyph appears.
        static let placeholderDelay: TimeInterval = 0.5
        /// No spinner for waits under about a second.
        static let spinnerDelay: TimeInterval = 0.8
    }

    // MARK: AppKit / Core Animation (status item, panels, HUD, video layers)

    enum CA {
        static let quick: CFTimeInterval = 0.16
        static let standard: CFTimeInterval = 0.28
        /// Reduce Motion's dissolve, matching SwiftUI's `reduced`.
        static let reduced: CFTimeInterval = 0.18
        /// The menu-bar ticker's line push.
        static let line: CFTimeInterval = 0.32

        /// Arrivals: a fast start that settles.
        static var easeOut: CAMediaTimingFunction { .init(controlPoints: 0.22, 1, 0.36, 1) }
        /// Departures: a gentle start that accelerates away.
        static var easeIn: CAMediaTimingFunction { .init(controlPoints: 0.3, 0, 1, 1) }

        /// Fades a window from its current (presented) alpha to `alpha`, so a
        /// reversal mid-fade never jumps. Uses `easeOut` when fading in and
        /// `easeIn` when fading out. `completion` also runs when a later fade
        /// interrupts this one, so check the window's state before acting on it.
        @MainActor
        static func fade(_ window: NSWindow, to alpha: CGFloat, duration: CFTimeInterval,
                         completion: (() -> Void)? = nil) {
            let current = window.alphaValue
            NSAnimationContext.runAnimationGroup { context in
                context.duration = duration
                context.timingFunction = alpha >= current ? easeOut : easeIn
                window.animator().alphaValue = alpha
            } completionHandler: {
                completion?()
            }
        }
    }
}

// MARK: - Wallpaper (time-driven)

/// Time-driven tokens for the wallpaper (TimelineView). Durations in seconds;
/// the comment says which curve to use.
enum WallpaperMotion {
    // Surfaces
    /// Background layer and palette cross-fade. Curve: smoothstep.
    static let surfaceFade: Double = 0.8
    /// Video, motion art, custom image, web visualiser first frame. Curve: easeOut.
    static let mediaReveal: Double = 0.6
    /// Video hiding. Curve: easeIn.
    static let mediaHide: Double = 0.3

    // Lyric scenes (vc_settle, critically damped)
    /// Song change: the old scene leaving.
    static let sceneOut: Double = 0.45
    /// Song change: the new scene arriving.
    static let sceneIn: Double = 0.55
    /// Same-track jump (smoothstep): arrival, departure and the departure's delay.
    static let seekIn: Double = 0.25
    static let seekOut: Double = 0.30
    static let seekOutDelay: Double = 0.10
    /// Every line or stage exit, including a stage's `leave`.
    static let lineExit: Double = 0.46
    /// A word leaving, and the stagger between words.
    static let wordExit: Double = 0.40
    static let wordExitStagger: Double = 0.14
    /// A line's attack, the next-line preview ramp, the exit span, and how
    /// long a sung line holds before it leaves.
    static let lineAttack: Double = 0.22
    static let previewRamp: Double = 0.16
    static let exitSpan: Double = 0.70
    static let holdAfterSung: Double = 1.2
    /// The layout changed in Settings.
    static let styleSwitch: Double = 0.35

    // Notices and toasts
    static let noticeHold: Double = 4.0
    static let noticeFade: Double = 1.0
    static let idleNoticeHold: Double = 5.0
    static let toastIn: Double = 0.32
    /// When the toast starts leaving, measured from its start.
    static let toastOutAt: Double = 1.9
    static let toastOut: Double = 0.55
    static let toastLife: Double = 2.6
    static let interludeIn: Double = 0.5
    static let interludeInDelay: Double = 0.42
    static let interludeOut: Double = 0.4
    static let loadingDelay: Double = 0.8
    static let loadingIn: Double = 0.4

    // Reduce Motion
    /// What movement, scale and blur become: an opacity dissolve.
    static let reducedDissolve: Double = 0.18
}

// MARK: - Curves (time-driven)

/// The wallpaper's curves, for motion driven from a time value instead of
/// a SwiftUI animation.
enum Easing {
    /// Classic 3x²-2x³ ease, clamped: symmetric cross-fades.
    static func smoothstep(_ x: Double) -> Double {
        let clamped = min(max(x, 0), 1)
        return clamped * clamped * (3 - 2 * clamped)
    }

    /// Critically damped arrival (`vc_settle`): 0 until `age` 0, exactly 1
    /// from `duration` on. Anything that arrives or leaves.
    static func settle(_ age: Double, _ duration: Double) -> Double {
        vc_settle(age, duration)
    }
}

// MARK: - Reveal modifier

private struct LunalyrRevealModifier<Value: Equatable>: ViewModifier {
    let value: Value
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    func body(content: Content) -> some View {
        content
            .transition(LunalyrDesign.Motion.reveal(reduceMotion: reduceMotion))
            .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion),
                       value: value)
    }
}

extension View {
    /// Animates changes inside this view with `standard` (or the Reduce Motion
    /// fade) when `value` changes: put it on a section, keyed on the flag that
    /// shows or hides its rows. Keyed on `value`, so it never leaks into
    /// unrelated changes such as the preview. The view itself also gets the
    /// `reveal` transition for when an animated parent inserts or removes it;
    /// conditional rows inside take `.transition(Motion.reveal(reduceMotion:))`
    /// to get the 4 pt settle rather than the default fade.
    func lunalyrReveal<Value: Equatable>(_ value: Value) -> some View {
        modifier(LunalyrRevealModifier(value: value))
    }
}
