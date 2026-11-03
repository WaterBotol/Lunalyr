import XCTest
@testable import VersoCustom

final class UpdateConfigurationTests: XCTestCase {
    private let key = Data(repeating: 7, count: 32).base64EncodedString()
    func testGitHubReleaseFeedAndPublicKeyEnableConfiguration() {
        let config = UpdateConfiguration(info: ["SUFeedURL": "https://github.com/WaterBotol/Lunalyr/releases/latest/download/appcast.xml", "SUPublicEDKey": key])
        XCTAssertEqual(config?.feed.host, "github.com")
        XCTAssertEqual(config?.publicKey, key)
    }
    func testMissingFeedOrKeyKeepsUpdatesUnconfigured() {
        XCTAssertNil(UpdateConfiguration(info: [:]))
        XCTAssertNil(UpdateConfiguration(info: ["SUFeedURL": "", "SUPublicEDKey": ""]))
        XCTAssertNil(UpdateConfiguration(info: ["SUFeedURL": "https://example.com/appcast.xml"]))
    }
    func testRejectsInsecureCredentialedAndFragmentFeeds() {
        for url in ["http://example.com/appcast.xml", "file:///tmp/appcast.xml", "https://user:pass@example.com/appcast.xml", "https://example.com/appcast.xml#other"] {
            XCTAssertNil(UpdateConfiguration(info: ["SUFeedURL": url, "SUPublicEDKey": key]))
        }
    }
    func testRejectsMalformedAndWrongLengthKeys() {
        for value in ["not a key", Data(repeating: 7, count: 31).base64EncodedString(), Data(repeating: 7, count: 64).base64EncodedString()] {
            XCTAssertNil(UpdateConfiguration(info: ["SUFeedURL": "https://example.com/appcast.xml", "SUPublicEDKey": value]))
        }
    }
}
