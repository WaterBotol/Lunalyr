namespace Lunalyr.Core.Visualisers.Capture;

/// <summary>
/// A synthetic piece of music, generated sample by sample in memory: 120 bpm with a kick on every beat, hi-hats
/// between, a walking bass line and a soft three-note pad that changes chord every two bars. The demo's visualiser
/// signal (<c>--demo</c> never captures) and the preview tool's beat. Never played or saved: it only feeds the analyser.
/// </summary>
public sealed class SyntheticSignal
{
    private static readonly double[] Roots = [82.41, 73.42, 98.0, 110.0];
    private readonly Random _random;
    private readonly double _rate;
    private long _sample;

    public SyntheticSignal(double sampleRate = 48_000, int seed = 7)
    {
        _rate = sampleRate;
        _random = new Random(seed);
    }

    public double SampleRate => _rate;

    /// <summary>Writes the next <paramref name="into"/>.Length mono samples.</summary>
    public void Next(Span<float> into)
    {
        for (var i = 0; i < into.Length; i++, _sample++)
        {
            var t = _sample / _rate;
            var beat = t % 0.5;
            var kick = Math.Sin(2 * Math.PI * (50 + 80 * Math.Exp(-beat * 30)) * beat) * Math.Exp(-beat * 9) * 0.7;
            var off = (t + 0.25) % 0.5;
            var hat = (_random.NextDouble() * 2 - 1) * Math.Exp(-off * 60) * 0.2;
            var bar = (int)(t / 4) % Roots.Length;
            var root = Roots[bar];
            var bass = Math.Sin(2 * Math.PI * root * t) * 0.16 * (0.6 + 0.4 * Math.Exp(-beat * 4));
            var pad = (Math.Sin(2 * Math.PI * root * 4 * t) + Math.Sin(2 * Math.PI * root * 5 * t) + Math.Sin(2 * Math.PI * root * 6 * t)) * 0.035;
            into[i] = (float)(kick + hat + bass + pad);
        }
    }
}
