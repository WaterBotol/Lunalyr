import AppKit
import CoreGraphics

extension NSScreen {
    var versoDisplayID: CGDirectDisplayID? {
        (deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value
    }
    var versoFrame: VCBox {
        VCBox(x: Double(frame.minX), y: Double(frame.minY), width: Double(frame.width), height: Double(frame.height))
    }
}

extension VCBox {
    var rect: CGRect { CGRect(x: x, y: y, width: width, height: height) }
}

/// Ask AppKit what would receive a mouse-down at this exact desktop point.
/// Unrelated helper/transparent windows no longer veto the entire desktop.
@MainActor
enum DesktopVisibility {
    static func isExposed(at point: CGPoint, ignoring windowIDs: Set<CGWindowID>) -> Bool {
        isExposed(windowNumber: NSWindow.windowNumber(at: point, belowWindowWithWindowNumber: 0), ignoring: windowIDs)
    }

    /// Classifies a window already found under the pointer, so callers that
    /// cache the hit can skip the first window-server query.
    static func isExposed(windowNumber number: Int, ignoring windowIDs: Set<CGWindowID>) -> Bool {
        if number == 0 { return true }
        guard number > 0, let id = CGWindowID(exactly: number) else { return false }
        if windowIDs.contains(id) { return true }
        // Required layer metadata only; no images, names, titles or owner data.
        let metadata = CGWindowListCopyWindowInfo(.optionIncludingWindow, id) as? [[String: Any]]
        return evaluate(hitWindowNumber: number, ignoring: windowIDs, metadata: metadata)
    }

    /// Separate lookup from classification so incomplete/unrelated metadata can
    /// be regression-tested without creating real desktop windows.
    static func evaluate(hitWindowNumber: Int, ignoring windowIDs: Set<CGWindowID>, metadata: [[String: Any]]?) -> Bool {
        guard hitWindowNumber >= 0, let id = CGWindowID(exactly: hitWindowNumber) else { return false }
        let hit = metadata?.first { ($0[kCGWindowNumber as String] as? NSNumber)?.uint32Value == id }
        let layer = hit?[kCGWindowLayer as String] as? NSNumber
        return vc_hit_exposes_desktop(id, layer == nil ? 0 : 1, layer?.int32Value ?? 0,
                                      CGWindowLevelForKey(.normalWindow), windowIDs.contains(id) ? 1 : 0) != 0
    }
}
