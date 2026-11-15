// Moiré: two sets of fine parallel lines at slightly different angles, slowly turning against each other so the
// interference bands drift. The sets are combined with max(), not added: where their lines coincide the light halves,
// where they interleave it doubles, which is what makes the bands show.
#include "../Common.hlsli"
#include "../PatternLines.hlsli"

float LineSet(float2 d, float angle, float spacing)
{
    float2 n = float2(cos(angle), sin(angle));
    return Hairline(LevelOffset(dot(d, n), spacing), 1.1);
}

float Lines(float2 p)
{
    float2 d = p - 0.5 * Size;
    float t = Flow;
    float base = 0.35 + SeedA() * 1.2 + 0.05 * sin(t * 0.11);
    float apart = 0.07 + 0.035 * sin(t * 0.17 + SeedB() * Tau);
    float spacing = 6.5;
    float a = LineSet(d, base, spacing);
    float b = LineSet(d, base + apart, spacing);
    return max(a, b) * 0.10;
}

#include "../PatternMain.hlsli"
