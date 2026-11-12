// Ported from Fosfora Aurora, copyright (c) 2025-2026 Kevin Raymond, MIT, via Lunalyr for Mac's FosforaAurora.metal.
// The shared value noise in Common.hlsli replaces fosfora_noise2.
#include "Common.hlsli"

// Fosfora's option slots: the 60-150 Hz and 400-1200 Hz registers, so no ribbon contains its neighbours' frequencies
// (bass and mid are broad).
static const int kFosforaBass = 3;
static const int kFosforaMid = 4;

static const float3 kFosforaColours[7] =
{
    float3(0.8, 0.1, 0.2), float3(1.0, 0.4, 0.1), float3(1.0, 0.8, 0.2), float3(0.2, 1.0, 0.3),
    float3(0.1, 0.7, 1.0), float3(0.3, 0.3, 1.0), float3(0.7, 0.2, 1.0),
};

float4 main(FSQuadOut i) : SV_Target
{
    float2 uv = i.uv;
    float2 p = (uv - 0.5) * float2(resolution.x / max(resolution.y, 1.0), 1.0);
    float t = time * speed;
    float travel = t * 1.3; // The containing clock already integrates Motion speed.
    float registers[7] = { subBass, opt(kFosforaBass), lowMid, opt(kFosforaMid), highMid, presence, air };
    float3 col = float3(0.002, 0.003, 0.008);
    [unroll] for (int r = 0; r < 7; r++)
    {
        float fi = float(r);
        float energy = visualEnergy(registers[r], sensitivity);
        float section = spectrumAlongRibbon((fi + uv.x) / 7.0, sensitivity);
        float phase = travel * (0.8 + fi * 0.1);
        float curve = sin(p.x * (2.0 + fi * 0.5) + phase) * 0.05
            + sin(p.x * (2.0 + fi * 0.5) * 2.3 + phase * 0.7) * 0.03;
        float ripple = (vnoise(float2(p.x * 3.0 + phase * 0.5, fi * 2.0 + t * 0.1)) - 0.5) * 0.08;
        float dy = p.y - (fi - 3.0) / 7.0 * 0.85 - curve - ripple
            - sin(uv.x * 9.0 + phase) * section * 0.045;
        float width = 0.014 * (1.0 + 1.5 * energy + section);
        float glow = exp(-dy * dy / (width * width));
        col += kFosforaColours[r] * glow * (0.06 + 1.7 * energy + 0.8 * section);
    }
    // Keep Fosfora's frequency curtains; omit its global onset/beat flashes and shimmer so the material stays calm
    // behind lyrics.
    float luma = dot(col, float3(0.299, 0.587, 0.114));
    col = max(lerp(float3(luma, luma, luma), col, opt(15)), 0.0);
    return limitedExposure(albumTint(col / (1.0 + col), uv));
}
