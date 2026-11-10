import AVFoundation
import Foundation
import ImageIO
import UniformTypeIdentifiers

enum CustomWallpaperStore {
    struct Imported: Sendable {
        let url: URL
        let originalName: String
    }
    static func isVideo(_ url: URL) -> Bool {
        UTType(filenameExtension: url.pathExtension)?.conforms(to: .movie) == true
    }
    static func importFile(_ source: URL, directory: URL? = nil) async throws -> Imported {
        guard source.isFileURL, let type = UTType(filenameExtension: source.pathExtension),
              type.conforms(to: .image) || type.conforms(to: .movie) else {
            throw LyricHTTP.Failure(message: "Choose an image or a video file.")
        }
        if type.conforms(to: .image) {
            guard let image = CGImageSourceCreateWithURL(source as CFURL, nil), CGImageSourceGetCount(image) > 0,
                  CGImageSourceCreateThumbnailAtIndex(image, 0, [kCGImageSourceCreateThumbnailFromImageAlways: true, kCGImageSourceThumbnailMaxPixelSize: 64] as CFDictionary) != nil else {
                throw LyricHTTP.Failure(message: "This image could not be opened.")
            }
        } else {
            guard !(try await AVURLAsset(url: source).loadTracks(withMediaType: .video)).isEmpty else {
                throw LyricHTTP.Failure(message: "This file has no playable video track.")
            }
        }
        let folder = directory ?? defaultFolder
        let destination = folder.appendingPathComponent(UUID().uuidString).appendingPathExtension(source.pathExtension)
        // Keep the original untouched and retain a private app-managed copy.
        try await Task.detached(priority: .utility) {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            try FileManager.default.copyItem(at: source, to: destination)
        }.value
        return Imported(url: destination, originalName: source.lastPathComponent)
    }

    /// Where imported copies are kept.
    static var defaultFolder: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Lunalyr/Wallpapers", isDirectory: true)
    }

    /// Whether `path` names an imported copy that is still there. Someone
    /// tidying Application Support can delete it; the wallpaper then falls
    /// back instead of drawing black, and Settings asks for the file again.
    static func isAvailable(_ path: String) -> Bool {
        var isFolder: ObjCBool = false
        return !path.isEmpty && FileManager.default.fileExists(atPath: path, isDirectory: &isFolder) && !isFolder.boolValue
    }

    /// Deletes the copy a new import replaced, so each import doesn't leave
    /// another image or video behind for good. Only a file Lunalyr made in
    /// its own folder is ever removed, never the one now in use.
    @discardableResult
    static func discardReplaced(_ path: String, keeping current: URL, directory: URL? = nil) -> Bool {
        guard !path.isEmpty else { return false }
        let old = URL(fileURLWithPath: path).standardizedFileURL.resolvingSymlinksInPath()
        let folder = (directory ?? defaultFolder).standardizedFileURL.resolvingSymlinksInPath()
        guard old != current.standardizedFileURL.resolvingSymlinksInPath(),
              old.deletingLastPathComponent().path == folder.path,
              (try? FileManager.default.removeItem(at: old)) != nil else { return false }
        return true
    }
}
