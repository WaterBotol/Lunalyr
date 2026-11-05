#pragma once

// Layout must match VizUniforms in VisualizerUniforms.swift exactly (54 floats /
// 216 bytes). The CPU fills that Swift struct each frame and copies its raw
// bytes here; see VisualizerUniforms.swift for what each field means.
//
// `p` is the visualizer's own options in descriptor order — an array rather
// than p0…p15 fields so it can be indexed: each visualizer names its slots
// with `constant int k…` below, and a future generic host for data-only
// plugins can map declared options onto slots it knows nothing about.
struct VizUniforms {
    float2 resolution;
    float time;
    float dt;
    float bass;
    float mid;
    float treble;
    float level;
    float beat;
    float sensitivity;
    float speed;
    float palette;
    float p[16];
    float subBass;
    float lowMid;
    float highMid;
    float presence;
    float air;
    float trebleBeat;
    float flux;
    float centroid;
    float beatHit;        // 1 on the frame a kick was detected, else 0
    float trebleHit;      // 1 on the frame a hi-hat/snare transient landed
    float beatCount;      // kicks counted since the source started (= kickOnsets)
    float frame;          // frames rendered since launch
    float reduceMotion;   // 1 when the system Reduce Motion setting is on
    float intro;          // 0 → 1 ramp after this visualizer was built
    // Sound types at the displayed time (SoundTypeDetector; see VisualizerUniforms.swift).
    float soundKick;      // kick envelope 0..1 (attack 10 ms, release 0.26 s; × Punch; calm: slow, ≤ 0.5)
    float soundSnare;     // snare/clap envelope 0..1 (release 0.22 s)
    float soundHat;       // hi-hat envelope 0..1 (release 0.15 s)
    float soundBass;      // sustained bass line 0..1 (kick tails held out)
    float soundVocal;     // lead voice / melodic lead 0..1
    float soundPad;       // sustained harmonic bed 0..1
    float kickOnsets;     // +1 per kick; integer-valued, wraps at 2^24 → uint(u.kickOnsets)
    float snareOnsets;
    float hatOnsets;
    float kickAge;        // seconds since the newest kick at the displayed time (60 = none)
    float snareAge;
    float hatAge;
};

// The one invariant that used to be uncheckable: both structs are 54 floats,
// and the Swift side is pinned by VisualizerTests. Editing one without the
// other now fails the build here or the test suite there, instead of silently
// reading every field one slot over.
static_assert(sizeof(VizUniforms) == 216,
              "VizUniforms must stay byte-identical to the Swift struct in VisualizerUniforms.swift");

struct FSQuadOut {
    float4 position [[position]];
    float2 uv;
};

// Every scene returns through this. Buffer 5 holds the photosensitivity
// limiter's gain (ExposureLimiter.metal): 1 unless Reduce Motion or Dim
// Flashing Lights is on and the mean brightness is rising too fast.
static inline float4 limitedExposure(float3 colour, constant float* exposure) {
    return float4(colour * exposure[0], 1.0);
}

// Colour richness for scenes that are already in their final colours: the
// same luma/chroma mix the older scenes apply before tone mapping.
static inline float3 richness(float3 colour, float amount) {
    float luma = dot(colour, float3(0.299, 0.587, 0.114));
    return max(mix(float3(luma), colour, amount), 0.0);
}

// Recolour the surface while keeping the shader's light and shadow. The
// spatial ramp gives separate parts of each ribbon, field or sphere their own ink.
static inline float3 albumTint(float3 scene, float2 uv, constant float4* stops) {
    if (stops[0].w == 0.0) return scene;
    const float3 weights = float3(0.30, 0.59, 0.11);
    float luma = dot(clamp(scene, 0.0, 1.0), weights);
    float position = clamp(0.66 * uv.x + 0.34 * (1.0 - uv.y), 0.0, 1.0) * 4.0;
    int segment = min(3, int(position));
    float3 color = mix(stops[segment].rgb, stops[segment + 1].rgb, position - float(segment));
    float3 chroma = (color - dot(color, weights)) * stops[0].w;
    float low = min(chroma.r, min(chroma.g, chroma.b));
    float high = max(chroma.r, max(chroma.g, chroma.b));
    float scale = min(1.0, min(luma / max(0.0001, -low), (1.0 - luma) / max(0.0001, high)));
    return clamp(luma + chroma * scale, 0.0, 1.0);
}
