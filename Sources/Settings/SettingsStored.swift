import Foundation

// MARK: - Reading a stored value, once

extension Settings {
    /// One reader per value a lyric style or a monitor's look keeps: what
    /// is stored, or its fallback (`ShippedDefaults` where it changed in
    /// November 2026). `init` and the reloads (`reloadLyricStyle`,
    /// `reloadWallpaper`) both read through these, so a value never stored
    /// reads the same at launch as after Settings moved to another monitor
    /// or style, and the fallbacks cannot drift apart again.
    @MainActor
    enum Stored {
        // MARK: The wallpaper (`MonitorLookKeys.wallpaper`)

        static func backgroundMode(_ d: UserDefaults) -> BackgroundMode {
            BackgroundMode(rawValue: d.string(forKey: "backgroundMode") ?? "") ?? ShippedDefaults.backgroundMode
        }

        /// Never Music video itself.
        static func musicVideoFallback(_ d: UserDefaults) -> BackgroundMode {
            let stored: BackgroundMode = BackgroundMode(rawValue: d.string(forKey: "custom.musicVideoFallback") ?? "") ?? .auto
            return stored == .musicVideo ? .auto : stored
        }

        static func musicVideoDim(_ d: UserDefaults) -> Double {
            let stored: Double = d.object(forKey: "custom.musicVideoDim") as? Double ?? 0.35
            return min(Settings.musicVideoDimRange.upperBound, max(0, stored))
        }

        static func musicVideoBlur(_ d: UserDefaults) -> Double {
            let stored: Double = d.object(forKey: "custom.musicVideoBlur") as? Double ?? 0
            return min(Settings.musicVideoBlurRange.upperBound, max(0, stored))
        }

        static func albumGradientWallpaper(_ d: UserDefaults) -> Bool {
            d.object(forKey: "custom.albumGradientWallpaper") as? Bool ?? true
        }

        /// Missing: the shipped pattern; stored but not a pattern
        /// (`ShippedDefaults.noPattern`): none, the track picks.
        static func patternStyle(_ d: UserDefaults) -> BackgroundStyle? {
            guard let stored = d.object(forKey: "patternStyle") as? Int else { return ShippedDefaults.patternStyle }
            return BackgroundStyle(rawValue: stored)
        }

        /// Missing: the shipped shader; stored but not a shader
        /// (`ShippedDefaults.automatic`): none, the track picks.
        static func shaderStyle(_ d: UserDefaults) -> ShaderStyle? {
            guard let stored = d.string(forKey: "shaderStyle") else { return ShippedDefaults.shaderStyle }
            return ShaderStyle(rawValue: stored)
        }

        static func useMotionArtwork(_ d: UserDefaults) -> Bool {
            d.object(forKey: "useMotionArtwork") as? Bool ?? ShippedDefaults.useMotionArtwork
        }

        static func effectSpeed(_ d: UserDefaults) -> Double {
            d.object(forKey: "effectSpeed") as? Double ?? ShippedDefaults.effectSpeed
        }

        /// A preset no longer in the catalogue reads as the default one.
        static func wavefieldPreset(_ d: UserDefaults) -> String {
            let stored: String = d.string(forKey: "custom.wavefieldPreset") ?? "default"
            return WavefieldWebPreset.catalogue.contains { $0.id == stored } ? stored : "default"
        }

        static func wavefieldProjection(_ d: UserDefaults) -> WavefieldProjection {
            WavefieldProjection(rawValue: d.string(forKey: "custom.wavefieldProjection") ?? "")
                ?? ShippedDefaults.wavefieldProjection
        }

        static func audioVisualizerStyle(_ d: UserDefaults) -> AudioVisualizerStyle {
            AudioVisualizerStyle(rawValue: d.string(forKey: "custom.visualizerStyle") ?? "")
                ?? ShippedDefaults.visualizerStyle
        }

        static func patternOpacity(_ d: UserDefaults) -> Double {
            d.object(forKey: "patternOpacity") as? Double ?? ShippedDefaults.patternOpacity
        }

        /// The darkening chosen (`preferredScrimStrength`).
        static func scrimStrength(_ d: UserDefaults) -> Double {
            d.object(forKey: "scrimStrength") as? Double ?? ShippedDefaults.scrimStrength
        }

        static func vividness(_ d: UserDefaults) -> Double {
            d.object(forKey: "vividness") as? Double ?? ShippedDefaults.vividness
        }

        static func motionBlur(_ d: UserDefaults) -> Double {
            d.object(forKey: "motionBlur") as? Double ?? ShippedDefaults.motionBlur
        }

        static func motionPlacement(_ d: UserDefaults) -> MotionPlacement {
            MotionPlacement(rawValue: d.string(forKey: "motionPlacement") ?? "") ?? ShippedDefaults.motionPlacement
        }

        static func customWallpaperPath(_ d: UserDefaults) -> String {
            d.string(forKey: "custom.wallpaperPath") ?? ""
        }

        static func customWallpaperName(_ d: UserDefaults) -> String {
            d.string(forKey: "custom.wallpaperName") ?? ""
        }

        static func customWallpaperPlacement(_ d: UserDefaults) -> MotionPlacement {
            MotionPlacement(rawValue: d.string(forKey: "custom.wallpaperPlacement") ?? "") ?? .fill
        }

        static func light3DColours(_ d: UserDefaults) -> Light3DColours {
            Light3DColours(rawValue: d.string(forKey: "custom.light3DColours") ?? "") ?? ShippedDefaults.light3DColours
        }

        // MARK: The lyric style (`LyricStyleProfiles.keys`)

        static func fontChoice(_ d: UserDefaults) -> FontChoice {
            FontChoice(rawValue: d.string(forKey: "fontChoice") ?? "") ?? ShippedDefaults.fontChoice
        }

        static func googleFontFamily(_ d: UserDefaults) -> String {
            d.string(forKey: "googleFontFamily") ?? ""
        }

        static func installedFontFamily(_ d: UserDefaults) -> String {
            d.string(forKey: Settings.installedFontFamilyKey) ?? ""
        }

        /// nil: Custom Mix, stored as `automatic`.
        static func lyricComposition(_ d: UserDefaults) -> LyricComposition? {
            let saved: String? = d.string(forKey: "lyricComposition")
            if saved == "automatic" { return nil }
            return saved.flatMap(LyricComposition.saved) ?? ShippedDefaults.lyricComposition
        }

        static func constellationSpread(_ d: UserDefaults) -> Double {
            d.object(forKey: "constellationSpread") as? Double ?? ShippedDefaults.constellationSpread
        }

        static func constellationLinkOpacity(_ d: UserDefaults) -> Double {
            d.object(forKey: "constellationLinkOpacity") as? Double ?? ShippedDefaults.constellationLinkOpacity
        }

        static func wordRevealByStyle(_ d: UserDefaults) -> [String: Bool] {
            d.dictionary(forKey: "custom.wordRevealByStyle") as? [String: Bool] ?? ShippedDefaults.wordRevealByStyle
        }

        static func wordArrivalByStyle(_ d: UserDefaults) -> [String: String] {
            d.dictionary(forKey: "custom.wordArrivalByStyle") as? [String: String] ?? ShippedDefaults.wordArrivalByStyle
        }

        static func nextWordPreviewByStyle(_ d: UserDefaults) -> [String: Bool] {
            d.dictionary(forKey: "custom.nextWordPreviewByStyle") as? [String: Bool] ?? ShippedDefaults.nextWordPreviewByStyle
        }

        static func nextWordPreviewLead(_ d: UserDefaults) -> Double {
            d.object(forKey: "custom.nextWordPreviewLead") as? Double ?? 0.45
        }

        static func wordEntranceMix(_ d: UserDefaults) -> [String] {
            d.stringArray(forKey: "custom.wordEntranceMix") ?? ShippedDefaults.wordEntranceMix
        }

        static func echoStrength(_ d: UserDefaults) -> Double {
            d.object(forKey: "echoStrength") as? Double ?? ShippedDefaults.echoStrength
        }

        static func lyricScale(_ d: UserDefaults) -> Double {
            d.object(forKey: "lyricScale") as? Double ?? ShippedDefaults.lyricScale
        }

        static func showInterludes(_ d: UserDefaults) -> Bool {
            d.object(forKey: "custom.showInterludes") as? Bool ?? true
        }

        static func thinkingOrbStyle(_ d: UserDefaults) -> String {
            ThinkingOrb.normalised(d.string(forKey: ThinkingOrb.styleKey))
        }

        static func wordSymbols(_ d: UserDefaults) -> Bool {
            d.object(forKey: Settings.wordSymbolsKey) as? Bool ?? true
        }

        static func actionWords(_ d: UserDefaults) -> Bool {
            d.object(forKey: Settings.actionWordsKey) as? Bool ?? true
        }

        static func actionWordStrength(_ d: UserDefaults) -> ActionWordStrength {
            ActionWordStrength(rawValue: d.string(forKey: Settings.actionWordStrengthKey) ?? "") ?? .wild
        }

        static func editorialTypography(_ d: UserDefaults) -> Bool {
            d.bool(forKey: "custom.editorialTypography")
        }

        static func editorialAmount(_ d: UserDefaults) -> Double {
            d.object(forKey: "custom.editorialAmount") as? Double ?? 1
        }

        static func randomizeLyricFonts(_ d: UserDefaults) -> Bool {
            d.bool(forKey: "custom.randomizeLyricFonts")
        }
    }
}
