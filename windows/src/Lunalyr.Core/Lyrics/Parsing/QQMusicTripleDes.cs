// Ported from the Mac app's QQMusicTripleDES.swift (7ddb08a; Lunalyr's own port of the community implementation
// Lyrimuse uses: Yudaotor/lyrimuse, lyrimuse-engine/des3_qmusic.go, itself from WXRIW/QQMusicDecoder).

namespace Lunalyr.Core.Lyrics.Parsing;

/// <summary>
/// The triple-DES variant QQ Music encrypts its QRC word-timed lyrics with. It is not standard FIPS-46 DES (.NET's
/// TripleDES cannot decrypt QQ's data): a line-for-line port. Keep every table and bit operation exactly as it is;
/// "equivalent-looking" rewrites break compatibility with QQ's ciphertext.
/// </summary>
public static class QQMusicTripleDes
{
    /// <summary>The fixed 24-byte key for QRC lyric content.</summary>
    public static ReadOnlySpan<byte> QrcKey => "!@#)(*$%123ZXC!@!@#)(NHL"u8;

    private static readonly int[][] Sbox =
    [
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
    ];

    private static readonly int[] KeyRndShift = [1, 1, 2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 2, 2, 2, 1];
    private static readonly int[] KeyPermC = [56, 48, 40, 32, 24, 16, 8, 0, 57, 49, 41, 33, 25, 17, 9, 1, 58, 50, 42, 34, 26, 18, 10, 2, 59, 51, 43, 35];
    private static readonly int[] KeyPermD = [62, 54, 46, 38, 30, 22, 14, 6, 61, 53, 45, 37, 29, 21, 13, 5, 60, 52, 44, 36, 28, 20, 12, 4, 27, 19, 11, 3];
    private static readonly int[] KeyCompression = [13, 16, 10, 23, 0, 4, 2, 27, 14, 5, 20, 9, 22, 18, 11, 3, 25, 7, 15, 6, 26, 19, 12, 1, 40, 51, 30, 36,
                                                    46, 54, 29, 39, 50, 44, 32, 47, 43, 48, 38, 55, 33, 52, 45, 41, 49, 35, 28, 31];
    private static readonly int[] Order0 = [57, 49, 41, 33, 25, 17, 9, 1, 59, 51, 43, 35, 27, 19, 11, 3, 61, 53, 45, 37, 29, 21, 13, 5, 63, 55, 47, 39, 31, 23, 15, 7];
    private static readonly int[] Order1 = [56, 48, 40, 32, 24, 16, 8, 0, 58, 50, 42, 34, 26, 18, 10, 2, 60, 52, 44, 36, 28, 20, 12, 4, 62, 54, 46, 38, 30, 22, 14, 6];
    private static readonly int[] Permutation = [15, 6, 19, 20, 28, 11, 27, 16, 0, 14, 22, 25, 4, 17, 30, 9, 1, 7, 23, 13, 31, 26, 2, 8, 18, 12, 29, 5, 21, 10, 3, 24];
    private static readonly (int Index, int Base)[] Bases = [(3, 7), (2, 6), (1, 5), (0, 4), (7, 3), (6, 2), (5, 1), (4, 0)];

    private static uint BitNum(ReadOnlySpan<byte> a, int b, int c)
    {
        var value = a[b / 32 * 4 + 3 - b % 32 / 8];
        return (uint)((value >> (7 - b % 8)) & 1) << c;
    }

    private static uint BitNumIntR(uint a, int b, int c) => ((a >> (31 - b)) & 1) << c;

    private static uint BitNumIntL(uint a, int b, int c) => ((a << b) & 0x8000_0000) >> c;

    private static int SboxBit(int a) => (a & 32) | ((a & 31) >> 1) | ((a & 1) << 4);

    private static (uint, uint) InitialPermutation(ReadOnlySpan<byte> input)
    {
        uint s0 = 0, s1 = 0;
        for (var n = 0; n < Order0.Length; n++) s0 |= BitNum(input, Order0[n], 31 - n);
        for (var n = 0; n < Order1.Length; n++) s1 |= BitNum(input, Order1[n], 31 - n);
        return (s0, s1);
    }

    private static void InversePermutation(uint s0, uint s1, Span<byte> data)
    {
        // data[index] collects bits (base, base+8, base+16, base+24) of s1/s0 alternately.
        foreach (var (index, b) in Bases)
        {
            data[index] = unchecked((byte)(BitNumIntR(s1, b, 7) | BitNumIntR(s0, b, 6) | BitNumIntR(s1, b + 8, 5)
                | BitNumIntR(s0, b + 8, 4) | BitNumIntR(s1, b + 16, 3) | BitNumIntR(s0, b + 16, 2)
                | BitNumIntR(s1, b + 24, 1) | BitNumIntR(s0, b + 24, 0)));
        }
    }

    private static uint F(uint state, int[] key)
    {
        var t1 = BitNumIntL(state, 31, 0) | ((state & 0xf000_0000) >> 1) | BitNumIntL(state, 4, 5)
            | BitNumIntL(state, 3, 6) | ((state & 0x0f00_0000) >> 3) | BitNumIntL(state, 8, 11)
            | BitNumIntL(state, 7, 12) | ((state & 0x00f0_0000) >> 5) | BitNumIntL(state, 12, 17)
            | BitNumIntL(state, 11, 18) | ((state & 0x000f_0000) >> 7) | BitNumIntL(state, 16, 23);
        var t2 = BitNumIntL(state, 15, 0) | ((state & 0x0000_f000) << 15) | BitNumIntL(state, 20, 5)
            | BitNumIntL(state, 19, 6) | ((state & 0x0000_0f00) << 13) | BitNumIntL(state, 24, 11)
            | BitNumIntL(state, 23, 12) | ((state & 0x0000_00f0) << 11) | BitNumIntL(state, 28, 17)
            | BitNumIntL(state, 27, 18) | ((state & 0x0000_000f) << 9) | BitNumIntL(state, 0, 23);
        Span<int> l =
        [
            (int)((t1 >> 24) & 0xff), (int)((t1 >> 16) & 0xff), (int)((t1 >> 8) & 0xff),
            (int)((t2 >> 24) & 0xff), (int)((t2 >> 16) & 0xff), (int)((t2 >> 8) & 0xff),
        ];
        for (var index = 0; index < 6; index++) l[index] ^= key[index];
        var s = ((uint)Sbox[0][SboxBit(l[0] >> 2)] << 28)
            | ((uint)Sbox[1][SboxBit(((l[0] & 0x03) << 4) | (l[1] >> 4))] << 24)
            | ((uint)Sbox[2][SboxBit(((l[1] & 0x0f) << 2) | (l[2] >> 6))] << 20)
            | ((uint)Sbox[3][SboxBit(l[2] & 0x3f)] << 16)
            | ((uint)Sbox[4][SboxBit(l[3] >> 2)] << 12)
            | ((uint)Sbox[5][SboxBit(((l[3] & 0x03) << 4) | (l[4] >> 4))] << 8)
            | ((uint)Sbox[6][SboxBit(((l[4] & 0x0f) << 2) | (l[5] >> 6))] << 4)
            | (uint)Sbox[7][SboxBit(l[5] & 0x3f)];
        uint output = 0;
        for (var n = 0; n < Permutation.Length; n++) output |= BitNumIntL(s, Permutation[n], n);
        return output;
    }

    private static void Crypt(ReadOnlySpan<byte> input, int[][] key, Span<byte> output)
    {
        var (s0, s1) = InitialPermutation(input);
        for (var index = 0; index < 15; index++)
        {
            var previous = s1;
            s1 = F(s1, key[index]) ^ s0;
            s0 = previous;
        }
        s0 = F(s1, key[15]) ^ s0;
        InversePermutation(s0, s1, output);
    }

    private static int[][] Schedule(ReadOnlySpan<byte> key, bool decrypt)
    {
        var result = new int[16][];
        uint c = 0, d = 0;
        for (var index = 0; index < 28; index++)
        {
            c |= BitNum(key, KeyPermC[index], 31 - index);
            d |= BitNum(key, KeyPermD[index], 31 - index);
        }
        for (var index = 0; index < 16; index++)
        {
            var shift = KeyRndShift[index];
            c = ((c << shift) | (c >> (28 - shift))) & 0xffff_fff0;
            d = ((d << shift) | (d >> (28 - shift))) & 0xffff_fff0;
            var target = decrypt ? 15 - index : index;
            var round = new int[6];
            for (var j = 0; j < 24; j++) round[j / 8] |= (int)BitNumIntR(c, KeyCompression[j], 7 - j % 8);
            for (var j = 24; j < 48; j++) round[j / 8] |= (int)BitNumIntR(d, KeyCompression[j] - 27, 7 - j % 8);
            result[target] = round;
        }
        return result;
    }

    private static byte[] Run(ReadOnlySpan<byte> data, int[][][] schedules)
    {
        var output = new byte[data.Length];
        Span<byte> block = stackalloc byte[8];
        Span<byte> next = stackalloc byte[8];
        for (var offset = 0; offset + 8 <= data.Length; offset += 8)
        {
            data.Slice(offset, 8).CopyTo(block);
            foreach (var schedule in schedules)
            {
                Crypt(block, schedule, next);
                next.CopyTo(block);
            }
            block.CopyTo(output.AsSpan(offset, 8));
        }
        return output;
    }

    /// <summary>EDE3 decrypt (D k3 → E k2 → D k1), 8-byte blocks with no chaining. Null for a key that isn't 24 bytes.</summary>
    public static byte[]? Decrypt(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key)
    {
        if (key.Length != 24) return null;
        return Run(data, [Schedule(key[16..24], decrypt: true), Schedule(key[8..16], decrypt: false), Schedule(key[..8], decrypt: true)]);
    }

    public static byte[]? Decrypt(ReadOnlySpan<byte> data) => Decrypt(data, QrcKey);

    /// <summary>The inverse of <see cref="Decrypt(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/> (E k1 → D k2 → E k3); whole blocks only.</summary>
    public static byte[]? Encrypt(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key)
    {
        if (key.Length != 24) return null;
        return Run(data, [Schedule(key[..8], decrypt: false), Schedule(key[8..16], decrypt: true), Schedule(key[16..24], decrypt: false)]);
    }

    public static byte[]? Encrypt(ReadOnlySpan<byte> data) => Encrypt(data, QrcKey);
}
