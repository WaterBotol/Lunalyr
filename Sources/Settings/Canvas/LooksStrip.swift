import SwiftUI

// MARK: - Looks, at the top of the Wallpaper and Lyric style inspectors
//
// The six built-in Looks and the person's own, as pictures (spec §6).
// Choosing one applies it through `LookStore` as one undoable change; the
// header says which Look is in use and whether it has been edited since.

@MainActor
struct LooksSection: View {
    @ObservedObject var store: LookStore = .shared
    @Environment(\.undoManager) private var undoManager
    @State private var naming: LookNaming?
    @State private var deleting: Look?

    static let columns = 3
    static let gap: CGFloat = 10

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .firstTextBaseline) {
                Text("Look").font(LunalyrDesign.Studio.TypeScale.section).foregroundStyle(Color.studio(.ink))
                    .accessibilityAddTraits(.isHeader)
                Spacer(minLength: 8)
                Text(store.displayName)
                    .font(.system(size: 12))
                    .foregroundStyle(Color.studio(.ink2))
                    .lineLimit(1)
                    .contentTransition(.opacity)
            }
            .accessibilityElement(children: .combine)
            GeometryReader { proxy in
                grid(width: proxy.size.width)
            }
            .frame(height: gridHeight)
            HStack(spacing: 8) {
                Button("Save as Look…") {
                    naming = LookNaming(kind: .save, name: Self.defaultName(for: store.currentLook, in: store.looks))
                }
                .controlSize(.small)
                .help("Keep the wallpaper and lyric style as a Look of your own")
                if store.isEdited, let look = store.currentLook {
                    Button("Revert to \(look.name)") { store.apply(look, undoManager: undoManager) }
                        .controlSize(.small)
                        .help("Put back what \(look.name) sets")
                }
            }
            .padding(.top, 2)
        }
        .sheet(item: $naming) { request in
            LookNameSheet(request: request, taken: Set(store.looks.filter { $0.id != request.lookID }.map { $0.name.lowercased() })) { name in
                switch request.kind {
                case .save: store.saveCurrent(as: name)
                case .rename: if let id = request.lookID { store.rename(id, to: name) }
                }
            }
        }
        .confirmationDialog("Delete the Look “\(deleting?.name ?? "")”?", isPresented: Binding(
            get: { deleting != nil }, set: { if !$0 { deleting = nil } }), titleVisibility: .visible) {
            Button("Delete", role: .destructive) {
                if let look = deleting { store.delete(look.id) }
                deleting = nil
            }
        } message: {
            Text("Your settings stay as they are.")
        }
    }

    /// Rows of three tiles, a 75 × 47 picture and its name each.
    private var gridHeight: CGFloat {
        let rows = CGFloat((store.looks.count + Self.columns - 1) / Self.columns)
        let tileHeight = ((LunalyrDesign.Studio.Layout.inspectorWidth - 40 - Self.gap * 2) / 3 * 0.625).rounded() + 19
        return rows * tileHeight + max(0, rows - 1) * Self.gap
    }

    private func grid(width: CGFloat) -> some View {
        let size = ChoiceGridMetrics.tile(count: Self.columns, ratio: 0.625, width: width, gap: Self.gap)
        return VStack(alignment: .leading, spacing: Self.gap) {
            ForEach(Array(ChoiceGridMetrics.rows(store.looks, count: Self.columns).enumerated()), id: \.offset) { _, row in
                HStack(alignment: .top, spacing: Self.gap) {
                    ForEach(row) { look in tile(look, size: size) }
                }
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Looks")
    }

    @ViewBuilder private func tile(_ look: Look, size: CGSize) -> some View {
        let chosen = store.current == look.id
        let name = chosen ? store.displayName : look.name
        let tile = ChoiceTile(name: look.name, chosen: chosen, size: size, radius: 5, hint: look.summary,
                              spokenName: name, action: { store.apply(look, undoManager: undoManager) }) {
            LookArt(look: look)
        }
        if look.isBuiltIn {
            tile
        } else {
            tile.contextMenu {
                Button("Rename…") { naming = LookNaming(kind: .rename, name: look.name, lookID: look.id) }
                Button("Delete…") { deleting = look }
            }
        }
    }

    /// "<chosen name> copy", unique among the Looks; "My Look" with none chosen.
    static func defaultName(for look: Look?, in looks: [Look]) -> String {
        let base = look.map { "\($0.name) copy" } ?? "My Look"
        let taken = Set(looks.map { $0.name.lowercased() })
        guard taken.contains(base.lowercased()) else { return base }
        var number = 2
        while taken.contains("\(base) \(number)".lowercased()) { number += 1 }
        return "\(base) \(number)"
    }
}

/// A Save as Look… or Rename… request.
struct LookNaming: Identifiable {
    enum Kind { case save, rename }
    let kind: Kind
    var name: String
    var lookID: Look.ID?
    let id = UUID()
}

/// One field, Name, and Save (or Rename).
struct LookNameSheet: View {
    let request: LookNaming
    /// Names in use (lowercased), the Look being renamed left out.
    let taken: Set<String>
    let commit: (String) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var name = ""
    @FocusState private var focused: Bool

    static let maximumLength = 30

    /// Why `name` can't be used, or nil.
    static func problem(with name: String, taken: Set<String>) -> String? {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty { return "Give the Look a name." }
        if trimmed.count > maximumLength { return "Use \(maximumLength) characters or fewer." }
        if taken.contains(trimmed.lowercased()) { return "A Look already has this name." }
        return nil
    }

    var body: some View {
        let problem = Self.problem(with: name, taken: taken)
        VStack(alignment: .leading, spacing: 14) {
            Text(request.kind == .save ? "Save as Look" : "Rename Look")
                .font(LunalyrDesign.Studio.TypeScale.section)
            TextField("Name", text: $name)
                .textFieldStyle(.roundedBorder)
                .focused($focused)
                .onSubmit(save)
                .frame(width: 260)
            Text(problem ?? (request.kind == .save
                             ? "Keeps the wallpaper type, layout, typeface, entrance and highlight colour."
                             : " "))
                .font(LunalyrDesign.Studio.TypeScale.caption)
                .foregroundStyle(Color.studio(.ink2))
                .fixedSize(horizontal: false, vertical: true)
                .frame(width: 260, alignment: .leading)
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button(request.kind == .save ? "Save" : "Rename", action: save)
                    .keyboardShortcut(.defaultAction)
                    .disabled(problem != nil)
            }
        }
        .padding(20)
        .onAppear {
            name = request.name
            focused = true
        }
    }

    private func save() {
        guard Self.problem(with: name, taken: taken) == nil else { return }
        commit(name.trimmingCharacters(in: .whitespacesAndNewlines))
        dismiss()
    }
}

// MARK: - A Look's picture

/// The built-ins' pictures (direction 2's art, in the sample's colours); a
/// saved Look's picture is its own wallpaper type with the sample line in
/// its typeface.
struct LookArt: View {
    let look: Look

    private static let orange = Color(red: 0.99, green: 0.56, blue: 0.32)
    private static let pink = Color(red: 0.88, green: 0.27, blue: 0.52)
    private static let deep = Color(red: 0.07, green: 0.05, blue: 0.15)

    var body: some View {
        GeometryReader { proxy in
            let w = proxy.size.width
            let h = proxy.size.height
            ZStack(alignment: .topLeading) {
                switch look.id {
                case "builtin.matched": matched(w, h)
                case "builtin.quiet": quiet(w, h)
                case "builtin.nightDrive": nightDrive(w, h)
                case "builtin.poster": poster(w, h)
                case "builtin.starChart": starChart(w, h)
                case "builtin.collage": collage(w, h)
                default: saved(w, h)
                }
            }
            .frame(width: w, height: h)
            .clipped()
        }
        .environment(\.colorScheme, .dark)
        .accessibilityHidden(true)
    }

    private func saved(_ w: CGFloat, _ h: CGFloat) -> some View {
        ZStack(alignment: .bottomLeading) {
            WallpaperTypePicture(mode: look.wallpaper, palette: ChoicePalette.sample)
            Text("gold across the bay")
                .font(TypefaceSpecimen.font(look.typeface, size: h * 0.17))
                .foregroundStyle(.white)
                .lineLimit(1)
                .minimumScaleFactor(0.5)
                .padding(w * 0.07)
        }
    }

    private func matched(_ w: CGFloat, _ h: CGFloat) -> some View {
        ZStack(alignment: .bottomLeading) {
            SampleWallpaper()
            VStack(alignment: .leading, spacing: h * 0.01) {
                Text("Static on the radio,")
                Text("gold across the bay")
            }
            .font(.system(size: h * 0.105, weight: .semibold))
            .foregroundStyle(.white)
            .padding(.leading, w * 0.08)
            .padding(.bottom, h * 0.14)
        }
    }

    private func quiet(_ w: CGFloat, _ h: CGFloat) -> some View {
        ZStack(alignment: .bottom) {
            LinearGradient(colors: [Color(red: 0.56, green: 0.64, blue: 0.75), Color(red: 0.86, green: 0.76, blue: 0.70)],
                           startPoint: .top, endPoint: .bottom)
            Ellipse().fill(Color(red: 0.24, green: 0.30, blue: 0.37))
                .frame(width: w * 1.3, height: h * 0.7).offset(x: -w * 0.25, y: h * 0.42)
            Ellipse().fill(Color(red: 0.12, green: 0.16, blue: 0.21))
                .frame(width: w * 1.2, height: h * 0.6).offset(x: w * 0.3, y: h * 0.40)
            HStack(spacing: w * 0.012) {
                Text("Static on the radio,").foregroundStyle(.white.opacity(0.6))
                Text("gold").foregroundStyle(.white)
            }
            .font(.system(size: h * 0.09, weight: .medium))
            .padding(.bottom, h * 0.11)
        }
    }

    private static let barHeights: [CGFloat] = [0.22, 0.34, 0.18, 0.4, 0.28, 0.5, 0.36, 0.24, 0.44, 0.3,
                                                0.2, 0.38, 0.26, 0.46, 0.32, 0.2, 0.3, 0.42, 0.24, 0.34]

    private func nightDrive(_ w: CGFloat, _ h: CGFloat) -> some View {
        ZStack(alignment: .bottomLeading) {
            Self.deep
            HStack(alignment: .bottom, spacing: w * 0.012) {
                ForEach(Self.barHeights.indices, id: \.self) { index in
                    RoundedRectangle(cornerRadius: w * 0.006)
                        .fill(index % 3 == 0 ? Self.pink : Self.orange)
                        .opacity(0.75)
                        .frame(height: h * Self.barHeights[index] * 0.7)
                }
            }
            .frame(width: w, height: h, alignment: .bottom)
            VStack(alignment: .leading, spacing: 0) {
                Text("on the radio,").foregroundStyle(.white.opacity(0.6))
                Text("gold").foregroundStyle(Self.orange)
                    .font(.custom("Avenir Next", size: h * 0.2).weight(.bold))
                Text("across the bay").foregroundStyle(.white)
            }
            .font(.custom("Avenir Next", size: h * 0.1).weight(.semibold))
            .padding(.leading, w * 0.1)
            .frame(maxHeight: .infinity, alignment: .center)
            .offset(y: -h * 0.08)
        }
    }

    private func poster(_ w: CGFloat, _ h: CGFloat) -> some View {
        ZStack(alignment: .leading) {
            Color(red: 0.40, green: 0.09, blue: 0.27)
            VStack(alignment: .leading, spacing: h * 0.01) {
                Text("static on the radio,")
                    .font(.custom("Futura", size: h * 0.075).weight(.medium))
                    .foregroundStyle(Self.orange)
                    .padding(.leading, w * 0.08)
                Text("gold across")
                    .font(.custom("Futura", size: h * 0.21).weight(.bold))
                    .foregroundStyle(.white)
                    .fixedSize()
                    .frame(width: w, alignment: .leading)
                    .offset(x: w * 0.07)
            }
            .frame(width: w, height: h, alignment: .leading)
        }
    }

    private static let stars: [(text: String, x: CGFloat, y: CGFloat, size: CGFloat)] = [
        ("Static", 0.14, 0.26, 0.08), ("on the radio,", 0.5, 0.18, 0.08),
        ("gold", 0.3, 0.55, 0.14), ("across the bay", 0.62, 0.72, 0.08),
    ]

    private func starChart(_ w: CGFloat, _ h: CGFloat) -> some View {
        ZStack(alignment: .topLeading) {
            Color(red: 0.06, green: 0.07, blue: 0.17)
            Path { path in
                var x: CGFloat = -h
                while x < w {
                    path.move(to: CGPoint(x: x, y: h))
                    path.addLine(to: CGPoint(x: x + h, y: 0))
                    x += max(6, w / 16)
                }
            }
            .stroke(.white.opacity(0.08), lineWidth: max(0.5, h * 0.004))
            Path { path in
                for (index, star) in Self.stars.enumerated() {
                    let anchor = CGPoint(x: w * star.x + w * 0.02, y: h * star.y + h * star.size * 0.6)
                    if index == 0 { path.move(to: anchor) } else { path.addLine(to: anchor) }
                }
            }
            .stroke(.white.opacity(0.35), lineWidth: max(0.5, h * 0.004))
            ForEach(Self.stars.indices, id: \.self) { index in
                let star = Self.stars[index]
                Text(star.text)
                    .font(.system(size: h * star.size, weight: index == 2 ? .semibold : .regular, design: .serif))
                    .foregroundStyle(.white)
                    .fixedSize()
                    .position(x: w * star.x + w * 0.02, y: h * star.y)
                    .offset(x: w * 0.06)
            }
        }
    }

    private func collage(_ w: CGFloat, _ h: CGFloat) -> some View {
        let unit = max(w, h)
        return ZStack(alignment: .leading) {
            Self.deep
            RadialGradient(colors: [Color(red: 0.12, green: 0.55, blue: 0.60), .clear],
                           center: UnitPoint(x: 0.85, y: 0.2), startRadius: 0, endRadius: unit * 0.55)
            RadialGradient(colors: [Self.pink.opacity(0.9), .clear],
                           center: UnitPoint(x: 0.2, y: 0.9), startRadius: 0, endRadius: unit * 0.5)
            RadialGradient(colors: [Self.orange.opacity(0.7), .clear],
                           center: UnitPoint(x: 0.6, y: 0.65), startRadius: 0, endRadius: unit * 0.3)
            VStack(alignment: .leading, spacing: h * 0.02) {
                HStack(spacing: w * 0.02) {
                    Text("Static").foregroundStyle(.black)
                        .padding(.horizontal, w * 0.015)
                        .background(Self.orange)
                    Text("on the").foregroundStyle(.white)
                }
                .font(.custom("Futura", size: h * 0.09).weight(.bold))
                Text("radio,")
                    .font(.custom("Futura", size: h * 0.24).weight(.bold))
                    .foregroundStyle(.white)
                HStack(spacing: w * 0.02) {
                    Text("gold").foregroundStyle(.white)
                        .padding(.horizontal, w * 0.015)
                        .background(Color(red: 0.62, green: 0.10, blue: 0.36))
                    Text("across the bay").foregroundStyle(.white)
                }
                .font(.custom("Futura", size: h * 0.09).weight(.medium))
            }
            .padding(.leading, w * 0.09)
        }
    }
}
