using System.Globalization;

namespace Lunalyr.Install;

/// <summary>
/// A Lunalyr version: <c>major.minor.patch</c>, optionally <c>-prerelease</c> (<c>0.8.0-beta.2</c>) and <c>+build</c>
/// (ignored, as SemVer says). A prerelease sorts before its release; prerelease parts compare numerically when both are
/// numbers, otherwise ordinally.
/// </summary>
public sealed record ProductVersion(int Major, int Minor, int Patch, string Prerelease) : IComparable<ProductVersion>
{
    public static ProductVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var core = text.Trim();
        if (core.StartsWith('v') || core.StartsWith('V')) core = core[1..];
        var plus = core.IndexOf('+');
        if (plus >= 0) core = core[..plus];
        var dash = core.IndexOf('-');
        var pre = dash >= 0 ? core[(dash + 1)..] : "";
        if (dash >= 0) core = core[..dash];
        var parts = core.Split('.');
        if (parts.Length is < 1 or > 4) return null;
        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return null;
            if (i < 3) numbers[i] = n;
        }
        if (dash >= 0 && (pre.Length == 0 || pre.Split('.').Any(p => p.Length == 0))) return null;
        return new ProductVersion(numbers[0], numbers[1], numbers[2], pre);
    }

    public int CompareTo(ProductVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (Prerelease.Length == 0 || other.Prerelease.Length == 0)
            return (Prerelease.Length == 0 ? 1 : 0) - (other.Prerelease.Length == 0 ? 1 : 0);
        var a = Prerelease.Split('.');
        var b = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var an = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var x);
            var bn = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var y);
            c = an && bn ? x.CompareTo(y) : an ? -1 : bn ? 1 : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    public static bool operator >(ProductVersion a, ProductVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(ProductVersion a, ProductVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(ProductVersion a, ProductVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(ProductVersion a, ProductVersion b) => a.CompareTo(b) <= 0;

    public override string ToString() => Prerelease.Length == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";
}
