// Voronoi: a mosaic of cells whose seeds each circle inside their own lattice cell. A second pass finds the true
// distance to the nearest border, which glows; the interiors stay dark.
#include "../Common.hlsli"

float2 Seed(float2 cell, float t)
{
    float2 h = Random2(cell);
    float2 g = frac(h * 13.37 + h.yx * 7.13);
    return 0.5 + 0.36 * float2(sin(t * (0.35 + 0.4 * g.x) + Tau * h.x), cos(t * (0.30 + 0.4 * g.y) + Tau * h.y));
}

float3 Effect(float2 p, float light)
{
    float2 q = Centred(p);
    float t = Flow;
    float2 x = q * 5.5 + float2(t * 0.03, 0.0);
    float2 cell = floor(x);
    float2 f = x - cell;

    float2 nearest = float2(0.0, 0.0);
    float2 nearestCell = float2(0.0, 0.0);
    float best = 8.0;
    [unroll] for (int j = -1; j <= 1; j++)
    {
        [unroll] for (int i = -1; i <= 1; i++)
        {
            float2 g = float2(i, j);
            float2 r = g + Seed(cell + g, t) - f;
            float d = dot(r, r);
            if (d < best) { best = d; nearest = r; nearestCell = g; }
        }
    }

    float border = 8.0;
    [unroll] for (int j2 = -1; j2 <= 1; j2++)
    {
        [unroll] for (int i2 = -1; i2 <= 1; i2++)
        {
            float2 g = nearestCell + float2(i2, j2);
            float2 r = g + Seed(cell + g, t) - f;
            float2 between = r - nearest;
            float len2 = dot(between, between);
            if (len2 > 1e-5) border = min(border, dot(0.5 * (nearest + r), between * rsqrt(len2)));
        }
    }

    float edge = 1.0 - smoothstep(0.012, 0.035, border);
    float glow = exp(-border * 9.0);
    float lum = 0.035 + 0.05 * sqrt(best) + 0.30 * glow + 0.42 * edge;
    return lum.xxx * light;
}

#include "../EffectMain.hlsli"
