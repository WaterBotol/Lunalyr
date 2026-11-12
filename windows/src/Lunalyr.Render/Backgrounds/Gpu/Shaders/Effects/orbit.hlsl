// Orbit: four solid spheres on a tilted circle around the centre, so they pass in front of and behind one another.
// Each pixel's ray is tested against every sphere analytically (no marching); the nearest hit is lit with diffuse,
// specular and rim light over a soft sky.
#include "../Common.hlsli"

float3 Effect(float2 p, float light)
{
    float2 q = Centred(p);
    float t = Flow * 0.28;
    float3 rd = normalize(float3(q.x, -q.y, 1.7));

    float best = 1e4;
    float3 bestCentre = float3(0.0, 0.0, 0.0);
    float bestLevel = 0.0;
    [unroll] for (int i = 0; i < 4; i++)
    {
        float fi = (float)i;
        float theta = t * (1.0 + 0.07 * fi) + fi * (Tau / 4.0) + 0.3 * sin(fi * 2.7);
        float orbitRadius = 1.25 + 0.15 * sin(fi * 1.9);
        float3 c = float3(orbitRadius * cos(theta), -0.38 * sin(theta) + 0.05 * fi - 0.08, 5.2 + orbitRadius * sin(theta));
        float radius = 0.30 + 0.07 * frac(fi * 0.618034 + 0.3);
        float b = dot(c, rd);
        float h = b * b - dot(c, c) + radius * radius;
        if (h > 0.0)
        {
            float hit = b - sqrt(h);
            if (hit > 0.0 && hit < best) { best = hit; bestCentre = c; bestLevel = 0.75 + 0.25 * frac(fi * 0.414214 + 0.5); }
        }
    }

    float lum = 0.07 + 0.10 * saturate(0.6 - length(q)) + 0.05 * saturate(-q.y + 0.3);
    if (best < 1e3)
    {
        float3 n = normalize(rd * best - bestCentre);
        float3 key = normalize(float3(-0.5, 0.65, -0.55));
        float diffuse = saturate(dot(n, key));
        float spec = pow(saturate(dot(reflect(rd, n), key)), 32.0);
        float rim = pow(1.0 - saturate(dot(n, -rd)), 3.0);
        float depthDim = saturate(1.25 - (bestCentre.z - 4.0) * 0.18);
        lum = bestLevel * depthDim * (0.07 + 0.50 * diffuse + 0.22 * rim) + 0.45 * spec;
    }
    return saturate(lum).xxx * light;
}

#include "../EffectMain.hlsli"
