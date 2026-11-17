import Foundation

/// The triple-DES variant QQ Music encrypts its QRC word-timed lyrics with.
/// It is not standard FIPS-46 DES (CommonCrypto cannot decrypt QQ's data): this
/// is a line-for-line port of the community implementation Lyrimuse uses
/// (Yudaotor/lyrimuse, lyrimuse-engine/des3_qmusic.go, itself from
/// WXRIW/QQMusicDecoder). Keep every table and bit operation exactly as it is;
/// "equivalent-looking" rewrites break compatibility with QQ's ciphertext.
enum QQMusicTripleDES {
    /// The fixed 24-byte key for QRC lyric content.
    static let qrcKey = Array("!@#)(*$%123ZXC!@!@#)(NHL".utf8)

    private static let sbox: [[Int]] = [
        [14, 4, 13, 1, 2, 15, 11, 8, 3, 10, 6, 12, 5, 9, 0, 7,
         0, 15, 7, 4, 14, 2, 13, 1, 10, 6, 12, 11, 9, 5, 3, 8,
         4, 1, 14, 8, 13, 6, 2, 11, 15, 12, 9, 7, 3, 10, 5, 0,
         15, 12, 8, 2, 4, 9, 1, 7, 5, 11, 3, 14, 10, 0, 6, 13],
        [15, 1, 8, 14, 6, 11, 3, 4, 9, 7, 2, 13, 12, 0, 5, 10,
         3, 13, 4, 7, 15, 2, 8, 15, 12, 0, 1, 10, 6, 9, 11, 5,
         0, 14, 7, 11, 10, 4, 13, 1, 5, 8, 12, 6, 9, 3, 2, 15,
         13, 8, 10, 1, 3, 15, 4, 2, 11, 6, 7, 12, 0, 5, 14, 9],
        [10, 0, 9, 14, 6, 3, 15, 5, 1, 13, 12, 7, 11, 4, 2, 8,
         13, 7, 0, 9, 3, 4, 6, 10, 2, 8, 5, 14, 12, 11, 15, 1,
         13, 6, 4, 9, 8, 15, 3, 0, 11, 1, 2, 12, 5, 10, 14, 7,
         1, 10, 13, 0, 6, 9, 8, 7, 4, 15, 14, 3, 11, 5, 2, 12],
        [7, 13, 14, 3, 0, 6, 9, 10, 1, 2, 8, 5, 11, 12, 4, 15,
         13, 8, 11, 5, 6, 15, 0, 3, 4, 7, 2, 12, 1, 10, 14, 9,
         10, 6, 9, 0, 12, 11, 7, 13, 15, 1, 3, 14, 5, 2, 8, 4,
         3, 15, 0, 6, 10, 10, 13, 8, 9, 4, 5, 11, 12, 7, 2, 14],
        [2, 12, 4, 1, 7, 10, 11, 6, 8, 5, 3, 15, 13, 0, 14, 9,
         14, 11, 2, 12, 4, 7, 13, 1, 5, 0, 15, 10, 3, 9, 8, 6,
         4, 2, 1, 11, 10, 13, 7, 8, 15, 9, 12, 5, 6, 3, 0, 14,
         11, 8, 12, 7, 1, 14, 2, 13, 6, 15, 0, 9, 10, 4, 5, 3],
        [12, 1, 10, 15, 9, 2, 6, 8, 0, 13, 3, 4, 14, 7, 5, 11,
         10, 15, 4, 2, 7, 12, 9, 5, 6, 1, 13, 14, 0, 11, 3, 8,
         9, 14, 15, 5, 2, 8, 12, 3, 7, 0, 4, 10, 1, 13, 11, 6,
         4, 3, 2, 12, 9, 5, 15, 10, 11, 14, 1, 7, 6, 0, 8, 13],
        [4, 11, 2, 14, 15, 0, 8, 13, 3, 12, 9, 7, 5, 10, 6, 1,
         13, 0, 11, 7, 4, 9, 1, 10, 14, 3, 5, 12, 2, 15, 8, 6,
         1, 4, 11, 13, 12, 3, 7, 14, 10, 15, 6, 8, 0, 5, 9, 2,
         6, 11, 13, 8, 1, 4, 10, 7, 9, 5, 0, 15, 14, 2, 3, 12],
        [13, 2, 8, 4, 6, 15, 11, 1, 10, 9, 3, 14, 5, 0, 12, 7,
         1, 15, 13, 8, 10, 3, 7, 4, 12, 5, 6, 11, 0, 14, 9, 2,
         7, 11, 4, 1, 9, 12, 14, 2, 0, 6, 10, 13, 15, 3, 5, 8,
         2, 1, 14, 7, 4, 10, 8, 13, 15, 12, 9, 0, 3, 5, 6, 11],
    ]
    private static let keyRndShift: [UInt32] = [1, 1, 2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 2, 2, 2, 1]
    private static let keyPermC: [Int] = [56, 48, 40, 32, 24, 16, 8, 0, 57, 49, 41, 33, 25, 17, 9, 1, 58, 50, 42, 34, 26, 18, 10, 2, 59, 51, 43, 35]
    private static let keyPermD: [Int] = [62, 54, 46, 38, 30, 22, 14, 6, 61, 53, 45, 37, 29, 21, 13, 5, 60, 52, 44, 36, 28, 20, 12, 4, 27, 19, 11, 3]
    private static let keyCompression: [Int] = [13, 16, 10, 23, 0, 4, 2, 27, 14, 5, 20, 9, 22, 18, 11, 3, 25, 7, 15, 6, 26, 19, 12, 1, 40, 51, 30, 36,
                                                46, 54, 29, 39, 50, 44, 32, 47, 43, 48, 38, 55, 33, 52, 45, 41, 49, 35, 28, 31]

    private typealias RoundKey = [Int] // six bytes, low six bits significant
    private typealias Schedule = [RoundKey]

    private static func bitnum(_ a: ArraySlice<UInt8>, _ b: Int, _ c: Int) -> UInt32 {
        let byte = a[a.startIndex + (b / 32) * 4 + 3 - (b % 32) / 8]
        return UInt32((byte >> UInt8(7 - b % 8)) & 1) << UInt32(c)
    }
    private static func bitnumIntr(_ a: UInt32, _ b: Int, _ c: Int) -> UInt32 {
        ((a >> UInt32(31 - b)) & 1) << UInt32(c)
    }
    private static func bitnumIntl(_ a: UInt32, _ b: Int, _ c: Int) -> UInt32 {
        ((a << UInt32(b)) & 0x8000_0000) >> UInt32(c)
    }
    private static func sboxBit(_ a: Int) -> Int {
        (a & 32) | ((a & 31) >> 1) | ((a & 1) << 4)
    }

    private static func initialPermutation(_ i: ArraySlice<UInt8>) -> (UInt32, UInt32) {
        let order0 = [57, 49, 41, 33, 25, 17, 9, 1, 59, 51, 43, 35, 27, 19, 11, 3, 61, 53, 45, 37, 29, 21, 13, 5, 63, 55, 47, 39, 31, 23, 15, 7]
        let order1 = [56, 48, 40, 32, 24, 16, 8, 0, 58, 50, 42, 34, 26, 18, 10, 2, 60, 52, 44, 36, 28, 20, 12, 4, 62, 54, 46, 38, 30, 22, 14, 6]
        var s0: UInt32 = 0, s1: UInt32 = 0
        for (n, bit) in order0.enumerated() { s0 |= bitnum(i, bit, 31 - n) }
        for (n, bit) in order1.enumerated() { s1 |= bitnum(i, bit, 31 - n) }
        return (s0, s1)
    }

    private static func inversePermutation(_ s0: UInt32, _ s1: UInt32) -> [UInt8] {
        var data = [UInt8](repeating: 0, count: 8)
        // data[index] collects bits (base, base+8, base+16, base+24) of s1/s0 alternately.
        let bases: [(index: Int, base: Int)] = [(3, 7), (2, 6), (1, 5), (0, 4), (7, 3), (6, 2), (5, 1), (4, 0)]
        for (index, base) in bases {
            data[index] = UInt8(truncatingIfNeeded:
                bitnumIntr(s1, base, 7) | bitnumIntr(s0, base, 6) | bitnumIntr(s1, base + 8, 5) |
                bitnumIntr(s0, base + 8, 4) | bitnumIntr(s1, base + 16, 3) | bitnumIntr(s0, base + 16, 2) |
                bitnumIntr(s1, base + 24, 1) | bitnumIntr(s0, base + 24, 0))
        }
        return data
    }

    private static func f(_ state: UInt32, _ key: RoundKey) -> UInt32 {
        // Built up a term at a time, each typed: Xcode 16.4 (CI) cannot
        // type-check these as single expressions in reasonable time.
        var t1: UInt32 = bitnumIntl(state, 31, 0)
        t1 |= (state & 0xf000_0000) >> 1
        t1 |= bitnumIntl(state, 4, 5)
        t1 |= bitnumIntl(state, 3, 6)
        t1 |= (state & 0x0f00_0000) >> 3
        t1 |= bitnumIntl(state, 8, 11)
        t1 |= bitnumIntl(state, 7, 12)
        t1 |= (state & 0x00f0_0000) >> 5
        t1 |= bitnumIntl(state, 12, 17)
        t1 |= bitnumIntl(state, 11, 18)
        t1 |= (state & 0x000f_0000) >> 7
        t1 |= bitnumIntl(state, 16, 23)
        var t2: UInt32 = bitnumIntl(state, 15, 0)
        t2 |= (state & 0x0000_f000) << 15
        t2 |= bitnumIntl(state, 20, 5)
        t2 |= bitnumIntl(state, 19, 6)
        t2 |= (state & 0x0000_0f00) << 13
        t2 |= bitnumIntl(state, 24, 11)
        t2 |= bitnumIntl(state, 23, 12)
        t2 |= (state & 0x0000_00f0) << 11
        t2 |= bitnumIntl(state, 28, 17)
        t2 |= bitnumIntl(state, 27, 18)
        t2 |= (state & 0x0000_000f) << 9
        t2 |= bitnumIntl(state, 0, 23)
        var l: [Int] = [Int((t1 >> 24) & 0xff), Int((t1 >> 16) & 0xff), Int((t1 >> 8) & 0xff),
                        Int((t2 >> 24) & 0xff), Int((t2 >> 16) & 0xff), Int((t2 >> 8) & 0xff)]
        for index in 0..<6 { l[index] ^= key[index] }
        let six: [Int] = [
            l[0] >> 2,
            ((l[0] & 0x03) << 4) | (l[1] >> 4),
            ((l[1] & 0x0f) << 2) | (l[2] >> 6),
            l[2] & 0x3f,
            l[3] >> 2,
            ((l[3] & 0x03) << 4) | (l[4] >> 4),
            ((l[4] & 0x0f) << 2) | (l[5] >> 6),
            l[5] & 0x3f,
        ]
        var s: UInt32 = 0
        for box in 0..<8 {
            let value: UInt32 = UInt32(sbox[box][sboxBit(six[box])])
            s |= value << UInt32(28 - 4 * box)
        }
        let permutation = [15, 6, 19, 20, 28, 11, 27, 16, 0, 14, 22, 25, 4, 17, 30, 9,
                           1, 7, 23, 13, 31, 26, 2, 8, 18, 12, 29, 5, 21, 10, 3, 24]
        var out: UInt32 = 0
        for (n, bit) in permutation.enumerated() { out |= bitnumIntl(s, bit, n) }
        return out
    }

    private static func crypt(_ input: ArraySlice<UInt8>, _ key: Schedule) -> [UInt8] {
        var (s0, s1) = initialPermutation(input)
        for index in 0..<15 {
            let previous = s1
            s1 = f(s1, key[index]) ^ s0
            s0 = previous
        }
        s0 = f(s1, key[15]) ^ s0
        return inversePermutation(s0, s1)
    }

    private static func schedule(_ key: ArraySlice<UInt8>, decrypt: Bool) -> Schedule {
        var result = Schedule(repeating: RoundKey(repeating: 0, count: 6), count: 16)
        var c: UInt32 = 0, d: UInt32 = 0
        for index in 0..<28 {
            c |= bitnum(key, keyPermC[index], 31 - index)
            d |= bitnum(key, keyPermD[index], 31 - index)
        }
        for index in 0..<16 {
            let shift = keyRndShift[index]
            c = ((c << shift) | (c >> (28 - shift))) & 0xffff_fff0
            d = ((d << shift) | (d >> (28 - shift))) & 0xffff_fff0
            let target = decrypt ? 15 - index : index
            var round = RoundKey(repeating: 0, count: 6)
            for j in 0..<24 { round[j / 8] |= Int(bitnumIntr(c, keyCompression[j], 7 - j % 8)) }
            for j in 24..<48 { round[j / 8] |= Int(bitnumIntr(d, keyCompression[j] - 27, 7 - j % 8)) }
            result[target] = round
        }
        return result
    }

    private static func run(_ data: [UInt8], key: [UInt8], schedules: [Schedule]) -> [UInt8] {
        var out = [UInt8](repeating: 0, count: data.count)
        var offset = 0
        while offset + 8 <= data.count {
            var block = data[offset..<offset + 8]
            for schedule in schedules { block = ArraySlice(crypt(block, schedule)) }
            out.replaceSubrange(offset..<offset + 8, with: block)
            offset += 8
        }
        return out
    }

    /// EDE3 decrypt (D k3 → E k2 → D k1), 8-byte blocks with no chaining.
    static func decrypt(_ data: [UInt8], key: [UInt8] = qrcKey) -> [UInt8]? {
        guard key.count == 24 else { return nil }
        return run(data, key: key, schedules: [schedule(key[16..<24], decrypt: true),
                                               schedule(key[8..<16], decrypt: false),
                                               schedule(key[0..<8], decrypt: true)])
    }

    /// The inverse of `decrypt` (E k1 → D k2 → E k3). `data` must be a whole number of blocks.
    static func encrypt(_ data: [UInt8], key: [UInt8] = qrcKey) -> [UInt8]? {
        guard key.count == 24 else { return nil }
        return run(data, key: key, schedules: [schedule(key[0..<8], decrypt: false),
                                               schedule(key[8..<16], decrypt: true),
                                               schedule(key[16..<24], decrypt: false)])
    }
}
