namespace Lunalyr.Core.Visualisers;

/// <summary>
/// A radix-2 FFT of real input that reproduces the scaling of vDSP's packed real forward transform
/// (<c>vDSP_ctoz</c> → <c>vDSP_fft_zrip</c> → <c>vDSP_zvmags</c>), which the Mac's analyser and its constants assume:
/// every value is twice the plain DFT, so bin k (0 &lt; k &lt; n/2) holds 4·|X_k|², and bin 0 holds the packed DC and
/// Nyquist terms, (2·X₀)² + (2·X_{n/2})².
/// </summary>
/// <remarks>
/// Every buffer is allocated once, for the largest size, so <see cref="PowerSpectrum"/> never allocates. Not
/// thread-safe: one caller at a time (each analyser owns one). The arithmetic runs in double precision.
/// </remarks>
public sealed class RealFft
{
    private readonly int _maximumSize;
    private readonly int _maximumLog2;
    /// <summary>e^(−2πik / maximumSize) for k in [0, maximumSize / 2).</summary>
    private readonly double[] _cos;
    private readonly double[] _sin;
    /// <summary>Bit reversal of k over log2(maximumSize / 2) bits.</summary>
    private readonly int[] _reverse;
    private readonly double[] _re;
    private readonly double[] _im;

    /// <param name="maximumSize">The largest transform length, a power of two of at least 4.</param>
    public RealFft(int maximumSize)
    {
        if (maximumSize < 4 || (maximumSize & (maximumSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(maximumSize), "A power of two of at least 4.");
        _maximumSize = maximumSize;
        _maximumLog2 = System.Numerics.BitOperations.Log2((uint)maximumSize);
        var half = maximumSize / 2;
        _cos = new double[half];
        _sin = new double[half];
        for (var k = 0; k < half; k++)
        {
            var angle = -2 * Math.PI * k / maximumSize;
            _cos[k] = Math.Cos(angle);
            _sin[k] = Math.Sin(angle);
        }
        var bits = _maximumLog2 - 1;
        _reverse = new int[half];
        for (var k = 0; k < half; k++)
        {
            int r = 0, v = k;
            for (var b = 0; b < bits; b++) { r = (r << 1) | (v & 1); v >>= 1; }
            _reverse[k] = r;
        }
        _re = new double[half];
        _im = new double[half];
    }

    /// <summary>The largest transform length this instance supports.</summary>
    public int MaximumSize => _maximumSize;

    /// <summary>
    /// The vDSP-scaled power spectrum of <paramref name="input"/> (length n, a power of two no larger than
    /// <see cref="MaximumSize"/>) into the first n / 2 values of <paramref name="power"/>.
    /// </summary>
    public void PowerSpectrum(ReadOnlySpan<float> input, Span<float> power)
    {
        var n = input.Length;
        if (n < 4 || n > _maximumSize || (n & (n - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(input), "A power-of-two length within the maximum size.");
        if (power.Length < n / 2) throw new ArgumentOutOfRangeException(nameof(power));
        var m = n / 2;
        var log2m = System.Numerics.BitOperations.Log2((uint)m);
        var shift = _maximumLog2 - 1 - log2m;
        var re = _re;
        var im = _im;
        // Pack even samples as real parts and odd samples as imaginary parts (vDSP_ctoz), in bit-reversed order.
        for (var k = 0; k < m; k++)
        {
            var j = _reverse[k] >> shift;
            re[j] = input[2 * k];
            im[j] = input[2 * k + 1];
        }
        // Iterative radix-2 complex FFT of length m.
        for (var length = 2; length <= m; length <<= 1)
        {
            var halfLength = length >> 1;
            var stride = _maximumSize / length;
            for (var start = 0; start < m; start += length)
            {
                for (var j = 0; j < halfLength; j++)
                {
                    double wr = _cos[j * stride], wi = _sin[j * stride];
                    int a = start + j, b = a + halfLength;
                    double tr = re[b] * wr - im[b] * wi, ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                }
            }
        }
        // Unpack the real transform: 2·X_k = (Z_k + conj Z_{m−k}) − i·W^k·(Z_k − conj Z_{m−k}), W = e^(−2πi/n).
        var tableStride = _maximumSize / n;
        {
            double dc = 2 * (re[0] + im[0]), nyquist = 2 * (re[0] - im[0]);
            power[0] = (float)(dc * dc + nyquist * nyquist);
        }
        for (var k = 1; k < m; k++)
        {
            double zr = re[k], zi = im[k], cr = re[m - k], ci = -im[m - k];
            double sr = zr + cr, si = zi + ci; // Z_k + conj Z_{m−k}
            double dr = zr - cr, di = zi - ci; // Z_k − conj Z_{m−k}
            double wr = _cos[k * tableStride], wi = _sin[k * tableStride];
            // −i·W·d = −i·(wr·dr − wi·di + i·(wr·di + wi·dr)) = (wr·di + wi·dr) − i·(wr·dr − wi·di)
            double xr = sr + (wr * di + wi * dr), xi = si - (wr * dr - wi * di);
            power[k] = (float)(xr * xr + xi * xi);
        }
    }
}
