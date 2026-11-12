// Crosshatch: three sets of widely spaced parallel lines at 60° to each other. The whole lattice turns slowly and each
// line wobbles gently along its length.
#include "../Common.hlsli"
#include "../PatternLines.hlsli"

float Lines(float2 p)
{
    float t = Flow;
    float2 d = p - 0.5 * Size;
    float turn = t * 0.018 + SeedA() * Tau;
    static const float Spacing = 52.0;
    float coverage = 0.0;

    [unroll] for (int k = 0; k < 3; k++)
    {
        float angle = turn + (float)k * (Tau / 6.0);
        float2 n = float2(cos(angle), sin(angle));
        float along = dot(d, float2(-n.y, n.x));
        float wobble = 3.5 * sin(along / 190.0 + t * 0.55 + (float)k * 2.1) + 1.5 * sin(along / 77.0 - t * 0.4 + (float)k);
        float f = dot(d, n) + wobble + SeedB() * Spacing;
        coverage += Hairline(LevelOffset(f, Spacing), 0.8);
    }
    return coverage * 0.10;
}

#include "../PatternMain.hlsli"
