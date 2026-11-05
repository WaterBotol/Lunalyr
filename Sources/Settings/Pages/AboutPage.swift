import AppKit
import SwiftUI

/// About: what Lunalyr is, its version and project page, what it is built
/// on, and what stays on this Mac. No preview: centred in the whole detail.
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
        }
        .background(Color(nsColor: .windowBackgroundColor))
    }

    private var content: some View {
            VStack(spacing: LunalyrDesign.Spacing.xxLarge) {
                VStack(spacing: LunalyrDesign.Spacing.small) {
                    Image(nsImage: NSImage(named: NSImage.applicationIconName) ?? NSApp.applicationIconImage)
                        .resizable()
                        .interpolation(.high)
                        .frame(width: 64, height: 64)
                        .accessibilityHidden(true)
                    Text("Lunalyr")
                        .lunalyrText(.title)
                        .accessibilityAddTraits(.isHeader)
                    Text("Desktop lyrics, in motion.")
                        .foregroundStyle(.secondary)
                    Text("Version \(GeneralPage.version)")
                        .lunalyrText(.footnote)
                        .textSelection(.enabled)
                    Link("Project & Downloads", destination: Self.projectURL)
                        // Text keeps the system link colour wherever a macOS
                        // release would tint links with the window's mint.
                        .tint(Color(nsColor: .linkColor))
                        .padding(.top, LunalyrDesign.Spacing.xSmall)
                        .help(Self.projectURL.absoluteString)
                }
                .multilineTextAlignment(.center)
                AboutCard(title: "Built on open source", paragraphs: [
                    "Based on Verso by Akki / AkkiCode06. The upstream GPLv3 with Commons Clause licence and attribution are preserved.",
                    "Visualisers include work from Synesthia, Fosfora, Wavefield, Wavecore and Cymatic (MIT). Bundled components retain their licence notices.",
                ]) {
                    Button("View Bundled Licences") {
                        if let folder = Bundle.main.resourceURL { NSWorkspace.shared.open(folder) }
                    }
                    .help("Show the licence files in the Finder")
                }
                AboutCard(title: "Privacy", paragraphs: [
                    "Audio analysis and readings stay on this Mac. Enabled lyric services receive song metadata. No Apple Music account cookies are requested.",
                ])
            }
            .frame(maxWidth: 480)
            .padding(.horizontal, LunalyrDesign.Spacing.section)
            .padding(.vertical, LunalyrDesign.Spacing.section)
            .frame(maxWidth: .infinity)
    }
}

/// A titled block of About's text, with an action beneath when it has one.
private struct AboutCard<Action: View>: View {
    let title: String
    let paragraphs: [String]
    @ViewBuilder let action: Action

    var body: some View {
        VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.small) {
            Text(title)
                .lunalyrText(.emphasis)
                .accessibilityAddTraits(.isHeader)
            ForEach(paragraphs, id: \.self) { paragraph in
                Text(paragraph).lunalyrText(.caption)
            }
            action
                .padding(.top, LunalyrDesign.Spacing.xSmall)
        }
        .padding(LunalyrDesign.Spacing.large)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: LunalyrDesign.Radius.card, style: .continuous)
                .fill(.quinary)
        )
        .overlay(
            RoundedRectangle(cornerRadius: LunalyrDesign.Radius.card, style: .continuous)
                .strokeBorder(Color.lunalyrContrastBorder, lineWidth: 1)
        )
    }
}

extension AboutCard where Action == EmptyView {
    init(title: String, paragraphs: [String]) {
        self.init(title: title, paragraphs: paragraphs) { EmptyView() }
    }
}
