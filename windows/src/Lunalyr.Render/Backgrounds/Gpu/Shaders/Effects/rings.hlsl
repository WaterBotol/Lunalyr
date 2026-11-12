// Rings: thin concentric rings at even steps of log(radius), so they emerge small at the centre and grow and spread
// as the clock advances, as if coming toward the viewer; each ring keeps its own brightness, nearer rings are wider
// and brighter, and the stream recycles without end.
#include "../Common.hlsli"

float3 Effect(float2 p, float light)
{
    float2 q = Centred(p);
    float r = max(length(q), 1e-4);
    static const float Density = 2.4;
    float s = log(r) * Density - Flow * 0.30;
    float index = floor(s + 0.5);
    float offset = abs(s - index) * r / Density;   // distance to the ring, in centred units

    float width = 0.0018 + 0.0045 * r;
    float ring = exp(-offset * offset / (width * width));
    float halo = exp(-offset / (width * 7.0));
    float level = 0.55 + 0.45 * RandomOf(index, 3.0);
    float reach = smoothstep(0.015, 0.22, r) * (0.45 + 0.55 * saturate(r * 1.3));
    float lum = 0.04 + 0.05 * exp(-r * 4.0) + reach * level * (0.72 * ring + 0.22 * halo);
    return saturate(lum).xxx * light;
}

#include "../EffectMain.hlsli"
