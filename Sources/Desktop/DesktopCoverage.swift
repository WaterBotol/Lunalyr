import AppKit
import Combine
import CoreGraphics

/// One on-screen window as the window server lists it. Only the number,
/// layer, alpha, bounds and owning process are read -- never names, titles or
/// contents -- so no Screen Recording permission is involved.
struct DesktopWindowRecord: Equatable, Sendable {
    let number: CGWindowID
    let layer: Int
    let alpha: Double
    /// Quartz global coordinates: origin at the top left of the main display.
    let bounds: CGRect
    /// Only compared with the few apps whose windows are often see-through.
    var ownerPID: pid_t = 0
}

/// A display as coverage sees it, in the same Quartz coordinates.
struct DesktopDisplayRecord: Equatable, Sendable {
    let displayID: CGDirectDisplayID
    let frame: CGRect
    /// AppKit's usable area. It excludes the Dock, but on a secondary display
    /// it can still include that display's menu bar, so coverage also trims
    /// the menu bar it finds in the window list.
    let visibleFrame: CGRect
    /// This display's wallpaper window, when it has been ordered in.
    let wallpaperWindow: CGWindowID?
}

/// What lies at one point of the desktop, as AppKit would hit it.
struct DesktopHitSample: Equatable, Sendable {
    /// Quartz global coordinates.
    let location: CGPoint
    /// The window a click there would reach; 0 when there is none.
    let windowNumber: Int
}

/// Decides, from a window list alone, whether a wallpaper can be seen.
///
/// `NSWindow.occlusionState` is unreliable this far down the window stack:
/// Finder's full-screen icon window sits above the wallpaper and can make a
/// visible wallpaper report itself covered. This model only counts ordinary
/// application windows that are opaque, and only calls a display hidden when
/// they leave nothing noticeable of the desktop showing.
///
/// Whenever the list could be describing something other than what is on
/// screen it errs towards rendering: windows of apps that are often
/// see-through never cover, and Stage Manager (which shows windows somewhere
/// other than their frames) turns the window rule off, though a full-screen
/// Space still pauses. Show Desktop moves every window on every display aside
/// without changing a frame, so before a check pauses a wallpaper it
/// hit-tests points spread over each display it would pause, each well inside
/// one of its windows (a display with no such point keeps rendering). The
/// desktop found at one of them discredits the list on all of them, until it
/// changes or a hit test finds one of the windows moved aside back in place.
/// The pointer is hit-tested too, but can only keep its own display rendering.
enum DesktopCoverage {
    /// Exposed strips thinner than this (tiling margins, the gap beside a
    /// maximised window) do not keep a wallpaper rendering, and a hit test
    /// closer than this to a window's edge says nothing about the window.
    static let noticeableGap: CGFloat = 24
    /// Upper bound on the share of the usable desktop allowed to show.
    static let maximumExposedFraction = 0.06
    /// Windows fading in or out are not opaque yet.
    static let opaqueAlpha = 0.95
    /// The tallest strip along a display's top edge still read as its menu
    /// bar (a notched display's is about 38 pt).
    static let menuBarMaximumHeight: CGFloat = 64

    /// Terminals whose background transparency leaves the wallpaper showing
    /// through a window that still reports full alpha. A wallpaper behind
    /// them keeps moving; an opaque profile only costs the pause.
    static let seeThroughApps: Set<String> = [
        "com.apple.Terminal", "com.googlecode.iterm2", "com.mitchellh.ghostty", "net.kovidgoyal.kitty",
        "org.alacritty", "io.alacritty", "com.github.wez.wezterm", "dev.warp.Warp-Stable", "dev.warp.Warp-Preview",
        "co.zeit.hyper", "org.tabby", "com.raphaelamorim.rio",
    ]

    /// The outcome of one look at the window list.
    struct Evaluation: Equatable, Sendable {
        var hidden: Set<CGDirectDisplayID> = []
        /// Set while hit tests discredit the window list. For each display
        /// left rendering on that account, the windows believed moved aside:
        /// all its covering windows when the desktop was found under them,
        /// and any found away from its frame since. A window opened later is
        /// not among them and still covers where the list puts it. Only one
        /// of these found back in place ends the distrust, which is how the
        /// end of Show Desktop is noticed.
        var distrustedCovers: [CGDirectDisplayID: [DesktopWindowRecord]]?
    }

    /// What one hit test says about the window list.
    enum HitEvidence: Equatable, Sendable {
        /// The list puts no opaque window well around it, or the hit proves nothing.
        case inconclusive
        /// A window believed moved aside was found back where the list puts it.
        case confirms
        /// The desktop was found well inside an opaque window the list puts there.
        case contradicts
    }

    /// A display the window list alone would pause.
    struct CoveredDisplay: Equatable, Sendable {
        let displayID: CGDirectDisplayID
        let frame: CGRect
        /// Where its wallpaper could be noticed; see `usableRegion`.
        let region: CGRect
        /// The opaque ordinary windows over it, largest first.
        let covers: [DesktopWindowRecord]
        /// Where a hit test can check the list (see `probePoints`): never
        /// empty, since a display that cannot be checked is not paused.
        let probes: [CGPoint]
    }

    /// What the window list says before any hit test is taken. Read off the
    /// main thread; the hit tests that may overrule it are taken on it.
    struct Listing: Equatable, Sendable {
        var windows: [DesktopWindowRecord] = []
        var ownWindows: Set<CGWindowID> = []
        var normalLayer = Int(CGWindowLevelForKey(.normalWindow))
        /// Displays a full-screen Space owns.
        var fullScreen: Set<CGDirectDisplayID> = []
        /// Displays the list would pause for their windows.
        var covered: [CoveredDisplay] = []

        /// The display that would pause whose usable area holds `point`; a
        /// hit test speaks only for that display.
        func display(at point: CGPoint) -> CoveredDisplay? {
            covered.first { $0.region.contains(point) }
        }
    }

    static func records(from list: [[String: Any]]) -> [DesktopWindowRecord] {
        list.compactMap { entry in
            guard let number = (entry[kCGWindowNumber as String] as? NSNumber)?.uint32Value, number != 0,
                  let layer = (entry[kCGWindowLayer as String] as? NSNumber)?.intValue,
                  let boundsInfo = entry[kCGWindowBounds as String] as? NSDictionary,
                  let bounds = CGRect(dictionaryRepresentation: boundsInfo as CFDictionary),
                  bounds.width.isFinite, bounds.height.isFinite
            else { return nil }
            // Alpha is optional in the window dictionary; absent means opaque.
            let alpha = (entry[kCGWindowAlpha as String] as? NSNumber)?.doubleValue ?? 1
            let owner = (entry[kCGWindowOwnerPID as String] as? NSNumber)?.int32Value ?? 0
            return DesktopWindowRecord(number: number, layer: layer, alpha: alpha, bounds: bounds, ownerPID: owner)
        }
    }

    /// Displays whose wallpaper cannot currently be seen. An empty or failed
    /// window list hides nothing.
    static func hiddenDisplays(_ displays: [DesktopDisplayRecord], windows: [DesktopWindowRecord],
                               ownWindows: Set<CGWindowID>,
                               normalLayer: Int = Int(CGWindowLevelForKey(.normalWindow))) -> Set<CGDirectDisplayID> {
        evaluate(displays, windows: windows, ownWindows: ownWindows, normalLayer: normalLayer).hidden
    }

    /// `listing` and `decide` in one: `samples` are hit tests at fixed
    /// points (see `samplePoints(for:)`) and `pointer` one at the pointer,
    /// taken as the list was read, and `previous` the last check's
    /// `distrustedCovers`.
    static func evaluate(_ displays: [DesktopDisplayRecord], windows: [DesktopWindowRecord],
                         ownWindows: Set<CGWindowID>, samples: [DesktopHitSample] = [],
                         pointer: DesktopHitSample? = nil,
                         distrusted previous: [CGDirectDisplayID: [DesktopWindowRecord]]? = nil,
                         appWindowsCover: Bool = true, seeThroughOwners: Set<pid_t> = [],
                         normalLayer: Int = Int(CGWindowLevelForKey(.normalWindow)),
                         menuBarLayer: Int = Int(CGWindowLevelForKey(.mainMenuWindow))) -> Evaluation {
        let listing = listing(displays, windows: windows, ownWindows: ownWindows, appWindowsCover: appWindowsCover,
                              seeThroughOwners: seeThroughOwners, normalLayer: normalLayer, menuBarLayer: menuBarLayer)
        return decide(listing, samples: samples, pointer: pointer, distrusted: previous)
    }

    /// What the window list alone says: which displays a full-screen Space
    /// owns, and which the list would pause for their windows.
    static func listing(_ displays: [DesktopDisplayRecord], windows: [DesktopWindowRecord],
                        ownWindows: Set<CGWindowID>, appWindowsCover: Bool = true, seeThroughOwners: Set<pid_t> = [],
                        normalLayer: Int = Int(CGWindowLevelForKey(.normalWindow)),
                        menuBarLayer: Int = Int(CGWindowLevelForKey(.mainMenuWindow))) -> Listing {
        var listing = Listing(windows: windows, ownWindows: ownWindows, normalLayer: normalLayer)
        guard !windows.isEmpty else { return listing }
        let listed = Set(windows.map(\.number))
        // Only when the list shows at least one of our wallpapers does a
        // missing one say enough by itself. Otherwise (one display, a race,
        // another session, a system that lists desktop-level windows
        // differently) the display must also show no desktop at all, rather
        // than freezing every display.
        let listsWallpapers = displays.contains { $0.wallpaperWindow.map(listed.contains) ?? false }
        for display in displays {
            if let wallpaper = display.wallpaperWindow, !listed.contains(wallpaper),
               listsWallpapers || showsNoDesktop(display, windows: windows, ownWindows: ownWindows,
                                                 normalLayer: normalLayer, menuBarLayer: menuBarLayer) {
                // Our window is not on screen there: a full-screen Space (or
                // another Space's exclusive window) owns that display.
                listing.fullScreen.insert(display.displayID)
                continue
            }
            guard appWindowsCover else { continue }
            let region = usableRegion(display, windows: windows, menuBarLayer: menuBarLayer)
            let covers = coveringWindows(of: region, windows: windows, ownWindows: ownWindows,
                                         seeThroughOwners: seeThroughOwners, normalLayer: normalLayer)
            guard isCovered(region, by: covers) else { continue }
            // Show Desktop could be uncovering a display no hit test can
            // check, so such a display keeps rendering.
            let probes = probePoints(for: display, region: region, covers: covers)
            guard !probes.isEmpty else { continue }
            listing.covered.append(CoveredDisplay(displayID: display.displayID, frame: display.frame, region: region,
                                                  covers: covers, probes: probes))
        }
        return listing
    }

    /// One check's hit tests and verdict, once `listing` has been read.
    /// `hitTest` says what a click at a point in AppKit's global coordinates
    /// would reach, as `NSWindow.windowNumber(at:belowWindowWithWindowNumber:)`
    /// does, and `pointer` where the pointer is in the same coordinates, as
    /// `NSEvent.mouseLocation` does. Both are asked only what could change
    /// the verdict, each point once:
    ///
    /// - nothing at all while the list would pause no wallpaper;
    /// - the pointer, if it rests well inside a window over a display that
    ///   would pause;
    /// - each such display's probes, starting `rotation` places in, up to the
    ///   first that finds the desktop, which alone keeps it rendering. While
    ///   the list is believed, also up to the first that finds one of its
    ///   windows where the list puts it, since Show Desktop would have moved
    ///   that window. So a paused display usually costs one hit test. A
    ///   window that stays put through Show Desktop (opened since it began,
    ///   or one it leaves alone) can hide it only from checks that start at
    ///   a probe under that window, and each check starts one probe on;
    /// - while the list is discredited, every probe, as they look for a
    ///   window moved aside back in place, and when none of a display's
    ///   finds anything either way, its `recordProbes`.
    static func check(_ listing: Listing, distrusted previous: [CGDirectDisplayID: [DesktopWindowRecord]]?,
                      primaryHeight: CGFloat, rotation: Int = 0, pointer: () -> CGPoint?,
                      hitTest: (CGPoint) -> Int) -> Evaluation {
        guard !listing.covered.isEmpty else { return decide(listing, samples: [], distrusted: previous) }
        var asked: [DesktopHitSample] = []
        func sample(at point: CGPoint) -> DesktopHitSample {
            if let known = asked.first(where: { $0.location == point }) { return known }
            let hit = DesktopHitSample(location: point,
                                       windowNumber: hitTest(appKitPoint(point, primaryHeight: primaryHeight)))
            asked.append(hit)
            return hit
        }
        var pointerSample: DesktopHitSample?
        if let location = pointer().map({ quartzPoint($0, primaryHeight: primaryHeight) }),
           listing.display(at: location)?.covers.contains(where: { deepInside($0.bounds, location) }) == true {
            pointerSample = sample(at: location)
        }
        let standing = standingSnapshots(previous, in: listing)
        var samples: [DesktopHitSample] = []
        for display in listing.covered {
            let moved = standing[display.displayID]
            var answered = false
            for point in rotated(display.probes, by: rotation) {
                let hit = sample(at: point)
                samples.append(hit)
                let found = evidence(of: hit, on: display, moved: moved, in: listing)
                answered = answered || found != .inconclusive
                if found == .contradicts || (moved == nil && findsCoverInPlace(hit, on: display)) { break }
            }
            guard let moved, !answered else { continue }
            samples += recordProbes(for: moved, on: display, in: listing).map { sample(at: $0) }
        }
        return decide(listing, samples: samples, pointer: pointerSample, distrusted: previous)
    }

    /// `points`, starting `rotation` places in.
    private static func rotated(_ points: [CGPoint], by rotation: Int) -> [CGPoint] {
        guard points.count > 1 else { return points }
        let start = (rotation % points.count + points.count) % points.count
        return Array(points[start...] + points[..<start])
    }

    /// Whether a hit test found one of the display's windows well inside the
    /// frame the list gives it.
    static func findsCoverInPlace(_ sample: DesktopHitSample, on display: CoveredDisplay) -> Bool {
        guard sample.windowNumber > 0, let id = CGWindowID(exactly: sample.windowNumber),
              let cover = display.covers.first(where: { $0.number == id }) else { return false }
        return deepInside(cover.bounds, sample.location)
    }

    /// The verdict from the list and the hit tests taken against it:
    /// `samples` at the probes, `pointer` at the pointer, and `previous` the
    /// last check's `distrustedCovers`.
    static func decide(_ listing: Listing, samples: [DesktopHitSample], pointer: DesktopHitSample? = nil,
                       distrusted previous: [CGDirectDisplayID: [DesktopWindowRecord]]? = nil) -> Evaluation {
        var result = Evaluation(hidden: listing.fullScreen)
        // The hit tests can only keep wallpapers rendering, so with nothing
        // to pause they have nothing to say.
        guard !listing.covered.isEmpty else { return result }
        let standing = standingSnapshots(previous, in: listing)
        var contradicted: Set<CGDirectDisplayID> = [], confirmed = false
        var displaced: [CGDirectDisplayID: Set<CGWindowID>] = [:]
        func noteContradiction(_ sample: DesktopHitSample, on display: CoveredDisplay) {
            contradicted.insert(display.displayID)
            displaced[display.displayID, default: []]
                .formUnion(display.covers.filter { deepInside($0.bounds, sample.location) }.map(\.number))
        }
        for sample in samples {
            // A sample on a full-screen Space's display, or one that would
            // not pause, says nothing about the rest.
            guard let display = listing.display(at: sample.location) else { continue }
            switch evidence(of: sample, on: display, moved: standing[display.displayID], in: listing) {
            case .contradicts: noteContradiction(sample, on: display)
            case .confirms: confirmed = true
            case .inconclusive: break
            }
        }
        var distrusted: [CGDirectDisplayID: [DesktopWindowRecord]]
        if !contradicted.isEmpty {
            // Show Desktop moves the windows of every display at once, so the
            // desktop found under one display's windows discredits them all.
            distrusted = Dictionary(listing.covered.map { ($0.displayID, standing[$0.displayID] ?? $0.covers) },
                                    uniquingKeysWith: { first, _ in first })
        } else if confirmed {
            // A window moved aside is back where the list puts it, so Show
            // Desktop has ended on every display (ended by keyboard, it leaves
            // every frame as it was).
            distrusted = [:]
        } else {
            // Nothing new: whatever still matches the list still stands.
            distrusted = standing
        }
        // The pointer can rest on desktop the fixed points miss, beside a
        // window opened during Show Desktop, say. But the desktop under it
        // may be a window's hole or a click-through overlay, which says
        // nothing about other displays, and a window under it may be a
        // sliver left by Show Desktop, which says nothing about its end: it
        // can only keep its own display rendering.
        if let pointer, let display = listing.display(at: pointer.location),
           evidence(of: pointer, on: display, moved: nil, in: listing) == .contradicts {
            distrusted[display.displayID] = distrusted[display.displayID] ?? standing[display.displayID] ?? display.covers
            noteContradiction(pointer, on: display)
        }
        for display in listing.covered {
            guard var moved = distrusted[display.displayID] else {
                result.hidden.insert(display.displayID)
                continue
            }
            // A window found away from its frame is believed moved too.
            let known = Set(moved.map(\.number))
            let found = displaced[display.displayID] ?? []
            moved += display.covers.filter { found.contains($0.number) && !known.contains($0.number) }
            distrusted[display.displayID] = moved
            // A display whose own hit tests found the desktop keeps
            // rendering. Otherwise the windows not believed moved, such as
            // ones opened since, are where the list puts them, and may cover
            // the display by themselves.
            guard !contradicted.contains(display.displayID) else { continue }
            let numbers = Set(moved.map(\.number))
            if isCovered(display.region, by: display.covers.filter { !numbers.contains($0.number) }) {
                result.hidden.insert(display.displayID)
            }
        }
        result.distrustedCovers = distrusted.isEmpty ? nil : distrusted
        return result
    }

    /// What a hit test says about the list. Only a point the list puts well
    /// inside an opaque window can be checked: finding the desktop there (one
    /// of our windows, or a desktop-level one such as Finder's icons)
    /// contradicts the list. While it is discredited, finding one of the
    /// windows believed moved aside (`moved`), well inside its own frame,
    /// shows that window is back. Any other window, such as one opened since,
    /// proves nothing, and nor does no window (0) or one missing from the list.
    static func evidence(of sample: DesktopHitSample, on display: CoveredDisplay,
                         moved: [DesktopWindowRecord]?, in listing: Listing) -> HitEvidence {
        guard display.covers.contains(where: { deepInside($0.bounds, sample.location) }) else { return .inconclusive }
        if exposesDesktop(hit: sample.windowNumber, windows: listing.windows, ownWindows: listing.ownWindows,
                          normalLayer: listing.normalLayer) {
            return .contradicts
        }
        guard let moved, sample.windowNumber > 0, let id = CGWindowID(exactly: sample.windowNumber),
              moved.contains(where: { $0.number == id }),
              let hit = listing.windows.first(where: { $0.number == id }),
              hit.layer == listing.normalLayer, deepInside(hit.bounds, sample.location) else { return .inconclusive }
        return .confirms
    }

    /// The discredited displays whose record still matches the list: every
    /// window believed moved aside is still listed with the frame it had, as
    /// Show Desktop changes none. A window opened since does not count
    /// against it, since one can open without bringing the others back. One
    /// that moved, resized or closed means the list has changed, and that
    /// display's list is believed again.
    static func standingSnapshots(_ previous: [CGDirectDisplayID: [DesktopWindowRecord]]?,
                                  in listing: Listing) -> [CGDirectDisplayID: [DesktopWindowRecord]] {
        guard let previous, !previous.isEmpty else { return [:] }
        let frames = Dictionary(listing.windows.map { ($0.number, $0.bounds) }, uniquingKeysWith: { first, _ in first })
        return previous.filter { id, moved in
            listing.covered.contains { $0.displayID == id } && moved.allSatisfy { frames[$0.number] == $0.bounds }
        }
    }

    /// Whether `point` lies at least `noticeableGap` inside `rect`. Nearer a
    /// window's edge a hit test can reach the desktop with nothing moved:
    /// through rounded corners and shadows, or the click-through border
    /// windows some utilities draw just outside other windows.
    static func deepInside(_ rect: CGRect, _ point: CGPoint) -> Bool {
        rect.insetBy(dx: noticeableGap, dy: noticeableGap).contains(point)
    }

    /// Where a check starts from on a display, in Quartz coordinates: the
    /// middle of the lyric stage, which is centred on the display, and the
    /// middle of each quarter of AppKit's usable area. Show Desktop leaves
    /// windows showing only as slivers along the edges, so while it is on
    /// these find the desktop wherever the list puts a window over them.
    static func samplePoints(for display: DesktopDisplayRecord) -> [CGPoint] {
        let area = appKitUsableArea(display)
        guard area.width > 1, area.height > 1 else { return [] }
        let stage = CGPoint(x: display.frame.midX, y: display.frame.midY)
        let quarters: [(CGFloat, CGFloat)] = [(0.25, 0.25), (0.75, 0.25), (0.25, 0.75), (0.75, 0.75)]
        let points = [stage] + quarters.map { CGPoint(x: area.minX + area.width * $0.0, y: area.minY + area.height * $0.1) }
        return points.filter { area.contains($0) }
    }

    /// The middle of a display, a fifth of it in from each edge: where the
    /// sample points lie, out of reach of the slivers Show Desktop leaves.
    static func middle(of frame: CGRect) -> CGRect {
        frame.insetBy(dx: frame.width / 5, dy: frame.height / 5)
    }

    /// Where a check hit-tests a display it would pause, in Quartz
    /// coordinates: each sample point the list puts well inside one of
    /// `covers`, and for each other one the nearest point in the display's
    /// middle that lies twice that far inside one. Windows tiled in equal
    /// columns or rows meet right under the sample points, where a hit test
    /// says nothing about either, so those points move into one of them.
    static func probePoints(for display: DesktopDisplayRecord, region: CGRect,
                            covers: [DesktopWindowRecord]) -> [CGPoint] {
        let middle = middle(of: display.frame).intersection(region)
        var probes: [CGPoint] = []
        for point in samplePoints(for: display) {
            let probe = region.contains(point) && covers.contains(where: { deepInside($0.bounds, point) })
                ? point : nearestProbe(to: point, covers: covers, within: middle, region: region)
            if let probe, !probes.contains(probe) { probes.append(probe) }
        }
        return probes
    }

    private static func nearestProbe(to point: CGPoint, covers: [DesktopWindowRecord], within middle: CGRect,
                                     region: CGRect) -> CGPoint? {
        guard !middle.isNull else { return nil }
        var nearest: (point: CGPoint, distance: CGFloat)?
        for cover in covers {
            let target = cover.bounds.insetBy(dx: 2 * noticeableGap, dy: 2 * noticeableGap).intersection(middle)
            guard !target.isNull else { continue }
            let candidate = CGPoint(x: min(max(point.x, target.minX), target.maxX),
                                    y: min(max(point.y, target.minY), target.maxY))
            guard region.contains(candidate), deepInside(cover.bounds, candidate) else { continue }
            let distance = hypot(candidate.x - point.x, candidate.y - point.y)
            if distance < nearest?.distance ?? .infinity { nearest = (candidate, distance) }
        }
        return nearest?.point
    }

    /// Where a hit test can still tell whether the windows believed moved
    /// aside on a display are back once windows opened since sit over all of
    /// its probes (a file opened from the desktop, say): points well inside
    /// one of them that no window listed in front of it comes near, in
    /// Quartz coordinates. There a hit test finds either that window, back
    /// in place, or the desktop. One point in the display's middle will do,
    /// like a probe. Nearer the edges, where that window's own sliver may lie
    /// while Show Desktop is on, two points in diagonally opposite quarters
    /// of the display are asked instead: a sliver along one edge, or in one
    /// corner, reaches at most one of them, and the desktop found at the
    /// other outweighs it. Empty when there is no such place.
    static func recordProbes(for moved: [DesktopWindowRecord], on display: CoveredDisplay,
                             in listing: Listing) -> [CGPoint] {
        let frame = display.frame
        let middle = middle(of: frame)
        /// How far a point lies from the display's nearest edge.
        func depth(_ point: CGPoint) -> CGFloat {
            min(point.x - frame.minX, frame.maxX - point.x, point.y - frame.minY, frame.maxY - point.y)
        }
        // Only the first few, to bound the work on the main thread.
        for window in moved.prefix(4) where display.covers.contains(where: { $0.number == window.number }) {
            let cells = uncoveredCells(of: window, on: display, in: listing)
            let central = cells.map { $0.intersection(middle) }
                .filter { !$0.isNull && $0.width > 0 && $0.height > 0 }
                .map { CGPoint(x: $0.midX, y: $0.midY) }
            if let point = central.max(by: { depth($0) < depth($1) }) { return [point] }
            // Cells never straddle the display's midlines, so each lies in one quarter.
            func deepest(left: Bool, top: Bool) -> CGPoint? {
                cells.map { CGPoint(x: $0.midX, y: $0.midY) }
                    .filter { ($0.x < frame.midX) == left && ($0.y < frame.midY) == top }
                    .max(by: { depth($0) < depth($1) })
            }
            if let topLeft = deepest(left: true, top: true), let bottomRight = deepest(left: false, top: false) {
                return [topLeft, bottomRight]
            }
            if let topRight = deepest(left: false, top: true), let bottomLeft = deepest(left: true, top: false) {
                return [topRight, bottomLeft]
            }
        }
        return []
    }

    /// The parts of `window`, well inside its frame and in the display's
    /// usable region, that no window listed in front of it, from the normal
    /// layer up to the Dock's, comes within `noticeableGap` of: cells of the
    /// grid their edges and the display's midlines draw. Empty when too many
    /// windows lie over it to be worth the work.
    private static func uncoveredCells(of window: DesktopWindowRecord, on display: CoveredDisplay,
                                       in listing: Listing) -> [CGRect] {
        guard let index = listing.windows.firstIndex(where: { $0.number == window.number }) else { return [] }
        let area = window.bounds.insetBy(dx: 2 * noticeableGap, dy: 2 * noticeableGap).intersection(display.region)
        guard !area.isNull, area.width > 0, area.height > 0 else { return [] }
        // The Dock and menu bar span the display, mostly letting clicks through.
        let dockLayer = Int(CGWindowLevelForKey(.dockWindow))
        let blockers = listing.windows[..<index]
            .filter { $0.layer >= listing.normalLayer && $0.layer < dockLayer && !listing.ownWindows.contains($0.number) }
            .map { $0.bounds.insetBy(dx: -noticeableGap, dy: -noticeableGap) }
            .filter { $0.intersects(area) }
        guard blockers.count <= 16 else { return [] }
        func breaks(_ edges: [CGFloat], from low: CGFloat, to high: CGFloat) -> [CGFloat] {
            Set([low, high] + edges.filter { $0 > low && $0 < high }).sorted()
        }
        let xs = breaks(blockers.flatMap { [$0.minX, $0.maxX] } + [display.frame.midX], from: area.minX, to: area.maxX)
        let ys = breaks(blockers.flatMap { [$0.minY, $0.maxY] } + [display.frame.midY], from: area.minY, to: area.maxY)
        var cells: [CGRect] = []
        for (left, right) in zip(xs, xs.dropFirst()) {
            for (top, bottom) in zip(ys, ys.dropFirst()) {
                let cell = CGRect(x: left, y: top, width: right - left, height: bottom - top)
                if !blockers.contains(where: { $0.contains(CGPoint(x: cell.midX, y: cell.midY)) }) { cells.append(cell) }
            }
        }
        return cells
    }

    /// AppKit's usable area for a display: its visible frame, or the whole
    /// frame when that is unusable.
    private static func appKitUsableArea(_ display: DesktopDisplayRecord) -> CGRect {
        let frame = display.frame
        let region = display.visibleFrame.width > 1 && display.visibleFrame.height > 1
            ? display.visibleFrame.intersection(frame) : frame
        return region.isNull ? frame : region
    }

    /// The part of a display where a covered wallpaper would be noticed:
    /// AppKit's usable area, less a menu bar along the display's top edge.
    static func usableRegion(_ display: DesktopDisplayRecord, windows: [DesktopWindowRecord],
                             menuBarLayer: Int) -> CGRect {
        let frame = display.frame
        var region = appKitUsableArea(display)
        // A menu bar is a full-width strip at the main-menu level, flush with
        // the top of its display. The Dock, whose window spans the whole
        // display, is never used; AppKit already leaves it out.
        for window in windows where window.layer == menuBarLayer {
            let strip = window.bounds.intersection(frame)
            guard !strip.isNull, abs(window.bounds.minY - frame.minY) <= 1,
                  strip.width >= frame.width * 0.9, strip.height > 0,
                  strip.height <= min(menuBarMaximumHeight, frame.height / 4),
                  strip.maxY > region.minY else { continue }
            let trimmed = region.maxY - strip.maxY
            guard trimmed > 1 else { continue }
            region = CGRect(x: region.minX, y: strip.maxY, width: region.width, height: trimmed)
        }
        return region
    }

    /// Whether a display shows no desktop at all: none of our windows and
    /// nothing from the desktop picture's level up to the normal layer is
    /// listed on it -- not our wallpaper or controls, Finder's icons, the
    /// window manager's desktop window or a desktop picture -- and ordinary
    /// windows fill it. Only a full-screen Space should look like that: on
    /// the desktop Space every display lists several such windows besides
    /// ours. The window server also lists a backstop below the desktop
    /// picture on every display, once for all its Spaces, so it may well be
    /// listed in a full-screen Space too and is not counted, nor is anything
    /// else below that level. Were a full-screen Space to list one of the
    /// others, this would never hold and the wallpaper would keep rendering,
    /// as before. (No real full-screen Space's list has been read; the tests
    /// use lists made up from this Mac's desktop.) Nothing of the desktop is
    /// drawn behind a full-screen window, so this holds whoever owns the
    /// window and under Stage Manager. The strip along the top where the menu
    /// bar or a camera housing sits is not needed: full-screen windows on a
    /// notched display stop below it, and a hidden menu bar need not be listed.
    static func showsNoDesktop(_ display: DesktopDisplayRecord, windows: [DesktopWindowRecord],
                               ownWindows: Set<CGWindowID>, normalLayer: Int, menuBarLayer: Int,
                               desktopLayer: Int = Int(CGWindowLevelForKey(.desktopWindow))) -> Bool {
        let showsDesktop = windows.contains { window in
            (ownWindows.contains(window.number) || (window.layer >= desktopLayer && window.layer < normalLayer))
                && window.bounds.intersects(display.frame)
        }
        guard !showsDesktop else { return false }
        let usable = usableRegion(display, windows: windows, menuBarLayer: menuBarLayer)
        let top = max(usable.minY, display.frame.minY + menuBarMaximumHeight)
        guard usable.maxY - top > 1 else { return false }
        let region = CGRect(x: usable.minX, y: top, width: usable.width, height: usable.maxY - top)
        return isCovered(region, by: coveringWindows(of: region, windows: windows, ownWindows: ownWindows,
                                                     seeThroughOwners: [], normalLayer: normalLayer))
    }

    /// Opaque ordinary windows over `region`, largest first and bounded:
    /// the C side reads at most `VC_MAX_COVERS`, and dropping the smallest
    /// can only under-report coverage.
    static func coveringWindows(of region: CGRect, windows: [DesktopWindowRecord], ownWindows: Set<CGWindowID>,
                                seeThroughOwners: Set<pid_t>, normalLayer: Int) -> [DesktopWindowRecord] {
        guard region.width > 1, region.height > 1 else { return [] }
        return windows
            .filter { $0.layer == normalLayer && $0.alpha >= opaqueAlpha && !ownWindows.contains($0.number)
                && !($0.ownerPID != 0 && seeThroughOwners.contains($0.ownerPID)) }
            .compactMap { record -> (record: DesktopWindowRecord, area: CGFloat)? in
                let overlap = record.bounds.intersection(region)
                guard !overlap.isNull, overlap.width >= 1, overlap.height >= 1 else { return nil }
                return (record, overlap.width * overlap.height)
            }
            .sorted { $0.area != $1.area ? $0.area > $1.area : $0.record.number < $1.record.number }
            .prefix(Int(VC_MAX_COVERS))
            .map(\.record)
    }

    static func isCovered(_ region: CGRect, by covers: [DesktopWindowRecord]) -> Bool {
        guard region.width > 1, region.height > 1, !covers.isEmpty else { return false }
        let boxes = covers.map {
            VCBox(x: Double($0.bounds.minX), y: Double($0.bounds.minY),
                  width: Double($0.bounds.width), height: Double($0.bounds.height))
        }
        let box = VCBox(x: Double(region.minX), y: Double(region.minY), width: Double(region.width), height: Double(region.height))
        return boxes.withUnsafeBufferPointer {
            vc_region_covered(box, $0.baseAddress, Int32($0.count), Double(noticeableGap), maximumExposedFraction)
        } != 0
    }

    /// Whether a click at a sampled point would land on the desktop: one of
    /// our windows, or a desktop-level window such as Finder's icons. No
    /// window (0) proves nothing -- AppKit also answers that before it can
    /// ask the window server -- so it never vouches for a display.
    static func exposesDesktop(hit number: Int, windows: [DesktopWindowRecord], ownWindows: Set<CGWindowID>,
                               normalLayer: Int) -> Bool {
        guard number > 0, let id = CGWindowID(exactly: number) else { return false }
        if ownWindows.contains(id) { return true }
        guard let hit = windows.first(where: { $0.number == id }) else { return false }
        return hit.layer < normalLayer
    }

    /// The running processes whose windows never count as covering.
    static func seeThroughOwners(among apps: [(bundleIdentifier: String?, pid: pid_t)]) -> Set<pid_t> {
        Set(apps.compactMap { app in app.bundleIdentifier.map(seeThroughApps.contains) == true ? app.pid : nil })
    }

    /// Stage Manager shows other apps' windows as thumbnails in its strip,
    /// where their listed frames need not say what they cover, so the window
    /// rule is switched off rather than trusted. Read from its preferences,
    /// as no API reports it.
    static var stageManagerEnabled: Bool {
        guard let value = CFPreferencesCopyAppValue("GloballyEnabled" as CFString,
                                                    "com.apple.WindowManager" as CFString) else { return false }
        return (value as? Bool) ?? ((value as? NSNumber)?.boolValue ?? false)
    }

    /// The displays as coverage sees them, from AppKit's screens: frames in
    /// AppKit's global coordinates, flipped about the primary display.
    static func displayRecords(_ screens: [(id: CGDirectDisplayID, frame: CGRect, visibleFrame: CGRect)],
                               primaryHeight: CGFloat,
                               wallpapers: [CGDirectDisplayID: CGWindowID]) -> [DesktopDisplayRecord] {
        screens.map { screen in
            DesktopDisplayRecord(displayID: screen.id,
                                 frame: quartzRect(screen.frame, primaryHeight: primaryHeight),
                                 visibleFrame: quartzRect(screen.visibleFrame, primaryHeight: primaryHeight),
                                 wallpaperWindow: wallpapers[screen.id])
        }
    }

    /// AppKit's global space has its origin at the bottom left of the
    /// primary display; Quartz's at the top left.
    static func quartzRect(_ rect: CGRect, primaryHeight: CGFloat) -> CGRect {
        CGRect(x: rect.minX, y: primaryHeight - rect.maxY, width: rect.width, height: rect.height)
    }

    static func quartzPoint(_ point: CGPoint, primaryHeight: CGFloat) -> CGPoint {
        CGPoint(x: point.x, y: primaryHeight - point.y)
    }

    /// The same flip, back from Quartz to AppKit.
    static func appKitPoint(_ point: CGPoint, primaryHeight: CGFloat) -> CGPoint {
        CGPoint(x: point.x, y: primaryHeight - point.y)
    }
}

/// Whether each wallpaper can be seen, and whether anything can be seen at
/// all. Window coverage is evaluated off the main thread from the window list,
/// debounced after Space switches, app activation, window drags and
/// occlusion changes, with a slow fallback poll for keyboard-driven changes
/// (and for Show Desktop, which announces nothing: while the list would pause
/// a wallpaper, each check's hit tests on the displays it would pause are what
/// notice the desktop coming into view, and the windows coming back when it
/// ends).
@MainActor
final class DesktopCoverageMonitor: ObservableObject {
    static let shared = DesktopCoverageMonitor()

    /// Displays whose wallpaper is covered or on another Space. Only
    /// evaluated while "Pause fully covered wallpapers" is on.
    @Published private(set) var hiddenDisplays: Set<CGDirectDisplayID> = []
    /// Displays asleep, screen locked, screen saver running or the user
    /// switched out: no wallpaper is visible anywhere.
    @Published private(set) var systemHidden = false

    private var displaysAsleep = false { didSet { updateSystemHidden() } }
    private var screenLocked = false { didSet { updateSystemHidden() } }
    private var screenSaverRunning = false { didSet { updateSystemHidden() } }
    private var sessionInactive = false { didSet { updateSystemHidden() } }

    private var started = false
    private var evaluatesCoverage = false
    private var observers: [(NotificationCenter, NSObjectProtocol)] = []
    private var mouseMonitor: Any?
    private var settingsCancellable: AnyCancellable?
    private var pendingCheck: DispatchWorkItem?
    private var pollTimer: Timer?
    private var generation = 0
    /// Turns which probe each check starts from; see `DesktopCoverage.check`.
    private var checks = 0
    /// Set while hit tests discredit the window list; see `DesktopCoverage`.
    private var distrustedCovers: [CGDirectDisplayID: [DesktopWindowRecord]]?
    /// The running apps whose windows never cover. Reading every running app
    /// costs about as much as a hit test, so it is kept until an app
    /// launches, quits or comes to the front.
    private var seeThroughCache: Set<pid_t>?
    private let queue = DispatchQueue(label: "local.verso.custom.desktop-coverage", qos: .utility)

    func start() {
        guard !started else { return }
        started = true
        let workspace = NSWorkspace.shared.notificationCenter
        observe(workspace, NSWorkspace.screensDidSleepNotification) { $0.displaysAsleep = true }
        observe(workspace, NSWorkspace.screensDidWakeNotification) { monitor in
            monitor.displaysAsleep = false
            // Waking also ends a screen saver whose stop notice was missed.
            monitor.screenSaverRunning = false
            monitor.setNeedsUpdate()
        }
        observe(workspace, NSWorkspace.sessionDidResignActiveNotification) { $0.sessionInactive = true }
        observe(workspace, NSWorkspace.sessionDidBecomeActiveNotification) { monitor in
            monitor.sessionInactive = false
            monitor.setNeedsUpdate()
        }
        // Another Space shows other windows; the next check decides afresh.
        observe(workspace, NSWorkspace.activeSpaceDidChangeNotification) { monitor in
            monitor.forgetDistrust()
            monitor.setNeedsUpdate()
        }
        // Bringing an app to the front can end Show Desktop (clicking a
        // window's sliver) or not (clicking the desktop, or opening a file
        // from it). The windows believed moved aside are kept either way:
        // the next check's hit tests find them back in place or not, and a
        // window the app opens meanwhile cannot pass for one of them.
        for name in [NSWorkspace.didActivateApplicationNotification, NSWorkspace.didLaunchApplicationNotification,
                     NSWorkspace.didTerminateApplicationNotification] {
            observe(workspace, name) { monitor in
                monitor.seeThroughCache = nil
                monitor.setNeedsUpdate()
            }
        }
        for name in [NSWorkspace.didHideApplicationNotification, NSWorkspace.didUnhideApplicationNotification] {
            observe(workspace, name) { $0.setNeedsUpdate() }
        }
        observe(NotificationCenter.default, NSApplication.didChangeScreenParametersNotification) { $0.setNeedsUpdate() }
        let distributed = DistributedNotificationCenter.default()
        observe(distributed, Notification.Name("com.apple.screenIsLocked")) { $0.screenLocked = true }
        observe(distributed, Notification.Name("com.apple.screenIsUnlocked")) { monitor in
            monitor.screenLocked = false
            monitor.screenSaverRunning = false
            monitor.setNeedsUpdate()
        }
        observe(distributed, Notification.Name("com.apple.screensaver.didstart")) { $0.screenSaverRunning = true }
        observe(distributed, Notification.Name("com.apple.screensaver.didstop")) { monitor in
            monitor.screenSaverRunning = false
            monitor.setNeedsUpdate()
        }
        // The end of a window drag, resize or click-to-front. Mouse events can
        // be observed globally without Accessibility access; only the fact of
        // the release is used.
        mouseMonitor = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseUp]) { _ in
            Task { @MainActor in DesktopCoverageMonitor.shared.setNeedsUpdate(delay: 0.12) }
        }
        settingsCancellable = Settings.shared.$pauseWhenCovered.removeDuplicates().sink { [weak self] enabled in
            Task { @MainActor [weak self] in self?.setEvaluatesCoverage(enabled) }
        }
    }

    /// Schedules a coverage check, coalescing bursts of triggers.
    func setNeedsUpdate(delay: TimeInterval = 0.25) {
        guard started else { return }
        pendingCheck?.cancel()
        let work = DispatchWorkItem { [weak self] in
            Task { @MainActor [weak self] in self?.refresh() }
        }
        pendingCheck = work
        DispatchQueue.main.asyncAfter(deadline: .now() + delay, execute: work)
    }

    private func setEvaluatesCoverage(_ enabled: Bool) {
        evaluatesCoverage = enabled
        if !enabled {
            generation += 1
            distrustedCovers = nil
            if !hiddenDisplays.isEmpty { hiddenDisplays = [] }
        }
        refresh()
    }

    private func forgetDistrust() {
        guard distrustedCovers != nil else { return }
        // A check already in flight was made with it; discard it.
        generation += 1
        distrustedCovers = nil
    }

    private func currentSeeThroughOwners() -> Set<pid_t> {
        if let seeThroughCache { return seeThroughCache }
        let owners = DesktopCoverage.seeThroughOwners(
            among: NSWorkspace.shared.runningApplications.map { ($0.bundleIdentifier, $0.processIdentifier) })
        seeThroughCache = owners
        return owners
    }

    private func refresh() {
        pendingCheck = nil
        healSystemState()
        reschedulePoll()
        guard evaluatesCoverage, !systemHidden else { return }
        let screens = NSScreen.screens
        let primaryHeight = screens.first?.frame.height ?? 0
        let displays = DesktopCoverage.displayRecords(
            screens.compactMap { screen in
                screen.versoDisplayID.map { (id: $0, frame: screen.frame, visibleFrame: screen.visibleFrame) }
            },
            primaryHeight: primaryHeight, wallpapers: DesktopWallpaperController.shared.wallpaperWindows)
        let own = DesktopWallpaperController.shared.windowIDs.union(ControlPanelController.shared.windowIDs)
            .union(FloatingLyricsController.shared.windowIDs)
        guard !displays.isEmpty, !own.isEmpty else { return }
        let seeThrough = currentSeeThroughOwners()
        let previous = distrustedCovers
        generation += 1
        let token = generation
        queue.async {
            let appWindowsCover = !DesktopCoverage.stageManagerEnabled
            let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly], kCGNullWindowID) as? [[String: Any]] ?? []
            let listing = DesktopCoverage.listing(
                displays, windows: DesktopCoverage.records(from: list), ownWindows: own,
                appWindowsCover: appWindowsCover, seeThroughOwners: seeThrough)
            Task { @MainActor in
                let monitor = DesktopCoverageMonitor.shared
                // Nothing is asked of a locked or sleeping screen either.
                guard monitor.generation == token, monitor.evaluatesCoverage, !monitor.systemHidden else { return }
                // What a click would reach, asked only now that the list says
                // which wallpapers would pause, and only where that could
                // change. The window server hit-tests windows where they are
                // drawn, so this finds the desktop Show Desktop uncovers
                // although no window frame has moved.
                monitor.checks &+= 1
                let evaluation = DesktopCoverage.check(listing, distrusted: previous, primaryHeight: primaryHeight,
                                                       rotation: monitor.checks,
                                                       pointer: { NSEvent.mouseLocation }) { point in
                    NSWindow.windowNumber(at: point, belowWindowWithWindowNumber: 0)
                }
                monitor.distrustedCovers = evaluation.distrustedCovers
                if monitor.hiddenDisplays != evaluation.hidden { monitor.hiddenDisplays = evaluation.hidden }
                monitor.reschedulePoll()
            }
        }
    }

    /// A fallback for changes nothing announces (keyboard window management,
    /// a closed window, the start and end of Show Desktop): quicker while
    /// something is hidden, so uncovering a wallpaper resumes it promptly,
    /// and while the window list is discredited, so wallpapers left rendering
    /// behind windows that come back from Show Desktop pause again promptly.
    private func reschedulePoll() {
        let needsPoll = evaluatesCoverage || screenLocked || screenSaverRunning
        let interval: TimeInterval = !hiddenDisplays.isEmpty || systemHidden || distrustedCovers != nil ? 1.0 : 2.5
        if !needsPoll || displaysAsleep {
            pollTimer?.invalidate(); pollTimer = nil
            return
        }
        if let pollTimer, pollTimer.isValid, abs(pollTimer.timeInterval - interval) < 0.01 { return }
        pollTimer?.invalidate()
        let timer = Timer(timeInterval: interval, repeats: true) { _ in
            Task { @MainActor in DesktopCoverageMonitor.shared.refresh() }
        }
        timer.tolerance = interval * 0.25
        RunLoop.main.add(timer, forMode: .common)
        pollTimer = timer
    }

    /// Distributed lock and screen-saver notices are not guaranteed to come
    /// in pairs. Confirm a "hidden" state against the session before trusting
    /// it, so a missed notice can never freeze the wallpaper for good.
    private func healSystemState() {
        guard screenLocked || screenSaverRunning || sessionInactive else { return }
        let session = CGSessionCopyCurrentDictionary() as? [String: Any]
        if screenLocked, let session, (session["CGSSessionScreenIsLocked"] as? Bool ?? false) == false {
            screenLocked = false
        }
        if sessionInactive, let onConsole = session?[kCGSessionOnConsoleKey as String] as? Bool, onConsole {
            sessionInactive = false
        }
        if screenSaverRunning,
           NSRunningApplication.runningApplications(withBundleIdentifier: "com.apple.ScreenSaver.Engine").isEmpty {
            screenSaverRunning = false
        }
    }

    private func updateSystemHidden() {
        let hidden = displaysAsleep || screenLocked || screenSaverRunning || sessionInactive
        if hidden != systemHidden { systemHidden = hidden }
    }

    private func observe(_ center: NotificationCenter, _ name: Notification.Name,
                         _ action: @escaping @MainActor (DesktopCoverageMonitor) -> Void) {
        let token = center.addObserver(forName: name, object: nil, queue: .main) { _ in
            Task { @MainActor in action(DesktopCoverageMonitor.shared) }
        }
        observers.append((center, token))
    }
}
