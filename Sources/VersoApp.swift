import AppKit
import SwiftUI

@main
struct VersoApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @StateObject private var nowPlaying = NowPlayingMonitor.shared
    /// For the icon, which says when the wallpaper is paused.
    @StateObject private var extras = LunalyrExtras.shared

    var body: some Scene {
        // The XCTest host is this app: without this, every test run put a
        // second Lunalyr icon in the menu bar beside the real one.
        MenuBarExtra(isInserted: .constant(!AppRuntime.isUnitTesting)) {
            MenuBarView()
                .environmentObject(nowPlaying)
        } label: {
            Image(nsImage: extras.wallpaperPaused ? Self.pausedMenuBarIcon : Self.menuBarIcon)
                .accessibilityLabel(extras.wallpaperPaused ? "Lunalyr, wallpaper paused" : "Lunalyr")
        }
        .menuBarExtraStyle(.window)
        .commands {
            CommandGroup(replacing: .appSettings) {
                Button("Lunalyr Settings…") { LunalyrNavigation.shared.openSettings() }
                    .keyboardShortcut(",", modifiers: .command)
            }
        }
    }

    /// The mark from the wordmark's "o", carrying shape in alpha only.
    ///
    /// The asset is named with a `Template` suffix, which is what makes AppKit
    /// treat it as a template image: the system supplies the colour, so one
    /// file reads correctly in light mode, in dark mode, and inverted while
    /// the menu is open. A coloured icon would have to fight all three.
    private static let menuBarIcon: NSImage = {
        let image = NSImage(named: "MenuBarIconTemplate") ?? NSImage()
        image.size = NSSize(width: 18, height: 18)
        image.isTemplate = true
        // VoiceOver names the status item by this; the glyph alone says nothing.
        image.accessibilityDescription = "Lunalyr"
        return image
    }()

    private static let pausedMenuBarIcon = MenuBarView.pausedIcon(from: menuBarIcon)
}
