// Prism: a fan of soft light bands spreading from a source off the bottom-left. Red, green and blue each get the band
// set at a slightly different angle, so every band is split into fringes that widen with distance; the colour is then
// pulled most of the way back toward grey (the album gradient colours it). The fan sways slowly about its source.
#include "../Common.hlsli"

float3 Effect(float2 p, float light)
{
    float2 q = Centred(p);
    float t = Flow;
    float2 d = q - float2(-1.05, 0.62);
    float r = length(d);
    float a = atan2(d.y, d.x) - 0.32 * sin(t * 0.06) - 0.06 * sin(t * 0.13 + 1.0);
    float bend = 0.25 * sin(r * 1.6 - t * 0.22);

    float spread = 0.016 * (0.5 + r);
    float3 offsets = float3(-spread, 0.0, spread);
    float3 bands = 0.5 + 0.5 * cos((a + offsets + bend * 0.05) * 24.0 + 0.4 * sin(a * 7.0));
    bands = bands * bands * bands * bands;

    float fan = smoothstep(-1.35, -1.0, a) * (1.0 - smoothstep(-0.1, 0.25, a));
    float reach = exp(-r * 0.55) * smoothstep(0.05, 0.5, r);
    float3 rgb = bands * fan * reach;
    rgb = lerp(dot(rgb, 1.0 / 3.0).xxx, rgb, 0.45);
    float3 lit = 0.05 + 0.04 * exp(-r * 1.5) + 0.85 * rgb;
    return saturate(lit) * light;
}

#include "../EffectMain.hlsli"
