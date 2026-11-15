// Strata: six ridgelines stacked from the horizon down, painted far to near. Each ridge is a 1D noise profile with two
// slow sines, scrolls sideways at a rate that grows with nearness (parallax), and carries a lit crest; nearer layers
// are brighter, the sky lightens toward the horizon.
#include "../Common.hlsli"

float3 Effect(float2 p, float light)
{
    float2 q = Centred(p);
    float t = Flow;
    float lum = 0.07 + 0.20 * exp(-abs(q.y + 0.16) * 4.0);

    [unroll] for (int i = 0; i < 6; i++)
    {
        float fi = (float)i;
        float near = fi / 5.0;
        float x = q.x * (1.6 - 0.15 * fi) + t * (0.015 + 0.09 * near * near) + fi * 17.3;
        float rise = 0.35 + 0.30 * sin(x * 1.9 + fi * 4.0) + 0.17 * sin(x * 4.3 + fi * 1.3)
                   + 0.08 * sin(x * 8.7 + fi * 2.7) + 0.15 * sin(x * 0.9 + fi);
        float ridge = -0.16 + 0.09 * fi + 0.05 * (1.0 + near) - (0.05 + 0.03 * fi) * rise;
        float below = q.y - ridge;
        float cover = smoothstep(-0.0015, 0.0015, below);
        float layer = lerp(0.10, 0.52, near) * (0.55 + 0.45 * exp(-max(below, 0.0) * 9.0)) + 0.16 * exp(-max(below, 0.0) / 0.005);
        lum = lerp(lum, layer, cover);
    }
    return saturate(lum).xxx * light;
}

#include "../EffectMain.hlsli"
