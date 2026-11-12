// Marble: layered noise whose input is pushed around by two more layered noises (domain warping), read through a sine
// so thin bright veins run along the warped levels; a very faint static grain on top. Nine noise evaluations.
#include "../Common.hlsli"

float3 Effect(float2 p, float light)
{
    float2 q = Centred(p);
    float t = Flow;
    float2 a = q * 2.1;

    float2 w = float2(Layered(a + float2(0.0, t * 0.05), 3),
                      Layered(a + float2(5.2, 1.3) - t * 0.04, 3));
    float v = Layered(a + 3.2 * w + float2(1.7, 9.2) + t * 0.03, 3);

    float s = abs(sin(v * 10.0 + w.x * 3.0));
    float vein = saturate(1.0 - s);
    vein = vein * vein * vein * vein * vein * vein;
    float grain = (Random1(p) - 0.5) * 0.018;
    float lum = 0.07 + 0.32 * v + 0.42 * vein * (0.5 + w.y) + grain;
    return lum.xxx * light;
}

#include "../EffectMain.hlsli"
