import Metal
import XCTest
@testable import VersoCustom

/// GPU and WebKit tests skip on hosts without a Metal device. On CI, a
/// missing device must fail the run instead of letting unrendered scenes
/// pass as skips, so both workflows run the native tests with
/// TEST_RUNNER_LUNALYR_REQUIRE_GPU=1 (Tests/Python/test_workflows.py
/// keeps it that way).
///
/// xcodebuild does not pass its own environment to the test process: it
/// forwards only variables prefixed with TEST_RUNNER_, without the prefix.
/// Set `TEST_RUNNER_LUNALYR_REQUIRE_GPU=1` for `xcodebuild test` (and so for
/// Run-Tests.command); a plain `LUNALYR_REQUIRE_GPU=1` there never reaches
/// the tests. Running the test bundle directly takes LUNALYR_REQUIRE_GPU=1.
enum GPUTestSupport {
    static let requirementVariables = ["LUNALYR_REQUIRE_GPU", "TEST_RUNNER_LUNALYR_REQUIRE_GPU"]

    static var required: Bool { required(in: ProcessInfo.processInfo.environment) }

    static func required(in environment: [String: String]) -> Bool {
        requirementVariables.contains { environment[$0] == "1" }
    }

    static func device(file: StaticString = #filePath, line: UInt = #line) throws -> MTLDevice {
        if let device = MTLCreateSystemDefaultDevice() { return device }
        try unavailable("This test host has no Metal device; GPU rendering is unverified here.", file: file, line: line)
    }

    /// Skips a GPU or WebKit test, or fails it when the GPU is required.
    static func unavailable(_ message: String, file: StaticString = #filePath, line: UInt = #line) throws -> Never {
        if required {
            XCTFail("LUNALYR_REQUIRE_GPU=1: \(message)", file: file, line: line)
            throw XCTSkip("Failed: \(message)")
        }
        throw XCTSkip(message)
    }

    /// Polls `condition` until it holds or `timeout` passes, then returns the
    /// last value. WebKit presents asynchronously, so a fixed number of short
    /// sleeps turned a slow CI machine into a flaky failure.
    @MainActor
    static func eventually<T>(timeout: TimeInterval = 10, interval: Duration = .milliseconds(50),
                              _ sample: () async throws -> T, until condition: (T) -> Bool) async throws -> T {
        let deadline = Date().addingTimeInterval(timeout)
        var value = try await sample()
        while !condition(value), Date() < deadline {
            try await Task.sleep(for: interval)
            value = try await sample()
        }
        return value
    }

    /// Mean relative luminance of BGRA8 pixels, the measure the
    /// photosensitivity limiter caps.
    static func meanLuminance(bgra pixels: [UInt8]) -> Float {
        var sum: Float = 0
        for offset in stride(from: 0, to: pixels.count, by: 4) {
            sum += ExposureLimiter.luminance(red: Float(pixels[offset + 2]) / 255,
                green: Float(pixels[offset + 1]) / 255, blue: Float(pixels[offset]) / 255)
        }
        return sum / Float(max(1, pixels.count / 4))
    }
}
