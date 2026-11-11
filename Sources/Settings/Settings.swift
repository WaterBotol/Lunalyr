import AppKit
import CoreText
import ServiceManagement
import SwiftUI

/// User preferences, persisted to `UserDefaults` and read live by the
/// wallpaper. Everything defaults to `auto`, where the track's own genre and
/// cadence choose for you; each can be pinned instead.
@MainActor
final class Settings: ObservableObject {
    static let shared = Settings()

    enum AnimationRate: Int, CaseIterable, Identifiable {
        case economical = 15, balanced = 30, smooth = 60
        var id: Int { rawValue }
        var title: String { "\(rawValue) fps" }
        var interval: Double { 1.0 / Double(rawValue) }
    }

    enum FontChoice: String, CaseIterable, Identifiable {
        case manrope, appleMusic, auto, sans, serif, slab, soft, google
        var id: String { rawValue }

        var title: String {
            switch self {
            case .manrope: return "Manrope"
            case .appleMusic: return "Apple Music — SF Pro"
            case .auto: return "Match the music"
            case .sans: return "Sans — Avenir Next"
            case .serif: return "Serif — Charter"
            case .slab: return "Slab — Rockwell"
            case .soft: return "Soft — SF Rounded"
            case .google: return "Google Fonts or installed font"
            }
        }
    }

    /// What to fall back to when the GPU should be spared.
    enum PowerBehaviour: String, CaseIterable, Identifiable {
        case full, lightEffects, patterns, colourOnly
        var id: String { rawValue }

        var title: String {
            switch self {
            case .full: return "Keep full effects"
            case .lightEffects: return "Only inexpensive effects"
            case .patterns: return "Drop to line patterns"
            case .colourOnly: return "Colour only"
            }
        }
    }

    /// What to show once a track turns out to have no lyrics.
    enum NoLyricsBehaviour: String, CaseIterable, Identifiable {
        case notice, trackName, nothing
        var id: String { rawValue }

        var title: String {
            switch self {
            case .notice: return "Say so briefly"
            case .trackName: return "Show title and artist"
            case .nothing: return "Show nothing"
            }
        }
    }

    /// How an animated cover is fitted to the screen.
    ///
    /// Covers are square and screens are wide, so something has to give: fill
    /// crops the top and bottom away, fit shows the whole frame and pillarboxes
    /// it. The anchored variants exist because the subject of a cover is rarely
    /// dead centre -- an artist's face is usually in the upper half.
    enum MotionPlacement: String, CaseIterable, Identifiable {
        case fill, fillTop, fillBottom, fit
        var id: String { rawValue }

        var title: String {
            switch self {
            case .fill: return "Fill"
            case .fillTop: return "Fill, top"
            case .fillBottom: return "Fill, bottom"
            case .fit: return "Fit whole frame"
            }
        }

        var detail: String {
            switch self {
            case .fill: return "Crops equally top and bottom."
            case .fillTop: return "Crops from the bottom, keeping the top of the cover."
            case .fillBottom: return "Crops from the top."
            case .fit: return "Nothing cropped; the sides are left black."
            }
        }
    }

    enum BackgroundMode: String, CaseIterable, Identifiable {
        case auto, pattern, shader, colourOnly, visualizer, desktop, custom, musicVideo
        var id: String { rawValue }

        /// What "Music video" can fall back to: every other type.
        static let musicVideoFallbacks: [BackgroundMode] = allCases.filter { $0 != .musicVideo }

        var title: String {
            switch self {
            case .auto: return "Match the music"
            case .pattern: return "Line patterns"
            case .shader: return "GPU effects"
            case .visualizer: return "Visualisers"
            case .desktop: return "Your wallpaper"
            case .custom: return "Imported image or video"
            case .colourOnly: return "Colour only"
            case .musicVideo: return "Music video"
            }
        }
    }

    @Published var fontChoice: FontChoice { didSet { store(fontChoice.rawValue, "fontChoice") } }
    @Published var googleFontFamily: String { didSet { store(googleFontFamily, "googleFontFamily") } }
    @Published var backgroundMode: BackgroundMode {
        didSet {
            // Music video falls back to whatever was chosen before it.
            if backgroundMode == .musicVideo, oldValue != .musicVideo { musicVideoFallback = oldValue }
            store(backgroundMode.rawValue, "backgroundMode")
        }
    }
    /// What "Music video" shows when a song has no video, and on displays
    /// that "Heavy scenes on main display only" keeps it off. Never itself.
    @Published var musicVideoFallback: BackgroundMode {
        didSet {
            if musicVideoFallback == .musicVideo { musicVideoFallback = .auto; return }
            store(musicVideoFallback.rawValue, "custom.musicVideoFallback")
        }
    }
    /// Off by default: looking a video up online sends the song's title
    /// and artist to YouTube's search page (www.youtube.com); nothing goes
    /// to Apple or song.link. Any YouTube video shown, found or pasted,
    /// plays in YouTube's embedded player (`iframe_api` from www.youtube.com,
    /// the video from youtube-nocookie.com), with this on or off.
    @Published var musicVideoOnline: Bool { didSet { store(musicVideoOnline, "custom.musicVideoOnline") } }
    /// How much the video is darkened, 0...`musicVideoDimRange` upper bound.
    @Published var musicVideoDim: Double { didSet { store(musicVideoDim, "custom.musicVideoDim") } }
    /// Blur radius in points.
    @Published var musicVideoBlur: Double { didSet { store(musicVideoBlur, "custom.musicVideoBlur") } }
    /// Which displays play the video. Follows "Heavy scenes on main display
    /// only" until the user picks; a pick overrides it for the video only.
    @Published var musicVideoDisplays: MusicVideoDisplayChoice {
        didSet { store(musicVideoDisplays.stored, MusicVideoDisplayChoice.key) }
    }
    static let musicVideoDimRange: ClosedRange<Double> = 0...0.85
    static let musicVideoBlurRange: ClosedRange<Double> = 0...24
    @Published var albumGradientWallpaper: Bool { didSet { store(albumGradientWallpaper, "custom.albumGradientWallpaper") } }
    /// `nil` means let the track pick within the mode.
    @Published var patternStyle: BackgroundStyle? { didSet { store(patternStyle?.rawValue, "patternStyle") } }
    @Published var shaderStyle: ShaderStyle? { didSet { store(shaderStyle?.rawValue, "shaderStyle") } }
    @Published var useMotionArtwork: Bool { didSet { store(useMotionArtwork, "useMotionArtwork") } }
    /// `nil` means let the track pick.
    @Published var lyricComposition: LyricComposition? { didSet { store(lyricComposition?.rawValue ?? "automatic", "lyricComposition") } }
    /// How far Constellation throws its words apart.
    @Published var constellationSpread: Double { didSet { store(constellationSpread, "constellationSpread") } }
    @Published var constellationLinkOpacity: Double { didSet { store(constellationLinkOpacity, "constellationLinkOpacity") } }
    @Published var wordRevealByStyle: [String: Bool] {
        didSet { store(wordRevealByStyle, "custom.wordRevealByStyle") }
    }
    @Published var wordArrivalByStyle: [String: String] {
        didSet { store(wordArrivalByStyle, "custom.wordArrivalByStyle") }
    }
    @Published var nextWordPreviewByStyle: [String: Bool] {
        didSet { store(nextWordPreviewByStyle, "custom.nextWordPreviewByStyle") }
    }
    var wordControlsKey: String { lyricComposition?.rawValue ?? "automatic" }
    @Published var nextWordPreviewLead: Double {
        didSet { store(nextWordPreviewLead, "custom.nextWordPreviewLead") }
    }

    func previewsNextWord(in style: LyricComposition) -> Bool {
        if lyricComposition == nil, let all = nextWordPreviewByStyle[wordControlsKey] { return all }
        return nextWordPreviewByStyle[style.rawValue] ?? true
    }

    func requiresWordEntrances(in style: LyricComposition) -> Bool {
        wordRevealLock(in: style) != nil
    }

    /// Why sung words must be revealed one by one in `style`, if they must --
    /// the reason the workspace gives for locking "Reveal sung words".
    enum WordRevealLock: Equatable {
        /// Spoken, Cascade and Cutout build every line from the sung words.
        case layout(LyricComposition)
        /// The chosen entrance unveils each word as it is sung.
        case entrance(WordArrival)
        /// The custom mix includes such an entrance.
        case mix
    }

    /// With a layout chosen, only an entrance it shows on sung words locks
    /// it: Hinge with Satin Ribbon moves its words its own way, so nothing
    /// needs unveiling. Match the music's choice is one for every layout it
    /// picks, so it locks as before.
    func wordRevealLock(in style: LyricComposition) -> WordRevealLock? {
        if style.arrivalOnly { return .layout(style) }
        let selected = arrival(in: style)
        let chosenLayout = lyricComposition != nil
        let unveils = { (arrival: WordArrival) in
            arrival.requiresReveal && (!chosenLayout || EntranceSupport.appliesToSungWords(arrival, in: style))
        }
        if unveils(selected) { return .entrance(selected) }
        if selected == .mix, wordEntranceMix.contains(where: { WordArrival(rawValue: $0).map(unveils) == true }) {
            return .mix
        }
        return nil
    }

    func revealsWords(in style: LyricComposition) -> Bool {
        if requiresWordEntrances(in: style) { return true }
        if lyricComposition == nil, let all = wordRevealByStyle[wordControlsKey] { return all }
        return wordRevealByStyle[style.rawValue] ?? true
    }
    @Published var wordEntranceMix: [String] {
        didSet { store(wordEntranceMix, "custom.wordEntranceMix") }
    }
    func arrival(in style: LyricComposition, for word: LyricsFetcher.Word) -> WordArrival {
        let selected = arrival(in: style)
        guard selected == .mix else { return selected.resolved(for: style) }
        let options = WordArrival.mixChoices.filter { wordEntranceMix.contains($0.rawValue) }
        guard !options.isEmpty else { return WordArrival.automatic.resolved(for: style) }
        return options[Int(word.presentationID.visualSeed % UInt64(options.count))]
    }
    func arrival(in style: LyricComposition) -> WordArrival {
        if lyricComposition == nil, let all = wordArrivalByStyle[wordControlsKey].flatMap(WordArrival.init(rawValue:)) {
            return all
        }
        return WordArrival(rawValue: wordArrivalByStyle[style.rawValue] ?? "") ?? .automatic
    }
    /// How present Echo's repeats are.
    @Published var echoStrength: Double { didSet { store(echoStrength, "echoStrength") } }
    @Published var lyricOffset: Double { didSet { store(lyricOffset, "custom.lyricOffset") } }
    @Published var lyricScale: Double { didSet { store(lyricScale, "lyricScale") } }
    @Published var showInterludes: Bool { didSet { store(showInterludes, "custom.showInterludes") } }
    @Published var animationRate: AnimationRate { didSet { store(animationRate.rawValue, "custom.animationRate") } }
    @Published var pauseWhenCovered: Bool { didSet { store(pauseWhenCovered, "custom.pauseWhenCovered") } }
    @Published var hasOnboarded: Bool { didSet { store(hasOnboarded, "hasOnboarded") } }
    /// Ease off the GPU all the time, whatever the power source.
    @Published var lowPowerVisuals: Bool {
        didSet { store(lowPowerVisuals, "custom.lowPowerVisuals") }
    }
    @Published var editorialTypography: Bool {
        didSet { store(editorialTypography, "custom.editorialTypography") }
    }
    @Published var editorialAmount: Double {
        didSet { store(editorialAmount, "custom.editorialAmount") }
    }
    @Published var randomizeLyricFonts: Bool {
        didSet { store(randomizeLyricFonts, "custom.randomizeLyricFonts") }
    }
    private var systemObservers: [NSObjectProtocol] = []
    /// Power saving caps the frame rate at 30 fps; it never raises a slower
    /// choice to meet it.
    var effectiveAnimationRate: AnimationRate { Self.effectiveRate(animationRate, sparingGPU: sparesGPU) }
    @Published var respectLowPower: Bool { didSet { store(respectLowPower, "respectLowPower") } }
    @Published var onBattery: PowerBehaviour { didSet { store(onBattery.rawValue, "onBattery") } }
    @Published var noLyrics: NoLyricsBehaviour { didSet { store(noLyrics.rawValue, "noLyrics") } }
    /// Registered with the system rather than stored by us -- macOS is the
    /// source of truth, so the toggle reflects what it actually did.
    @Published var launchAtLogin: Bool { didSet { applyLaunchAtLogin() } }
    // Advanced: fine control over how the background is drawn.
    /// Multiplies every animation clock.
    @Published var effectSpeed: Double { didSet { store(effectSpeed, "effectSpeed") } }
    @Published var audioReactiveEnabled: Bool { didSet { store(audioReactiveEnabled, "custom.audioReactiveEnabled") } }
    @Published var audioSensitivity: Double { didSet { store(audioSensitivity, "custom.audioSensitivity") } }
    @Published var audioCaptureMethod: AudioCaptureMethod { didSet { store(audioCaptureMethod.rawValue, "custom.audioCaptureMethod") } }
    @Published var customWallpaperPath: String {
        didSet { store(customWallpaperPath, "custom.wallpaperPath") }
    }
    @Published var customWallpaperName: String {
        didSet { store(customWallpaperName, "custom.wallpaperName") }
    }
    @Published var customWallpaperPlacement: MotionPlacement {
        didSet { store(customWallpaperPlacement.rawValue, "custom.wallpaperPlacement") }
    }
    enum Light3DColours: String, CaseIterable, Identifiable {
        case album, original
        var id: Self { self }
        var title: String { self == .album ? "Album cover" : "Original Cymatic" }
    }
    @Published var light3DColours: Light3DColours {
        didSet { store(light3DColours.rawValue, "custom.light3DColours") }
    }
    @Published var wavefieldPreset: String { didSet { store(wavefieldPreset, "custom.wavefieldPreset") } }
    @Published var wavefieldProjection: WavefieldProjection { didSet { store(wavefieldProjection.rawValue, "custom.wavefieldProjection") } }
    @Published var audioVisualizerStyle: AudioVisualizerStyle { didSet { store(audioVisualizerStyle.rawValue, "custom.visualizerStyle") } }
    /// Strength of the line-pattern layer over the colour field.
    @Published var patternOpacity: Double { didSet { store(patternOpacity, "patternOpacity") } }
    /// How much the backdrop is darkened to keep lyrics readable, as drawn:
    /// the chosen amount, but never less than `increasedContrastScrimFloor`
    /// while Increase Contrast is on. Setting it changes the chosen amount.
    var scrimStrength: Double {
        get { Self.effectiveScrim(preferredScrimStrength, increaseContrast: increaseContrast) }
        set { preferredScrimStrength = newValue }
    }
    /// The darkening the user chose. Kept as chosen under Increase Contrast,
    /// so it comes back when that is switched off.
    @Published private(set) var preferredScrimStrength: Double { didSet { store(preferredScrimStrength, "scrimStrength") } }
    /// Mirrors Accessibility > Display > Increase Contrast.
    @Published private(set) var increaseContrast: Bool
    /// Saturation applied to colours sampled from the artwork.
    @Published var vividness: Double { didSet { store(vividness, "vividness") } }
    /// Softening applied to Apple Music animated covers.
    @Published var motionBlur: Double { didSet { store(motionBlur, "motionBlur") } }
    @Published var motionPlacement: MotionPlacement { didSet { store(motionPlacement.rawValue, "motionPlacement") } }
    /// Albums excluded from motion artwork, keyed `artist|album`, lowercased.
    @Published var motionExclusions: [String] { didSet { store(motionExclusions, "motionExclusions") } }

    /// PostScript names of the chosen Google or installed family, once
    /// available to this process.
    @Published private(set) var googleRegular: String?
    @Published private(set) var googleBold: String?
    @Published private(set) var googleStatus: String?
    /// Where the chosen family's load stands, for the Typeface section's
    /// spinner, warning or tick beside `googleStatus`.
    @Published private(set) var googleFontState: GoogleFontState = .idle
    /// Whether that family reads as sans, serif, slab or soft, so Editorial
    /// can set its second voice against it.
    @Published private(set) var googleVoice: LyricFace = .sans

    private let defaults: UserDefaults
    private let loginItem: LoginItemService

    private convenience init() {
        self.init(defaults: .standard, notifications: .default)
    }

    /// Reads and writes `defaults`, and listens for power and accessibility
    /// changes on `notifications`. The app uses the shared instance; tests
    /// pass an isolated suite so they never touch real preferences.
    init(defaults d: UserDefaults, notifications: NotificationCenter,
         loginItem: LoginItemService = SMAppService.mainApp) {
        defaults = d
        self.loginItem = loginItem
        fontChoice = FontChoice(rawValue: d.string(forKey: "fontChoice") ?? "") ?? .manrope
        googleFontFamily = d.string(forKey: "googleFontFamily") ?? ""
        backgroundMode = BackgroundMode(rawValue: d.string(forKey: "backgroundMode") ?? "") ?? .auto
        let fallback = BackgroundMode(rawValue: d.string(forKey: "custom.musicVideoFallback") ?? "") ?? .auto
        musicVideoFallback = fallback == .musicVideo ? .auto : fallback
        musicVideoOnline = d.bool(forKey: "custom.musicVideoOnline")
        musicVideoDim = min(Self.musicVideoDimRange.upperBound,
                            max(0, d.object(forKey: "custom.musicVideoDim") as? Double ?? 0.35))
        musicVideoBlur = min(Self.musicVideoBlurRange.upperBound, max(0, d.object(forKey: "custom.musicVideoBlur") as? Double ?? 0))
        musicVideoDisplays = MusicVideoDisplayChoice(stored: d.object(forKey: MusicVideoDisplayChoice.key))
        albumGradientWallpaper = d.object(forKey: "custom.albumGradientWallpaper") as? Bool ?? true
        patternStyle = (d.object(forKey: "patternStyle") as? Int).flatMap(BackgroundStyle.init(rawValue:))
        shaderStyle = (d.string(forKey: "shaderStyle")).flatMap(ShaderStyle.init(rawValue:))
        useMotionArtwork = d.object(forKey: "useMotionArtwork") as? Bool ?? true
        let savedComposition = d.string(forKey: "lyricComposition")
        lyricComposition = savedComposition == "automatic" ? nil : savedComposition.flatMap(LyricComposition.init(rawValue:)) ?? .constellationEditorial
        constellationSpread = d.object(forKey: "constellationSpread") as? Double ?? 1.0
        constellationLinkOpacity = d.object(forKey: "constellationLinkOpacity") as? Double ?? 0.20
        wordRevealByStyle = d.dictionary(forKey: "custom.wordRevealByStyle") as? [String: Bool] ?? [:]
        wordArrivalByStyle = d.dictionary(forKey: "custom.wordArrivalByStyle") as? [String: String] ?? [:]
        nextWordPreviewByStyle = d.dictionary(forKey: "custom.nextWordPreviewByStyle") as? [String: Bool] ?? [:]
        echoStrength = d.object(forKey: "echoStrength") as? Double ?? 1.0
        lyricOffset = d.object(forKey: "custom.lyricOffset") as? Double ?? 0
        lyricScale = d.object(forKey: "lyricScale") as? Double ?? 1.0
        showInterludes = d.object(forKey: "custom.showInterludes") as? Bool ?? true
        animationRate = AnimationRate(rawValue: d.integer(forKey: "custom.animationRate")) ?? .balanced
        pauseWhenCovered = d.object(forKey: "custom.pauseWhenCovered") as? Bool ?? true
        hasOnboarded = d.bool(forKey: "hasOnboarded")
        respectLowPower = d.object(forKey: "respectLowPower") as? Bool ?? true
        onBattery = PowerBehaviour(rawValue: d.string(forKey: "onBattery") ?? "") ?? .lightEffects
        noLyrics = NoLyricsBehaviour(rawValue: d.string(forKey: "noLyrics") ?? "") ?? .notice
        launchAtLogin = Self.launchesAtLogin(loginItem.status)
        effectSpeed = d.object(forKey: "effectSpeed") as? Double ?? 1.0
        audioReactiveEnabled = d.bool(forKey: "custom.audioReactiveEnabled")
        audioSensitivity = d.object(forKey: "custom.audioSensitivity") as? Double ?? 1.0
        audioCaptureMethod = AudioCaptureMethod(rawValue: d.string(forKey: "custom.audioCaptureMethod") ?? "") ?? .automatic
        let wavefieldPreset = d.string(forKey: "custom.wavefieldPreset") ?? "default"
        self.wavefieldPreset = WavefieldWebPreset.catalogue.contains { $0.id == wavefieldPreset } ? wavefieldPreset : "default"
        wavefieldProjection = WavefieldProjection(rawValue: d.string(forKey: "custom.wavefieldProjection") ?? "") ?? .preset
        audioVisualizerStyle = AudioVisualizerStyle(rawValue: d.string(forKey: "custom.visualizerStyle") ?? "") ?? .silk
        patternOpacity = d.object(forKey: "patternOpacity") as? Double ?? 0.85
        preferredScrimStrength = d.object(forKey: "scrimStrength") as? Double ?? 1.0
        vividness = d.object(forKey: "vividness") as? Double ?? 1.0
        motionBlur = d.object(forKey: "motionBlur") as? Double ?? 5.0
        motionPlacement = MotionPlacement(rawValue: d.string(forKey: "motionPlacement") ?? "") ?? .fill
        motionExclusions = d.stringArray(forKey: "motionExclusions") ?? []
        nextWordPreviewLead = d.object(forKey: "custom.nextWordPreviewLead") as? Double ?? 0.45
        wordEntranceMix = d.stringArray(forKey: "custom.wordEntranceMix")
            ?? [WordArrival.fade.rawValue, WordArrival.lift.rawValue, WordArrival.cutoutFold.rawValue]
        lowPowerVisuals = d.bool(forKey: "custom.lowPowerVisuals")
        editorialTypography = d.bool(forKey: "custom.editorialTypography")
        editorialAmount = d.object(forKey: "custom.editorialAmount") as? Double ?? 1
        randomizeLyricFonts = d.bool(forKey: "custom.randomizeLyricFonts")
        customWallpaperPath = d.string(forKey: "custom.wallpaperPath") ?? ""
        customWallpaperName = d.string(forKey: "custom.wallpaperName") ?? ""
        customWallpaperPlacement = MotionPlacement(rawValue: d.string(forKey: "custom.wallpaperPlacement") ?? "") ?? .fill
        light3DColours = Light3DColours(rawValue: d.string(forKey: "custom.light3DColours") ?? "") ?? .album
        increaseContrast = NSWorkspace.shared.accessibilityDisplayShouldIncreaseContrast
        if launchAtLogin && loginItem.status == .requiresApproval { launchAtLoginError = Self.loginItemNeedsApproval }

        // Every power input feeds `effectiveBackgroundMode`, so each change is
        // republished: the wallpaper redraws and audio capture reconciles at
        // once rather than on whatever next touches Settings.
        for name in [Notification.Name.NSProcessInfoPowerStateDidChange, PowerSource.didChange] {
            systemObservers.append(notifications.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.objectWillChange.send() }
            })
        }
        systemObservers.append(NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.accessibilityDisplayOptionsDidChangeNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.increaseContrast = NSWorkspace.shared.accessibilityDisplayShouldIncreaseContrast
            }
        })
        // A family that failed while the Mac was offline is fetched again as
        // soon as the connection returns.
        systemObservers.append(notifications.addObserver(forName: GoogleFontsProvider.networkReturned, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.retryGoogleFontAfterReconnect() }
        })
        // Not inside the XCTest host, as the app delegate skips Manrope there:
        // the shared settings load as the host launches, before the tests'
        // throwaway home is in place, so the family saved by a Debug run
        // would be read from the account's own font cache, or downloaded.
        if fontChoice == .google, !googleFontFamily.isEmpty, !AppRuntime.isUnitTesting {
            Task { await loadGoogleFont(family: googleFontFamily) }
        }
    }

    /// Rereads every lyric-style preference (`LyricStyleProfiles.keys`)
    /// after the floating and desktop styles swapped, with the same
    /// fallbacks as at launch.
    func reloadLyricStyle() {
        let d = defaults
        func set<T: Equatable>(_ path: ReferenceWritableKeyPath<Settings, T>, _ value: T) {
            if self[keyPath: path] != value { self[keyPath: path] = value }
        }
        let savedComposition = d.string(forKey: "lyricComposition")
        set(\.lyricComposition, savedComposition == "automatic" ? nil
            : savedComposition.flatMap(LyricComposition.init(rawValue:)) ?? .constellationEditorial)
        set(\.constellationSpread, d.object(forKey: "constellationSpread") as? Double ?? 1.0)
        set(\.constellationLinkOpacity, d.object(forKey: "constellationLinkOpacity") as? Double ?? 0.20)
        set(\.wordRevealByStyle, d.dictionary(forKey: "custom.wordRevealByStyle") as? [String: Bool] ?? [:])
        set(\.wordArrivalByStyle, d.dictionary(forKey: "custom.wordArrivalByStyle") as? [String: String] ?? [:])
        set(\.nextWordPreviewByStyle, d.dictionary(forKey: "custom.nextWordPreviewByStyle") as? [String: Bool] ?? [:])
        set(\.nextWordPreviewLead, d.object(forKey: "custom.nextWordPreviewLead") as? Double ?? 0.45)
        set(\.wordEntranceMix, d.stringArray(forKey: "custom.wordEntranceMix")
            ?? [WordArrival.fade.rawValue, WordArrival.lift.rawValue, WordArrival.cutoutFold.rawValue])
        set(\.echoStrength, d.object(forKey: "echoStrength") as? Double ?? 1.0)
        set(\.lyricScale, d.object(forKey: "lyricScale") as? Double ?? 1.0)
        set(\.showInterludes, d.object(forKey: "custom.showInterludes") as? Bool ?? true)
        set(\.editorialTypography, d.bool(forKey: "custom.editorialTypography"))
        set(\.editorialAmount, d.object(forKey: "custom.editorialAmount") as? Double ?? 1)
        set(\.randomizeLyricFonts, d.bool(forKey: "custom.randomizeLyricFonts"))
        let choice = FontChoice(rawValue: d.string(forKey: "fontChoice") ?? "") ?? .manrope
        let family = d.string(forKey: "googleFontFamily") ?? ""
        let fontChanged = choice != fontChoice || family != googleFontFamily
        set(\.googleFontFamily, family)
        set(\.fontChoice, choice)
        guard fontChanged else { return }
        googleFontLoadID = UUID()
        // A Google or installed family loads as it does at launch; never
        // inside the test host (see `init`).
        if choice == .google, !family.isEmpty, !AppRuntime.isUnitTesting {
            Task { await loadGoogleFont(family: family) }
        }
    }

    private func store(_ value: Any?, _ key: String) {
        if let value { defaults.set(value, forKey: key) } else { defaults.removeObject(forKey: key) }
    }

    // MARK: - Resolution

    /// The face to set lyrics in, honouring an override or deferring to the
    /// track's own character.
    func face(for mood: TrackMood) -> LyricFace {
        switch fontChoice {
        case .manrope, .appleMusic, .auto, .google: return mood.face
        case .sans: return .sans
        case .serif: return .serif
        case .slab: return .slab
        case .soft: return .soft
        }
    }

    /// The voice of the face ordinary words are actually drawn in. Editorial
    /// sets its second typeface against this -- contrasting with the genre's
    /// face instead put Charter beside a chosen Charter, or a second sans
    /// beside Manrope.
    func renderedFace(for mood: TrackMood) -> LyricFace {
        Self.renderedFace(choice: fontChoice, moodFace: mood.face, manropeLoaded: AppFont.regular != nil,
                          customVoice: googleRegular == nil ? nil : googleVoice)
    }

    /// - Parameters:
    ///   - manropeLoaded: Manrope is drawn only once it has registered;
    ///     until then `lyricFont` falls back to the track's face.
    ///   - customVoice: the voice of the chosen Google or installed family,
    ///     or `nil` while it is unavailable (the track's face stands in).
    static func renderedFace(choice: FontChoice, moodFace: LyricFace, manropeLoaded: Bool,
                             customVoice: LyricFace?) -> LyricFace {
        switch choice {
        // Manrope and SF Pro are both sans.
        case .manrope: return manropeLoaded ? .sans : moodFace
        case .appleMusic: return .sans
        case .google: return customVoice ?? moodFace
        case .auto: return moodFace
        case .sans: return .sans
        case .serif: return .serif
        case .slab: return .slab
        case .soft: return .soft
        }
    }

    /// Lyric type, preferring a downloaded Google font when one is active.
    func lyricFont(size: CGFloat, hero: Bool, mood: TrackMood, seed: UInt64? = nil,
                   alternate: Bool = false) -> Font {
        if randomizeLyricFonts, let seed {
            let faces: [LyricFace] = [.sans, .serif, .slab, .soft]
            return faces[Int(seed % UInt64(faces.count))].font(size: size, hero: hero)
        }
        if alternate { return renderedFace(for: mood).contrasting.font(size: size, hero: hero) }
        // Manrope is the shipped default, fetched and registered like any
        // other Google family.
        if fontChoice == .manrope, let regular = AppFont.regular {
            return .custom(hero ? (AppFont.bold ?? regular) : regular, fixedSize: size)
        }
        // Apple Music sets its lyrics in SF Pro, heavy and tightly tracked --
        // which is simply the macOS system face, so this needs no download and
        // no licence. Bold even when idle, heavy on the word being sung, is
        // what gives that view its weight.
        if fontChoice == .appleMusic {
            return .system(size: size, weight: hero ? .heavy : .bold, design: .default)
        }
        if fontChoice == .google, let regular = googleRegular {
            return .custom(hero ? (googleBold ?? regular) : regular, fixedSize: size)
        }
        return face(for: mood).font(size: size, hero: hero)
    }

    func uiFont(size: CGFloat, bold: Bool = false, mood: TrackMood) -> Font {
        if fontChoice == .manrope, let regular = AppFont.regular {
            return .custom(bold ? (AppFont.bold ?? regular) : regular, fixedSize: size)
        }
        // Apple Music sets its lyrics in SF Pro, heavy and tightly tracked --
        // which is simply the macOS system face, so this needs no download and
        // no licence. Bold even when idle, heavy on the word being sung, is
        // what gives that view its weight.
        if fontChoice == .appleMusic {
            return .system(size: size, weight: bold ? .bold : .medium, design: .default)
        }
        if fontChoice == .google, let regular = googleRegular {
            return .custom(bold ? (googleBold ?? regular) : regular, fixedSize: size)
        }
        return face(for: mood).uiFont(size: size, bold: bold)
    }

    /// Which pattern to draw, if the current mode uses one.
    func resolvedPattern(for mood: TrackMood) -> BackgroundStyle {
        patternStyle ?? mood.background
    }

    /// How to arrange a lyric line, honouring an override or letting the
    /// track choose.
    func resolvedComposition(for mood: TrackMood, seed: Int, track: String? = nil) -> LyricComposition {
        if let lyricComposition { return lyricComposition }
        if let track, LyricLayoutRotation.shared.isEnabled {
            return LyricLayoutRotation.shared.layout(for: track, mood: mood)
        }
        return LyricComposition.matching(mood, seed: seed)
    }

    /// Which GPU effect to run, if the current mode uses one. Calmer effects
    /// are chosen for low-energy tracks so type stays readable.
    func resolvedShader(for mood: TrackMood, seed: Int) -> ShaderStyle {
        let sparing = sparesGPU

        if let shaderStyle {
            // An explicit choice is honoured, except when the machine has
            // asked for restraint and the choice is one of the expensive
            // ones -- then it falls through to a cheap substitute.
            if !sparing || shaderStyle.cost != .heavy { return shaderStyle }
        }

        // Matched, not bucketed. The old version cut the library in half at
        // `energy > 0.6` and picked at random inside the half -- which meant
        // two tracks that genuinely sound alike had no reason to look alike,
        // and a track sitting near the boundary could swing between wildly
        // different looks on a rounding error. Now the track and every effect
        // are described on the same three axes and the nearest one wins.
        var pool = ShaderStyle.allCases
        if sparing {
            let affordable = pool.filter { $0.cost != .heavy }
            if !affordable.isEmpty { pool = affordable }
        }

        return ShaderStyle.matching(mood.visualCharacter, seed: seed, from: pool)
    }

    // MARK: - Power saving

    /// Why power conditions are holding the wallpaper back.
    enum PowerConstraint: Equatable {
        /// Lunalyr's own Low power visuals switch.
        case lowPowerVisuals
        /// macOS Low Power Mode, which Lunalyr is set to follow.
        case lowPowerMode
        /// Running on battery under an On battery choice other than full
        /// effects.
        case battery(PowerBehaviour)

        /// How far the constraint eases off.
        var behaviour: PowerBehaviour {
            switch self {
            case .lowPowerVisuals, .lowPowerMode: return .patterns
            case .battery(let behaviour): return behaviour
            }
        }

        /// Why, phrased to open a sentence.
        var reason: String {
            switch self {
            case .lowPowerVisuals: return "Low power visuals is on"
            case .lowPowerMode: return "Low Power Mode is on"
            case .battery: return "Your Mac is on battery power"
            }
        }

        /// The one-tap way out, named after the setting it changes.
        var overrideTitle: String {
            switch self {
            case .lowPowerVisuals: return "Turn off Low power visuals"
            case .lowPowerMode: return "Stop following Low Power Mode"
            case .battery: return "Keep full effects on battery"
            }
        }
    }

    /// What power saving is doing right now, if anything.
    var powerConstraint: PowerConstraint? {
        Self.powerConstraint(
            lowPowerVisuals: lowPowerVisuals, followsLowPowerMode: respectLowPower,
            lowPowerModeOn: ProcessInfo.processInfo.isLowPowerModeEnabled, onBattery: onBattery,
            // Only consulted when a battery rule exists, so a Mac set to keep
            // full effects never needs to watch its power source at all.
            isOnBattery: onBattery != .full && PowerSource.isOnBattery
        )
    }

    /// The constraint in force, if any. Low power visuals and Low Power Mode
    /// drop to line patterns; the battery rule does what was chosen for it.
    /// When several apply, the one that eases off most wins -- so "Colour
    /// only" on battery still holds while Low Power Mode is on -- and ties go
    /// to Lunalyr's own switch, then Low Power Mode, then the battery.
    static func powerConstraint(lowPowerVisuals: Bool, followsLowPowerMode: Bool, lowPowerModeOn: Bool,
                                onBattery: PowerBehaviour, isOnBattery: Bool) -> PowerConstraint? {
        var candidates: [PowerConstraint] = []
        if lowPowerVisuals { candidates.append(.lowPowerVisuals) }
        if followsLowPowerMode && lowPowerModeOn { candidates.append(.lowPowerMode) }
        if onBattery != .full && isOnBattery { candidates.append(.battery(onBattery)) }
        var strictest: PowerConstraint?
        for candidate in candidates {
            if let current = strictest, strictness(candidate.behaviour) <= strictness(current.behaviour) { continue }
            strictest = candidate
        }
        return strictest
    }

    private static func strictness(_ behaviour: PowerBehaviour) -> Int {
        switch behaviour {
        case .full: return 0
        case .lightEffects: return 1
        case .patterns: return 2
        case .colourOnly: return 3
        }
    }

    /// Whether power conditions call for keeping the GPU load down, even
    /// where effects are still permitted at all.
    var sparesGPU: Bool { powerConstraint != nil }

    /// The effective background mode once power conditions are taken into
    /// account -- GPU shaders are the expensive layer, so they are the first
    /// thing dropped on battery or in Low Power Mode.
    var effectiveBackgroundMode: BackgroundMode {
        Self.effectiveMode(backgroundMode, under: powerConstraint)
    }

    static func effectiveMode(_ chosen: BackgroundMode, under constraint: PowerConstraint?) -> BackgroundMode {
        // A transparent lyrics overlay needs no generated wallpaper, even
        // under power constraints. Never cover the user's desktop as a fallback.
        if chosen == .desktop { return .desktop }
        switch constraint?.behaviour ?? .full {
        case .full: return chosen
        // Effects stay on; `resolvedShader` narrows them to the cheap ones.
        case .lightEffects: return chosen
        case .patterns: return chosen == .colourOnly ? .colourOnly : .pattern
        case .colourOnly: return .colourOnly
        }
    }

    /// Power saving holds animation to 30 fps at most; a slower choice stays.
    static func effectiveRate(_ chosen: AnimationRate, sparingGPU: Bool) -> AnimationRate {
        sparingGPU && chosen.rawValue > AnimationRate.balanced.rawValue ? .balanced : chosen
    }

    /// What the Wallpaper page says while power saving changes what is drawn.
    struct PowerSavingNotice: Equatable {
        let constraint: PowerConstraint
        let message: String
        /// Set when the frame rate is held down as well.
        let frameRateNote: String?
    }

    var powerSavingNotice: PowerSavingNotice? {
        guard let constraint = powerConstraint else { return nil }
        return Self.powerSavingNotice(chosen: backgroundMode, pinnedShader: shaderStyle,
                                      rate: animationRate, constraint: constraint)
    }

    static func powerSavingNotice(chosen: BackgroundMode, pinnedShader: ShaderStyle?,
                                  rate: AnimationRate, constraint: PowerConstraint?) -> PowerSavingNotice? {
        guard let constraint else { return nil }
        let effective = effectiveMode(chosen, under: constraint)
        let held = effectiveRate(rate, sparingGPU: true)
        let rateNote = held != rate ? "Animation is held at \(held.title) as well." : nil
        if effective != chosen {
            return PowerSavingNotice(constraint: constraint, message:
                "\(constraint.reason), so Lunalyr is showing \(effective.title) instead of “\(chosen.title)”.",
                frameRateNote: rateNote)
        }
        // GPU effects and Match the music keep running, but `resolvedShader`
        // swaps a pinned heavy effect for a cheap one -- only a swap power
        // saving causes is reported, and Match the music draws an effect
        // only for some tracks.
        if chosen == .shader || chosen == .auto, let pinnedShader, pinnedShader.cost == .heavy {
            return PowerSavingNotice(constraint: constraint, message: chosen == .shader
                ? "\(constraint.reason), so \(pinnedShader.title) is swapped for a lighter GPU effect."
                : "\(constraint.reason), so when Match the music shows a GPU effect, \(pinnedShader.title) is swapped for a lighter one.",
                frameRateNote: rateNote)
        }
        if held != rate {
            return PowerSavingNotice(constraint: constraint, message:
                "\(constraint.reason), so animation is held at \(held.title) instead of \(rate.title).",
                frameRateNote: nil)
        }
        return nil
    }

    /// Lifts `constraint` by changing the setting behind it, exactly as the
    /// Performance page would.
    func overridePowerSaving(_ constraint: PowerConstraint) {
        switch constraint {
        case .lowPowerVisuals: lowPowerVisuals = false
        case .lowPowerMode: respectLowPower = false
        case .battery: onBattery = .full
        }
    }

    // MARK: - Appearance adjustments

    /// The least darkening drawn while Increase Contrast is on: half as much
    /// again as the shipped amount.
    static let increasedContrastScrimFloor = 1.5

    static func effectiveScrim(_ preferred: Double, increaseContrast: Bool) -> Double {
        increaseContrast ? max(preferred, increasedContrastScrimFloor) : preferred
    }

    /// What the Darkening slider offers. Under Increase Contrast it starts at
    /// the floor, so the slider always shows what is drawn and a drag can
    /// only store a strength that the user saw.
    static func scrimRange(increaseContrast: Bool) -> ClosedRange<Double> {
        (increaseContrast ? increasedContrastScrimFloor : 0)...2
    }

    var scrimRange: ClosedRange<Double> { Self.scrimRange(increaseContrast: increaseContrast) }

    /// The values Reset Adjustments changes, to put back with `restoreAdvanced(_:)` (Undo).
    struct AdvancedValues: Equatable {
        let effectSpeed, patternOpacity, scrimStrength, vividness, motionBlur: Double
    }

    var advancedValues: AdvancedValues {
        AdvancedValues(effectSpeed: effectSpeed, patternOpacity: patternOpacity, scrimStrength: preferredScrimStrength,
                       vividness: vividness, motionBlur: motionBlur)
    }

    func restoreAdvanced(_ values: AdvancedValues) {
        effectSpeed = values.effectSpeed
        patternOpacity = values.patternOpacity
        scrimStrength = values.scrimStrength
        vividness = values.vividness
        motionBlur = values.motionBlur
    }

    /// Restores every advanced control to its shipped value.
    func resetAdvanced() {
        effectSpeed = 1.0
        patternOpacity = 0.85
        scrimStrength = 1.0
        vividness = 1.0
        motionBlur = 5.0
    }

    var advancedIsDefault: Bool {
        effectSpeed == 1.0 && patternOpacity == 0.85
            && preferredScrimStrength == 1.0 && vividness == 1.0 && motionBlur == 5.0
    }

    /// Login items are managed by the system; if registration fails the
    /// toggle is put back so it never claims something untrue. A change that
    /// works clears the last failure's message, and one macOS holds for the
    /// user's approval (switched off in System Settings › Login Items) says
    /// where to allow it instead of looking done.
    private func applyLaunchAtLogin() {
        guard !followingLoginItem else { return }
        var failure: String?
        do {
            if launchAtLogin {
                if loginItem.status != .enabled { try loginItem.register() }
            } else if loginItem.status != .notRegistered && loginItem.status != .notFound {
                try loginItem.unregister()
            }
        } catch {
            failure = error.localizedDescription
        }
        followLoginItem(failure: failure)
    }

    /// Shows what macOS has now, without asking it to change: Settings
    /// calls this as its window comes forward, as Login Items can be
    /// changed in System Settings while Lunalyr runs.
    func refreshLaunchAtLogin() {
        followLoginItem(failure: nil)
    }

    private var followingLoginItem = false

    private func followLoginItem(failure: String?) {
        let status = loginItem.status
        let actual = Self.launchesAtLogin(status)
        followingLoginItem = true
        if actual != launchAtLogin { launchAtLogin = actual }
        followingLoginItem = false
        let message = actual && status == .requiresApproval ? Self.loginItemNeedsApproval : failure
        if launchAtLoginError != message { launchAtLoginError = message }
    }

    /// Registered counts as on, even while macOS waits for approval: the
    /// toggle then stays on beside the message saying where to allow it.
    static func launchesAtLogin(_ status: SMAppService.Status) -> Bool {
        status == .enabled || status == .requiresApproval
    }

    static let loginItemNeedsApproval = "Lunalyr is switched off in System Settings › General › Login Items. Switch it on there to open Lunalyr at login."

    /// Launch at login is waiting for the user's approval in System Settings.
    var launchAtLoginNeedsApproval: Bool { launchAtLoginError == Self.loginItemNeedsApproval }

    @Published var launchAtLoginError: String?

    /// Free for everyone. The paid tier covers our own work -- the advanced
    /// effects and controls -- not Apple's artwork.
    var motionArtworkEnabled: Bool { useMotionArtwork }

    /// Keyed on the album, because motion artwork belongs to a release rather
    /// than to a track -- excluding one song would leave the rest of the album
    /// still animating, which is not what anyone means by "not this one".
    static func exclusionKey(artist: String?, album: String?) -> String {
        "\((artist ?? "").lowercased())|\((album ?? "").lowercased())"
    }

    func excludesMotion(artist: String?, album: String?) -> Bool {
        motionExclusions.contains(Self.exclusionKey(artist: artist, album: album))
    }

    func setMotionExcluded(_ excluded: Bool, artist: String?, album: String?) {
        let key = Self.exclusionKey(artist: artist, album: album)
        guard key != "|" else { return }
        if excluded {
            if !motionExclusions.contains(key) { motionExclusions.append(key) }
        } else {
            motionExclusions.removeAll { $0 == key }
        }
    }

    // MARK: - Google and installed fonts

    private var googleFontLoadID = UUID()
    /// Set when the last attempt failed for want of a connection, so the
    /// family is fetched again once the Mac is back online.
    private var googleFontAwaitsNetwork = false

    func selectFontChoice(_ choice: FontChoice) {
        if choice == .google {
            applyGoogleFont(family: googleFontFamily.isEmpty ? "Inter" : googleFontFamily)
        } else {
            fontChoice = choice
            googleFontLoadID = UUID()
        }
    }

    func applyGoogleFont(family: String) {
        googleFontFamily = family
        fontChoice = .google
        Task { await loadGoogleFont(family: family) }
    }

    /// A typed family name, tidied: surrounding and repeated spaces go.
    /// `nil` when nothing usable is left -- empty, implausibly long, or
    /// holding characters that no family name uses and that would corrupt
    /// a font request.
    static func normalizedFontFamily(_ raw: String) -> String? {
        let name = raw.split(whereSeparator: \.isWhitespace).joined(separator: " ")
        let forbidden = CharacterSet.controlCharacters.union(CharacterSet(charactersIn: "/\\:;@&=?#%<>\"|*"))
        guard !name.isEmpty, name.count <= 64,
              !name.unicodeScalars.contains(where: forbidden.contains) else { return nil }
        return name
    }

    /// The voice a registered font declares in its own classification, when
    /// it declares one.
    static func voice(ofFontNamed name: String) -> LyricFace? {
        let font = CTFontCreateWithName(name as CFString, 12, nil)
        let style = CTFontGetSymbolicTraits(font).rawValue & CTFontSymbolicTraits.traitClassMask.rawValue
        switch CTFontStylisticClass(rawValue: style) {
        case .classSlabSerifs: return .slab
        // Georgia, for one, files itself as Clarendon -- a serif to readers.
        case .classOldStyleSerifs, .classTransitionalSerifs, .classModernSerifs,
             .classClarendonSerifs, .classFreeformSerifs: return .serif
        case .classSansSerif: return .sans
        default: return nil
        }
    }

    private func loadGoogleFont(family: String) async {
        guard fontChoice == .google, googleFontFamily == family else { return }
        let id = UUID()
        googleFontLoadID = id
        googleFontAwaitsNetwork = false
        guard let name = Self.normalizedFontFamily(family) else {
            applyCustomFont(nil, status: family.isEmpty ? nil : "“\(family)” isn’t a font family name.")
            return
        }
        func current() -> Bool { googleFontLoadID == id && fontChoice == .google && googleFontFamily == family }

        // A family fetched on an earlier run comes straight from the cache,
        // online or not; then fonts installed on this Mac; then a download.
        let provider = GoogleFontsProvider.shared
        if let cached = await provider.cachedFont(family: name) {
            guard current() else { return }
            applyCustomFont(cached, family: name, status: "Using \(name).")
            return
        }
        guard current() else { return }
        if let installed = InstalledFontFamily.resolve(name) {
            applyCustomFont(GoogleFontsProvider.LoadedFont(regular: installed.regular, bold: installed.bold),
                            family: installed.family, status: "Using \(installed.family), installed on this Mac.")
            return
        }
        googleStatus = "Downloading \(name) from Google Fonts…"
        googleFontState = .loading
        let result = await provider.load(family: name)
        guard current() else { return }
        switch result {
        case .success(let loaded):
            let spelling = loaded.family ?? name
            applyCustomFont(loaded, family: spelling, status: "Using \(spelling).")
            // Keep Google's spelling, so the next launch asks for it directly.
            if spelling != family { googleFontFamily = spelling }
        case .failure(.notFound):
            applyCustomFont(nil, status: "Couldn’t find “\(name)” on Google Fonts or on this Mac. Check the spelling and capitals at fonts.google.com.")
        case .failure(.offline):
            googleFontAwaitsNetwork = true
            applyCustomFont(nil, status: "Can’t reach Google Fonts to download \(name). Lunalyr will try again when you’re back online.")
        case .failure(.unusable):
            applyCustomFont(nil, status: "\(name) downloaded, but macOS couldn’t use it. Try another family.")
        case .failure(.unavailable):
            applyCustomFont(nil, status: "Google Fonts didn’t send \(name). Try again later.")
        }
    }

    private func applyCustomFont(_ font: GoogleFontsProvider.LoadedFont?, family: String = "", status: String?) {
        googleRegular = font?.regular
        googleBold = font?.bold
        if let font {
            googleVoice = GoogleFontsCatalog.voice(of: family) ?? Self.voice(ofFontNamed: font.regular) ?? .sans
        }
        googleStatus = status
        googleFontState = font != nil ? .ready : status == nil ? .idle : .failed
    }

    private func retryGoogleFontAfterReconnect() {
        guard googleFontAwaitsNetwork, fontChoice == .google, googleRegular == nil, !googleFontFamily.isEmpty else { return }
        googleFontAwaitsNetwork = false
        let family = googleFontFamily
        Task { await loadGoogleFont(family: family) }
    }
}

/// A Google or installed family's load: nothing asked for, downloading,
/// in use, or refused (the status says why).
enum GoogleFontState: Equatable {
    case idle, loading, ready, failed
}

/// Launch at login, as macOS keeps it. The app uses `SMAppService.mainApp`;
/// tests pass a stand-in, so they never add a login item.
protocol LoginItemService: AnyObject {
    var status: SMAppService.Status { get }
    func register() throws
    func unregister() throws
}

extension SMAppService: LoginItemService {}
