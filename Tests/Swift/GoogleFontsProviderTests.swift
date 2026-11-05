import CoreText
import XCTest
@testable import VersoCustom

/// Every request fails as if the Mac were offline.
private final class OfflineFontStub: URLProtocol {
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() { client?.urlProtocol(self, didFailWithError: URLError(.notConnectedToInternet)) }
    override func stopLoading() {}
}

/// Google's answer for a family it does not have.
private final class UnknownFamilyStub: URLProtocol {
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        let response = HTTPURLResponse(url: request.url!, statusCode: 400, httpVersion: "HTTP/1.1", headerFields: nil)!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data("<!DOCTYPE html>".utf8))
        client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}

/// Serves a css2 sheet listing two cuts, and the cuts themselves from fonts
/// that ship with macOS.
private final class ServingFontStub: URLProtocol {
    static let regular = URL(fileURLWithPath: "/System/Library/Fonts/Supplemental/Arial.ttf")
    static let bold = URL(fileURLWithPath: "/System/Library/Fonts/Supplemental/Arial Bold.ttf")
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        let url = request.url!
        let body: Data?
        if url.host == "fonts.googleapis.com" {
            body = Data("""
            @font-face { font-family: 'Stub Family'; font-weight: 400; src: url(https://fonts.gstatic.com/s/stub/regular.ttf) format('truetype'); }
            @font-face { font-family: 'Stub Family'; font-weight: 700; src: url(https://fonts.gstatic.com/s/stub/bold.ttf) format('truetype'); }
            """.utf8)
        } else {
            body = try? Data(contentsOf: url.lastPathComponent == "bold.ttf" ? Self.bold : Self.regular)
        }
        let response = HTTPURLResponse(url: url, statusCode: body == nil ? 404 : 200, httpVersion: "HTTP/1.1", headerFields: nil)!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: body ?? Data())
        client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}

final class GoogleFontsProviderTests: XCTestCase {
    private func session(_ stub: URLProtocol.Type) -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [stub]
        let session = URLSession(configuration: configuration)
        addTeardownBlock { session.invalidateAndCancel() }
        return session
    }

    private func temporaryFolder() throws -> URL {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("LunalyrFonts-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        addTeardownBlock {
            for file in (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil)) ?? [] {
                CTFontManagerUnregisterFontsForURL(file as CFURL, .process, nil)
            }
            try? FileManager.default.removeItem(at: folder)
        }
        return folder
    }

    private func requireSystemFonts() throws {
        for url in [ServingFontStub.regular, ServingFontStub.bold] where !FileManager.default.fileExists(atPath: url.path) {
            throw XCTSkip("\(url.lastPathComponent) is not installed")
        }
    }

    /// The Manrope regression: a family fetched on an earlier run must load
    /// while the Mac is offline, without asking Google first.
    func testCachedFamilyLoadsWhileOffline() async throws {
        try requireSystemFonts()
        let folder = try temporaryFolder()
        try FileManager.default.copyItem(at: ServingFontStub.regular, to: folder.appendingPathComponent("Cached-Family-0.ttf"))
        try FileManager.default.copyItem(at: ServingFontStub.bold, to: folder.appendingPathComponent("Cached-Family-1.ttf"))
        let provider = GoogleFontsProvider(cacheDirectory: folder, session: session(OfflineFontStub.self))

        let result = await provider.load(family: "Cached Family")
        XCTAssertEqual(result, .success(GoogleFontsProvider.LoadedFont(regular: "ArialMT", bold: "Arial-BoldMT")))
        let cached = await provider.cachedFont(family: "Cached Family")
        XCTAssertEqual(cached?.regular, "ArialMT")
        // AppFont's entry point returns at once rather than waiting to go online.
        let font = await provider.font(family: "Cached Family")
        XCTAssertEqual(font?.bold, "Arial-BoldMT")
    }

    func testDownloadIsKeptForTheNextLaunch() async throws {
        try requireSystemFonts()
        let folder = try temporaryFolder()
        let online = GoogleFontsProvider(cacheDirectory: folder, session: session(ServingFontStub.self))
        let downloaded = await online.load(family: "Stub Family")
        XCTAssertEqual(downloaded, .success(GoogleFontsProvider.LoadedFont(regular: "ArialMT", bold: "Arial-BoldMT")))
        XCTAssertTrue(FileManager.default.fileExists(atPath: folder.appendingPathComponent("Stub-Family-0.ttf").path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: folder.appendingPathComponent("Stub-Family-1.ttf").path))

        let nextLaunch = GoogleFontsProvider(cacheDirectory: folder, session: session(OfflineFontStub.self))
        let reloaded = await nextLaunch.load(family: "Stub Family")
        XCTAssertEqual(reloaded, downloaded)
    }

    func testUnknownFamilyIsNotFoundAndNothingIsCached() async throws {
        let folder = try temporaryFolder()
        let provider = GoogleFontsProvider(cacheDirectory: folder, session: session(UnknownFamilyStub.self))
        let typed = await provider.load(family: "nonexistent family")
        XCTAssertEqual(typed, .failure(.notFound))
        let unusable = await provider.load(family: "../Escape")
        XCTAssertEqual(unusable, .failure(.notFound))
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: folder.path), [])
    }

    func testConnectionFailuresAreTheOnesWorthRetryingOnline() {
        for code in [URLError.Code.notConnectedToInternet, .networkConnectionLost, .cannotFindHost, .timedOut, .dnsLookupFailed] {
            XCTAssertEqual(GoogleFontsProvider.classify(URLError(code)), .offline, "\(code)")
        }
        for code in [URLError.Code.badServerResponse, .cancelled, .badURL] {
            XCTAssertEqual(GoogleFontsProvider.classify(URLError(code)), .unavailable, "\(code)")
        }
        XCTAssertEqual(GoogleFontsProvider.classify(CocoaError(.fileWriteNoPermission)), .unavailable)
    }
}
