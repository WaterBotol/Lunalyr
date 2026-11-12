// Vortex: four logarithmic spiral arms winding into a bright core. The arms' phase advances with the clock so they
// slide inward along themselves, like water circling a drain.
#include "../Common.hlsli"

float3 Effect(float2 p, float light)
{
    float2 q = Centred(p);
    float r = max(length(q), 1e-4);
    float a = atan2(q.y, q.x);
    float f = a * 4.0 + log(r) * 5.5 + Flow * 0.95;

    float arm = 0.5 + 0.5 * cos(f);
    float sharp = arm * arm * arm;
    float wisps = 0.5 + 0.5 * cos(f * 2.0 + log(r) * 3.0 - Flow * 0.4);
    float swirl = (0.75 * sharp + 0.15 * wisps * arm) * smoothstep(0.0, 0.07, r) * exp(-r * 1.3);
    float core = 0.85 * exp(-r * r / 0.0025) + 0.30 * exp(-r * 8.0);
    float lum = 0.04 + 0.75 * swirl + core;
    return saturate(lum).xxx * light;
}

#include "../EffectMain.hlsli"
