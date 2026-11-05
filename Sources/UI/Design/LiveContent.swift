import SwiftUI

// Metal scenes, WebKit visualisers and the real wallpaper in the Settings
// preview draw through the GPU or another process, which an offscreen
// snapshot can't capture (and a test shouldn't start). Views that host them
// check `lunalyrRendersLiveContent` and show `LiveContentPlaceholder` when
// it is off:
//
//     @Environment(\.lunalyrRendersLiveContent) private var live
//     …
//     if live { NowPlayingView(…) } else { LiveContentPlaceholder(kind: .wallpaper) }

private struct LiveContentKey: EnvironmentKey {
    static let defaultValue = true
}

extension EnvironmentValues {
    /// Whether live wallpaper, Metal and WebKit content renders. On in the
    /// app; the snapshot harness and previews switch it off.
    var lunalyrRendersLiveContent: Bool {
        get { self[LiveContentKey.self] }
        set { self[LiveContentKey.self] = newValue }
    }
}

/// What stands in for live content where it can't render: a calm, dark
/// wallpaper-like field with a sample lyric, so layout, cropping and the
/// type around it can still be judged. Never shown to people using the app.
struct LiveContentPlaceholder: View {
    enum Kind {
        /// The whole wallpaper: lyrics over a scene.
        case wallpaper
        /// A visualiser scene alone.
        case scene
    }

    var kind: Kind = .wallpaper
    /// Only the lyrics, over a desktop picture drawn underneath (Your wallpaper).
    var overDesktop = false

    var body: some View {
        GeometryReader { proxy in
            let unit = max(1, min(proxy.size.width / 16, proxy.size.height / 9))
            ZStack {
                if !overDesktop {
                    LinearGradient(colors: [Color(red: 0.10, green: 0.12, blue: 0.20), Color(red: 0.04, green: 0.05, blue: 0.08)],
                                   startPoint: .topLeading, endPoint: .bottomTrailing)
                    RadialGradient(colors: [Color(red: 0.20, green: 0.42, blue: 0.48).opacity(0.55), .clear],
                                   center: UnitPoint(x: 0.28, y: 0.72), startRadius: 0, endRadius: unit * 9)
                    RadialGradient(colors: [Color(red: 0.42, green: 0.26, blue: 0.52).opacity(0.45), .clear],
                                   center: UnitPoint(x: 0.78, y: 0.25), startRadius: 0, endRadius: unit * 8)
                }
                if kind == .wallpaper {
                    VStack(spacing: unit * 0.35) {
                        Text("Every word, in time")
                            .font(.system(size: unit * 0.95, weight: .bold))
                        Text("with the music")
                            .font(.system(size: unit * 0.95, weight: .bold))
                            .opacity(0.45)
                    }
                    .foregroundStyle(.white)
                    .multilineTextAlignment(.center)
                }
                VStack {
                    Spacer()
                    HStack {
                        Text(kind == .wallpaper ? "Live preview stand-in" : "Scene stand-in")
                            .font(.system(size: max(8, unit * 0.32), weight: .medium))
                            .foregroundStyle(.white.opacity(0.55))
                            .padding(.horizontal, unit * 0.3)
                            .padding(.vertical, unit * 0.12)
                            .background(Capsule().fill(.white.opacity(0.10)))
                        Spacer()
                    }
                }
                .padding(unit * 0.35)
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(kind == .wallpaper ? "Live wallpaper preview" : "Visualiser scene")
    }
}
