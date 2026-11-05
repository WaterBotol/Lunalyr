import AppKit

/// The window server's id for an AppKit window number.
///
/// `NSWindow.windowNumber` is an `Int`; `CGWindowID` is 32 bits. A window that
/// has no window-server window yet reports 0 or a negative number, and some
/// system-owned windows in an app's window list can report numbers that do not
/// fit 32 bits at all. A plain `CGWindowID(_:)` traps on those, so every
/// conversion goes through here and simply yields nil.
enum WindowServerID {
    static func id(forWindowNumber number: Int) -> CGWindowID? {
        number > 0 ? CGWindowID(exactly: number) : nil
    }
}

extension NSWindow {
    /// This window's `CGWindowID`, when it has one that fits.
    var windowServerID: CGWindowID? { WindowServerID.id(forWindowNumber: windowNumber) }
}
