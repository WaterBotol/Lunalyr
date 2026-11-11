namespace LunalyrSpike;

internal sealed class Options
{
    public double Seconds = 0;
    public string? StatsPath;
    public int FpsCap = 0;
    public bool Windowed;
    public string? SnapshotPath;
    public double SnapshotAt = 3;
    public int StartLine = 1;
    public bool Settings;
    public bool Layered;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            switch (a)
            {
                case "--seconds": o.Seconds = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--stats": o.StatsPath = Next(); break;
                case "--fps": o.FpsCap = int.Parse(Next()); break;
                case "--windowed": o.Windowed = true; break;
                case "--snapshot": o.SnapshotPath = Next(); break;
                case "--snapshot-at": o.SnapshotAt = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--start-line": o.StartLine = int.Parse(Next()); break;
                case "--settings": o.Settings = true; break;
                case "--layered": o.Layered = true; break;
                case "--no-playback": break; // the spike never plays anything
            }
        }
        return o;
    }
}

internal sealed record LyricLine(string[] Words, bool Spaced, bool Rtl)
{
    public string Text => string.Join(Spaced ? " " : "", Words);
}

internal static class Scene
{
    public const double WordStepMs = 450, HoldMs = 900, LeadInMs = 300, RevealMs = 280;

    public static readonly LyricLine[] Lines =
    [
        new(["The", "night", "is", "young", "and", "the", "city", "is", "ours"], true, false),
        new(["夜空に", "光る", "星を", "数えて"], false, false),
        new(["我们", "在", "月光下", "一起", "唱歌"], false, false),
        new(["우리는", "밤새", "노래했어"], true, false),
        new(["نحن", "نغني", "تحت", "ضوء", "القمر"], true, true),
        new(["שרים", "יחד", "עד", "הבוקר"], true, true),
        new(["Starlight", "🌙✨", "tonight", "🎶", "forever", "💜"], true, false),
    ];

    public static double LineDurationMs(LyricLine l) => l.Words.Length * WordStepMs + HoldMs;

    /// <summary>Which line is showing at time t (ms since launch), and when it started.</summary>
    public static (int index, double startMs) LineAt(double tMs, int startLine)
    {
        double total = Lines.Sum(LineDurationMs);
        double t = tMs % total;
        double cycleStart = tMs - t;
        int idx = Math.Clamp(startLine - 1, 0, Lines.Length - 1);
        double acc = 0;
        for (int k = 0; k < Lines.Length; k++)
        {
            int li = (idx + k) % Lines.Length;
            double d = LineDurationMs(Lines[li]);
            if (t < acc + d) return (li, cycleStart + acc);
            acc += d;
        }
        return (idx, cycleStart);
    }

    public static double WordStartMs(int word) => LeadInMs + word * WordStepMs;

    public static double EaseOutCubic(double p) { p = Math.Clamp(p, 0, 1); double q = 1 - p; return 1 - q * q * q; }

    // Background palette
    public static readonly (float r, float g, float b) Base = Hex("#1B1035");
    public static readonly (float r, float g, float b)[] Blobs = [Hex("#7B3FE4"), Hex("#E4572E"), Hex("#2E86AB"), Hex("#F2C14E")];
    public static readonly double[] BlobPeriodS = [23, 31, 37, 29];

    static (float, float, float) Hex(string s) =>
        (Convert.ToInt32(s[1..3], 16) / 255f, Convert.ToInt32(s[3..5], 16) / 255f, Convert.ToInt32(s[5..7], 16) / 255f);

    /// <summary>Blob centre in 0..1 coordinates, on a slow Lissajous path.</summary>
    public static (float x, float y) BlobCentre(int i, double tS)
    {
        double p = BlobPeriodS[i];
        double ph = i * 1.7;
        double x = 0.5 + 0.38 * Math.Sin(2 * Math.PI * tS / p + ph);
        double y = 0.5 + 0.34 * Math.Sin(2 * Math.PI * tS / (p * 1.31) + ph * 0.7);
        return ((float)x, (float)y);
    }
}
