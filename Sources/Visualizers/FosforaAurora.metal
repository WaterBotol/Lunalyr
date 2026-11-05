// Ported from Fosfora Aurora, copyright (c) 2025-2026 Kevin Raymond, MIT.
// Original WGSL, pinned revision and local adaptations: ThirdParty/Fosfora.
// Complete notice is bundled as Resources/Fosfora-LICENSE.txt.

// The shared value noise in VisualizerShaderCommon.h replaces fosfora_noise2.

#include <metal_stdlib>
using namespace metal;
#include "VisualizerShaderCommon.h"

// Fosfora's option slots: the 60-150 Hz and 400-1200 Hz registers, so no
// ribbon contains its neighbours' frequencies (u.bass and u.mid are broad).
constant int kFosforaBass = 3;
constant int kFosforaMid = 4;

fragment float4 fosforaAuroraFragment(FSQuadOut in [[stage_in]],
                                     constant VizUniforms& u [[buffer(0)]],
                                     constant float4* album [[buffer(4)]],
                                     constant float* bands [[buffer(1)]],
                                     constant float* wave [[buffer(2)]],
                                     constant float* exposure [[buffer(5)]]) {
    float2 uv = in.uv;
    float2 p = (uv - 0.5) * float2(u.resolution.x / max(u.resolution.y, 1.0), 1.0);
    float t = u.time * u.speed;
    float travel = t * 1.3; // The containing clock already integrates Motion speed.
    float registers[7] = {u.subBass, u.p[kFosforaBass], u.lowMid, u.p[kFosforaMid],
        u.highMid, u.presence, u.air};
    float3 colours[7] = {float3(0.8, 0.1, 0.2), float3(1.0, 0.4, 0.1), float3(1.0, 0.8, 0.2),
        float3(0.2, 1.0, 0.3), float3(0.1, 0.7, 1.0), float3(0.3, 0.3, 1.0), float3(0.7, 0.2, 1.0)};
    float3 col = float3(0.002, 0.003, 0.008);
    for (int i = 0; i < 7; i++) {
        float fi = float(i);
        float bandValue = visualEnergy(registers[i], u.sensitivity);
        float section = spectrumAlongRibbon(bands, (fi + uv.x) / 7.0, u.sensitivity);
        float phase = travel * (0.8 + fi * 0.1);
        float curve = sin(p.x * (2.0 + fi * 0.5) + phase) * 0.05
            + sin(p.x * (2.0 + fi * 0.5) * 2.3 + phase * 0.7) * 0.03;
        float ripple = (vnoise(float2(p.x * 3.0 + phase * 0.5, fi * 2.0 + t * 0.1)) - 0.5) * 0.08;
        float dy = p.y - (fi - 3.0) / 7.0 * 0.85 - curve - ripple
            - sin(uv.x * 9.0 + phase) * section * 0.045;
        float width = 0.014 * (1.0 + 1.5 * bandValue + section);
        float glow = exp(-dy * dy / (width * width));
        col += colours[i] * glow * (0.06 + 1.7 * bandValue + 0.8 * section);
    }
    // Keep Fosfora's frequency curtains; omit its global onset/beat flashes
    // and shimmer so the material stays calm behind lyrics.
    float luma = dot(col, float3(0.299, 0.587, 0.114));
    col = max(mix(float3(luma), col, u.p[15]), 0.0);
    return limitedExposure(albumTint(col / (1.0 + col), in.uv, album), exposure);
}
