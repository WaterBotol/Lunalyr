// Flow Field: streamlines of a smooth, slowly changing two-dimensional flow, drawn as the level lines of its stream
// function (so they never cross and bunch where the flow speeds up). Faint pulses travel along each line in the
// direction of the flow, each line at its own offset.
#include "../Common.hlsli"
#include "../PatternLines.hlsli"

float Lines(float2 p)
{
    float t = Flow;
    float2 s = p / 280.0;
    float seed = SeedA() * Tau;
    float psi = s.y * 0.85
              + 0.42 * sin(s.x * 0.95 + t * 0.050 + seed) * cos(s.y * 0.75 - t * 0.040)
              + 0.22 * sin(s.x * 1.70 - s.y * 1.20 + t * 0.065 + 2.0 * seed)
              + 0.12 * sin(s.x * 3.10 + s.y * 2.40 - t * 0.080 + SeedB() * Tau);
    float g = GradientPerDip(psi);
    static const float Spacing = 0.085;
    float lines = LevelLine(psi, g, Spacing, 0.8);

    float index = floor(psi / Spacing + 0.5);
    float pulse = frac(p.x / 240.0 - t * 0.30 + frac(index * 0.618034 + SeedB()));
    pulse = pulse * pulse;
    return lines * (0.04 + 0.10 * pulse);
}

#include "../PatternMain.hlsli"
