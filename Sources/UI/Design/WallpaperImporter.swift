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
    /// True once an import has run past `Motion.Hold.spinnerDelay`: show a
    /// spinner then, and nothing for a copy that finishes sooner.
    @Published private(set) var showsProgress = false
    /// Why the last import failed, if it did.
    @Published private(set) var message: String?
    private var progressDelay: Task<Void, Never>?

    /// Asks for a file, then imports it. Only ever in answer to a click. The
    /// panel is a sheet on the window that asked, so it stays with it.
    func choose(into settings: Settings) {
        let panel = NSOpenPanel()
        panel.title = "Choose your wallpaper"
        panel.allowedContentTypes = [.image, .movie]
        panel.allowsMultipleSelection = false
        let chosen: (NSApplication.ModalResponse) -> Void = { [weak self, weak panel] response in
            guard response == .OK, let url = panel?.url else { return }
            self?.importFile(url, into: settings)
        }
        if let window = NSApp.keyWindow, window.isVisible, window.attachedSheet == nil {
            panel.beginSheetModal(for: window, completionHandler: chosen)
        } else {
            chosen(panel.runModal())
        }
    }

    /// Copies `url` into Lunalyr's wallpaper folder and shows it.
    func importFile(_ url: URL, into settings: Settings) {
        importing = true
        message = nil
        progressDelay?.cancel()
        progressDelay = Task { [weak self] in
            try? await Task.sleep(for: .seconds(LunalyrDesign.Motion.Hold.spinnerDelay))
            guard !Task.isCancelled, let self, self.importing else { return }
            self.showsProgress = true
        }
        Task {
            defer {
                importing = false
                showsProgress = false
                progressDelay?.cancel()
            }
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
