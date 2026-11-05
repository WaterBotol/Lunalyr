import Foundation
import IOKit.ps
import os

/// Whether the Mac is running on battery, so the visuals can ease off.
///
/// The wallpaper asks on every frame while an On battery rule is set, so
/// the answer is cached: IOKit is queried when it is first needed, then
/// again only when it reports that the Mac moved between mains and battery
/// (or UPS) power. A flip posts `didChange` on the main queue, which
/// `Settings` republishes -- so the wallpaper and audio capture follow
/// plugging in or unplugging straight away instead of waiting for something
/// else to redraw.
enum PowerSource {
    /// Posted on the main queue when the Mac moves between battery and mains.
    static let didChange = Notification.Name("LunalyrPowerSourceDidChange")

    /// The cached state. Cheap enough to read per frame, from any thread.
    static var isOnBattery: Bool { monitor.isOnBattery }

    private static let monitor = PowerSourceMonitor(read: currentlyOnBattery)

    /// Asks IOKit directly. The first source decides: the internal battery,
    /// or a UPS, is listed before any accessory.
    static func currentlyOnBattery() -> Bool {
        guard let snapshot = IOPSCopyPowerSourcesInfo()?.takeRetainedValue(),
              let sources = IOPSCopyPowerSourcesList(snapshot)?.takeRetainedValue() as? [CFTypeRef]
        else { return false }

        for source in sources {
            guard let description = IOPSGetPowerSourceDescription(snapshot, source)?
                .takeUnretainedValue() as? [String: Any],
                let state = description[kIOPSPowerSourceStateKey] as? String
            else { continue }
            return state == kIOPSBatteryPowerValue
        }
        return false
    }
}

/// Holds the last known battery state and refreshes it whenever the power
/// source it subscribes to reports a change. Tests give it their own reader,
/// notification centre and subscription.
final class PowerSourceMonitor {
    /// Starts delivering `changed` and returns whatever keeps that going;
    /// releasing it stops the deliveries.
    typealias Subscribe = (_ changed: @escaping () -> Void) -> AnyObject?

    private let read: () -> Bool
    private let center: NotificationCenter
    private let state: OSAllocatedUnfairLock<Bool>
    private var subscription: AnyObject?

    init(read: @escaping () -> Bool, center: NotificationCenter = .default,
         subscribe: Subscribe = { PowerSourceSubscription(handler: $0) }) {
        self.read = read
        self.center = center
        state = OSAllocatedUnfairLock(initialState: read())
        subscription = subscribe { [weak self] in self?.refresh() }
        // A flip between the first read and the subscription would otherwise
        // go unseen. Nobody has read the state yet, so nothing is announced.
        let now = read()
        state.withLock { $0 = now }
    }

    var isOnBattery: Bool { state.withLock { $0 } }

    /// Re-reads the power state, posting `PowerSource.didChange` if it moved.
    func refresh() {
        let now = read()
        let changed = state.withLock { stored -> Bool in
            guard stored != now else { return false }
            stored = now
            return true
        }
        guard changed else { return }
        if Thread.isMainThread {
            center.post(name: PowerSource.didChange, object: nil)
        } else {
            let center = center
            DispatchQueue.main.async { center.post(name: PowerSource.didChange, object: nil) }
        }
    }
}

/// IOKit's notice that the Mac moved between mains and battery or UPS
/// power, delivered on a run loop for as long as this lives. It is the
/// limited-power notification, so charge-level updates never wake it.
final class PowerSourceSubscription {
    private let handler: () -> Void
    /// The run loop source IOKit created, invalidated on release.
    private(set) var source: CFRunLoopSource?

    /// `nil` when IOKit cannot provide the notification.
    init?(runLoop: CFRunLoop = CFRunLoopGetMain(), handler: @escaping () -> Void) {
        self.handler = handler
        guard let source = IOPSCreateLimitedPowerNotification(Self.callback, context)?.takeRetainedValue() else {
            return nil
        }
        self.source = source
        // Common modes keep it arriving while a menu is open or a slider is
        // being dragged.
        CFRunLoopAddSource(runLoop, source, .commonModes)
    }

    deinit {
        // IOKit holds `context` unretained, so it must never call back once
        // this is gone.
        if let source { CFRunLoopSourceInvalidate(source) }
    }

    /// What IOKit hands back to `callback`: this subscription, unretained.
    var context: UnsafeMutableRawPointer { Unmanaged.passUnretained(self).toOpaque() }

    /// IOKit calls back through a plain C function, so the subscription
    /// travels as the context pointer.
    static let callback: IOPowerSourceCallbackType = { context in
        guard let context else { return }
        Unmanaged<PowerSourceSubscription>.fromOpaque(context).takeUnretainedValue().handler()
    }
}
