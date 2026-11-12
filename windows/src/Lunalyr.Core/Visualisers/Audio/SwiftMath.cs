namespace Lunalyr.Core.Visualisers;

/// <summary>
/// Swift's two-argument <c>min</c>, <c>max</c> and rounding, with their exact semantics, so the ported maths treats
/// NaN and halves the way the Mac does. <c>Math.Max</c> and <c>MathF.Min</c> propagate NaN where Swift's
/// <c>max(0, .nan)</c> returns 0, and <c>Math.Round</c> rounds halves to even where Swift rounds them away from zero.
/// </summary>
internal static class SwiftMath
{
    /// <summary>Swift.max(a, b): <c>b &gt;= a ? b : a</c>.</summary>
    public static float Max(float a, float b) => b >= a ? b : a;

    /// <summary>Swift.min(a, b): <c>b &lt; a ? b : a</c>.</summary>
    public static float Min(float a, float b) => b < a ? b : a;

    public static double Max(double a, double b) => b >= a ? b : a;

    public static double Min(double a, double b) => b < a ? b : a;

    public static int Max(int a, int b) => b >= a ? b : a;

    public static int Min(int a, int b) => b < a ? b : a;

    public static long Max(long a, long b) => b >= a ? b : a;

    public static long Min(long a, long b) => b < a ? b : a;

    /// <summary><c>min(hi, max(lo, x))</c>.</summary>
    public static float Clamp(float x, float lo, float hi) => Min(hi, Max(lo, x));

    /// <summary><c>min(hi, max(lo, x))</c>.</summary>
    public static double Clamp(double x, double lo, double hi) => Min(hi, Max(lo, x));

    /// <summary>Swift's <c>rounded()</c>: to nearest, halves away from zero.</summary>
    public static double Round(double x) => Math.Round(x, MidpointRounding.AwayFromZero);

    /// <summary>Swift's <c>rounded()</c> / <c>roundf</c>: to nearest, halves away from zero.</summary>
    public static float Round(float x) => MathF.Round(x, MidpointRounding.AwayFromZero);
}
