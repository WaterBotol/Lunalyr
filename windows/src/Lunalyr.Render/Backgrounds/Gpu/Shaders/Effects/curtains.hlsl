// Curtains: three hanging aurora sheets at different distances. Each has a wavy lower hem with light rising above it,
// fine vertical striations, and sways (its striations bend with height and time); it covers only stretches of the sky.
// Nearer sheets hang lower, are taller and brighter, with broader striations.
#include "../Common.hlsli"

float3 Effect(float2 p, float light)
{
    float2 q = Centred(p);
    float t = Flow;
    float lum = 0.035 + 0.03 * saturate(q.y + 0.5);

    [unroll] for (int i = 0; i < 3; i++)
    {
        float fi = (float)i;
        float sway = 0.035 * (1.0 + fi) * sin(q.y * 2.2 + t * (0.25 + 0.08 * fi) + fi * 2.0);
        float x = q.x + sway;
        float hem = 0.02 + 0.13 * fi + 0.07 * sin(x * (1.7 - 0.35 * fi) + t * 0.18 + fi * 1.3);
        float height = 0.22 + 0.13 * fi;
        float d = q.y - hem;
        float vertical = d < 0.0 ? exp(d / height) : exp(-d / 0.010);
        float fine = x * (75.0 - 20.0 * fi) + fi * 31.0;
        float stripes = 0.5 + 0.5 * sin(fine + t * 0.06) * sin(fine * 0.618 - 1.0 - t * 0.04);
        stripes = 0.30 + 0.70 * stripes * stripes;
        float spread = 0.5 + 0.3 * sin(x * 1.4 + t * 0.035 + fi * 3.0) + 0.2 * sin(x * 2.9 - t * 0.05 + fi);
        float presence = smoothstep(0.25, 0.65, spread);
        lum += vertical * stripes * presence * (0.26 + 0.18 * fi);
    }
    return saturate(lum).xxx * light;
}

#include "../EffectMain.hlsli"
