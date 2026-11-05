import AVFoundation
import Foundation

/// A silent, looping video that several layers can show at once.
@MainActor
protocol LoopingVideo: AnyObject {
    var player: AVPlayer { get }
    /// Oriented pixel size, once the asset reports it.
    var naturalSize: CGSize? { get }
    var onNaturalSize: ((CGSize) -> Void)? { get set }
    func play()
    func pause()
    func stop()
}

/// One decoder per video, however many displays show it.
///
/// An animated cover or imported video used to get its own AVPlayer and
/// asset in every window: two displays decoded the same clip twice. Each
/// window now takes a lease and attaches its own AVPlayerLayer to the shared
/// player. The video plays while any lease wants it to, and is released a
/// moment after the last lease goes, so a crossfade that hands a video from
/// one layer to another never restarts it.
@MainActor
final class SharedVideoPlayers {
    static let shared = SharedVideoPlayers()

    final class Lease {
        let url: URL
        fileprivate(set) var isReleased = false
        fileprivate init(url: URL) { self.url = url }
    }

    private final class Entry {
        let video: LoopingVideo
        var leases: [ObjectIdentifier: (lease: Lease, paused: Bool)] = [:]
        var sizeHandlers: [ObjectIdentifier: (CGSize) -> Void] = [:]
        var teardown: DispatchWorkItem?
        var isPlaying = false
        init(video: LoopingVideo) { self.video = video }
    }

    private var entries: [URL: Entry] = [:]
    private let makeVideo: @MainActor (URL) -> LoopingVideo
    private let linger: TimeInterval

    init(linger: TimeInterval = 1.5, makeVideo: @escaping @MainActor (URL) -> LoopingVideo = { AVLoopingVideo(url: $0) }) {
        self.linger = linger
        self.makeVideo = makeVideo
    }

    var videoCount: Int { entries.count }
    func leaseCount(for url: URL) -> Int { entries[url]?.leases.count ?? 0 }
    func isPlaying(_ url: URL) -> Bool { entries[url]?.isPlaying ?? false }

    func acquire(_ url: URL, paused: Bool, onNaturalSize: @escaping (CGSize) -> Void) -> Lease {
        let entry: Entry
        if let existing = entries[url] {
            existing.teardown?.cancel()
            existing.teardown = nil
            entry = existing
        } else {
            entry = Entry(video: makeVideo(url))
            entries[url] = entry
            entry.video.onNaturalSize = { [weak entry] size in
                entry?.sizeHandlers.values.forEach { $0(size) }
            }
        }
        let lease = Lease(url: url)
        let id = ObjectIdentifier(lease)
        entry.leases[id] = (lease, paused)
        entry.sizeHandlers[id] = onNaturalSize
        apply(entry)
        return lease
    }

    func player(for lease: Lease) -> AVPlayer? {
        guard !lease.isReleased else { return nil }
        return entries[lease.url]?.video.player
    }

    func naturalSize(for lease: Lease) -> CGSize? {
        guard !lease.isReleased else { return nil }
        return entries[lease.url]?.video.naturalSize
    }

    func setPaused(_ paused: Bool, for lease: Lease) {
        let id = ObjectIdentifier(lease)
        guard !lease.isReleased, let entry = entries[lease.url], entry.leases[id] != nil else { return }
        entry.leases[id]?.paused = paused
        apply(entry)
    }

    func release(_ lease: Lease) {
        guard !lease.isReleased else { return }
        lease.isReleased = true
        let url = lease.url
        guard let entry = entries[url] else { return }
        let id = ObjectIdentifier(lease)
        entry.leases[id] = nil
        entry.sizeHandlers[id] = nil
        apply(entry)
        guard entry.leases.isEmpty else { return }
        let finish = { [weak self] in
            guard let self, let current = self.entries[url], current === entry, current.leases.isEmpty else { return }
            current.video.stop()
            self.entries[url] = nil
        }
        if linger <= 0 { finish(); return }
        let work = DispatchWorkItem { Task { @MainActor in finish() } }
        entry.teardown = work
        DispatchQueue.main.asyncAfter(deadline: .now() + linger, execute: work)
    }

    /// Plays while any holder can be seen.
    private func apply(_ entry: Entry) {
        let wantsPlay = entry.leases.values.contains { !$0.paused }
        guard wantsPlay != entry.isPlaying else { return }
        entry.isPlaying = wantsPlay
        if wantsPlay { entry.video.play() } else { entry.video.pause() }
    }
}

/// AVQueuePlayer with a looper, loading duration and size asynchronously.
@MainActor
final class AVLoopingVideo: LoopingVideo {
    private let queuePlayer = AVQueuePlayer()
    var player: AVPlayer { queuePlayer }
    private var looper: AVPlayerLooper?
    private var setup: Task<Void, Never>?
    private var loaded = false
    private var wantsPlay = false
    private(set) var naturalSize: CGSize?
    var onNaturalSize: ((CGSize) -> Void)?

    init(url: URL) {
        queuePlayer.isMuted = true
        queuePlayer.preventsDisplaySleepDuringVideoPlayback = false
        let asset = AVURLAsset(url: url)
        let item = AVPlayerItem(asset: asset)
        setup = Task { [weak self] in
            // AVPlayerLooper duplicates a template after its duration is
            // available. Loading it first also covers remote renditions.
            let duration = try? await asset.load(.duration)
            guard !Task.isCancelled, let self else { return }
            if let duration, duration.isNumeric, duration.seconds > 0 {
                self.looper = AVPlayerLooper(player: self.queuePlayer, templateItem: item)
            } else {
                self.queuePlayer.insert(item, after: nil)
            }
            self.loaded = true
            if self.wantsPlay { self.queuePlayer.play() }
            guard let track = try? await asset.loadTracks(withMediaType: .video).first,
                  let size = try? await track.load(.naturalSize) else { return }
            let transform = (try? await track.load(.preferredTransform)) ?? .identity
            let oriented = CGRect(origin: .zero, size: size).applying(transform).size
            guard !Task.isCancelled else { return }
            let natural = CGSize(width: abs(oriented.width), height: abs(oriented.height))
            self.naturalSize = natural
            self.onNaturalSize?(natural)
        }
    }

    func play() {
        wantsPlay = true
        if loaded { queuePlayer.play() }
    }

    func pause() {
        wantsPlay = false
        queuePlayer.pause()
    }

    func stop() {
        setup?.cancel()
        setup = nil
        wantsPlay = false
        queuePlayer.pause()
        looper?.disableLooping()
        looper = nil
        queuePlayer.removeAllItems()
    }
}
