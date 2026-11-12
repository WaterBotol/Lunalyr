// Kaleidoscope: layered ripples folded into a mirrored eight-fold wedge around the centre; the whole turns slowly, the
// ripple sources drift so the detail shifts, and it fades toward the edges.
#include "../Common.hlsli"

float3 Effect(float2 p, float light)
{
    float2 q = Centred(p);
    float t = Flow;
    float r = length(q);

    // Fold the angle into one mirrored wedge (eight wedges, sixteen mirror images).
    static const float Wedge = Tau / 8.0;
    float a = atan2(q.y, q.x) + t * 0.045;
    float s = abs(frac(a / Wedge) * Wedge - 0.5 * Wedge);
    float2 k = r * float2(cos(s), sin(s));

    // Ripples from three wandering sources inside the wedge, plus one slow plane wave.
    float2 c1 = float2(0.22 + 0.08 * sin(t * 0.23), 0.05 + 0.04 * cos(t * 0.31));
    float2 c2 = float2(0.40 + 0.10 * cos(t * 0.17), 0.12 * sin(t * 0.21 + 1.0));
    float2 c3 = float2(0.10 * sin(t * 0.13 + 2.0), 0.02);
    float v = sin(length(k - c1) * 34.0 - t * 0.90);
    v += sin(length(k - c2) * 22.0 + t * 0.60 + 1.3);
    v += 0.7 * sin(length(k - c3) * 48.0 - t * 1.20);
    v += 0.6 * sin(k.x * 18.0 + k.y * 9.0 + t * 0.40);

    float band = 0.5 + 0.5 * sin(v * 1.25);
    band *= band;
    float fade = 1.0 - smoothstep(0.22, 0.85, r);
    float lum = 0.05 + (0.10 + 0.66 * band) * fade;
    return lum.xxx * light;
}

#include "../EffectMain.hlsli"
