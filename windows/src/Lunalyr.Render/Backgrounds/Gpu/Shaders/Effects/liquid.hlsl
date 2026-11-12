// Liquid: six blobs on their own slow Lissajous paths; their inverse-square fields add into one surface, so blobs fuse
// and part like a lava lamp. The surface brightens toward where the field is strongest, with a faint halo outside.
#include "../Common.hlsli"

float3 Effect(float2 p, float light)
{
    float2 q = Centred(p);
    float t = Flow;
    float field = 0.0;

    [unroll] for (int i = 0; i < 6; i++)
    {
        float fi = (float)i;
        float2 c = float2(0.48 * sin(t * (0.11 + 0.031 * fi) + fi * 2.1),
                          0.24 * cos(t * (0.13 + 0.023 * fi) + fi * 1.3));
        float r = 0.125 + 0.035 * sin(fi * 3.7);
        float2 d = q - c;
        field += r * r / (dot(d, d) + 0.0015);
    }

    float body = smoothstep(0.88, 1.08, field);
    float depth = saturate((field - 1.0) / 7.0);
    float edge = exp(-(field - 1.0) * (field - 1.0) / 0.02);
    float lum = 0.06 + 0.07 * saturate(field * 0.8)
              + body * (0.20 + 0.62 * sqrt(depth))
              + 0.10 * edge * body;
    return lum.xxx * light;
}

#include "../EffectMain.hlsli"
