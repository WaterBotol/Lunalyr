// Bubbles: sixteen glass bubbles rising slowly at their own speeds with a slight sway, each recycled below the screen
// once it has left the top. Clear in the middle, bright at the rim, with a small highlight up and to the left.
#include "../Common.hlsli"

float3 Effect(float2 p, float light)
{
    float2 q = Centred(p);
    float2 extent = 0.5 * Size / min(Size.x, Size.y);
    float lum = 0.05 + 0.06 * saturate(q.y / (2.0 * extent.y) + 0.5);

    [unroll] for (int i = 0; i < 16; i++)
    {
        float fi = (float)i;
        float lane = frac(fi * 0.618034 + 0.13);
        float size = frac(fi * 0.414214 + 0.71);
        float start = frac(fi * 0.732051 + 0.29);
        float radius = 0.028 + 0.075 * size * size;
        float rise = 0.030 + 0.025 * (1.0 - size);
        float u = frac(Flow * rise + start);
        float y = extent.y + radius - u * 2.0 * (extent.y + radius);
        float x = (lane * 2.0 - 1.0) * (extent.x - 0.05) + 0.035 * sin(Flow * (0.35 + 0.3 * size) + fi * 2.3);
        float2 d = (q - float2(x, y)) / radius;
        float rr = length(d);
        float inside = 1.0 - smoothstep(1.0 - 0.006 / radius, 1.0, rr);
        float rim = smoothstep(0.62, 0.98, rr);
        float2 h = d - float2(-0.36, -0.40);
        float highlight = exp(-dot(h, h) * 60.0);
        lum += inside * (0.035 + 0.42 * rim * rim + 0.55 * highlight);
    }
    return saturate(lum).xxx * light;
}

#include "../EffectMain.hlsli"
