// Orbs: seven soft lights at different depths. Near ones are larger, softer and travel further (parallax); far ones
// are small with a tighter core. Each wanders on its own slow path at its own brightness.
#include "../Common.hlsli"

float3 Effect(float2 p, float light)
{
    float2 q = Centred(p);
    float t = Flow;
    float sum = 0.0;

    [unroll] for (int i = 0; i < 7; i++)
    {
        float fi = (float)i;
        float depth = frac(fi * 0.618034 + 0.21);          // 0 far ... 1 near
        float level = 0.45 + 0.55 * frac(fi * 0.414214 + 0.6);
        float size = lerp(0.045, 0.20, depth);
        float2 c = float2(0.80 * sin(t * (0.050 + 0.017 * fi) + fi * 2.4),
                          0.40 * sin(t * (0.069 + 0.013 * fi) + fi * 1.1 + 0.5));
        c *= lerp(0.65, 1.1, depth);
        float2 d = q - c;
        float d2 = dot(d, d) / (size * size);
        float halo = exp(-d2);
        float core = exp(-d2 * 6.0) * (1.0 - 0.6 * depth);
        sum += level * (0.75 * halo + 0.45 * core);
    }

    float lum = 0.05 + 0.88 * (1.0 - exp(-sum));
    return lum.xxx * light;
}

#include "../EffectMain.hlsli"
