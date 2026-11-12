// Aurora Curtains, ported from Lunalyr for Mac's CurtainAurora.metal (Lunalyr's own scene; the palette and spectrum
// helpers in Common.hlsli carry the Synesthia notice). GPU Aurora's flowing material, with a frequency axis along every
// curtain.
#include "Common.hlsli"

float4 main(FSQuadOut i) : SV_Target
{
    float2 uv = i.uv;
    float local = spectrumAlongRibbon(uv.x, sensitivity);
    float glow = 0.0;
    [unroll] for (int c = 0; c < 5; c++)
    {
        float fi = float(c);
        float phase = time * speed * (0.5 + fi * 0.14) + fi * 2.1;
        // Each line favours a different register while keeping the complete bass-left/treble-right axis. Notes affect
        // their own section, never a whole-frame translation or a global brightness pulse.
        float offset = (uv.x - fi / 4.0) * 2.8;
        float emphasis = 0.45 + 0.55 * exp(-offset * offset);
        float response = local * emphasis;
        float centre = 0.5 + sin(uv.x * 2.3 + phase) * 0.16
            + sin(uv.x * 5.1 - phase * 0.7) * 0.06
            + sin(uv.x * (5.0 + fi) + phase + fi) * response * 0.17;
        float thickness = 0.018 + 0.009 * (1.0 + sin(phase * 0.8)) + 0.045 * response;
        float band = thickness / (abs(uv.y - centre) + thickness * 0.55);
        glow += band * (0.15 + 0.10 * sin(phase) + 1.15 * response);
    }
    glow = clamp(glow * 0.28, 0.0, 1.7);
    float3 colour = lerp(cosPalette(0.16, palette), cosPalette(0.7, palette), saturate(uv.y * 1.2));
    float3 col = colour * glow;
    float luma = dot(col, float3(0.299, 0.587, 0.114));
    col = max(lerp(float3(luma, luma, luma), col, opt(15)), 0.0);
    col = col / (1.0 + col);
    return limitedExposure(albumTint(col, uv));
}
