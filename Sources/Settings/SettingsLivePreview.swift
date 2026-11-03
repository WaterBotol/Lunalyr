import AppKit
import SwiftUI

/// Uses the same wallpaper and lyrics renderer as the desktop.
@MainActor
struct SettingsLivePreview: View {
    @StateObject private var activity = WindowRenderActivity()
    var body: some View {
        GeometryReader { geometry in
            NowPlayingView(displayID: 0, renderActivity: activity)
                .environmentObject(NowPlayingMonitor.shared)
                .frame(width: 1280, height: 720)
                .scaleEffect(geometry.size.height / 720, anchor: .center)
                .frame(width: geometry.size.width, height: geometry.size.height)
                .clipped()
                .allowsHitTesting(false)
                .accessibilityLabel("Live wallpaper preview")
        }
    }
}
