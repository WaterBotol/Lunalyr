// Mirror: a softly banded sky above a horizon and a polished floor below it that reflects the same sky, mirrored about
// the horizon, with a faint perspective wobble; both meet in a band of haze at the horizon.
#include "../Common.hlsli"

static const float Horizon = 0.06;   // in centred units, below the middle

// The sky at height s above the horizon (centred units) and across position x.
float SkyBands(float x, float s, float t)
{
    float v = s * 7.5 + 0.22 * sin(x * 2.1 + t * 0.20) + 0.12 * sin(x * 4.3 - t * 0.13 + 1.0) - t * 0.06;
    float band = 0.5 + 0.5 * sin(v * Tau * 0.5);
    band = band * band * (3.0 - 2.0 * band);
    return 0.06 + 0.16 * exp(-s * 3.5) + 0.20 * band * (0.4 + 0.6 * exp(-s * 1.5));
}

float3 Effect(float2 p, float light)
{
    float2 q = Centred(p);
    float t = Flow;
    float above = Horizon - q.y;
    float lum;
    if (above >= 0.0)
    {
        lum = SkyBands(q.x, above, t);
    }
    else
    {
        float below = -above;
        float depth = 0.4 / below;   // distance along the floor
        float wobble = below * (0.020 * sin(depth * 8.0 + t * 1.3) + 0.012 * sin(depth * 13.0 - t * 1.05 + q.x * 3.0));
        float x = q.x + 0.4 * wobble;
        lum = 0.72 * SkyBands(x, below + wobble, t) * (1.0 - 0.35 * saturate(below * 1.6));
    }
    float haze = exp(-abs(above) * 16.0);
    lum = lerp(lum, 0.32, 0.75 * haze);
    return lum.xxx * light;
}

#include "../EffectMain.hlsli"
