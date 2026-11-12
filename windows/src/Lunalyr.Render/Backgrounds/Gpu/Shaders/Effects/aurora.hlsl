// Aurora: a few glowing horizontal ribbons over a dark sky. Each ribbon has a bright lower edge with light streaming up
// from it, wavers along its length, swells and thins, and drifts; faint vertical rays run through the light.
#include "../Common.hlsli"

float3 Effect(float2 p, float light)
{
    float2 q = Centred(p);
    float t = Flow;
    float glow = 0.0;

    [unroll] for (int i = 0; i < 4; i++)
    {
        float fi = (float)i;
        float x = q.x;
        float y = -0.30 + 0.15 * fi
                + 0.060 * sin(x * (2.1 + 0.6 * fi) + t * (0.35 + 0.07 * fi) + fi * 1.7)
                + 0.035 * sin(x * (4.7 - 0.5 * fi) - t * (0.52 - 0.05 * fi) + fi * 4.1)
                + 0.060 * (Noise(float2(x * 1.6 + t * 0.12, fi * 11.0)) - 0.5);
        float swell = 0.5 + 0.25 * sin(x * (2.9 + 0.4 * fi) - t * 0.21 + fi * 2.6) + 0.25 * sin(x * 1.3 + t * 0.13 + fi);
        float thick = 0.014 + 0.036 * swell;
        float d = q.y - y; // positive below the ribbon's edge
        float body = d > 0.0 ? exp(-d * d / (thick * thick * 0.18)) : exp(d / (thick * 3.2));
        float rays = 0.72 + 0.14 * sin(x * 61.0 + fi * 13.0 + t * 0.20) + 0.14 * sin(x * 97.0 - fi * 7.0 - t * 0.27);
        glow += body * rays * (0.62 - 0.09 * fi);
    }

    // The sky lightens a little toward the bottom; the ribbons' light rolls off softly.
    float sky = 0.035 + 0.05 * saturate(q.y + 0.5);
    float lum = sky + 0.92 * (1.0 - exp(-glow * 1.15));
    return lum.xxx * light;
}

#include "../EffectMain.hlsli"
