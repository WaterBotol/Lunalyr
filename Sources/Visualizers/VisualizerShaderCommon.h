#pragma once

// Adapted from Synesthia by Jon Jaques, MIT licence.
// Pinned source and local changes: ThirdParty/Synesthia/SOURCE.md.
// Copyright (c) 2026 Jon Jaques. See Resources/Synesthia-LICENSE.txt.

// Helpers shared by the native visualizer scenes. Every scene file includes
// this header, which brings in metal_stdlib and VisualizerShaderTypes.h
// (VizUniforms, FSQuadOut, limitedExposure, richness, albumTint) as well.
//
// Each .metal file is compiled as its own translation unit, so everything
// here is `static inline`: a scene gets its own copy of the helpers it calls,
// unused ones cost nothing and raise no unused-function warnings, and no
// symbol can clash when the files are linked into the default library.
// General building blocks (palette, spectrum sampling, noise) live here;
// helpers tied to one scene's look (the tunnel's centreline, the aurora's
// star layer) stay `static` in that scene's file.

#include <metal_stdlib>
using namespace metal;

#include "VisualizerShaderTypes.h"

// ---------------------------------------------------------------- helpers

// Cosine palette (Íñigo Quílez): a + b*cos(2π(c*t + d)) per channel maps any
// scalar t to a smooth cyclic gradient. `which` is u.palette (0 Prism,
// 1 Ember, 2 Ocean, 3 Violet, 4 Mono).
static inline float3 cosPalette(float t, float which) {
    float3 a, b, c, d;
    int p = int(which + 0.5);
    if (p == 1)      { a = float3(0.55, 0.25, 0.12); b = float3(0.45, 0.35, 0.20); c = float3(1.0); d = float3(0.00, 0.12, 0.25); } // Ember
    else if (p == 2) { a = float3(0.10, 0.35, 0.45); b = float3(0.25, 0.35, 0.45); c = float3(1.0); d = float3(0.00, 0.10, 0.20); } // Ocean
    else if (p == 3) { a = float3(0.40, 0.20, 0.50); b = float3(0.50, 0.30, 0.50); c = float3(1.0); d = float3(0.80, 0.90, 0.30); } // Violet
    else if (p == 4) { a = float3(0.60);             b = float3(0.40);             c = float3(1.0); d = float3(0.0);              } // Mono
    else             { a = float3(0.50);             b = float3(0.50);             c = float3(1.0); d = float3(0.00, 0.33, 0.67); } // Prism
    return a + b * cos(6.28318 * (c * t + d));
}

// Samples the 64-band spectrum at a continuous position x in 0...1, linearly
// interpolating between neighboring bands so sweeps look smooth.
static inline float bandAt(constant float* bands, float x) {
    float f = clamp(x, 0.0, 0.9999) * 63.0;
    int i = int(f);
    return mix(bands[i], bands[min(i + 1, 63)], fract(f));
}

// Soft knee: retain variation through the full sensitivity range.
static inline float visualEnergy(float value, float sensitivity) {
    float x = max(value, 0.0) * sqrt(max(sensitivity, 0.3));
    return x / (0.65 + x);
}

// The horizontal coordinate is a fixed logarithmic frequency axis. Smooth
// neighbours without scrolling the bins or turning them into sharp bars.
static inline float spectrumAlongRibbon(constant float* bands, float x, float sensitivity) {
    float energy = (bandAt(bands, x - 1.0 / 63.0) + 2.0 * bandAt(bands, x)
                  + bandAt(bands, x + 1.0 / 63.0)) * 0.25;
    return visualEnergy(energy, sensitivity);
}

// Samples the 256-point waveform at x in 0...1, averaged with its neighbors
// to soften single-sample spikes.
static inline float waveAt(constant float* wave, float x) {
    int i = int(clamp(x, 0.0, 0.9999) * 255.0);
    int a = max(i - 1, 0), b = min(i + 1, 255);
    return (wave[a] + wave[i] + wave[b]) / 3.0;
}

// ---------------------------------------------------------------- 2D noise

// Cheap 2D → pseudo-random hash in 0...1. Not statistically great, but fast
// and stable per input — used to scatter stars/particles deterministically.
static inline float hash21(float2 p) {
    p = fract(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

// Value noise: random values at integer grid points, smoothly interpolated
// between them. Gives soft, organic variation rather than white noise.
static inline float vnoise(float2 p) {
    float2 i = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash21(i);
    float b = hash21(i + float2(1.0, 0.0));
    float c = hash21(i + float2(0.0, 1.0));
    float d = hash21(i + float2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

// Fractal Brownian motion: several octaves of noise, each at double the
// frequency and half the amplitude of the last. The classic recipe for
// clouds, smoke, and haze.
static inline float fbm(float2 p) {
    float v = 0.0, amp = 0.5;
    for (int i = 0; i < 4; i++) {
        v += amp * vnoise(p);
        p *= 2.03;
        amp *= 0.5;
    }
    return v;
}

// ---------------------------------------------------------------- 3D noise

// 3D value-noise stack standing in for Shadertoy's RGBA-noise texture (the
// Spectrum Tunnel's rock, steam and glitter are carved from it).
static inline float hash31(float3 p) {
    p = fract(p * float3(443.897, 441.423, 437.195));
    p += dot(p, p.yzx + 19.19);
    return fract((p.x + p.y) * p.z);
}

static inline float vnoise3(float3 x) {
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
    return mix(mix(mix(n000, n100, f.x), mix(n010, n110, f.x), f.y),
               mix(mix(n001, n101, f.x), mix(n011, n111, f.x), f.y), f.z);
}

// Balanced three-octave fbm, ~0…0.875 — used for color/steam variation.
static inline float fbm3(float3 p) {
    float v = 0.0, amp = 0.5;
    for (int i = 0; i < 3; i++) {
        v += amp * vnoise3(p);
        p *= 2.03;
        amp *= 0.5;
    }
    return v;
}

// Demoscene-style fbm (after "Sailing Beyond"): heavily weighted first
// octave with frequency *tripling* per octave, ~0…0.33. Shaped with a power
// curve at the call site it makes terraced, cliff-like billows rather than
// soft lumps.
static inline float fbmSharp(float3 p) {
    float r = 0.0, w = 1.0, s = 1.0;
    for (int i = 0; i < 3; i++) {
        w *= 0.25;
        s *= 3.0;
        r += w * vnoise3(p * s);
    }
    return r;
}
