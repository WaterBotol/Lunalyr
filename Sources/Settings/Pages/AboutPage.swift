import AppKit
import SwiftUI

/// About: what Lunalyr is, its version and project page, what it is built
/// on, and what stays on this Mac. Centred; the two sections are plain
/// sections (no cards) in a 440 pt column.
@MainActor
struct AboutPage: View {
    static let projectURL = URL(string: "https://github.com/WaterBotol/Lunalyr")!

    var body: some View {
        GeometryReader { proxy in
            ScrollView {
                content
                    // Centred in the window while it fits; scrolls when it doesn't.
                    .frame(minHeight: proxy.size.height)
            }
            .scrollContentBackground(.hidden)
        }
        .themedFormControls()
    }

    private var content: some View {
        VStack(spacing: 28) {
            VStack(spacing: 6) {
                Image(nsImage: NSImage(named: NSImage.applicationIconName) ?? NSApp.applicationIconImage)
                    .resizable()
                    .interpolation(.high)
                    .frame(width: 64, height: 64)
                    .accessibilityHidden(true)
                Text("Lunalyr")
                    .font(LunalyrDesign.Studio.TypeScale.title)
                    .foregroundStyle(Color.studio(.ink))
                    .accessibilityAddTraits(.isHeader)
                Text("Desktop lyrics, in motion.")
                    .foregroundStyle(Color.studio(.ink2))
                Text("Version \(GeneralPage.version)")
                    .font(LunalyrDesign.Studio.TypeScale.caption)
                    .foregroundStyle(Color.studio(.ink2))
                    .textSelection(.enabled)
                Link("Project & Downloads", destination: Self.projectURL)
                    .font(LunalyrDesign.Studio.TypeScale.row)
                    .foregroundStyle(Color.studio(.accentText))
                    .tint(Color.studio(.accentText))
                    .padding(.top, 4)
                    .help(Self.projectURL.absoluteString)
            }
            .multilineTextAlignment(.center)
            VStack(alignment: .leading, spacing: 22) {
                ThemedSection("Built on open source") {
                    AboutParagraph("Based on Verso by Akki / AkkiCode06. The upstream GPLv3 with Commons Clause licence and attribution are preserved.")
                    AboutParagraph("Visualisers include work from Synesthia, Fosfora, Wavefield, Wavecore and Cymatic (MIT). Bundled components retain their licence notices.")
                    Button("View Bundled Licences") {
                        if let folder = Bundle.main.resourceURL { NSWorkspace.shared.open(folder) }
                    }
                    .controlSize(.small)
                    .help("Show the licence files in the Finder")
                }
                ThemedSection("Privacy") {
                    AboutParagraph("Audio analysis and readings stay on this Mac. Enabled lyric services receive song metadata. No Apple Music account cookies are requested.")
                }
            }
            .frame(width: 440)
        }
        .padding(.vertical, 40)
        .padding(.horizontal, 16)
        .frame(maxWidth: .infinity)
    }
}

/// A paragraph of About's text: caption size, ink2, never folded.
private struct AboutParagraph: View {
    let text: String

    init(_ text: String) { self.text = text }

    var body: some View {
        Text(text)
            .font(LunalyrDesign.Studio.TypeScale.caption)
            .foregroundStyle(Color.studio(.ink2))
            .fixedSize(horizontal: false, vertical: true)
            .frame(maxWidth: .infinity, alignment: .leading)
    }
}
