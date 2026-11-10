import Foundation

/// A small least-recently-used store for lookups kept for the session.
/// Lunalyr runs all day, so a plain dictionary keyed by song grows with
/// every song played; this keeps the most recent `capacity` entries (and,
/// when `maxCost` is set, no more than that total cost) and forgets the
/// rest. Not thread-safe: keep it inside an actor or on one queue.
struct BoundedCache<Key: Hashable, Value> {
    let capacity: Int
    let maxCost: Int?
    private let costOf: (Value) -> Int
    private var values: [Key: (value: Value, cost: Int, used: UInt64)] = [:]
    private var clock: UInt64 = 0
    private(set) var totalCost = 0

    init(capacity: Int, maxCost: Int? = nil, cost: @escaping (Value) -> Int = { _ in 0 }) {
        self.capacity = max(1, capacity)
        self.maxCost = maxCost
        costOf = cost
    }

    var count: Int { values.count }

    /// The value, marked as just used.
    mutating func value(for key: Key) -> Value? {
        guard let entry = values[key] else { return nil }
        clock &+= 1
        values[key]?.used = clock
        return entry.value
    }

    func contains(_ key: Key) -> Bool { values[key] != nil }

    mutating func insert(_ value: Value, for key: Key) {
        clock &+= 1
        let cost = costOf(value)
        if let old = values[key] { totalCost -= old.cost }
        values[key] = (value, cost, clock)
        totalCost += cost
        while values.count > capacity || (maxCost.map { totalCost > $0 } ?? false) && values.count > 1 {
            guard let oldest = values.min(by: { $0.value.used < $1.value.used })?.key else { break }
            remove(oldest)
        }
    }

    mutating func remove(_ key: Key) {
        if let old = values.removeValue(forKey: key) { totalCost -= old.cost }
    }

    mutating func removeAll() {
        values.removeAll()
        totalCost = 0
    }
}
