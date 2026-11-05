// Adapted from Synesthia by Jon Jaques, MIT licence.
// Pinned source and local changes: ThirdParty/Synesthia/SOURCE.md.
// Copyright (c) 2026 Jon Jaques. See Resources/Synesthia-LICENSE.txt.

// Spectral Aurora and Silk Ribbons both draw with auroraFragment: Silk
// Ribbons is the same renderer with restrained settings (its palette and
// option slots are set in AudioVisualizerView.Coordinator.uniforms).

#include <metal_stdlib>
using namespace metal;
#include "VisualizerShaderCommon.h"

// ---------------------------------------------------------------- Aurora
//
// A night-sky scene built in layers, back to front: gradient sky, a star
// field that flashes with the percussion, haze, ground fog, and up to eight
// aurora ribbons — one per instrument group, sub-bass at the bottom of the
// sky through air at the top, each with its own motion personality and each
// shaded along its length by the fine spectrum inside its own range.

// The aurora's option slots, in descriptor order.
constant int kAuroraRibbons = 0;
constant int kAuroraHeight = 1;
constant int kAuroraStars = 2;

// One layer of the aurora's star field: a jittered grid where ~10% of cells
// hold a star, each with its own size, tint, twinkle phase, and flash
// gates. The air band sets the ambient twinkle level; every hi-hat lights a
// different constellation and every kick a different (larger) one, so the
// sky visibly plays the drum kit.
static float3 auroraStarLayer(float2 sp, float scale, constant VizUniforms& u) {
    float2 g = sp * scale;
    float2 id = floor(g);
    float h1 = hash21(id);
    if (h1 < 0.90) { return float3(0.0); }
    float2 offs = float2(hash21(id + 7.1), hash21(id + 3.7)) - 0.5;
    float2 f = fract(g) - 0.5 - offs * 0.7;
    float d = length(f);
    float h2 = hash21(id + 11.3);
    float h3 = hash21(id + 17.9);
    float h4 = hash21(id + 23.7);
    float core = exp(-d * d * (150.0 - 90.0 * h2));
    // 4-point diffraction cross on the brightest few
    float sparkle = (exp(-fabs(f.x) * 42.0) * exp(-fabs(f.y) * 8.0)
                   + exp(-fabs(f.y) * 42.0) * exp(-fabs(f.x) * 8.0))
                  * max(h1 - 0.975, 0.0) * 40.0;
    float twinkle = 0.45 + 0.55 * sin(u.time * (2.0 + 4.0 * h3) + h3 * 40.0);
    // two independent, periodically reshuffled gates: each hi-hat lights a
    // different constellation, each kick a different (larger) one
    float gateHat = step(0.55, fract(h4 + floor(u.time * 1.5) * 0.618));
    float gateKick = step(0.45, fract(h4 * 3.7 + floor(u.time * 2.0) * 0.618));
    float amp = (0.30 + 0.70 * twinkle) * (0.30 + 0.70 * clamp(u.air * u.sensitivity, 0.0, 1.0))
              + gateHat * u.trebleBeat * 1.3
              + gateKick * u.beat * 1.2;
    float3 tint = mix(float3(1.0), cosPalette(h2, u.palette), 0.35);
    return tint * (core + sparkle) * amp * (h1 - 0.90) * 10.0;
}

fragment float4 auroraFragment(FSQuadOut in [[stage_in]],
                               constant VizUniforms& u [[buffer(0)]],
                               constant float4* album [[buffer(4)]],
                               constant float* bands [[buffer(1)]],
                               constant float* wave [[buffer(2)]],
                               constant float* exposure [[buffer(5)]]) {
    float2 uv = in.uv;
    float aspect = u.resolution.x / max(u.resolution.y, 1.0);
    float2 suv = float2(uv.x * aspect, uv.y);
    float starsAmt = u.p[kAuroraStars];

    // Night-sky gradient, tinted faintly by the palette and the spectral
    // centroid so the sky's mood follows the music's brightness.
    float3 col = mix(float3(0.008, 0.010, 0.026), float3(0.016, 0.026, 0.050), uv.y);
    col += cosPalette(0.62 + u.centroid * 0.3, u.palette) * 0.012;

    // the star field (see auroraStarLayer): three depths of sky
    float3 stars = auroraStarLayer(suv, 40.0, u)
                 + auroraStarLayer(suv + 5.3, 75.0, u) * 0.6
                 + auroraStarLayer(suv + 9.1, 140.0, u) * 0.35;
    col += stars * starsAmt * smoothstep(0.16, 0.50, uv.y);

    // drifting haze that brightens with the low end, tinted by brightness
    float haze = fbm(uv * 3.0 + float2(u.time * 0.05 * u.speed, 0.0));
    col += cosPalette(0.6 + u.centroid * 0.3 + u.time * 0.01, u.palette) * haze
         * (0.03 + 0.10 * u.bass * u.sensitivity);

    // ground fog breathing with the sub-bass
    float fog = fbm(float2(uv.x * 4.0 + u.time * 0.08 * u.speed, uv.y * 9.0));
    col += cosPalette(0.05, u.palette) * fog * smoothstep(0.30, 0.0, uv.y)
         * (0.04 + 0.30 * u.subBass * u.sensitivity);

    int layers = clamp(int(u.p[kAuroraRibbons] + 0.5), 2, 8);
    float height = u.p[kAuroraHeight];

    // One ribbon per instrument group, low to high: sub-bass at the bottom
    // of the sky through air at the top. Each has its own motion
    // personality — lows are fat and slow and jump on the kick, mids carry
    // the waveform (melody and vocals) and answer onsets, highs are thin
    // and fast and shiver on hi-hats — and each is shaded along its length
    // by the fine spectrum *inside* its own frequency range, so every
    // ribbon is a small equalizer for its register.
    float comps[8] = {u.subBass, u.bass, u.lowMid, u.mid,
                      u.highMid, u.presence, u.treble, u.air};
    for (int i = 0; i < 8; i++) {
        if (i >= layers) break;
        float fi = float(i) / float(max(layers - 1, 1));
        int cIdx = int(fi * 7.0 + 0.5);
        float reg = float(cIdx) / 7.0;
        float compE = visualEnergy(comps[cIdx], u.sensitivity);
        // the fine spectrum inside this register, spread across the screen
        float localE = spectrumAlongRibbon(bands, (float(cIdx) + clamp(uv.x, 0.0, 1.0)) / 8.0, u.sensitivity);
        // transient personality: lows jump on the kick, mids on onsets,
        // highs shiver on hi-hat transients
        float transient = u.beat * (1.0 - smoothstep(0.15, 0.45, reg))
                        + u.flux * exp(-pow((reg - 0.5) * 3.5, 2.0))
                        + u.trebleBeat * smoothstep(0.55, 0.85, reg);
        // mids carry the waveform; lows and highs mostly ignore it
        float wWeight = exp(-pow((reg - 0.5) * 2.4, 2.0));
        float w = (localE - 0.3) * 0.8; // Each section bends with frequencies inside this ribbon's own register.
        // low ribbons undulate slowly and widely, high ribbons shiver fast
        float sway = (0.065 * (1.0 - reg) + 0.018 + 0.055 * compE)
                   * sin(uv.x * (4.0 + reg * 14.0)
                         + u.time * u.speed * (0.4 + 1.6 * reg) * (0.6 + fi * 0.8));
        float ripple = 0.014 * reg
                     * sin(uv.x * (30.0 + 60.0 * reg) + u.time * u.speed * (2.0 + 3.0 * reg))
                     * clamp(u.highMid * u.sensitivity, 0.0, 1.2);
        float yC = 0.5
                 + (fi - 0.5) * 0.58
                 + w * height * wWeight * (0.5 + 0.8 * compE)
                 + sway + ripple
                 + 0.04 * transient * (1.0 - 0.5 * reg);
        // thickness: fat lows, thin highs, detailed by the local spectrum,
        // puffed by this register's transients
        float thickness = (0.009 + 0.045 * compE) * (1.6 - 1.1 * reg)
                        * (0.6 + 0.9 * localE) * (1.0 + 0.5 * transient);
        // t/(|dy|+t) is a soft glow profile: 1 on the center line, falling
        // off hyperbolically; the pow sharpens its core.
        float glowR = thickness / (fabs(uv.y - yC) + thickness);
        glowR = pow(glowR, 1.4);
        // vertical curtain rays driven by the presence band
        float rays = 0.85 + 0.30 * sin(uv.x * (90.0 + fi * 20.0) + u.time * u.speed * (1.5 + fi))
                   * clamp(u.presence * u.sensitivity * 1.5, 0.0, 1.0);
        col += cosPalette(fi * 0.85 + u.centroid * 0.2 + u.time * 0.015, u.palette)
             * glowR * rays * (0.10 + 0.85 * compE + 0.30 * localE) * (1.0 + 0.45 * transient);
    }

    // beat lift + onset shimmer
    col *= 1.0 + 0.18 * u.beat + 0.10 * u.flux;

    // vibrance, Reinhard tone mapping, and a gentle S-curve — the same
    // finishing chain as the tunnel, so the ribbons saturate instead of
    // washing out
    float luma = dot(col, float3(0.299, 0.587, 0.114));
    col = max(mix(float3(luma), col, 1.35 * u.p[15]), 0.0);
    col = col / (1.0 + col);
    col = col * col * (3.0 - 2.0 * col);
    return limitedExposure(albumTint(col, in.uv, album), exposure);
}
