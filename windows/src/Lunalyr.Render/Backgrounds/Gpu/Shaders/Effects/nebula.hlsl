// Nebula: billowing cloud from layered noise, itself displaced by two more layered noises, drifting slowly. Thresholded
// so there are dark gaps; a thin band around one density level glows as bright wisps. Thirteen noise evaluations.
#include "../Common.hlsli"

float3 Effect(float2 p, float light)
{
    float2 q = Centred(p);
    float t = Flow;
    float2 a = q * 1.7 + float2(t * 0.030, -t * 0.014);

    float2 w = float2(Layered(a + float2(0.0, t * 0.02), 4),
                      Layered(a + float2(7.3, 2.9) - float2(t * 0.015, 0.0), 4));
    float n = Layered(a + 2.8 * w + float2(t * 0.02, 0.0), 5);

    float cloud = smoothstep(0.34, 0.78, n);
    float wisp = saturate(1.0 - abs(n - 0.60) * 7.0);
    wisp = wisp * wisp * wisp;
    float lum = 0.025 + 0.55 * cloud * cloud + 0.30 * wisp * (0.4 + w.x) + 0.05 * w.y;
    return lum.xxx * light;
}

#include "../EffectMain.hlsli"
