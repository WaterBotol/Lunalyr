// Plasma: broad smooth waves crossing at different angles, folded into soft bands; slightly darker edges.
#include "../Common.hlsli"

float3 Effect(float2 p, float light)
{
    float2 q = Centred(p);
    float t = Flow * 0.32;

    // Four broad waves: two plane waves at different headings, one bent around a wandering point, one slow diagonal.
    float v = sin(dot(q, float2(0.83, 0.56)) * 5.1 + t * 1.10);
    v += sin(dot(q, float2(-0.42, 0.91)) * 4.3 - t * 0.83 + 1.7);
    float2 c = float2(0.45 * sin(t * 0.37), 0.30 * cos(t * 0.29 + 0.8));
    v += sin(length(q - c) * 6.2 - t * 0.96);
    v += 0.6 * sin(dot(q, float2(0.97, -0.24)) * 2.4 + t * 0.51 + 3.1);

    // Fold the sum into soft bands that ease from dark to light.
    float band = 0.5 + 0.5 * sin(v * 1.35 + t * 0.20);
    band = band * band * (3.0 - 2.0 * band);

    float edge = 1.0 - 0.45 * saturate(dot(q, q) * 1.1);
    float lum = (0.10 + 0.50 * band) * edge;
    return lum.xxx * light;
}

#include "../EffectMain.hlsli"
