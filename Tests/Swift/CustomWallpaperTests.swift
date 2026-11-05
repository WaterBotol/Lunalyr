import AppKit
import ImageIO
import UniformTypeIdentifiers
import XCTest
@testable import VersoCustom

final class CustomWallpaperTests: XCTestCase {
    /// A genuine PNG encoded by ImageIO, so the "valid image" really is valid
    /// rather than passing only because the decoder tolerates a bad checksum.
    private func pngData() throws -> Data {
        let context = try XCTUnwrap(CGContext(data: nil, width: 2, height: 2, bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.setFillColor(CGColor(red: 0.2, green: 0.4, blue: 0.8, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: 2, height: 2))
        let image = try XCTUnwrap(context.makeImage())
        let data = NSMutableData()
        let destination = try XCTUnwrap(CGImageDestinationCreateWithData(data, UTType.png.identifier as CFString, 1, nil))
        CGImageDestinationAddImage(destination, image, nil)
        XCTAssertTrue(CGImageDestinationFinalize(destination))
        let decoded = try XCTUnwrap(CGImageSourceCreateWithData(data, nil))
        XCTAssertEqual(CGImageSourceGetStatus(decoded), .statusComplete)
        XCTAssertEqual(CGImageSourceCreateImageAtIndex(decoded, 0, nil)?.width, 2)
        return data as Data
    }

    private func temporaryFolder() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }
        return root
    }

    func testImportedImageHasIndependentDurableCopy() async throws {
        let root = try temporaryFolder()
        let source = root.appendingPathComponent("original.png")
        let pixel = try pngData()
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
        let root = try temporaryFolder()
        let source = root.appendingPathComponent("invalid.png"), library = root.appendingPathComponent("library")
        try Data("not an image".utf8).write(to: source)
        do {
            _ = try await CustomWallpaperStore.importFile(source, directory: library)
            XCTFail("Invalid image imported")
        } catch { XCTAssertFalse(FileManager.default.fileExists(atPath: library.path)) }
    }

    /// The default library is under Application Support, which the installed
    /// app shares; under test it must land in the throwaway home instead.
    func testDefaultLibraryStaysInsideTheTestHome() async throws {
        let support = try XCTUnwrap(FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first)
        guard LunalyrTestEnvironment.contains(support) else {
            return XCTFail("Application Support is not redirected; refusing to import into \(support.path)")
        }
        let source = try temporaryFolder().appendingPathComponent("default.png")
        let pixel = try pngData()
        try pixel.write(to: source)
        let imported = try await CustomWallpaperStore.importFile(source)
        XCTAssertTrue(LunalyrTestEnvironment.contains(imported.url), imported.url.path)
        XCTAssertEqual(LunalyrTestEnvironment.canonicalPath(imported.url.deletingLastPathComponent()),
                       LunalyrTestEnvironment.canonicalPath(support.appendingPathComponent("Lunalyr/Wallpapers")))
        XCTAssertEqual(try Data(contentsOf: imported.url), pixel)
    }
}
