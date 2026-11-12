// Wormhole: flying down a round tunnel. Each pixel's distance from the centre maps to a depth along the tunnel; thin
// rings sit at whole depths and rush outward as the clock moves us forward, twisting strands spiral along the wall,
// and the far centre fades to dark.
#include "../Common.hlsli"

float3 Effect(float2 p, float light)
{
    float2 q = Centred(p);
    float t = Flow;
    float r = max(length(q), 1e-3);
    float angle = atan2(q.y, q.x) / Tau;   // -0.5...0.5, seamless when multiplied by whole numbers

    float depth = 0.30 / r;
    float v = depth * 1.6 + t * 0.85;
    float u = angle + depth * 0.22 + t * 0.015;

    float ring = smoothstep(0.80, 1.0, 1.0 - 2.0 * abs(frac(v) - 0.5));
    float strand = 0.5 + 0.5 * cos(Tau * (u * 7.0) + 1.6 * sin(v * 0.8));
    strand = strand * strand * strand * strand;
    strand *= strand * strand;
    float wall = 0.5 + 0.5 * cos(Tau * (u * 3.0 - v * 0.25));

    float fog = exp(-depth * 0.28);
    float lum = 0.03 + fog * (0.07 + 0.06 * wall + 0.55 * ring + 0.32 * strand * (0.6 + 0.4 * ring));
    return saturate(lum).xxx * light;
}

#include "../EffectMain.hlsli"
