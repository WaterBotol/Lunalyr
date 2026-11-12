// Wave Bands: nine horizontal sine lines evenly down the screen, each with its own height, wavelength, speed and
// direction (from the song's seed). Only the three lines nearest a pixel are tested.
#include "../Common.hlsli"
#include "../PatternLines.hlsli"

float Lines(float2 p)
{
    float t = Flow;
    float gap = Size.y / 10.0;
    float nearest = floor(p.y / gap + 0.5);
    float coverage = 0.0;

    [unroll] for (int j = -1; j <= 1; j++)
    {
        float i = clamp(nearest + (float)j, 1.0, 9.0);
        float a = frac(i * 0.754878 + SeedA());
        float b = frac(i * 0.569840 + SeedB());
        float c = frac(i * 0.414214 + SeedA() * 0.7 + SeedB() * 0.3);
        float amp = gap * (0.12 + 0.26 * a);
        float wavelength = lerp(170.0, 560.0, b);
        float speed = lerp(0.35, 1.0, c) * (frac(i * 0.5) > 0.25 ? -1.0 : 1.0);
        float k = Tau / wavelength;
        float phase = p.x * k + t * speed + a * Tau;
        float slope = amp * k * cos(phase);
        float d = (p.y - (i * gap + amp * sin(phase))) * rsqrt(1.0 + slope * slope);
        coverage = max(coverage, Hairline(d, 0.9));
    }
    return coverage * 0.13;
}

#include "../PatternMain.hlsli"
