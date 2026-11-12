// Clouds: three layers of soft cloud (thresholded layered noise, flattened vertically) over a sky that lightens toward
// the horizon. Far layers are smaller, dimmer and slower; nearer ones larger, brighter and faster. Nine noise evaluations.
#include "../Common.hlsli"

float3 Effect(float2 p, float light)
{
    float2 q = Centred(p);
    float t = Flow;
    float lum = 0.08 + 0.16 * saturate(0.55 + q.y);

    [unroll] for (int i = 0; i < 3; i++)
    {
        float fi = (float)i;
        float scale = 3.0 - 0.85 * fi;
        float2 x = q * float2(1.0, 2.1) * scale + float2(t * (0.03 + 0.05 * fi), fi * 13.1);
        float n = Layered(x, 3);
        float cover = smoothstep(0.47 - 0.02 * fi, 0.74, n);
        float lit = 0.28 + 0.13 * fi + 0.45 * saturate(n - 0.55);
        lum = lerp(lum, lit, cover * (0.70 + 0.10 * fi));
    }
    return saturate(lum).xxx * light;
}

#include "../EffectMain.hlsli"
