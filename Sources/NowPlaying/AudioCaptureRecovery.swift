import Foundation

/// Bounded recovery. An explicitly selected method is never silently changed.
struct AudioCaptureRecovery {
    static func shouldUseCompatibility(method: AudioCaptureMethod, usingTap: Bool,
        elapsed: Double, packets: Int, packetAge: Double, quietFor: Double, playing: Bool) -> Bool {
        guard method == .automatic, usingTap, elapsed >= 4 else { return false }
        return packets == 0 || packetAge > 2.5 || (playing && quietFor >= 8)
    }
}
