import AppKit
import CoreText
import Network

/// Downloads a family from Google Fonts and registers it for this process.
///
/// Two details make this work without an API key or an install:
///
/// - Google's `css2` endpoint serves WOFF2 to modern browsers, which CoreText
///   cannot register. Asking with an old user agent makes it fall back to
///   plain TTF, which CoreText handles.
/// - `CTFontManagerRegisterFontsForURL` with `.process` scope makes the font
///   available to this app alone -- no admin prompt, and nothing is added to
///   the user's installed fonts.
///
/// A family fetched once is kept on disk and registered from there on later
/// launches, so Manrope and a chosen family keep working offline.
actor GoogleFontsProvider {
    static let shared = GoogleFontsProvider()

    /// Posted on the main queue when the network comes back after a request
    /// failed for want of it, so a waiting family can be fetched again.
    static let networkReturned = Notification.Name("LunalyrGoogleFontsNetworkReturned")

    struct LoadedFont: Equatable {
        var regular: String
        var bold: String?
        /// The spelling Google knows the family by, when it differs from the
        /// one asked for.
        var family: String?
    }

    enum LoadFailure: Error, Equatable {
        /// Google Fonts has no family by that name.
        case notFound
        /// Google Fonts could not be reached at all.
        case offline
        /// Google answered, but not with a usable sheet or file.
        case unavailable
        /// The file arrived but macOS could not use it as a font.
        case unusable
    }

    private var loaded: [String: LoadedFont] = [:]
    private var inFlight: [String: Task<Result<LoadedFont, LoadFailure>, Never>] = [:]
    private let cacheDirectory: URL
    private let session: URLSession
    private var pathMonitor: NWPathMonitor?
    /// `nil` until the path monitor's first report.
    private var pathSatisfied: Bool?
    private var networkWaiters: [CheckedContinuation<Void, Never>] = []

    /// Tests pass a temporary directory and a stubbed session.
    init(cacheDirectory: URL = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/Verso/Fonts"),
         session: URLSession = .shared) {
        self.cacheDirectory = cacheDirectory
        self.session = session
    }

    /// The family, or `nil` if it cannot be had. While the Mac is offline
    /// this waits for the connection to return and tries again, so a launch
    /// without a network still ends with the font registered.
    func font(family: String) async -> LoadedFont? {
        while true {
            switch await load(family: family) {
            case .success(let font): return font
            case .failure(.offline): await waitForNetwork()
            case .failure: return nil
            }
        }
    }

    /// One attempt: the cached copy if there is one, otherwise a download.
    /// Concurrent requests for the same family share the work.
    func load(family: String) async -> Result<LoadedFont, LoadFailure> {
        if let hit = loaded[family] { return .success(hit) }
        if let running = inFlight[family] { return await running.value }
        let task = Task { await fetch(family: family) }
        inFlight[family] = task
        let result = await task.value
        inFlight[family] = nil
        if case .success(let font) = result {
            loaded[family] = font
            // New font files are on disk and registered: let anything that
            // fell back to a system face (AppFont's default) try again now.
            NotificationCenter.default.post(name: .fontFilesAvailable, object: nil)
        } else if case .failure(.offline) = result {
            startPathMonitor()
        }
        return result
    }

    /// The family, if a previous download left it on disk -- never touches
    /// the network.
    func cachedFont(family: String) -> LoadedFont? {
        if let hit = loaded[family] { return hit }
        guard let cached = registerCached(family: family) else { return nil }
        loaded[family] = cached
        return cached
    }

    /// Whether `family` could be a Google Fonts name: their names use Latin
    /// letters, digits and spaces only, which also keeps the request and the
    /// cache file name well formed.
    static func isPlausibleFamilyName(_ family: String) -> Bool {
        !family.isEmpty && family.count <= 64 && family.unicodeScalars.allSatisfy {
            ($0.isASCII && CharacterSet.alphanumerics.contains($0)) || $0 == " " || $0 == "-"
        }
    }

    private func fetch(family: String) async -> Result<LoadedFont, LoadFailure> {
        if let cached = registerCached(family: family) { return .success(cached) }
        guard Self.isPlausibleFamilyName(family) else { return .failure(.notFound) }
        var spelling = family
        var listing = await ttfURLs(family: family)
        // Google's names are case-sensitive and nearly all title case, so a
        // typed "open sans" is asked for again as "Open Sans".
        let titled = Self.titleCased(family)
        if listing == .failure(.notFound), titled != family {
            if let cached = registerCached(family: titled) {
                return .success(LoadedFont(regular: cached.regular, bold: cached.bold, family: titled))
            }
            listing = await ttfURLs(family: titled)
            spelling = titled
        }
        let urls: [URL]
        switch listing {
        case .success(let found): urls = found
        case .failure(let failure): return .failure(failure)
        }

        var regularName: String?
        var boldName: String?
        var failure = LoadFailure.unusable
        for (index, url) in urls.prefix(2).enumerated() {
            switch await install(url: url, family: spelling, index: index) {
            case .success(let name): if index == 0 { regularName = name } else { boldName = name }
            case .failure(let problem): if index == 0 { failure = problem }
            }
        }

        guard let regularName else { return .failure(failure) }
        return .success(LoadedFont(regular: regularName, bold: boldName, family: spelling == family ? nil : spelling))
    }

    /// Capitalises the first letter of each word and leaves the rest as
    /// typed, so "ibm Plex mono" becomes "Ibm Plex Mono" but "IBM plex" keeps
    /// its capitals.
    static func titleCased(_ family: String) -> String {
        family.split(separator: " ").map { $0.prefix(1).uppercased() + $0.dropFirst() }.joined(separator: " ")
    }

    private func cacheFile(family: String, index: Int) -> URL {
        cacheDirectory.appendingPathComponent("\(family.replacingOccurrences(of: " ", with: "-"))-\(index).ttf")
    }

    /// Registers a family left by an earlier download: regular, then bold if
    /// that was fetched too.
    private func registerCached(family: String) -> LoadedFont? {
        guard Self.isPlausibleFamilyName(family) else { return nil }
        let regular = cacheFile(family: family, index: 0)
        guard FileManager.default.fileExists(atPath: regular.path), let regularName = register(regular) else { return nil }
        let bold = cacheFile(family: family, index: 1)
        let boldName = FileManager.default.fileExists(atPath: bold.path) ? register(bold) : nil
        return LoadedFont(regular: regularName, bold: boldName)
    }

    /// Asks Google for regular and bold cuts, in TTF.
    private func ttfURLs(family: String) async -> Result<[URL], LoadFailure> {
        // `isPlausibleFamilyName` has already limited the name to letters,
        // digits, spaces and hyphens, so it needs no escaping beyond this.
        let encoded = family.replacingOccurrences(of: " ", with: "+")
        guard let url = URL(string: "https://fonts.googleapis.com/css2?family=\(encoded):wght@400;700") else {
            return .failure(.notFound)
        }
        var request = URLRequest(url: url)
        // Deliberately archaic: a modern agent gets WOFF2 back, which
        // CoreText cannot use.
        request.setValue("Mozilla/4.0", forHTTPHeaderField: "User-Agent")
        request.timeoutInterval = 15

        do {
            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else { return .failure(.unavailable) }
            // An unknown family is a 400 from css2.
            if http.statusCode == 400 || http.statusCode == 404 { return .failure(.notFound) }
            guard http.statusCode == 200, let css = String(data: data, encoding: .utf8) else {
                return .failure(.unavailable)
            }

            guard let regex = try? NSRegularExpression(pattern: #"https://[^)]+?\.ttf"#) else { return .failure(.unavailable) }
            let range = NSRange(css.startIndex..., in: css)

            // The sheet repeats each weight once per unicode subset; one URL
            // per distinct file is enough.
            var seen: [URL] = []
            for match in regex.matches(in: css, range: range) {
                guard let matchRange = Range(match.range, in: css),
                      let candidate = URL(string: String(css[matchRange]))
                else { continue }
                if !seen.contains(candidate) { seen.append(candidate) }
            }
            return seen.isEmpty ? .failure(.unavailable) : .success(seen)
        } catch {
            return .failure(Self.classify(error))
        }
    }

    private func install(url: URL, family: String, index: Int) async -> Result<String, LoadFailure> {
        let destination = cacheFile(family: family, index: index)
        if FileManager.default.fileExists(atPath: destination.path) {
            if let name = register(destination) { return .success(name) }
            // A damaged leftover would otherwise block every later attempt.
            try? FileManager.default.removeItem(at: destination)
        }
        do {
            let (data, response) = try await session.data(from: url)
            guard let http = response as? HTTPURLResponse, http.statusCode == 200, data.count > 1000
            else { return .failure(.unavailable) }
            try FileManager.default.createDirectory(at: cacheDirectory, withIntermediateDirectories: true)
            // Atomic, so an interrupted write never leaves half a font behind.
            try data.write(to: destination, options: .atomic)
        } catch {
            return .failure(Self.classify(error))
        }
        guard let name = register(destination) else {
            try? FileManager.default.removeItem(at: destination)
            return .failure(.unusable)
        }
        return .success(name)
    }

    /// Connection-level failures mean "try again once online"; anything
    /// else is Google's or the file's problem.
    static func classify(_ error: Error) -> LoadFailure {
        guard let error = error as? URLError else { return .unavailable }
        switch error.code {
        case .notConnectedToInternet, .networkConnectionLost, .dataNotAllowed, .internationalRoamingOff,
             .cannotFindHost, .cannotConnectToHost, .dnsLookupFailed, .timedOut, .callIsActive:
            return .offline
        default:
            return .unavailable
        }
    }

    /// Registers the file and reports the PostScript name SwiftUI needs.
    private func register(_ url: URL) -> String? {
        var error: Unmanaged<CFError>?
        let registered = CTFontManagerRegisterFontsForURL(url as CFURL, .process, &error)
        // An "already registered" failure is fine -- the name still resolves.
        if !registered, error != nil {
            _ = error?.takeRetainedValue()
        }
        guard let descriptors = CTFontManagerCreateFontDescriptorsFromURL(url as CFURL) as? [CTFontDescriptor],
              let first = descriptors.first,
              let name = CTFontDescriptorCopyAttribute(first, kCTFontNameAttribute) as? String
        else { return nil }
        // Confirm AppKit can actually instantiate it before handing the name on.
        return NSFont(name: name, size: 12) != nil ? name : nil
    }

    // MARK: - Waiting for the network

    /// Suspends until the network comes back: the next time the path becomes
    /// usable after being unusable. A network that is up but cannot reach
    /// Google never resumes this by itself, so a failing request cannot spin.
    func waitForNetwork() async {
        await withCheckedContinuation { continuation in
            networkWaiters.append(continuation)
            startPathMonitor()
        }
    }

    /// Kept running once started; offline failures are rare enough that one
    /// monitor for the life of the app costs nothing.
    private func startPathMonitor() {
        guard pathMonitor == nil else { return }
        let monitor = NWPathMonitor()
        monitor.pathUpdateHandler = { [weak self] path in
            let satisfied = path.status == .satisfied
            Task { await self?.pathUpdated(satisfied: satisfied) }
        }
        monitor.start(queue: DispatchQueue(label: "Lunalyr.GoogleFonts.network", qos: .utility))
        pathMonitor = monitor
    }

    private func pathUpdated(satisfied: Bool) {
        let previous = pathSatisfied
        pathSatisfied = satisfied
        // The first report describes the present. A usable path then still
        // counts as a return: it covers a connection that came back between
        // the failed request and the monitor starting. It happens once, as
        // the monitor is never restarted.
        guard satisfied, previous != true else { return }
        let waiters = networkWaiters
        networkWaiters.removeAll()
        waiters.forEach { $0.resume() }
        DispatchQueue.main.async {
            NotificationCenter.default.post(name: GoogleFontsProvider.networkReturned, object: nil)
        }
    }
}

/// Families installed on this Mac, usable without any download.
@MainActor
enum InstalledFontFamily {
    struct Resolved: Equatable {
        /// The family's own spelling.
        let family: String
        let regular: String
        let bold: String?
    }

    /// Matches the family name case-insensitively and picks its regular and
    /// bold members.
    static func resolve(_ family: String, manager: NSFontManager = .shared) -> Resolved? {
        // One implementation for the whole app: AppFont picks the upright
        // regular and bold cuts nearest to weights 5 and 9.
        guard let match = canonicalName(family, manager: manager),
              let cuts = AppFont.installed(family: match) else { return nil }
        return Resolved(family: match, regular: cuts.regular, bold: cuts.bold)
    }

    /// The installed family's own spelling, matched case-insensitively.
    static func canonicalName(_ family: String, manager: NSFontManager = .shared) -> String? {
        let name = family.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty else { return nil }
        return manager.availableFontFamilies.first { $0.caseInsensitiveCompare(name) == .orderedSame }
    }
}
