import AppKit
import SwiftUI

@MainActor
struct CustomWallpaperView: View {
    let url: URL
    let placement: Settings.MotionPlacement
    let paused: Bool
    @State private var image: NSImage?
    private var alignment: Alignment { placement == .fillTop ? .top : placement == .fillBottom ? .bottom : .center }
    var body: some View {
        Group {
            if CustomWallpaperStore.isVideo(url) {
                MotionArtworkView(url: url, placement: placement, isPaused: paused)
            } else {
                GeometryReader { geometry in
                    if let image {
                        Image(nsImage: image).resizable()
                            .aspectRatio(contentMode: placement == .fit ? .fit : .fill)
                            .frame(width: geometry.size.width, height: geometry.size.height, alignment: alignment)
                            .clipped()
                    }
                }.task(id: url) { image = NSImage(contentsOf: url) }
            }
        }.background(.black).ignoresSafeArea()
    }
}
