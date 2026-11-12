// Waves: a height field of four travelling swells seen from just above, rolling toward the viewer. Ray-marched (at most
// 48 steps, then one refinement), lit by a low key light ahead with a Fresnel rim; distance fades it into a dim sky.
#include "../Common.hlsli"

float Height(float2 xz, float t)
{
    float h = 0.20 * sin(xz.y * 0.85 + t * 1.10 + 0.4 * sin(xz.x * 0.35));
    h += 0.11 * sin(dot(xz, float2(0.38, 1.30)) + t * 1.45 + 1.0);
    h += 0.065 * sin(dot(xz, float2(-0.85, 1.90)) + t * 1.85 + 2.0);
    h += 0.035 * sin(dot(xz, float2(1.70, 2.90)) + t * 2.50);
    return h;
}

float Sky(float3 rd)
{
    return 0.05 + 0.16 * exp(-max(rd.y, 0.0) * 7.0);
}

float3 Effect(float2 p, float light)
{
    float2 q = Centred(p);
    float t = Flow * 0.9;
    float3 ro = float3(0.0, 1.35, 0.0);
    float3 rd = normalize(float3(q.x, -q.y - 0.16, 1.25));

    float lum = Sky(rd);
    if (rd.y < -0.002)
    {
        float dist = 0.0, last = 0.0, lastGap = 1.0;
        float hit = 0.0;
        [loop] for (int i = 0; i < 48; i++)
        {
            float3 pos = ro + rd * dist;
            float gap = pos.y - Height(pos.xz, t);
            if (gap < 0.0) { hit = 1.0; break; }
            last = dist;
            lastGap = gap;
            dist += max(gap * 0.75, 0.02 + dist * 0.012);
            if (dist > 45.0) break;
        }
        // Refine between the last point above the surface and the first below it.
        if (hit > 0.0)
        {
            float3 pos = ro + rd * dist;
            float below = pos.y - Height(pos.xz, t);
            dist = lerp(last, dist, lastGap / max(lastGap - below, 1e-4));
        }
        else
        {
            dist = -ro.y / rd.y;
        }

        float3 pos = ro + rd * dist;
        float e = 0.02 + dist * 0.004;
        float h0 = Height(pos.xz, t);
        float3 n = normalize(float3(h0 - Height(pos.xz + float2(e, 0.0), t), e, h0 - Height(pos.xz + float2(0.0, e), t)));

        float3 key = normalize(float3(-0.35, 0.40, 1.0));
        float diffuse = saturate(dot(n, key));
        float spec = pow(saturate(dot(reflect(rd, n), key)), 36.0);
        float rim = pow(1.0 - saturate(dot(n, -rd)), 4.0);
        float water = 0.04 + 0.20 * diffuse + 0.55 * spec + 0.28 * rim + 0.10 * saturate(h0 * 2.5);
        float fog = exp(-dist * 0.065);
        lum = lerp(Sky(float3(rd.x, 0.0, rd.z)), water, fog);
    }
    return saturate(lum).xxx * light;
}

#include "../EffectMain.hlsli"
