import AppKit
import SwiftUI

// MARK: - The six steps
//
// Each reads its stores through `@Model`, so the snapshot harness can draw
// it against isolated ones; the choices made here are the real settings.

typealias OnboardingTitleFocus = AccessibilityFocusState<OnboardingFlow.Step?>.Binding

/// 1. What Lunalyr does, with a picture of it.
struct WelcomeStep: View {
    var focus: OnboardingTitleFocus

    var body: some View {
        OnboardingStepLayout {
            WelcomeHero()
                .padding(.bottom, LunalyrDesign.Spacing.xxLarge)
            OnboardingHeader(step: .welcome, title: OnboardingCopy.welcomeTitle, message: OnboardingCopy.welcome,
                             focus: focus)
            SettingsStylePicker()
                .padding(.top, LunalyrDesign.Spacing.large)
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
        OnboardingStepLayout {
            OnboardingHeader(step: .music, symbol: "music.note.list", title: OnboardingCopy.musicTitle,
                             message: OnboardingCopy.permissionNote(installed: flow.models.installedPlayers), focus: focus)
                .padding(.bottom, LunalyrDesign.Spacing.xLarge)
            OnboardingCard {
                PlayerAccessList()
                Divider()
                VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.xSmall) {
                    LabeledContent("Follow") {
                        LunalyrPlayerPicker(title: "Follow").labelsHidden().fixedSize()
                    }
                    InspectorCaption(OnboardingCopy.followCaption)
                }
            }
            let asking = flow.playersMacOSWillAsk
            if !asking.isEmpty {
                Label(OnboardingCopy.asksOnContinue(asking), systemImage: "info.circle")
                    .labelStyle(CaptionLabelStyle())
                    .padding(.top, LunalyrDesign.Spacing.small)
                    .padding(.horizontal, LunalyrDesign.Spacing.xSmall)
                    .accessibilityElement(children: .combine)
            }
        }
    }
}

/// 3. The wallpaper type, chosen while watching the live preview.
struct LookStep: View {
    @ObservedObject var previewDisplay: SettingsPreviewDisplay
    var focus: OnboardingTitleFocus
    @Model(\.settings) private var settings
    @StateObject private var importer = WallpaperImporter()
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        OnboardingStepLayout {
            OnboardingHeader(step: .look, title: OnboardingCopy.lookTitle, focus: focus)
                .padding(.bottom, LunalyrDesign.Spacing.medium + 2)
            VStack(spacing: LunalyrDesign.Spacing.medium + 2) {
                PreviewStage(desktop: previewDisplay.size) {
                    SettingsLivePreview(desktopSize: previewDisplay.size, desktopPicture: previewDisplay.desktopPicture)
                }
                WallpaperModeGrid(chooseImport: { importer.choose(into: settings) })
            }
            .frame(width: Self.columnWidth)
            // Room for two caption lines, the usual summary. (Three would
            // stop the jump for the longest states, but at the window's
            // 540 points the step would then scroll.) Changes cross-fade.
            status
                .frame(maxWidth: 480, minHeight: Self.statusHeight, alignment: .top)
                .padding(.top, LunalyrDesign.Spacing.small + 2)
                .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion),
                           value: settings.backgroundMode)
                .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion),
                           value: importer.showsProgress)
                .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion),
                           value: importer.message)
        }
    }

    /// The preview and the tiles beneath it: about 370 x 240 on a 13-inch Air.
    static let columnWidth: CGFloat = 368
    /// The status line's reserved height: two caption lines.
    static let statusHeight: CGFloat = 30

    /// What the chosen look draws, or the import going on, or why it failed.
    @ViewBuilder private var status: some View {
        if importer.showsProgress {
            HStack(spacing: LunalyrDesign.Spacing.small) {
                ProgressView().controlSize(.small)
                Text("Importing…").lunalyrText(.caption)
            }
            .transition(.opacity)
        } else if let message = importer.message {
            StatusBanner(.warning, title: "Couldn’t import that file", message: message, density: .compact)
                .transition(.opacity)
        } else {
            VStack(spacing: LunalyrDesign.Spacing.xSmall) {
                if let notice = powerSaving {
                    // The leaf the tile power saving draws instead carries.
                    (Text(Image(systemName: "leaf.fill")).foregroundStyle(LunalyrStatus.success.text) + Text(" ") + Text(notice))
                        .lunalyrText(.caption)
                        .multilineTextAlignment(.center)
                        .accessibilityLabel(notice)
                } else {
                    Text(summary)
                        .lunalyrText(.caption)
                        .multilineTextAlignment(.center)
                }
                if settings.backgroundMode == .custom {
                    Button("Choose Another Image or Video…") { importer.choose(into: settings) }
                        .buttonStyle(.link)
                        .controlSize(.small)
                        .transition(.opacity)
                }
            }
            .frame(maxWidth: .infinity)
            // A new summary cross-fades in place of the last.
            .id(settings.backgroundMode)
            .transition(.opacity)
        }
    }

    /// What the chosen type draws.
    private var summary: String {
        let mode = settings.backgroundMode
        if mode == .custom, !settings.customWallpaperName.isEmpty {
            return "\(settings.customWallpaperName): \(mode.summary)"
        }
        return mode.summary
    }

    /// Why something else is drawn for now, while power saving replaces the
    /// chosen type (Settings › Performance can change that).
    private var powerSaving: String? {
        guard let notice = settings.powerSavingNotice, settings.effectiveBackgroundMode != settings.backgroundMode
        else { return nil }
        return notice.message
    }
}

/// 4. Where lyrics come from, putting their timing right, and readings.
struct LyricsStep: View {
    var focus: OnboardingTitleFocus
    @Model(\.hotkeys) private var hotkeys
    @Model(\.readings) private var readings
    @Model(\.extras) private var extras

    var body: some View {
        OnboardingStepLayout {
            OnboardingHeader(step: .lyrics, symbol: "quote.bubble", title: OnboardingCopy.lyricsTitle, focus: focus)
                .padding(.bottom, LunalyrDesign.Spacing.xLarge)
            OnboardingCard {
                OnboardingFeatureRow(symbol: "text.quote", title: "Synced lyrics for each song",
                                     text: OnboardingCopy.sources)
                Divider()
                OnboardingFeatureRow(symbol: "clock", title: "Lyrics early or late?",
                                     text: OnboardingCopy.timing(earlier: hotkeys.label(for: .syncEarlier) ?? "⌃⌥⌘[",
                                                                 later: hotkeys.label(for: .syncLater) ?? "⌃⌥⌘]"))
                Divider()
                OnboardingFeatureRow(symbol: "character.book.closed", title: "Readings for Japanese, Korean and Chinese",
                                     text: OnboardingCopy.readings) {
                    VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.small) {
                        Grid(alignment: .leading, horizontalSpacing: LunalyrDesign.Spacing.small,
                             verticalSpacing: LunalyrDesign.Spacing.xSmall + 2) {
                            GridRow {
                                Text("Display").gridColumnAlignment(.trailing).accessibilityHidden(true)
                                Picker("Display", selection: $readings.display) {
                                    ForEach(RomanizationOptions.Display.allCases) { Text($0.title).tag($0) }
                                }
                                .labelsHidden()
                                .fixedSize()
                                .gridColumnAlignment(.leading)
                            }
                            GridRow {
                                Text("Language").accessibilityHidden(true)
                                Picker("Language", selection: $readings.language) {
                                    ForEach(RomanizationOptions.Language.allCases) { Text($0.title).tag($0) }
                                }
                                .labelsHidden()
                                .fixedSize()
                            }
                        }
                        Toggle(isOn: $extras.showsTranslations) {
                            VStack(alignment: .leading, spacing: 2) {
                                Text("Show translations under lyrics")
                                Text(OnboardingCopy.translations).lunalyrText(.caption)
                            }
                        }
                        .toggleStyle(.checkbox)
                    }
                    .padding(.top, LunalyrDesign.Spacing.xSmall)
                }
            }
        }
    }
}

/// 5. Where Lunalyr lives, and two switches for living there.
struct MenuBarStep: View {
    var focus: OnboardingTitleFocus
    @Model(\.extras) private var extras
    @Model(\.settings) private var settings

    var body: some View {
        OnboardingStepLayout {
            MenuBarIllustration(showsLine: extras.showsMenuBarLine)
                .padding(.bottom, LunalyrDesign.Spacing.xLarge)
            OnboardingHeader(step: .menuBar, title: OnboardingCopy.menuBarTitle, message: OnboardingCopy.menuBarNote,
                             focus: focus)
                .padding(.bottom, LunalyrDesign.Spacing.xLarge)
            OnboardingCard {
                switchRow("Show the current line in the menu bar", caption: OnboardingCopy.menuBarLineCaption,
                          isOn: $extras.showsMenuBarLine)
                Divider()
                switchRow("Launch at login", caption: OnboardingCopy.launchAtLoginCaption, isOn: $settings.launchAtLogin)
                if let error = settings.launchAtLoginError {
                    Label(error, systemImage: LunalyrStatus.warning.symbol)
                        .font(LunalyrTextStyle.caption.font)
                        .foregroundStyle(LunalyrStatus.warning.text)
                        .fixedSize(horizontal: false, vertical: true)
                        .accessibilityLabel("Warning: \(error)")
                }
            }
        }
    }

    /// A switch at the trailing edge, as a grouped form lays one out; to
    /// VoiceOver, the switch named by its title, with the caption as a hint.
    private func switchRow(_ title: String, caption: String, isOn: Binding<Bool>) -> some View {
        HStack(spacing: LunalyrDesign.Spacing.medium) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                Text(caption).lunalyrText(.caption)
            }
            .accessibilityHidden(true)
            Spacer(minLength: 0)
            Toggle(title, isOn: isOn)
                .labelsHidden()
                .toggleStyle(.switch)
                .controlSize(.small)
                .accessibilityHint(caption)
        }
    }
}

/// 6. The end: what is set up, with any fix still waiting, then Settings.
struct DoneStep: View {
    @ObservedObject var flow: OnboardingFlow
    var focus: OnboardingTitleFocus
    @Model(\.nowPlaying) private var playing
    @Model(\.settings) private var settings

    var body: some View {
        OnboardingStepLayout {
            OnboardingHeader(step: .done, symbol: "checkmark.circle.fill", symbolColour: LunalyrStatus.success.text,
                             title: OnboardingCopy.doneTitle,
                             message: OnboardingCopy.done(playing: playing.current?.title, idlePrompt: playing.idlePrompt),
                             focus: focus)
                .padding(.bottom, LunalyrDesign.Spacing.xLarge)
            OnboardingCard {
                PlayerAccessList()
                Divider()
                lookRow
            }
            Text(flow.firstRun ? OnboardingCopy.doneFirstRun : OnboardingCopy.doneAgain)
                .lunalyrText(.caption)
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
                .foregroundStyle(Color.lunalyrAccentText)
                .frame(width: 13)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                Text("Wallpaper: \(mode.title)").lunalyrText(.emphasis)
                Text(detail).lunalyrText(.caption)
            }
            .accessibilityElement(children: .combine)
        }
    }
}
