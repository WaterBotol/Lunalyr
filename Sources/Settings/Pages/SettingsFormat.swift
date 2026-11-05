import SwiftUI

/// How Settings shows slider values, so every page reads the same.
enum SettingsFormat {
    /// "85%".
    static func percent(_ value: Double) -> String {
        value.formatted(.percent.precision(.fractionLength(0)))
    }

    /// "1.2×".
    static func multiplier(_ value: Double) -> String {
        value.formatted(.number.precision(.fractionLength(1...2))) + "×"
    }

    /// "0.45 s".
    static func seconds(_ value: Double) -> String {
        value.formatted(.number.precision(.fractionLength(2))) + " s"
    }

    /// "0.45 seconds".
    static func spokenSeconds(_ value: Double) -> String {
        value.formatted(.number.precision(.fractionLength(2))) + " seconds"
    }
}
