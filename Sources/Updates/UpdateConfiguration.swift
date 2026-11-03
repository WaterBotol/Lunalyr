import Foundation

/// Only a publisher-configured HTTPS feed and Ed25519 public key enable
/// updates. No upstream/placeholder feed is silently substituted.
struct UpdateConfiguration {
    let feed: URL
    let publicKey: String
    init?(info: [String: Any]) {
        guard let rawURL = info["SUFeedURL"] as? String else { return nil }
        let address = rawURL.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !address.contains(where: \.isWhitespace), let url = URL(string: address),
              url.scheme?.lowercased() == "https", let host = url.host, !host.isEmpty,
              url.user == nil, url.password == nil, url.fragment == nil,
              let rawKey = info["SUPublicEDKey"] as? String else { return nil }
        let key = rawKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let bytes = Data(base64Encoded: key), bytes.count == 32,
              bytes.base64EncodedString() == key else { return nil }
        feed = url; publicKey = key
    }
}
