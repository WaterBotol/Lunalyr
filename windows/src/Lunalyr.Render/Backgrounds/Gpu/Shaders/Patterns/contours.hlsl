// Contours: about twenty stacked horizontal lines, the level lines of height plus a gentle travelling warp, so each line
// bends a little differently and they drift like a topographic map.
#include "../Common.hlsli"
#include "../PatternLines.hlsli"

float Lines(float2 p)
{
    float t = Flow;
    float unit = min(Size.x, Size.y);
    float2 s = p / unit;
    float seed = SeedA() * Tau;
    float warp = 0.040 * sin(s.x * 2.7 + s.y * 0.8 + t * 0.21 + seed)
               + 0.026 * sin(s.x * 5.1 - s.y * 2.3 - t * 0.16 + 2.0 * seed)
               + 0.018 * sin(s.x * 8.3 + s.y * 4.1 + t * 0.12 + SeedB() * Tau);
    float f = p.y + warp * unit + t * 3.0;
    float spacing = Size.y / 20.0;
    float g = GradientPerDip(f);
    return LevelLine(f, g, spacing, 0.85) * 0.12;
}

#include "../PatternMain.hlsli"
