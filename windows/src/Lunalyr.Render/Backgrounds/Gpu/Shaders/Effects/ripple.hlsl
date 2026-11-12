// Ripple: a water plane under a horizon, intersected analytically. Its slope is three sets of expanding circular
// ripples and a long swell; it reflects a sky with a low sun (so a glitter streak runs down the water), more so at
// grazing angles (Fresnel), and fades into haze with distance.
#include "../Common.hlsli"

static const float3 Sun = float3(0.22, 0.10, 0.97);

float SkyLight(float3 dir)
{
    float up = max(dir.y, 0.0);
    float s = saturate(dot(dir, normalize(Sun)));
    return 0.07 + 0.24 * exp(-up * 5.0) + 0.9 * pow(s, 900.0) + 0.18 * pow(s, 24.0);
}

// The slope of one set of rings centred on c.
float2 RingSlope(float2 xz, float2 c, float k, float speed, float t)
{
    float2 d = xz - c;
    float r = max(length(d), 1e-3);
    return (0.035 * k * cos(r * k - t * speed) * exp(-r * 0.22) / r) * d;
}

float3 Effect(float2 p, float light)
{
    float2 q = Centred(p);
    float t = Flow * 1.6;
    float3 rd = normalize(float3(q.x, -q.y - 0.14, 1.1));

    float lum = SkyLight(rd);
    if (rd.y < -1e-3)
    {
        float dist = 1.0 / -rd.y;   // the camera is one unit above the water
        float2 xz = rd.xz * dist;
        float2 slope = RingSlope(xz, float2(-1.6, 6.5), 4.0, 1.0, t)
                     + RingSlope(xz, float2(1.4, 4.2), 5.0, 1.2, t + 1.7)
                     + RingSlope(xz, float2(0.2, 10.0), 3.2, 0.9, t + 4.1);
        slope.y += 0.05 * cos(xz.y * 0.9 + xz.x * 0.2 + t * 0.7);
        slope /= 1.0 + dist * 0.12;   // distant ripples blur into the surface instead of aliasing
        float3 n = normalize(float3(-slope.x, 1.0, -slope.y));
        float3 r = reflect(rd, n);
        r.y = abs(r.y);
        float facing = saturate(dot(n, -rd));
        float fresnel = 0.03 + 0.97 * pow(1.0 - facing, 5.0);
        float water = lerp(0.045, SkyLight(r), fresnel);
        float haze = 1.0 - exp(-dist * 0.055);
        lum = lerp(water, 0.30, haze);
    }
    return saturate(lum).xxx * light;
}

#include "../EffectMain.hlsli"
