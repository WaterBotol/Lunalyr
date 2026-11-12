// Tidal: smooth contour bands of a slowly flowing noise field, with one level traced as a thin bright filament (its
// width held to about a pixel with screen-space derivatives); a gentle veil and a vignette.
#include "../Common.hlsli"

float3 Effect(float2 p, float light)
{
    float2 q = Centred(p);
    float t = Flow;
    float2 x = q * 1.5;
    float f = Layered(x + float2(t * 0.040, -t * 0.022), 3) + 0.12 * sin(x.x * 1.3 + x.y * 0.7 + t * 0.10);

    float bands = 0.5 + 0.5 * cos(f * Tau * 5.0);
    bands = smoothstep(0.15, 0.85, bands);

    float level = 0.56;
    float pixels = abs(f - level) / max(fwidth(f), 1e-5);
    float filament = exp(-pixels * pixels / 2.2);
    float glow = exp(-abs(f - level) * 45.0);

    float veil = 0.05 * Noise(q * 1.1 + float2(t * 0.02, 3.0));
    float vignette = 1.0 - 0.55 * saturate(dot(q, q));
    float lum = (0.07 + 0.16 * bands + 0.60 * filament + 0.20 * glow + veil) * vignette;
    return saturate(lum).xxx * light;
}

#include "../EffectMain.hlsli"
