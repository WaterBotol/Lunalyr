import SwiftUI

/// Lyrics may report very large layout bounds before their visual fit is
/// applied. An overlay cannot resize the wallpaper's coordinate space.
struct WallpaperViewport<Background: View, Lyrics: View>: View {
    let size: CGSize
    let background: Background
    let lyrics: Lyrics

    init(size: CGSize, @ViewBuilder background: () -> Background, @ViewBuilder lyrics: () -> Lyrics) {
        self.size = size
        self.background = background()
        self.lyrics = lyrics()
    }

    var body: some View {
        Color.clear.frame(width: size.width, height: size.height)
            .background {
                background.frame(width: size.width, height: size.height).clipped()
            }
            .overlay {
                lyrics.frame(width: size.width, height: size.height)
            }
    }
}
