// Adapted from Synesthia by Jon Jaques (MIT), via Lunalyr for Mac's VisualizerShaderTypes.h and
// VisualizerShaderCommon.h. Helpers shared by the native visualiser scenes: the uniforms (Lunalyr.Core's VizUniforms),
// the audio arrays, the album stops, the photosensitivity limiter's gain, and palette, spectrum and noise helpers.
//
// Bindings, the same for every scene:
//   b0 Viz       VizUniforms (224 bytes)
//   b1 Audio     64 bands and 256 waveform samples, four to a register
//   b2 Album     the cover's five stops, colour richness in w (all zero: keep the scene's own colours)
//   b3 Scene     per-scene data: Star Trails' 205-float4 state, or Wavefield's modes (3 float4s each)
//   t0           the limiter's state: x = the gain for sRGB-encoded output (1 while the limiter is off)

#ifndef VISUALISER_COMMON
#define VISUALISER_COMMON

cbuffer Viz : register(b0)
{
    float2 resolution;
    float time;
    float dt;
    float bass, mid, treble, level;
    float beat, sensitivity, speed, palette;
    float4 P[4];
    float subBass, lowMid, highMid, presence;
    float air, trebleBeat, flux, spectralCentroid; // the Mac's u.centroid ("centroid" is an HLSL keyword)
    float beatHit, trebleHit, beatCount, frameIndex;
    float reduceMotion, intro, soundKick, soundSnare;
    float soundHat, soundBass, soundVocal, soundPad;
    float kickOnsets, snareOnsets, hatOnsets, kickAge;
    float snareAge, hatAge, vizPad0, vizPad1;
};

cbuffer Audio : register(b1)
{
    float4 Bands4[16];
    float4 Wave4[64];
};

cbuffer Album : register(b2)
{
    float4 album[5];
};

cbuffer Scene : register(b3)
{
    float4 sceneData[205];
};

Texture2D<float2> ExposureState : register(t0);

struct FSQuadOut
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

// The scene's option slot i (the Mac's u.p[i]).
float opt(int i) { return P[i >> 2][i & 3]; }

float bandValue(int i) { return Bands4[i >> 2][i & 3]; }
float waveValue(int i) { return Wave4[i >> 2][i & 3]; }

// Every scene returns through this: the limiter's gain multiplies sRGB-encoded output.
float4 limitedExposure(float3 colour)
{
    return float4(colour * ExposureState.Load(int3(0, 0, 0)).x, 1.0);
}

// Colour richness for scenes that are already in their final colours.
float3 richness(float3 colour, float amount)
{
    float luma = dot(colour, float3(0.299, 0.587, 0.114));
    return max(lerp(float3(luma, luma, luma), colour, amount), 0.0);
}

// Recolour the surface while keeping the shader's light and shadow. The spatial ramp gives separate parts of each
// ribbon, field or sphere their own ink.
float3 albumTint(float3 scene, float2 uv)
{
    if (album[0].w == 0.0) return scene;
    const float3 weights = float3(0.30, 0.59, 0.11);
    float luma = dot(saturate(scene), weights);
    float position = saturate(0.66 * uv.x + 0.34 * (1.0 - uv.y)) * 4.0;
    int segment = min(3, int(position));
    float3 colour = lerp(album[segment].rgb, album[segment + 1].rgb, position - float(segment));
    float3 chroma = (colour - dot(colour, weights)) * album[0].w;
    float low = min(chroma.r, min(chroma.g, chroma.b));
    float high = max(chroma.r, max(chroma.g, chroma.b));
    float scale = min(1.0, min(luma / max(0.0001, -low), (1.0 - luma) / max(0.0001, high)));
    return saturate(luma + chroma * scale);
}

// Cosine palette: a + b*cos(2π(c*t + d)) per channel. `which` is the palette (0 Prism, 1 Ember, 2 Ocean, 3 Violet, 4 Mono).
float3 cosPalette(float t, float which)
{
    float3 a, b, c, d;
    int p = int(which + 0.5);
    c = float3(1.0, 1.0, 1.0);
    if (p == 1)      { a = float3(0.55, 0.25, 0.12); b = float3(0.45, 0.35, 0.20); d = float3(0.00, 0.12, 0.25); }
    else if (p == 2) { a = float3(0.10, 0.35, 0.45); b = float3(0.25, 0.35, 0.45); d = float3(0.00, 0.10, 0.20); }
    else if (p == 3) { a = float3(0.40, 0.20, 0.50); b = float3(0.50, 0.30, 0.50); d = float3(0.80, 0.90, 0.30); }
    else if (p == 4) { a = float3(0.6, 0.6, 0.6);    b = float3(0.4, 0.4, 0.4);    d = float3(0.0, 0.0, 0.0); }
    else             { a = float3(0.5, 0.5, 0.5);    b = float3(0.5, 0.5, 0.5);    d = float3(0.00, 0.33, 0.67); }
    return a + b * cos(6.28318 * (c * t + d));
}

// The 64-band spectrum at a continuous position x in 0...1, interpolating between neighbouring bands.
float bandAt(float x)
{
    float f = clamp(x, 0.0, 0.9999) * 63.0;
    int i = int(f);
    return lerp(bandValue(i), bandValue(min(i + 1, 63)), frac(f));
}

// Soft knee: retain variation through the full sensitivity range.
float visualEnergy(float value, float sens)
{
    float x = max(value, 0.0) * sqrt(max(sens, 0.3));
    return x / (0.65 + x);
}

// The horizontal coordinate is a fixed logarithmic frequency axis; smooth neighbours without scrolling the bins.
float spectrumAlongRibbon(float x, float sens)
{
    float energy = (bandAt(x - 1.0 / 63.0) + 2.0 * bandAt(x) + bandAt(x + 1.0 / 63.0)) * 0.25;
    return visualEnergy(energy, sens);
}

// The 256-point waveform at x in 0...1, averaged with its neighbours.
float waveAt(float x)
{
    int i = int(clamp(x, 0.0, 0.9999) * 255.0);
    int a = max(i - 1, 0), b = min(i + 1, 255);
    return (waveValue(a) + waveValue(i) + waveValue(b)) / 3.0;
}

// Metal's fract and mix, for readability against the reference.
float  fract(float x)  { return x - floor(x); }
float2 fract(float2 x) { return x - floor(x); }
float3 fract(float3 x) { return x - floor(x); }

// Cheap 2D -> 0...1 hash, stable per input.
float hash21(float2 p)
{
    p = fract(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

// Value noise: random values at grid points, smoothly interpolated.
float vnoise(float2 p)
{
    float2 i = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash21(i);
    float b = hash21(i + float2(1.0, 0.0));
    float c = hash21(i + float2(0.0, 1.0));
    float d = hash21(i + float2(1.0, 1.0));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

// Four octaves of value noise.
float fbm(float2 p)
{
    float v = 0.0, amp = 0.5;
    [unroll] for (int i = 0; i < 4; i++)
    {
        v += amp * vnoise(p);
        p *= 2.03;
        amp *= 0.5;
    }
    return v;
}

float hash31(float3 p)
{
    p = fract(p * float3(443.897, 441.423, 437.195));
    p += dot(p, p.yzx + 19.19);
    return fract((p.x + p.y) * p.z);
}

float vnoise3(float3 x)
{
    float3 i = floor(x), f = fract(x);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = hash31(i);
    float n100 = hash31(i + float3(1.0, 0.0, 0.0));
    float n010 = hash31(i + float3(0.0, 1.0, 0.0));
    float n110 = hash31(i + float3(1.0, 1.0, 0.0));
    float n001 = hash31(i + float3(0.0, 0.0, 1.0));
    float n101 = hash31(i + float3(1.0, 0.0, 1.0));
    float n011 = hash31(i + float3(0.0, 1.0, 1.0));
    float n111 = hash31(i + float3(1.0, 1.0, 1.0));
    return lerp(lerp(lerp(n000, n100, f.x), lerp(n010, n110, f.x), f.y),
                lerp(lerp(n001, n101, f.x), lerp(n011, n111, f.x), f.y), f.z);
}

// Three octaves of 3D value noise, ~0...0.875.
float fbm3(float3 p)
{
    float v = 0.0, amp = 0.5;
    [unroll] for (int i = 0; i < 3; i++)
    {
        v += amp * vnoise3(p);
        p *= 2.03;
        amp *= 0.5;
    }
    return v;
}

#endif
