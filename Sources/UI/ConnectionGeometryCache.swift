import SwiftUI

/// Non-published memoization: a cache hit never invalidates the view while
/// Canvas is drawing. The bounded C implementation owns exact geometry keys.
@MainActor
final class ConnectionGeometryCache: ObservableObject {
    private var storage = VCConnectionCache()
    func segments(boxes: [VCBox], viewport: VCBox) -> [VCSegment] {
        guard boxes.count > 1, boxes.count <= 64 else { return [] }
        var output = Array(repeating: VCSegment(), count: boxes.count - 1)
        let count = boxes.withUnsafeBufferPointer { input in
            output.withUnsafeMutableBufferPointer { result in
                vc_cached_connections(&storage, input.baseAddress, Int32(input.count), viewport,
                                      7, result.baseAddress, Int32(result.count))
            }
        }
        return Array(output.prefix(Int(count)))
    }
}
