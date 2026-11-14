import AppKit
import SwiftUI

// MARK: - The eight steps
//
// Each reads its stores through `@Model`, so the snapshot harness can draw
// it against isolated ones; the choices made here are the real settings.

typealias OnboardingTitleFocus = AccessibilityFocusState<OnboardingFlow.Step?>.Binding

/// 1. What Lunalyr does: the liquid-glass logo, then the desktop it draws.
struct WelcomeStep: View {
    var focus: OnboardingTitleFocus

    var body: some View {
        OnboardingStepLayout {
            WelcomeHero()
                .padding(.bottom, LunalyrDesign.Spacing.xxLarge)
            OnboardingHeader(step: .welcome, title: OnboardingCopy.welcomeTitle, message: OnboardingCopy.welcome,
                             focus: focus)
        }
    }
}

/// 2. Reading Music and Spotify: what macOS will ask, each player's access
/// with its fix, and which player to follow.
struct MusicStep: View {
    @ObservedObject var flow: OnboardingFlow
    var focus: OnboardingTitleFocus
    // Observed so the note under the list follows the monitor and the choice.
    @Model(\.nowPlaying) private var playing
    @Model(\.players) private var players

    var body: some View {
        OnboardingPage(step: .music, title: OnboardingCopy.musicTitle,
                       message: OnboardingCopy.permissionNote(installed: flow.models.installedPlayers), focus: focus) {
            OnboardingCard {
                PlayerAccessList()
                VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.xSmall) {
                    LabeledContent("Follow") {
                        LunalyrPlayerPicker(title: "Follow").labelsHidden().fixedSize()
                    }
                    Text(OnboardingCopy.followCaption).onboardingCaption()
                }
            }
            let asking = flow.playersMacOSWillAsk
            if !asking.isEmpty {
                Label(OnboardingCopy.asksOnContinue(asking), systemImage: "info.circle")
                    .labelStyle(CaptionLabelStyle())
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.top, LunalyrDesign.Spacing.small)
                    .accessibilityElement(children: .combine)
            }
            Text(OnboardingCopy.permissionDetail(installed: flow.models.installedPlayers))
                .onboardingCaption()
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.top, LunalyrDesign.Spacing.medium)
        }
    }
}

/// 3. The look, chosen while watching the live preview: the wallpaper type,
/// or the lyrics' layout.
struct LookStep: View {
    @ObservedObject var flow: OnboardingFlow
    @ObservedObject var previewDisplay: SettingsPreviewDisplay
    var focus: OnboardingTitleFocus
    @Model(\.settings) private var settings
    @StateObject private var importer = WallpaperImporter()
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        OnboardingPage(step: .look, title: OnboardingCopy.lookTitle, focus: focus) {
            PreviewStage(desktop: previewDisplay.size) {
                SettingsLivePreview(desktopSize: previewDisplay.size, desktopPicture: previewDisplay.desktopPicture)
            }
            .frame(width: LunalyrDesign.Onboarding.previewWidth)
            Picker("Show", selection: $flow.lookPart) {
                ForEach(OnboardingFlow.LookPart.allCases) { Text($0.title).tag($0) }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .fixedSize()
            .padding(.vertical, LunalyrDesign.Spacing.small + LunalyrDesign.Spacing.hairline)
            ZStack(alignment: .top) {
                switch flow.lookPart {
                case .wallpaper:
                    VStack(spacing: 6) {
                        OnboardingWallpaperTiles(chooseImport: { importer.choose(into: settings) },
                                                 desktopPicture: previewDisplay.desktopPicture)
                            .frame(width: OnboardingWallpaperTiles.width)
                        status
                    }
                    .transition(.opacity)
                case .lyrics:
                    LayoutChoices()
                        .transition(.opacity)
                }
            }
            .frame(width: LunalyrDesign.Onboarding.choicesWidth, height: LunalyrDesign.Onboarding.choicesHeight,
                   alignment: .top)
            .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion),
                       value: flow.lookPart)
        }
    }

    /// What the chosen look draws, or the import going on, or why it failed.
    @ViewBuilder private var status: some View {
        Group {
            if importer.showsProgress {
                HStack(spacing: LunalyrDesign.Spacing.small) {
                    ProgressView().controlSize(.small)
                    Text("Importing…").onboardingCaption()
                }
            } else if let message = importer.message {
                Label {
                    Text("Couldn’t import that file. \(message)")
                } icon: {
                    Image(systemName: LunalyrStatus.warning.symbol).foregroundStyle(Color.studio(.warning))
                }
                .labelStyle(CaptionLabelStyle())
                .accessibilityLabel("Warning: Couldn’t import that file. \(message)")
            } else if let notice = powerSaving {
                // The leaf the tile power saving draws instead carries.
                Label { Text(notice) } icon: {
                    Image(systemName: "leaf.fill").foregroundStyle(Color.studio(.success))
                }
                .labelStyle(CaptionLabelStyle())
            } else if settings.backgroundMode == .custom {
                Button("Choose Another Image or Video…") { importer.choose(into: settings) }
                    .buttonStyle(.link)
                    .controlSize(.small)
            } else {
                Text(settings.backgroundMode.summary)
                    .onboardingCaption()
                    .multilineTextAlignment(.center)
                    .lineLimit(2)
            }
        }
        .frame(maxWidth: .infinity)
        // A new summary cross-fades in place of the last.
        .id(statusID)
        .transition(.opacity)
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion), value: statusID)
    }

    private var statusID: String {
        "\(settings.backgroundMode.rawValue)-\(importer.showsProgress)-\(importer.message ?? "")-\(powerSaving ?? "")"
    }

    /// Why something else is drawn for now, while power saving replaces the
    /// chosen type (Settings › Performance can change that).
    private var powerSaving: String? {
        guard let notice = settings.powerSavingNotice, settings.effectiveBackgroundMode != settings.backgroundMode
        else { return nil }
        return notice.message
    }
}

/// 4. Music videos as the wallpaper: on or off, how they fit the screen, and
/// whether YouTube may be searched (off by default, with what it sends).
/// Nothing here loads a video or a player: the framings are drawn.
struct VideosStep: View {
    var focus: OnboardingTitleFocus
    @Model(\.settings) private var settings

    var body: some View {
        OnboardingPage(step: .videos, title: OnboardingCopy.videosTitle, message: OnboardingCopy.videos,
                       focus: focus) {
            HStack(spacing: LunalyrDesign.Spacing.xLarge) {
                ForEach(MusicVideoFraming.allCases) { framing in
                    VStack(spacing: LunalyrDesign.Spacing.small) {
                        VideoFramingPicture(framing: framing)
                        Text(framing.title).onboardingCaption()
                    }
                }
            }
            .accessibilityHidden(true)
            .padding(.bottom, LunalyrDesign.Spacing.medium)
            OnboardingCard {
                OnboardingSwitchRow(title: OnboardingCopy.videoWallpaper, caption: OnboardingCopy.videoWallpaperCaption,
                                    isOn: videoWallpaper)
                Grid(alignment: .leading, horizontalSpacing: LunalyrDesign.Spacing.medium,
                     verticalSpacing: LunalyrDesign.Spacing.small) {
                    framingRow("YouTube videos", selection: $settings.musicVideoFraming)
                    framingRow("Library videos", selection: $settings.libraryVideoFraming)
                }
                OnboardingSwitchRow(title: OnboardingCopy.searchYouTube, caption: OnboardingCopy.youTubePrivacy,
                                    isOn: $settings.musicVideoOnline)
            }
        }
    }

    /// On: Music video. Off: back to what it falls back to, which is the
    /// type chosen before it (`Settings.musicVideoFallback`).
    private var videoWallpaper: Binding<Bool> { Self.videoWallpaper(settings) }

    static func videoWallpaper(_ settings: Settings) -> Binding<Bool> {
        Binding(get: { settings.backgroundMode == .musicVideo },
                set: { on in settings.backgroundMode = on ? .musicVideo : settings.musicVideoFallback })
    }

    private func framingRow(_ title: String, selection: Binding<MusicVideoFraming>) -> some View {
        GridRow {
            Text(title).accessibilityHidden(true)
            Picker(title, selection: selection) {
                ForEach(MusicVideoFraming.allCases) { Text($0.title).tag($0) }
            }
            .labelsHidden()
            .fixedSize()
        }
    }
}

/// 5. Where lyrics come from, putting their timing right, and readings.
struct LyricsStep: View {
    var focus: OnboardingTitleFocus
    @Model(\.hotkeys) private var hotkeys
    @Model(\.readings) private var readings
    @Model(\.extras) private var extras

    var body: some View {
        OnboardingPage(step: .lyrics, title: OnboardingCopy.lyricsTitle, focus: focus) {
            OnboardingCard {
                OnboardingFeatureRow(title: "Synced lyrics for each song", text: OnboardingCopy.sources)
                OnboardingFeatureRow(title: "Lyrics early or late?",
                                     text: OnboardingCopy.timing(earlier: hotkeys.label(for: .syncEarlier) ?? "⌃⌥⌘[",
                                                                 later: hotkeys.label(for: .syncLater) ?? "⌃⌥⌘]"))
                HStack(alignment: .top, spacing: LunalyrDesign.Spacing.large) {
                    OnboardingFeatureRow(title: "Readings for Japanese, Korean and Chinese", text: OnboardingCopy.readings) {
                        Grid(alignment: .leading, horizontalSpacing: LunalyrDesign.Spacing.small,
                             verticalSpacing: LunalyrDesign.Spacing.small) {
                            GridRow {
                                Text("Display").fixedSize().gridColumnAlignment(.trailing).accessibilityHidden(true)
                                Picker("Display", selection: $readings.display) {
                                    ForEach(RomanizationOptions.Display.allCases) { Text($0.title).tag($0) }
                                }
                                .labelsHidden()
                                .fixedSize()
                            }
                            GridRow {
                                Text("Language").fixedSize().accessibilityHidden(true)
                                Picker("Language", selection: $readings.language) {
                                    ForEach(RomanizationOptions.Language.allCases) { Text($0.title).tag($0) }
                                }
                                .labelsHidden()
                                .fixedSize()
                            }
                        }
                    }
                    OnboardingClipView(clip: .readings, cornerRadius: LunalyrDesign.Radius.control)
                        .frame(width: LunalyrDesign.Onboarding.readingsClip.width,
                               height: LunalyrDesign.Onboarding.readingsClip.height)
                }
                OnboardingSwitchRow(title: "Show translations under lyrics", caption: OnboardingCopy.translations,
                                    isOn: $extras.showsTranslations)
            }
        }
    }
}

/// 6. The extras: the desktop player, floating lyrics and the Discord status,
/// each with a picture of itself.
struct ExtrasStep: View {
    var focus: OnboardingTitleFocus
    @Model(\.extras) private var extras
    @Model(\.desktop) private var desktop

    var body: some View {
        OnboardingPage(step: .extras, title: OnboardingCopy.extrasTitle, message: OnboardingCopy.extras, focus: focus) {
            OnboardingCard {
                row(picture: MiniDesktopPlayer(), title: OnboardingCopy.playerTitle, text: OnboardingCopy.player) {
                    Toggle("Show player on hover", isOn: $desktop.showsControlsOnHover)
                }
                row(picture: OnboardingClipView(clip: .floating, cornerRadius: LunalyrDesign.Radius.control), title: OnboardingCopy.floatingTitle, text: OnboardingCopy.floating) {
                    EmptyView()
                }
                row(picture: DiscordStatusPicture(), title: OnboardingCopy.discordTitle, text: OnboardingCopy.discord) {
                    Toggle("Show what I’m listening to on Discord", isOn: $extras.showsDiscordPresence)
                }
                Text(OnboardingCopy.discordPrivacy)
                    .onboardingCaption()
                    .padding(.leading, LunalyrDesign.Onboarding.extraPicture.width + LunalyrDesign.Spacing.large)
                    .padding(.top, -LunalyrDesign.Spacing.xSmall)
            }
        }
    }

    /// A picture, the extra's name and line, and its switch at the trailing
    /// edge; to VoiceOver, the switch named for what it does, with the line
    /// as a hint.
    private func row<Picture: View, Switch: View>(picture: Picture, title: String, text: String,
                                                  @ViewBuilder toggle: () -> Switch) -> some View {
        HStack(spacing: LunalyrDesign.Spacing.large) {
            picture
                .frame(width: LunalyrDesign.Onboarding.extraPicture.width,
                       height: LunalyrDesign.Onboarding.extraPicture.height)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.hairline) {
                Text(title).onboardingEmphasis()
                Text(text).onboardingCaption()
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .accessibilityElement(children: .combine)
            toggle()
                .labelsHidden()
                .toggleStyle(.switch)
                .controlSize(.small)
                .accessibilityHint(text)
        }
    }
}

/// 7. Where Lunalyr lives, and two switches for living there.
struct MenuBarStep: View {
    var focus: OnboardingTitleFocus
    @Model(\.extras) private var extras
    @Model(\.settings) private var settings

    var body: some View {
        OnboardingPage(step: .menuBar, title: OnboardingCopy.menuBarTitle, message: OnboardingCopy.menuBarNote,
                       focus: focus) {
            MenuBarIllustration(showsLine: extras.showsMenuBarLine)
                .padding(.bottom, LunalyrDesign.Spacing.xLarge)
            OnboardingCard {
                OnboardingSwitchRow(title: "Show the current line in the menu bar",
                                    caption: OnboardingCopy.menuBarLineCaption, isOn: $extras.showsMenuBarLine)
                OnboardingSwitchRow(title: "Launch at login", caption: OnboardingCopy.launchAtLoginCaption,
                                    isOn: $settings.launchAtLogin)
                if let error = settings.launchAtLoginError {
                    Label {
                        Text(error).foregroundStyle(Color.studio(.ink))
                    } icon: {
                        Image(systemName: LunalyrStatus.warning.symbol).foregroundStyle(Color.studio(.warning))
                    }
                        .font(LunalyrDesign.Studio.TypeScale.caption)
                        .fixedSize(horizontal: false, vertical: true)
                        .accessibilityLabel("Warning: \(error)")
                }
            }
        }
    }
}

/// 8. The end: what is set up, with any fix still waiting, then Settings.
struct DoneStep: View {
    @ObservedObject var flow: OnboardingFlow
    var focus: OnboardingTitleFocus
    @Model(\.nowPlaying) private var playing
    @Model(\.settings) private var settings

    var body: some View {
        OnboardingStepLayout {
            Image(systemName: "checkmark.circle.fill")
                .font(.largeTitle)
                .symbolRenderingMode(.hierarchical)
                .foregroundStyle(Color.studio(.success))
                .padding(.bottom, LunalyrDesign.Spacing.medium)
                .accessibilityHidden(true)
            OnboardingHeader(step: .done, title: OnboardingCopy.doneTitle,
                             message: OnboardingCopy.done(playing: playing.current?.title, idlePrompt: playing.idlePrompt),
                             focus: focus)
                .padding(.bottom, LunalyrDesign.Spacing.xLarge)
            OnboardingCard {
                PlayerAccessList()
                lookRow
            }
            Text(flow.firstRun ? OnboardingCopy.doneFirstRun : OnboardingCopy.doneAgain)
                .onboardingCaption()
                .multilineTextAlignment(.center)
                .frame(maxWidth: .infinity)
                .padding(.top, LunalyrDesign.Spacing.medium)
        }
    }

    private var lookRow: some View {
        let mode = settings.backgroundMode
        let drawn = settings.effectiveBackgroundMode
        let detail: String
        if drawn != mode {
            detail = "Power saving shows \(drawn.title) for now."
        } else if mode == .custom, !settings.customWallpaperName.isEmpty {
            detail = settings.customWallpaperName
        } else {
            detail = mode.summary
        }
        return HStack(alignment: .firstTextBaseline, spacing: LunalyrDesign.Spacing.small) {
            // As wide as the access rows' status circles, so the names line up.
            Image(systemName: mode.symbol)
                .foregroundStyle(Color.studio(.ink2))
                .frame(width: 13)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.hairline) {
                Text("Wallpaper: \(mode.title)").onboardingEmphasis()
                Text(detail).onboardingCaption()
            }
            .accessibilityElement(children: .combine)
        }
    }
}
