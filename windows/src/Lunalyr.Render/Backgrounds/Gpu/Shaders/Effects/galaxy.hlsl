// Galaxy: a disc seen at a tilt (turned, then squashed), with two logarithmic spiral arms broken up by fine noise grain
// that turns with the disc, a soft disc glow and a bright core.
#include "../Common.hlsli"

float3 Effect(float2 p, float light)
{
    float2 q = Centred(p);
    float2 g = Turn(q, -0.42) * 0.72;
    g.y *= 2.4;                                // the disc's tilt
    float r = length(g);
    float2 d = Turn(g, Flow * 0.07);           // the disc's own frame, turning
    float a = atan2(d.y, d.x);

    float f = 2.0 * a - log(max(r, 1e-3)) * 4.2;
    float arm = saturate(0.5 + 0.5 * cos(f));
    arm = arm * arm * sqrt(arm);
    float grain = 0.6 * Noise(d * 34.0) + 0.4 * Noise(d * 85.0 + 7.0);
    grain = grain * grain;

    float disc = exp(-r * 3.0);
    float core = 0.85 * exp(-r * r * 110.0) + 0.35 * exp(-r * 9.0);
    float lum = 0.03 + disc * (0.06 + 0.85 * arm * (0.25 + 1.2 * grain)) + core;
    return saturate(lum).xxx * light;
}

#include "../EffectMain.hlsli"
