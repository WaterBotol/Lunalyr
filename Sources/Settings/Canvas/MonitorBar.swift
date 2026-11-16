import AppKit
import SwiftUI

// MARK: - The monitor bar
//
// Above the Look pages' object switch: which monitor the pages below edit
// ("Monitor: Pro 27Q-10"), what that monitor shows (desktop lyrics,
// floating lyrics, both or none), which of its two lyric styles is edited,
// and Copy to All Monitors / Same on Every Monitor. Everything on the Look
// pages below changes the picked monitor's chosen style
// (`LyricStyles.edit(display:)`), and the picture is drawn at its shape.

@MainActor
struct MonitorBar: View {
    @ObservedObject var floating: FloatingLyricsOptions
    /// Draws the Desktop style / Floating style switch (not on Animated covers).
    var showsStyleSwitch = true
    @ObservedObject private var styles = LyricStyles.shared
    @State private var screens = FloatingDisplayChoice.connected()
    @State private var confirmsCopy = false
    @Environment(\.undoManager) private var undoManager

    /// One row's height; the bar takes two in a narrow canvas.
    static let height: CGFloat = 30

    private var several: Bool { screens.count > 1 }
    private var editing: String { styles.editingDisplay }
    private var monitorName: String {
        MonitorBar.name(of: editing, among: screens)
    }

    /// A display's name in the picker, numbered when two share one.
    static func name(of key: String, among screens: [FloatingDisplayChoice]) -> String {
        screens.first { $0.key == key }?.name ?? screens.first?.name ?? "This display"
    }

    var body: some View {
        // One row when it fits; else two, then two with shorter labels and
        // the monitor's name cut short, so nothing spills out of a narrow canvas.
        ViewThatFits(in: .horizontal) {
            HStack(spacing: 10) {
                if several { monitorMenu(compact: false) }
                showsMenu(compact: false)
                Spacer(minLength: 6)
                styleSwitch(compact: false)
                if several { moreMenu }
            }
            .frame(height: Self.height)
            twoRows(compact: false)
            twoRows(compact: true)
        }
        .onAppear(perform: followShows)
        .onChange(of: floating.choice(for: editing)) { _, _ in followShows() }
        .onChange(of: editing) { _, _ in followShows() }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didChangeScreenParametersNotification)) { _ in
            screens = FloatingDisplayChoice.connected()
        }
        .confirmationDialog("Copy \(monitorName)’s look to every monitor?", isPresented: $confirmsCopy) {
            Button("Copy to All Monitors") { styles.copyToAllMonitors() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Every monitor takes this monitor’s wallpaper and its Desktop and Floating styles.")
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Monitor")
    }

    private func twoRows(compact: Bool) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            if several {
                HStack(spacing: 10) {
                    monitorMenu(compact: compact)
                    Spacer(minLength: 6)
                    moreMenu
                }
                .frame(height: Self.height)
            }
            HStack(spacing: 10) {
                showsMenu(compact: compact)
                Spacer(minLength: 6)
                styleSwitch(compact: compact)
            }
            .frame(height: Self.height)
        }
    }

    /// The style a monitor showing one kind of lyrics has to edit; nil when
    /// it shows both (the switch picks) or none (the last one stays).
    static func impliedStyle(_ choice: DisplayLyrics) -> LyricsSurface? {
        switch choice {
        case .desktop: return .desktop
        case .floating: return .floating
        case .both, .none: return nil
        }
    }

    /// Only a monitor showing both kinds of lyrics has two styles to choose
    /// between; otherwise "Shows" already says which one these settings edit.
    private func followShows() {
        if let implied = Self.impliedStyle(floating.choice(for: editing)), floating.surface != implied {
            floating.surface = implied
        }
    }

    @ViewBuilder private func styleSwitch(compact: Bool) -> some View {
        if showsStyleSwitch && floating.choice(for: editing) == .both {
            CanvasSegments(title: "Editing", selection: $floating.surface,
                           options: LyricsSurface.allCases.map { ($0, compact ? $0.styleName : $0.title) }, small: true)
                .help("Which of this monitor’s lyric styles these settings change: its desktop lyrics’ or its floating lyrics’")
        }
    }

    private func monitorMenu(compact: Bool) -> some View {
        Menu {
            Picker("Monitor", selection: Binding(get: { editing }, set: { pick($0) })) {
                ForEach(screens, id: \.key) { Text($0.name).tag($0.key) }
            }
            .pickerStyle(.inline)
        } label: {
            ThemedPopUpLabel("Monitor: \(monitorName)")
        }
        .menuStyle(.button)
        .buttonStyle(ThemedPopUpButtonStyle())
        .menuIndicator(.hidden)
        .fixedSize(horizontal: !compact, vertical: true)
        .frame(maxWidth: compact ? 200 : nil, alignment: .leading)
        .disabled(styles.sameEverywhere)
        .help(styles.sameEverywhere ? "Same on every monitor is on: every monitor shows this look"
              : "The monitor these settings change; each monitor keeps its own look")
        .accessibilityLabel("Monitor")
        .accessibilityValue(monitorName)
    }

    /// Edits `display`'s look. Undo steps recorded for the monitor left
    /// would change this one's stores, so they go.
    private func pick(_ display: String) {
        guard display != editing else { return }
        undoManager?.removeAllActions()
        styles.edit(display: display)
    }

    private func showsMenu(compact: Bool) -> some View {
        let selection = Binding(get: { floating.choice(for: editing) },
                                set: { floating.setChoice($0, for: editing) })
        return Menu {
            Picker("This monitor shows", selection: selection) {
                ForEach(DisplayLyrics.allCases) { Text($0.title).tag($0) }
            }
            .pickerStyle(.inline)
        } label: {
            ThemedPopUpLabel(compact ? selection.wrappedValue.title : "Shows: \(selection.wrappedValue.title)")
        }
        .menuStyle(.button)
        .buttonStyle(ThemedPopUpButtonStyle())
        .menuIndicator(.hidden)
        .fixedSize()
        .help("What \(monitorName) shows: lyrics on its desktop, floating over its apps, both, or the wallpaper alone")
        .accessibilityLabel("This monitor shows")
        .accessibilityValue(selection.wrappedValue.title)
    }

    private var moreMenu: some View {
        Menu {
            Button("Copy to All Monitors…") { confirmsCopy = true }
                .disabled(styles.sameEverywhere)
            Toggle("Same on every monitor", isOn: Binding(get: { styles.sameEverywhere },
                                                          set: { styles.setSameEverywhere($0) }))
        } label: {
            Image(systemName: "ellipsis.circle")
                .font(.system(size: 14))
                .foregroundStyle(Color.studio(.ink2))
        }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .fixedSize()
        .help("Give every monitor this look")
        .accessibilityLabel("Monitor options")
    }
}
