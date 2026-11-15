// Silk: six thin bright folds stacked down the screen. Each is a gently undulating curve with a narrow highlight whose
// width and strength change along it, a soft sheen above and a quick fall into shade below, like light on fabric.
#include "../Common.hlsli"

float3 Effect(float2 p, float light)
{
    float2 q = Centred(p);
    float t = Flow;
    float lum = 0.045 + 0.03 * saturate(q.y + 0.5);

    [unroll] for (int i = 0; i < 6; i++)
    {
        float fi = (float)i;
        float phase = q.x * (1.6 + 0.25 * fi) + t * (0.22 + 0.03 * fi) + fi * 1.9;
        float y = -0.43 + 0.17 * fi + 0.07 * sin(phase) + 0.03 * sin(q.x * 3.9 - t * 0.31 + fi * 0.7);
        float d = q.y - y;
        float w = 0.005 + 0.010 * (0.5 + 0.5 * sin(phase * 0.5 + 1.0 + t * 0.1));
        float highlight = exp(-d * d / (w * w));
        float sheen = d < 0.0 ? exp(d / 0.09) : exp(-d / 0.02);
        float catchLight = 0.5 + 0.5 * sin(q.x * 1.3 + fi * 2.0 + t * 0.15);
        lum += (0.48 * highlight + 0.11 * sheen) * (0.30 + 0.70 * catchLight);
    }

    return saturate(lum).xxx * light;
}

#include "../EffectMain.hlsli"
