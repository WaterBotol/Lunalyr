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
    /// "Copied" shows on the button for `Hold.confirm`.
    @State private var copiedAt: Int?
    /// The room's code changed (a new room after the old one expired).
    @State private var newLinkAt: Int?
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.small) {
            PanelSectionHeader("Listen along") { EmptyView() }
            if let follower = listen.follower {
                following(follower)
                    .transition(.opacity)
            }
            if let state = listen.sharingState {
                sharing(state)
                    .transition(.opacity)
            }
            if listen.follower == nil {
                if let ended = listen.lastEnded {
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        InspectorCaption(ended)
                        Spacer(minLength: 4)
                        Button("Dismiss") { listen.dismissLastEnded() }
                            .buttonStyle(.borderless)
                            .controlSize(.small)
                            .accessibilityLabel("Dismiss: \(ended)")
                    }
                    .transition(.opacity)
                }
                HStack(spacing: 6) {
                    TextField("Join with code", text: $code, prompt: Text("Code or link"))
                        .textFieldStyle(.roundedBorder)
                        .controlSize(.small)
                        .onSubmit(join)
                    Button("Join", action: join)
                        .controlSize(.small)
                        .disabled(code.trimmingCharacters(in: .whitespaces).isEmpty)
                }
                .transition(.opacity)
                if invalid {
                    InspectorCaption("That isn\u{2019}t a listen-along code.")
                        .transition(.opacity)
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .animation(quick, value: listen.follower == nil)
        .animation(quick, value: listen.sharingState)
        .animation(quick, value: listen.lastEnded)
        .animation(quick, value: invalid)
        // A stale "isn't a code" goes as soon as the code is edited.
        .onChange(of: code) { _, _ in if invalid { invalid = false } }
        .onChange(of: listen.room) { old, new in
            if old != nil, new != nil, old != new { newLinkAt = (newLinkAt ?? 0) + 1 }
        }
        .task(id: copiedAt) {
            guard copiedAt != nil else { return }
            try? await Task.sleep(for: .seconds(LunalyrDesign.Motion.Hold.confirm))
            if !Task.isCancelled { copiedAt = nil }
        }
        .task(id: newLinkAt) {
            guard newLinkAt != nil else { return }
            try? await Task.sleep(for: .seconds(Self.newLinkHold))
            if !Task.isCancelled { newLinkAt = nil }
        }
    }

    private var quick: Animation {
        LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion)
    }

    /// How long "New link" shows after the room's code changed.
    static let newLinkHold: TimeInterval = 2

    @ViewBuilder
    private func sharing(_ state: ListenAlong.SharingState) -> some View {
        HStack(spacing: 6) {
            Image(systemName: Self.sharingSymbol(state))
                .foregroundStyle(.secondary)
                .contentTransition(LunalyrDesign.Motion.symbolSwap(reduceMotion: reduceMotion))
            switch state {
            case .live(let room, let listeners):
                Text(Self.sharingTitle(listeners: listeners))
                    .font(.callout)
                    .contentTransition(reduceMotion ? .opacity : .numericText(value: Double(listeners)))
                if newLinkAt != nil {
                    Text("New link")
                        .lunalyrText(.footnote)
                        .transition(.opacity)
                }
                Spacer(minLength: 4)
                Button {
                    Self.copy(listen.webPageURL?.absoluteString ?? room)
                    copiedAt = (copiedAt ?? 0) + 1
                } label: {
                    if copiedAt != nil {
                        Label("Copied", systemImage: "checkmark")
                    } else {
                        Text("Copy Link")
                    }
                }
                .buttonStyle(.borderless)
                .controlSize(.small)
                .help("Copies the web listen-along page. The code is \(room).")
            case .waitingForMusic, .retrying:
                Text(Self.sharingTitle(state))
                    .font(.callout)
                    .foregroundStyle(.secondary)
                Spacer(minLength: 0)
            }
        }
        .animation(quick, value: copiedAt)
        .animation(quick, value: newLinkAt)
        .accessibilityElement(children: .combine)
    }

    /// What sharing says before a room is open.
    static func sharingTitle(_ state: ListenAlong.SharingState) -> String {
        switch state {
        case .waitingForMusic: return "Sharing starts when something plays"
        case .retrying: return "Can\u{2019}t reach the relay \u{00B7} trying again"
        case .live(_, let listeners): return sharingTitle(listeners: listeners)
        }
    }

    static func sharingSymbol(_ state: ListenAlong.SharingState) -> String {
        switch state {
        case .waitingForMusic: return "person.2"
        case .retrying: return "exclamationmark.arrow.triangle.2.circlepath"
        case .live: return "person.2.wave.2"
        }
    }

    private func following(_ follower: ListenFollower) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 6) {
                Image(systemName: "headphones")
                    .foregroundStyle(.secondary)
                    .symbolEffect(.pulse, options: .repeating,
                                  isActive: follower.isReconnecting && !reduceMotion)
                Text(Self.followingTitle(phase: follower.phase, reconnecting: follower.isReconnecting,
                                         name: follower.displayName))
                    .font(.callout)
                    .lineLimit(1)
                    .contentTransition(.opacity)
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
        let failed = !listen.requestJoin(code: code)
        if !failed { code = "" }
        invalid = failed
    }

    /// "Joining Sam…", "Reconnecting to Sam…", "Listening along with Sam".
    static func followingTitle(phase: ListenFollower.Phase, reconnecting: Bool, name: String) -> String {
        if reconnecting { return "Reconnecting to \(name)\u{2026}" }
        return phase == .connecting ? "Joining \(name)\u{2026}" : "Listening along with \(name)"
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
