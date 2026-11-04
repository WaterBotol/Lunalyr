import AppKit
import XCTest
@testable import VersoCustom

final class CustomWallpaperTests: XCTestCase {
    func testImportedImageHasIndependentDurableCopy() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let source = root.appendingPathComponent("original.png")
        let pixel = Data(base64Encoded: "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a/v8AAAAASUVORK5CYII=")!
        try pixel.write(to: source)
        let imported = try await CustomWallpaperStore.importFile(source, directory: root.appendingPathComponent("library"))
        XCTAssertNotEqual(imported.url, source)
        XCTAssertEqual(imported.originalName, "original.png")
        XCTAssertEqual(try Data(contentsOf: source), pixel)
        try FileManager.default.removeItem(at: source)
        XCTAssertEqual(try Data(contentsOf: imported.url), pixel)
        XCTAssertFalse(CustomWallpaperStore.isVideo(imported.url))
    }
    func testInvalidImageIsRejectedWithoutSelectingOrCopyingIt() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let source = root.appendingPathComponent("invalid.png"), library = root.appendingPathComponent("library")
        try Data("not an image".utf8).write(to: source)
        do {
            _ = try await CustomWallpaperStore.importFile(source, directory: library)
            XCTFail("Invalid image imported")
        } catch { XCTAssertFalse(FileManager.default.fileExists(atPath: library.path)) }
    }
}
