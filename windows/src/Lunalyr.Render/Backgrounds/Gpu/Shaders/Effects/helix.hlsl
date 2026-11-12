// Helix: two tubes winding round a common axis (a double helix) that runs away from a camera set off to one side, so
// the coils recede into the distance; the helix turns about its axis. Ray-marched (at most 48 steps) against a
// slope-corrected distance to the nearer tube, lit with diffuse, specular and rim light, and fogged with distance.
#include "../Common.hlsli"

static const float Coil = 0.62;   // helix radius
static const float Tube = 0.15;   // tube radius
static const float Pitch = 1.25;  // radians per unit along the axis

float2 Centre(float z, float spin) { return Coil * float2(cos(z * Pitch + spin), sin(z * Pitch + spin)); }

// Distance to the nearer tube (lower bound: the tube is slanted, so the in-plane distance is scaled down).
float Tubes(float3 pos, float spin, out float which)
{
    float2 c = Centre(pos.z, spin);
    float a = length(pos.xy - c);
    float b = length(pos.xy + c);
    which = a < b ? 0.0 : 1.0;
    return (min(a, b) - Tube) * 0.78;
}

float3 Effect(float2 p, float light)
{
    float2 q = Centred(p);
    float spin = Flow * 0.55;

    float3 ro = float3(3.0, 1.1, -1.2);
    float3 target = float3(-0.2, -0.1, 5.5);
    float3 fw = normalize(target - ro);
    float3 right = normalize(cross(float3(0.0, 1.0, 0.0), fw));
    float3 up = cross(fw, right);
    float3 rd = normalize(fw * 1.5 + right * q.x - up * q.y);

    float dist = 0.0, which = 0.0, hit = 0.0;
    [loop] for (int i = 0; i < 48; i++)
    {
        float d = Tubes(ro + rd * dist, spin, which);
        if (d < 0.002 * (1.0 + dist)) { hit = 1.0; break; }
        dist += d;
        if (dist > 28.0) break;
    }

    // A faint glow where the helix vanishes.
    float2 vanish = q - float2(0.03, -0.02);
    float lum = 0.04 + 0.08 * exp(-dot(vanish, vanish) * 18.0);
    if (hit > 0.0)
    {
        float3 pos = ro + rd * dist;
        float2 c = Centre(pos.z, spin) * (which > 0.5 ? -1.0 : 1.0);
        float3 n = normalize(float3(pos.xy - c, 0.0));
        float3 key = normalize(float3(-0.55, 0.70, -0.45));
        float diffuse = saturate(dot(n, key));
        float spec = pow(saturate(dot(reflect(rd, n), key)), 24.0);
        float rim = pow(1.0 - saturate(dot(n, -rd)), 3.0);
        float surface = (which > 0.5 ? 0.78 : 1.0) * (0.06 + 0.42 * diffuse) + 0.45 * spec + 0.22 * rim;
        lum = lerp(lum, surface, exp(-dist * 0.11));
    }
    return saturate(lum).xxx * light;
}

#include "../EffectMain.hlsli"
