// Aurora Curtains. Lunalyr's own scene rather than part of the Synesthia
// import; its palette and spectrum helpers come from VisualizerShaderCommon.h,
// which carries that notice.

#include <metal_stdlib>
using namespace metal;
#include "VisualizerShaderCommon.h"

// GPU Aurora's flowing material, with a frequency axis along every curtain.
fragment float4 curtainAuroraFragment(FSQuadOut in [[stage_in]],
                                      constant VizUniforms& u [[buffer(0)]],
                                      constant float4* album [[buffer(4)]],
                                      constant float* bands [[buffer(1)]],
                                      constant float* wave [[buffer(2)]],
                                      constant float* exposure [[buffer(5)]]) {
    float2 uv = in.uv;
    float local = spectrumAlongRibbon(bands, uv.x, u.sensitivity);
    float glow = 0.0;
    for (int i = 0; i < 5; i++) {
        float fi = float(i);
        float phase = u.time * u.speed * (0.5 + fi * 0.14) + fi * 2.1;
        // Each line favours a different register while keeping the complete
        // bass-left/treble-right axis. Notes affect their own section, never
        // a whole-frame translation or a global brightness pulse.
        float emphasis = 0.45 + 0.55 * exp(-pow((uv.x - fi / 4.0) * 2.8, 2.0));
        float response = local * emphasis;
        float centre = 0.5 + sin(uv.x * 2.3 + phase) * 0.16
            + sin(uv.x * 5.1 - phase * 0.7) * 0.06
            + sin(uv.x * (5.0 + fi) + phase + fi) * response * 0.17;
        float thickness = 0.018 + 0.009 * (1.0 + sin(phase * 0.8)) + 0.045 * response;
        float band = thickness / (abs(uv.y - centre) + thickness * 0.55);
        glow += band * (0.15 + 0.10 * sin(phase) + 1.15 * response);
    }
    glow = clamp(glow * 0.28, 0.0, 1.7);
    float3 colour = mix(cosPalette(0.16, u.palette), cosPalette(0.7, u.palette), clamp(uv.y * 1.2, 0.0, 1.0));
    float3 col = colour * glow;
    float luma = dot(col, float3(0.299, 0.587, 0.114));
    col = max(mix(float3(luma), col, u.p[15]), 0.0);
    col = col / (1.0 + col);
    return limitedExposure(albumTint(col, in.uv, album), exposure);
}
