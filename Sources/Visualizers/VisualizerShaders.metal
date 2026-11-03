// Adapted from Synesthia by Jon Jaques, MIT licence.
// Pinned source and local changes: ThirdParty/Synesthia/SOURCE.md.
// Copyright (c) 2026 Jon Jaques. See Resources/Synesthia-LICENSE.txt.

// All shader code for the built-in visualizers, compiled at build time into
// the app's default Metal library (loaded via device.makeDefaultLibrary()).
//
// A primer for readers new to shaders: a *vertex* function runs once per
// vertex and decides where geometry lands on screen; a *fragment* function
// runs once per covered pixel and decides its color. The visualizers here
// are mostly "full-screen shader" style — a single triangle covers the
// window, and the fragment function computes every pixel's color from
// scratch each frame using only its coordinates, the clock, and the audio
// uniforms (the style popularized by Shadertoy).
//
// External visualizer plugins (a roadmap item) wouldn't compile into this
// library; they can ship MSL source and compile it at load time with
// MTLDevice.makeLibrary(source:), which is how this whole file used to work.

#include <metal_stdlib>
using namespace metal;

// Layout must match VizUniforms in VisualizerCore.swift exactly (42 floats /
// 168 bytes). The CPU fills that Swift struct each frame and copies its raw
// bytes here; see VisualizerCore.swift for what each field means.
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
    float beatCount;      // kicks counted since launch
    float frame;          // frames rendered since launch
    float reduceMotion;   // 1 when the system Reduce Motion setting is on
    float intro;          // 0 → 1 ramp after this visualizer was built
};

// The one invariant that used to be uncheckable: both structs are 42 floats,
// and the Swift side is pinned by VizUniformsTests. Editing one without the
// other now fails the build here or the test suite there, instead of silently
// reading every field one slot over.
static_assert(sizeof(VizUniforms) == 168,
              "VizUniforms must stay byte-identical to the Swift struct in VisualizerCore.swift");

struct FSQuadOut {
    float4 position [[position]];
    float2 uv;
};

// The standard "fullscreen triangle" trick: one triangle big enough that the
// screen rectangle is entirely inside it (clipped at the edges). Cheaper and
// simpler than two triangles forming a quad, and the vertex positions are
// generated from the vertex index — no vertex buffer needed at all.
// uv comes out as 0...1 across the visible screen.
vertex FSQuadOut fullscreenVertex(uint vid [[vertex_id]]) {
    float2 pos[3] = { float2(-1.0, -1.0), float2(3.0, -1.0), float2(-1.0, 3.0) };
    FSQuadOut out;
    out.position = float4(pos[vid], 0.0, 1.0);
    out.uv = pos[vid] * 0.5 + 0.5;
    return out;
}

// ---------------------------------------------------------------- helpers

// Cosine palette (Íñigo Quílez): a + b*cos(2π(c*t + d)) per channel maps any
// scalar t to a smooth cyclic gradient. Coefficients must stay in sync with
// the CPU mirror in VisualizerCore.swift (Palettes.color).
static float3 cosPalette(float t, float which) {
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
static float bandAt(constant float* bands, float x) {
    float f = clamp(x, 0.0, 0.9999) * 63.0;
    int i = int(f);
    return mix(bands[i], bands[min(i + 1, 63)], fract(f));
}

// Samples the 256-point waveform at x in 0...1, averaged with its neighbors
// to soften single-sample spikes.
static float waveAt(constant float* wave, float x) {
    int i = int(clamp(x, 0.0, 0.9999) * 255.0);
    int a = max(i - 1, 0), b = min(i + 1, 255);
    return (wave[a] + wave[i] + wave[b]) / 3.0;
}

// Cheap 2D → pseudo-random hash in 0...1. Not statistically great, but fast
// and stable per input — used to scatter stars/particles deterministically.
static float hash21(float2 p) {
    p = fract(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

// Value noise: random values at integer grid points, smoothly interpolated
// between them. Gives soft, organic variation rather than white noise.
static float vnoise(float2 p) {
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
static float fbm(float2 p) {
    float v = 0.0, amp = 0.5;
    for (int i = 0; i < 4; i++) {
        v += amp * vnoise(p);
        p *= 2.03;
        amp *= 0.5;
    }
    return v;
}

// 2D rotation by angle a.
static float2 rot2(float2 p, float a) {
    float c = cos(a), s = sin(a);
    return float2(c * p.x - s * p.y, s * p.x + c * p.y);
}

// Sparse twinkling point field; returns brightness at uv for one layer.
// Space is cut into a grid; ~8% of cells (hash > 0.92) contain a star at a
// hashed offset, each pulsing on its own phase.
static float starLayer(float2 uv, float scale, float t, float twinkleSpeed) {
    float2 g = uv * scale;
    float2 id = floor(g);
    float h = hash21(id);
    if (h < 0.92) { return 0.0; }
    float2 f = fract(g) - 0.5;
    float2 offs = float2(hash21(id + 7.1), hash21(id + 3.7)) - 0.5;
    float d = length(f - offs * 0.6);
    float tw = 0.55 + 0.45 * sin(t * twinkleSpeed * (0.5 + h) + h * 40.0);
    return smoothstep(0.10, 0.0, d) * tw * (h - 0.92) * 12.5;
}

// ---------------------------------------------------------------- Spectrum Tunnel
//
// A raymarched flight through a winding cave. Unlike the other fragment
// shaders (which fake depth with a polar mapping), this one marches an
// actual 3D signed-distance field: the tunnel is a bore of varying radius
// around a centerline that snakes through space, so bends really occlude —
// the far end disappears behind the curve of the wall and you never see the
// end. The camera rides the centerline. Adapted from two Shadertoy
// references: XtdGR7 (the winding SDF tunnel) and the "Hyper Tunnel" from
// the demo "Sailing Beyond" (crevice lighting from the iteration count,
// power-shaped fbm crags, the volumetric steam pass, and the audio-pumped
// exposure). Their texture lookups are replaced with procedural 3D value
// noise, so no assets are needed.
//
// The audio is carved into the geometry itself: each angular lane of the
// wall reads one slice of the 64-band spectrum (bass at the floor, treble
// overhead, mirror-folded so there is no seam) and loud bands physically
// bulge the rock inward; the bass swells the whole bore; spiral ridges
// corkscrew past at a rate set by the Twist slider with depth from the
// low-mids; the waveform rings the bore along its length; every kick throws
// a wall of light up the tunnel toward the camera; the mids, presence and
// onsets pump the steam; and the scene's exposure breathes with the track's
// overall loudness.

// The tunnel's option slots, in the order TunnelVisualizer.descriptor
// declares them (`u.p[kTunnelGlow]` reads the Glow slider).
constant int kTunnelTwist = 0;
constant int kTunnelGlow = 1;
constant int kTunnelBend = 2;
constant int kTunnelPulse = 3;
constant int kTunnelRipple = 4;
constant int kTunnelFog = 5;

// 3D value-noise stack standing in for Shadertoy's RGBA-noise texture.
static float hash31(float3 p) {
    p = fract(p * float3(443.897, 441.423, 437.195));
    p += dot(p, p.yzx + 19.19);
    return fract((p.x + p.y) * p.z);
}

static float vnoise3(float3 x) {
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
static float fbm3(float3 p) {
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
static float fbmSharp(float3 p) {
    float r = 0.0, w = 1.0, s = 1.0;
    for (int i = 0; i < 3; i++) {
        w *= 0.25;
        s *= 3.0;
        r += w * vnoise3(p * s);
    }
    return r;
}

// Where the tunnel's centerline sits at distance z: layered incommensurate
// sines, so the path winds forever without repeating. The amplitude is on
// the order of the bore radius — that is what makes curves actually occlude
// instead of just leaning.
static float2 tunnelCenter(float z, float bend) {
    return float2(sin(z * 0.31) + 0.6 * sin(z * 0.17 + 4.0),
                  cos(z * 0.23) + 0.6 * sin(z * 0.13 + 1.0)) * 0.55 * bend;
}

// Mirror-folded angle around the bore (0 = floor, 1 = ceiling): band 0 and
// band 63 never sit next to each other, so the spectrum wall has no seam.
// The +0.25 phase puts the fold's minimum at angle -π/2 (straight down), so
// the bass really is at the floor and the treble overhead as documented —
// +0.75 (the obvious other choice) flips the whole spectrum upside down.
static float tunnelFold(float angle) {
    return 1.0 - fabs(fract(angle / 6.28318 + 0.25) * 2.0 - 1.0);
}

// The signed-distance field: positive inside the air of the tunnel, zero at
// the wall. Everything audible deforms it — see the section comment.
static float tunnelMap(float3 pos, constant VizUniforms& u,
                       constant float* bands, constant float* wave) {
    float2 xy = pos.xy - tunnelCenter(pos.z, u.p[kTunnelBend]);
    float angle = atan2(xy.y, xy.x);
    float e = clamp(bandAt(bands, tunnelFold(angle)) * u.sensitivity, 0.0, 1.2);
    // spiral ridges spinning past: rate from the Twist slider, depth from
    // the low-mids
    float arms = sin(angle * 3.0 - pos.z * 0.4 + u.time * u.speed * (0.5 + 1.0 * u.p[kTunnelTwist]))
               * (0.05 + 0.25 * clamp(u.lowMid * u.sensitivity, 0.0, 1.0));
    // power-shaped noise carves terraced crags into the rock
    float crags = pow(fbmSharp(pos * 0.55) * 3.3, 1.3);
    // the bore's own slow, irregular breathing: a drifting noise field, so
    // caverns open and close instead of repeating like a sine would
    float cavern = vnoise3(float3(1.7, 2.9, pos.z * 0.06) + float3(0.0, u.time * 0.05, 0.0)) - 0.5;
    float r = 1.1
        + 0.5 * cavern
        + 0.35 * u.bass * u.sensitivity                     // bass blows the tunnel wide
        + u.p[kTunnelRipple] * (0.42 * crags - 0.30                       // crags (Ripple slider)
                  + 0.06 * waveAt(wave, fract(pos.z * 0.10)))
        + arms
        - 0.30 * e * e;                                     // loud bands bulge the wall inward
    // Floor the bore radius so a worst-case pile-up of carvings can never
    // close the tunnel over the camera (which rides the centerline).
    r = max(r, 0.30);
    return r - length(xy);
}

fragment float4 tunnelFragment(FSQuadOut in [[stage_in]],
                               constant VizUniforms& u [[buffer(0)]],
                               constant float* bands [[buffer(1)]],
                               constant float* wave [[buffer(2)]]) {
    float glow = u.p[kTunnelGlow];
    float pulse = u.p[kTunnelPulse];
    float fogAmt = u.p[kTunnelFog];

    // Square-space pixel coordinates (y half-height = 1).
    float2 suv = in.uv * 2.0 - 1.0;
    suv.x *= u.resolution.x / max(u.resolution.y, 1.0);

    // The camera rides the centerline, aimed at the centerline further on
    // (that look-ahead is what keeps the mouth centered while the far end
    // swings). The kick surges it forward; a slow continuous roll keeps the
    // flight alive.
    float camZ = u.time * u.speed * 2.2; // Continuous travel; no kick-driven camera jumps.
    float3 ro = float3(tunnelCenter(camZ, u.p[kTunnelBend]), camZ);
    float3 ta = float3(tunnelCenter(camZ + 2.0, u.p[kTunnelBend]), camZ + 2.0);
    float rollA = u.time * 0.1 + 0.6 * sin(u.time * 0.3);
    float3 fw = normalize(ta - ro);
    float3 right = normalize(cross(fw, float3(sin(rollA), cos(rollA), 0.0)));
    float3 up = cross(right, fw);
    float3 rd = normalize(suv.x * right + suv.y * up + 1.4 * fw);

    // Sphere-trace to the wall, counting steps: grazing rays rack up
    // iterations, and that count *is* the lighting — crevices, crags and
    // silhouettes glow with no normal computation at all (the demoscene
    // trick).
    float t = 0.0;
    float3 p = ro;
    float steps = 0.0;
    for (int i = 0; i < 60; i++) {
        float d = tunnelMap(p, u, bands, wave);
        if (d < 0.0025 || t > 26.0) break;
        t += max(d * 0.8, 0.004);
        p = ro + rd * t;
        steps += 1.0;
    }

    // Recover the wall's slice of the spectrum.
    float2 xy = p.xy - tunnelCenter(p.z, u.p[kTunnelBend]);
    float fold = tunnelFold(atan2(xy.y, xy.x));
    float e = clamp(bandAt(bands, fold) * u.sensitivity, 0.0, 1.5);

    float crevice = min(0.85, steps / 42.0) + 0.06;

    // Mineral coloring: the same noise family that carved the rock steers
    // the hue on top of the bass→treble floor-to-ceiling gradient, so
    // colored strata run through the walls.
    float3 wallColor = cosPalette(0.06 + fold * 0.45 + 1.1 * fbm3(p * 0.35)
                                  + u.centroid * 0.30, u.palette);
    float3 col = wallColor * (0.35 + 3.0 * e * e) * crevice * glow;

    // ribs streaming past with the high-mids
    float segF = fract(p.z * 0.9);
    float rib = smoothstep(0.10, 0.0, min(segF, 1.0 - segF));
    col += cosPalette(0.5 + fold * 0.3, u.palette) * rib * crevice
         * (0.08 + 1.0 * clamp(u.highMid * u.sensitivity, 0.0, 1.2)) * glow;

    // glitter veins answering hi-hats and air: soft pow-shaped noise spots
    // tinted by the palette, twinkling as they stream past (hard hash cells
    // here read as flat gray confetti — never again)
    float sp = vnoise3(p * 13.0 + float3(0.0, 0.0, u.time * 0.6));
    float glit = pow(max(sp - 0.72, 0.0) * 3.5, 3.0);
    col += cosPalette(0.85 + fold * 0.3 + u.centroid * 0.2, u.palette) * glit
         * (0.5 + 0.5 * sin(u.time * 12.0 + p.z * 3.0))
         * (1.4 * u.trebleBeat + 0.8 * u.air * u.sensitivity) * 2.0;

    // every kick throws a wall of light up the tunnel toward the camera
    // (beat 1 → 0 maps far → here) — the Pulse slider scales it
    float lightZ = camZ + 1.5 + u.beat * 10.0;
    float pulseGlow = exp(-fabs(p.z - lightZ) * 0.6) * u.beat * pulse;
    col += cosPalette(0.1 + u.centroid * 0.3, u.palette) * pulseGlow * (1.5 + 1.0 * u.bass);

    // distance dissolves into palette haze pulsing with the low end (Fog
    // slider) — the far end of the cave is never a picture, just glow
    float atten = 1.0 / (1.0 + t * t * 0.010 * fogAmt);
    float3 fogColor = cosPalette(0.6 + u.centroid * 0.25 + u.time * 0.01, u.palette)
                    * (0.06 + 0.45 * u.bass * u.sensitivity + 0.20 * u.flux);
    col = col * atten + fogColor * (1.0 - atten);

    // volumetric steam (after "Sailing Beyond"): march back from the wall
    // toward the camera stacking noise, cube it, and let the mids, the
    // presence band (vocals) and onsets pump its brightness
    float f = 0.0;
    float distC = t;
    for (int i = 0; i < 16; i++) {
        float3 sp = ro + rd * distC;
        f += vnoise3(sp * 0.9 + float3(0.0, 0.0, -u.time * 0.30)) * 0.045;
        distC -= max(t * 0.06, 0.25);
        if (distC < 0.4) break;
    }
    float3 steamColor = cosPalette(0.55 + u.centroid * 0.3 + u.time * 0.008, u.palette);
    col += steamColor * pow(fabs(f * 1.5), 3.0)
         * (0.35 + 1.6 * clamp(u.mid * u.sensitivity, 0.0, 1.0)
            + 1.0 * clamp(u.presence * u.sensitivity, 0.0, 1.2) + 0.8 * u.flux);

    // the whole scene breathes with the track's loudness (the demoscene
    // audio-exposure trick, driven by our smoothed RMS level)
    col *= (0.50 + 0.85 * u.level * u.sensitivity) * (1.0 + 0.20 * u.flux) * 1.6;

    // vignette, then vibrance: push saturation while still HDR, Reinhard
    // tone map (c/(1+c)) so the additive pile-up rolls smoothly into 0...1
    // instead of clipping at white, and finish with a gentle S-curve — the
    // deepened shadows and richer primaries keep the cave from going muddy
    col *= smoothstep(4.5, 0.8, dot(suv, suv));
    float luma = dot(col, float3(0.299, 0.587, 0.114));
    col = max(mix(float3(luma), col, 1.5), 0.0);
    col = col / (1.0 + col);
    col = col * col * (3.0 - 2.0 * col);
    return float4(col, 1.0);
}

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
                               constant float* bands [[buffer(1)]],
                               constant float* wave [[buffer(2)]]) {
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
        float compE = clamp(comps[cIdx] * u.sensitivity, 0.0, 1.3);
        // the fine spectrum inside this register, spread across the screen
        float localE = clamp(bandAt(bands, (float(cIdx) + clamp(uv.x, 0.0, 1.0)) / 8.0)
                           * u.sensitivity, 0.0, 1.3);
        // transient personality: lows jump on the kick, mids on onsets,
        // highs shiver on hi-hat transients
        float transient = u.beat * (1.0 - smoothstep(0.15, 0.45, reg))
                        + u.flux * exp(-pow((reg - 0.5) * 3.5, 2.0))
                        + u.trebleBeat * smoothstep(0.55, 0.85, reg);
        // mids carry the waveform; lows and highs mostly ignore it
        float wWeight = exp(-pow((reg - 0.5) * 2.4, 2.0));
        float w = (bandAt(bands, clamp(uv.x * 0.55 + fi * 0.13, 0.0, 1.0)) - 0.3) * 0.45; // Stable spectrum curve, not rapidly changing waveform phase.
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
    col = max(mix(float3(luma), col, 1.35), 0.0);
    col = col / (1.0 + col);
    col = col * col * (3.0 - 2.0 * col);
    return float4(col, 1.0);
}
