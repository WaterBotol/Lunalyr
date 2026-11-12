// Ribbons: seven wavy horizontal bands at different depths, painted far to near so nearer bands pass over farther
// ones. Nearer bands are thicker, brighter, wave more broadly and move faster; each is shaded round like a tube.
#include "../Common.hlsli"

float3 Effect(float2 p, float light)
{
    float2 q = Centred(p);
    float t = Flow;
    float lum = 0.045 + 0.03 * saturate(q.y + 0.5);

    [unroll] for (int i = 0; i < 7; i++)
    {
        float fi = (float)i;
        float near = fi / 6.0;
        float base = lerp(-0.40, 0.40, frac(fi * 0.618034 + 0.15));
        float speed = lerp(0.10, 0.48, near);
        float y = base + 0.06 * (0.6 + near) * sin(q.x * (2.6 - 1.2 * near) + t * speed + fi * 1.7)
                       + 0.02 * sin(q.x * 5.1 - t * speed * 1.3 + fi);
        float thick = lerp(0.010, 0.060, near);
        float d = abs(q.y - y) / thick;
        float aa = 0.0015 / thick;
        float cover = 1.0 - smoothstep(1.0 - aa, 1.0 + aa, d);
        float shade = sqrt(saturate(1.0 - d * d));
        float band = lerp(0.13, 0.62, near) * (0.55 + 0.45 * shade);
        lum = lerp(lum, band, cover * lerp(0.65, 0.95, near));
    }
    return saturate(lum).xxx * light;
}

#include "../EffectMain.hlsli"
