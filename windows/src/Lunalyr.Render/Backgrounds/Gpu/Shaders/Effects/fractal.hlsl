// Fractal: a Julia set (z -> z^2 + c) with a smooth escape count; c wanders slowly around a circle so the shape
// morphs, and the plane turns slowly. Up to 64 iterations.
#include "../Common.hlsli"

float3 Effect(float2 p, float light)
{
    float2 q = Centred(p);
    float t = Flow;
    float2 z = Turn(q, t * 0.03) * 2.3;
    // Keep c near the edge of the connected region (angles about 1.9...2.9 on this circle) so the set stays whole.
    float wander = 2.42 + 0.34 * sin(t * 0.043) + 0.16 * sin(t * 0.071 + 1.0);
    float2 c = 0.776 * float2(cos(wander), sin(wander));

    float n = 0.0;
    float escaped = 0.0;
    [loop] for (int i = 0; i < 64; i++)
    {
        z = float2(z.x * z.x - z.y * z.y, 2.0 * z.x * z.y) + c;
        if (dot(z, z) > 256.0) { escaped = 1.0; break; }
        n += 1.0;
    }

    // Smooth count: subtract how far past the escape radius the last step went.
    float m = max(log2(max(dot(z, z), 1.0001)) * 0.5, 1.0001);
    float smoothCount = n + 1.0 - log2(m);
    float v = saturate(smoothCount / 40.0);
    float outside = 0.05 + 0.85 * sqrt(v) * (0.85 + 0.15 * sin(smoothCount * 0.45));
    float inside = 0.03 + 0.04 * saturate(length(z) * 0.5);
    float lum = lerp(inside, outside, escaped);
    return lum.xxx * light;
}

#include "../EffectMain.hlsli"
