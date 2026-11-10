import AppKit
import SwiftUI
import UniformTypeIdentifiers

/// Choosing a picture or video for "Imported image or video": the open
/// panel, the copy Lunalyr keeps, and switching the wallpaper to it. One
/// flow for the Settings Wallpaper page and onboarding's "pick a look":
///
///     @StateObject private var importer = WallpaperImporter()
///     Button(importer.importing ? "Importing…" : "Choose Image or Video…") {
///         importer.choose(into: settings)
///     }
///     .disabled(importer.importing)
///     if let message = importer.message { StatusBanner(.warning, title: "Couldn’t import", message: message) }
///
/// The menu-bar panel doesn't open panels of its own: it sends people to
/// Settings (`navigation.openSettings(.wallpaper)`).
@MainActor
final class WallpaperImporter: ObservableObject {
    @Published private(set) var importing = false
    /// Why the last import failed, if it did.
    @Published private(set) var message: String?

    /// Asks for a file, then imports it. Only ever in answer to a click.
    func choose(into settings: Settings) {
        let panel = NSOpenPanel()
        panel.title = "Choose your wallpaper"
        panel.allowedContentTypes = [.image, .movie]
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        importFile(url, into: settings)
    }

    /// Copies `url` into Lunalyr's wallpaper folder and shows it.
    func importFile(_ url: URL, into settings: Settings) {
        importing = true
        message = nil
        Task {
            defer { importing = false }
            do {
                let imported = try await CustomWallpaperStore.importFile(url)
                let replaced = settings.customWallpaperPath
                settings.customWallpaperPath = imported.url.path
                CustomWallpaperStore.discardReplaced(replaced, keeping: imported.url)
                settings.customWallpaperName = imported.originalName
                settings.backgroundMode = .custom
            } catch {
                message = error.localizedDescription
            }
        }
    }
}
