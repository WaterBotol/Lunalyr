import AppKit
import SwiftUI

/// Listen-along in the menu-bar panel, shown only once a relay is set:
/// "Listening along with … · Stop" while following, the room and how many
/// listen while sharing, and a field to join with a code.
@MainActor
struct MenuBarListenAlong: View {
    @ObservedObject var listen: ListenAlong
    @State private var code = ""
    @State private var invalid = false

    var body: some View {
        VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.small) {
            PanelSectionHeader("Listen along") { EmptyView() }
            if let follower = listen.follower {
                following(follower)
            }
            if let room = listen.room {
                HStack(spacing: 6) {
                    Image(systemName: "person.2.wave.2")
                        .foregroundStyle(.secondary)
                    Text(Self.sharingTitle(listeners: listen.listeners))
                        .font(.callout)
                    Spacer(minLength: 4)
                    Button("Copy Link") { Self.copy(listen.webPageURL?.absoluteString ?? room) }
                        .buttonStyle(.borderless)
                        .controlSize(.small)
                        .help("Copies the web listen-along page. The code is \(room).")
                }
                .accessibilityElement(children: .combine)
            }
            if listen.follower == nil {
                HStack(spacing: 6) {
                    TextField("Join with code", text: $code, prompt: Text("Code or link"))
                        .textFieldStyle(.roundedBorder)
                        .controlSize(.small)
                        .onSubmit(join)
                    Button("Join", action: join)
                        .controlSize(.small)
                        .disabled(code.trimmingCharacters(in: .whitespaces).isEmpty)
                }
                if invalid {
                    InspectorCaption("That isn\u{2019}t a listen-along code.")
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func following(_ follower: ListenFollower) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 6) {
                Image(systemName: "headphones")
                    .foregroundStyle(.secondary)
                Text(follower.phase == .connecting ? "Joining \(follower.displayName)\u{2026}"
                                                   : "Listening along with \(follower.displayName)")
                    .font(.callout)
                    .lineLimit(1)
                Text("\u{00B7}").foregroundStyle(.secondary)
                Button("Stop") { listen.stopFollowing() }
                    .buttonStyle(.borderless)
                    .controlSize(.small)
                    .accessibilityLabel("Stop listening along")
                Spacer(minLength: 0)
            }
            if let hold = follower.hold {
                HStack(spacing: 6) {
                    Image(systemName: hold == .paused ? "pause.circle" : "play.circle")
                        .foregroundStyle(.secondary)
                    Text(Self.holdTitle(hold))
                        .font(.callout)
                    Text("\u{00B7}").foregroundStyle(.secondary)
                    Button(Self.holdAction(hold)) { follower.resume() }
                        .buttonStyle(.borderless)
                        .controlSize(.small)
                        .accessibilityLabel(hold == .paused ? "Resume listening along" : "Play along")
                    Spacer(minLength: 0)
                }
                InspectorCaption(hold == .paused ? "You changed the music, so nothing is sent to your player until you resume."
                                                 : "Your player is open. Nothing plays until you click Play.")
            }
            if let note = follower.note {
                InspectorCaption(note)
            }
            if let page = follower.songPageURL {
                Button("Open on song.link") { NSWorkspace.shared.open(page) }
                    .buttonStyle(.borderless)
                    .controlSize(.small)
                    .help("Opens the song\u{2019}s song.link page, which lists it on every service")
            }
        }
    }

    private func join() {
        invalid = !listen.requestJoin(code: code)
        if !invalid { code = "" }
    }

    static func holdTitle(_ hold: ListenFollower.Hold) -> String {
        hold == .paused ? "Paused" : "Ready"
    }

    static func holdAction(_ hold: ListenFollower.Hold) -> String {
        hold == .paused ? "Resume" : "Play"
    }

    static func sharingTitle(listeners: Int) -> String {
        switch listeners {
        case 0: return "Sharing \u{00B7} no one listening yet"
        case 1: return "Sharing \u{00B7} 1 listening"
        default: return "Sharing \u{00B7} \(listeners) listening"
        }
    }

    private static func copy(_ text: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
    }
}

/// The relay address, saved when the field is submitted or loses focus
/// rather than on every keystroke.
@MainActor
struct ListenRelayField: View {
    @Binding var relay: String
    @State private var draft = ""
    @FocusState private var focused: Bool

    var body: some View {
        TextField("Listen-along relay", text: $draft, prompt: Text("https://\u{2026}"))
            .focused($focused)
            .onAppear { draft = relay }
            .onSubmit(commit)
            .onChange(of: focused) { _, isFocused in if !isFocused { commit() } }
            .onChange(of: relay) { _, value in if !focused { draft = value } }
    }

    private func commit() {
        let value = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        if value != relay { relay = value }
    }
}
