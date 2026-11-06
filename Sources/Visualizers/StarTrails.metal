// Star Trails ("star-final": the merged design, see specs/star-trails.md).
//
// A near-black violet sky with soft, out-of-focus magenta nebula clouds, a dense
// field of crisp pinpoint stars, two original dart-shaped craft orbiting a
// formation centre and leaving long neon trails (pink = the voice, lime = the
// bass) that drift to the lower left and cross, and a few tumbling asteroids.
// The camera follows the craft: all the scenery drifts steadily along the flight
// axis, against their heading, with parallax (clouds slowest, then dust stars,
// bright stars, asteroids). Individual stars light up by the TYPE of sound playing:
//   kick  -> warm gold flares that ripple out from two fresh epicentres, low in the sky
//   snare -> an ice-cyan crackle of x-glinting stars scattered across the sky
//   hat   -> tiny silver glitter in the dust layer, favouring the upper sky
//   bass  -> a ladder of blue giants: heavier bass lights more of them, the low sky first
//   vocal -> rose stars around the lead (pink) craft that shimmer only while the voice sings
//   pad   -> lavender embers inside the clouds; the clouds and the white stars breathe
//
// The craft glide on smooth paths; hits are light, never motion: a kick lays a bright
// packet into each trail and blooms the engines, a snare puts a sparkle and a cool rim
// light on the craft, and the voice warms the pink trail.
//
// Everything with memory (orbit, trail history, onset rings, kick epicentres,
// asteroids, scroll and morph clocks) is CPU state (StarTrailsSimulation); this
// shader is a pure function of VizUniforms and that state.
//
// Bindings: buffer(0) VizUniforms, buffer(4) album stops (unused: the sky's album colours
// come in the state), buffer(5) the exposure
// limiter's gain (ExposureLimiter.metal), buffer(6) StarTrailsState (195 float4, 3,120
// bytes, packed by StarTrailsSimulation.pack; layout in StarTrailsLayout).
// richness()/limitedExposure() come from VisualizerShaderTypes.h.
#include <metal_stdlib>
using namespace metal;
#include "VisualizerShaderTypes.h"

namespace st {
// Resting star magnitudes, calibrated so that after the app's upscale to 1440p the field measures
// like the reference: about 4,000 detectable stars per MP, peak excess p50 0.26-0.28, p90 0.5-0.55.
#ifndef DUST_A
#define DUST_A 0.50
#define DUST_B 0.92
#define BRIGHT_A 0.85
#define BRIGHT_B 0.60
#endif
#ifndef DUST_HAZE
#define DUST_HAZE 0.042
#endif

// ------------------------------------------------------------ state layout (float4 slots)
// Keep in sync with StarTrailsLayout (StarTrailsSimulation.swift; pinned by StarTrailsTests). 206 float4 = 3,296 bytes.
constant int HDR0 = 0;    // formation centre x, y (H), flight axis (rad: the craft's mean heading), aspect
constant int HDR1 = 1;    // trail s0 (H), trail ds (H), bright-star scroll, dust-star scroll ([0,256) H along the axis)
constant int HDR2 = 2;    // morph angle A, morph angle B [0, 2pi), calm 0..1 (ramped), star-light gain
constant int ENV0 = 3;    // display envelopes: kick, snare, hat, bass
constant int ENV1 = 4;    // vocal, pad, level, pad breath
constant int STAGE = 5;   // lyric stage centre (uv), radii (uv)
constant int CLK = 6;     // twinkle clock [0,64) s, vocal phase [0,4pi), cloud scroll [0,256) H, 1/drift (s per H)
constant int CRAFT = 7;   // craft k: 7+2k (x, y, heading, bank), 8+2k (engine glow, head s, snare sparkle, kick bloom)
constant int ONSET = 11;  // 20 onset slots x 2: (serial, age s, strength, pan), (kick: epicentres A, B in the bright
                          // stars' sky frame | hat: the dust scroll at the onset)
constant int LIVE = 51;   // live onsets: kick, snare, hat; asteroid count
constant int AST = 52;    // 10 asteroids x 2: (x, y, radius, rotation), (seed, blur, fade, kick rim light)
constant int TRAIL = 72;  // 2 trails x 56 samples: (cross offset c, width = 1 + 0.35 x lead, glow, hot)
constant int SKY = 184;   // 11 reference sky colours (display space): deep sky, cloud ramp x7, wisp, hot wisp, dust tint
constant int SKYTINT = 195; // 3x3 cover tints (linear RGB), with album strength in W
constant int TRAILCOLOUR = 204; // two trail cores (display RGB), with album strength in W
constant int kTrailN = 56;
constant int kAst = 10;
constant int kKick0 = 0, kKickN = 6, kSnare0 = 6, kSnareN = 6, kHat0 = 12, kHatN = 8;
constant float kKickLife = 0.90, kSnareLife = 0.78, kHatLife = 0.44;   // = slots x min gap (CPU merges closer onsets)
constant float kWorld = 256.0;    // the sky wraps every 256 H; every y lattice below has a whole number of cells per 256 H
constant float kTrailGlow = 0.95; // trail glow gain
// The sky frame: x across the flight axis, y along it (screen p = x m + y f, m = (f.y, -f.x)).
// Every scenery layer adds its own scroll to y, so it drifts along -f.
static inline float2 toScreen(float2 v, float2 f) { return v.x * float2(f.y, -f.x) + v.y * f; }

// ------------------------------------------------------------ hashing
static inline uint hashu(uint x) {
    x ^= x >> 16; x *= 0x7feb352dU; x ^= x >> 15; x *= 0x846ca68bU; x ^= x >> 16; return x;
}
static inline uint cellHash(int x, int y, uint salt) { return hashu(uint(x) * 0x8da6b343U ^ hashu(uint(y) * 0xd8163841U + salt)); }
static inline float unitf(uint h) { return float(h >> 8) * (1.0 / 16777216.0); }
static inline float byteOf(uint h, uint k) { return (float((h >> (8u * k)) & 0xffu) + 0.5) * (1.0 / 256.0); }
static inline int imod(int a, int m) { int r = a % m; return r < 0 ? r + m : r; }
static inline float wrapY(float y) { return y - kWorld * floor(y / kWorld); }
// Distance in sky coordinates, aware of the 256 H wrap.
static inline float skyDist(float2 a, float2 b) {
    float2 d = a - b;
    d.y -= kWorld * rint(d.y / kWorld);
    return length(d);
}

// ------------------------------------------------------------ gradient noise, periodic in y
// 16 unit gradients (no sincos per corner), one integer hash per corner. The y lattice index
// wraps at `py` cells so the sky can wrap at 256 H without a seam.
constant float2 kGrad[16] = {
    float2(0.981, 0.195), float2(0.831, 0.556), float2(0.556, 0.831), float2(0.195, 0.981),
    float2(-0.195, 0.981), float2(-0.556, 0.831), float2(-0.831, 0.556), float2(-0.981, 0.195),
    float2(-0.981, -0.195), float2(-0.831, -0.556), float2(-0.556, -0.831), float2(-0.195, -0.981),
    float2(0.195, -0.981), float2(0.556, -0.831), float2(0.831, -0.556), float2(0.981, -0.195) };
static inline float2 gradAt(int x, int y, uint salt) {
    return kGrad[hashu(uint(x) * 0x8da6b343U ^ uint(y) * 0xd8163841U ^ salt) >> 28];
}
static float gnoiseP(float2 x, int py, uint salt) {
    float2 fl = floor(x);
    int2 i = int2(fl);
    float2 f = x - fl;
    float2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float pyf = float(py);
    int y0 = int(fl.y - pyf * floor(fl.y / pyf));              // wrap without an integer divide
    int y1 = y0 + 1 == py ? 0 : y0 + 1;
    float a = dot(gradAt(i.x, y0, salt), f);
    float b = dot(gradAt(i.x + 1, y0, salt), f - float2(1.0, 0.0));
    float c = dot(gradAt(i.x, y1, salt), f - float2(0.0, 1.0));
    float d = dot(gradAt(i.x + 1, y1, salt), f - float2(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y) * 1.41;
}
// Octave k samples x * 2^k, so its y period is py * 2^k: still whole cells per 256 H.
static float fbmP(float2 x, int py, uint salt, int octaves) {
    float s = 0.0, a = 0.5;
    for (int i = 0; i < octaves; i++) {
        s += a * gnoiseP(x, py, salt + uint(i) * 0x9E37u);
        x = x * 2.0 + float2(11.3, 5.9);
        py *= 2;
        a *= 0.5;
    }
    return s;
}
// Non-periodic variant for rock surfaces (local coordinates).
static float fbmL(float2 x, uint salt, int octaves) { return fbmP(x + 512.0, 1 << 20, salt, octaves); }

// ------------------------------------------------------------ palette (display space; measured from the reference)
// The sky's own colours (the violet original or the album cover's) come from the state: SKY.
constant float3 kPinkGlow = float3(0.620, 0.140, 0.360); // glow at the core edge, above the sky (measured #B93C90 rim)
constant float3 kLimeGlow = float3(0.190, 0.420, 0.080);
// The pink trail while the voice sings: a warmer, rosier hue at the same luminance, so the voice
// warms the trail without brightening the lyric stage.
constant float3 kPinkWarmCore = float3(1.000, 0.500, 0.780);
constant float3 kPinkWarmGlow = float3(0.700, 0.130, 0.250);
constant float3 kStarWhite = float3(1.0, 0.97, 1.0);
// Sound-type colours: core tint, halo. Hues sit around the wheel away from the violet sky and
// the two trail colours: gold 38 deg, cyan 195, silver, blue 228, rose 330, lavender 265.
constant float3 kKickCore  = float3(1.00, 0.94, 0.82), kKickHalo  = float3(1.00, 0.62, 0.20);
constant float3 kSnareCore = float3(0.90, 0.98, 1.00), kSnareHalo = float3(0.28, 0.80, 1.00);
constant float3 kHatCore   = float3(0.95, 1.00, 1.00), kHatHalo   = float3(0.62, 0.90, 1.00);
constant float3 kBassCore  = float3(0.86, 0.90, 1.00), kBassHalo  = float3(0.30, 0.42, 1.00);
constant float3 kVocalCore = float3(1.00, 0.90, 0.95), kVocalHalo = float3(1.00, 0.34, 0.68);
constant float3 kPadHalo   = float3(0.64, 0.46, 1.00);
constant float3 kSnareRim  = float3(0.78, 0.95, 1.00);           // the snare's light on the craft (ice, as its stars)

// ------------------------------------------------------------ nebula
struct Nebula { float3 colour; float wisp; float cloud; };

constant float kRampPos[7] = { 0.40, 0.46, 0.53, 0.60, 0.67, 0.76, 0.90 };
// The cloud ramp: seven colours (SKY + 1...7) at kRampPos along the cloud density.
static float3 cloudRamp(constant float4* s, float d) {
    float3 c = s[SKY + 1].rgb;
    for (int i = 1; i < 7; i++) c = mix(c, s[SKY + 1 + i].rgb, saturate((d - kRampPos[i - 1]) / (kRampPos[i] - kRampPos[i - 1])));
    return c;
}

// Large, out-of-focus clouds (the reference's soft magenta nebula): a gently warped field of
// two low octaves, so every gradient is smooth, with wide soft edges on the dark lanes and soft
// pink cores. No fine octaves and no terraces: the stars are the only crisp detail. `ps` is the
// sky frame (x across the flight, y along it); the clouds are a little longer along the flight.
// Morphing moves the noise inputs round two slow circles (angles from the CPU, wrapped at
// 2 pi), so the offsets stay bounded and nothing ever loses precision.
static Nebula nebula(constant float4* s, float2 ps, float scroll, float2 mA, float2 mB, float veil, float pad, float vocal,
                     float breath, float4 cal, float softness) {
    float2 q = float2(ps.x * 1.3125, ps.y + scroll);                  // y: 256 H wraps whole lattice periods below
    float2 w = float2(gnoiseP(q * 1.0625 + mA * 1.6 + float2(1.7, 9.2), 272, 0x1A2Bu),
                      gnoiseP(q * 1.0625 + mB * 1.6 + float2(8.3, 2.8), 272, 0x3C4Du));
    float2 qw = q + mix(0.26, 0.14, softness) * w;                                         // slow warp: rounded, unbanded shapes
    float lo = 0.44 * gnoiseP(qw * 1.25 + mB * 0.45, 320, 0x5E6Fu)                                   // clouds (~0.4 H)
             + mix(0.22, 0.075, softness) * gnoiseP(qw * 2.5 + mB * 0.9 + float2(11.3, 5.9), 640, 0x5E6Fu + 0x9E37u);  // their lobes
    float D = 0.5 + cal.x + lo + 0.018 * breath;                     // pads let the clouds swell a little
    float lift = mix(1.08, 0.78, veil);                              // brighter and pinker off the lyric stage,
    float3 c = mix(s[SKY].rgb, cloudRamp(s, D) * cal.z * lift, smoothstep(mix(0.35, 0.30, softness), mix(0.45, 0.53, softness), D));
    float pinkField = lo + mix(0.16, 0.075, softness) * gnoiseP(qw * 1.875 + mA * 0.7 + float2(0.0, 3.7), 480, 0x7A8Bu);
    float pink = smoothstep(0.08 + cal.y, mix(0.50, 0.60, softness) + cal.y, pinkField) *
                 smoothstep(mix(0.50, 0.44, softness), mix(0.74, 0.80, softness), D);
    pink *= (0.94 + 0.16 * pad + 0.10 * vocal + 0.06 * breath) * mix(1.10, 0.50, veil);
    c = mix(c, s[SKY + 8].rgb, saturate(pink) * 0.92);
    c = mix(c, s[SKY + 9].rgb, smoothstep(0.70, 1.0, pink) * smoothstep(0.36, 0.62, pinkField) * 0.72);
    Nebula n; n.colour = c; n.wisp = pink; n.cloud = smoothstep(0.45, 0.62, D);
    return n;
}

// A soft 3x3 field across the sky. Cloud brightness stays independent
// of the cover hue, so saturated or pale artwork can't wash out the trails.
static float3 albumNebula(constant float4* s, float3 colour, float2 uv) {
    float2 p = saturate(float2(uv.x, 1.0 - uv.y)) * 2.0;
    int2 cell = min(int2(p), int2(1));
    float2 blend = smoothstep(0.0, 1.0, p - float2(cell));
    int first = SKYTINT + cell.y * 3 + cell.x;
    float3 upper = mix(s[first].rgb, s[first + 1].rgb, blend.x);
    float3 lower = mix(s[first + 3].rgb, s[first + 4].rgb, blend.x);
    float3 tint = mix(upper, lower, blend.y);

    float3 rgb = saturate(colour);
    float3 linear = select(rgb / 12.92, powr((rgb + 0.055) / 1.055, float3(2.4)), rgb > 0.04045);
    float luminance = dot(linear, float3(0.2126, 0.7152, 0.0722));
    float3 lit = saturate(tint * luminance);
    return select(12.92 * lit, 1.055 * powr(lit, float3(1.0 / 2.4)) - 0.055, lit > 0.0031308);
}

// ------------------------------------------------------------ stars
// A star's core, energy-conserving across resolutions: sizes are given in pixels of a
// 1000 px tall render; below 0.75 px a star stays 0.75 px wide and gets dimmer, so small
// renders (the settings preview, the limiter's 64x36 exposure probe) see the same mean light.
static inline float starCore(float d2px, float sigmaPx) {
    float s = max(sigmaPx, 0.75);
    return (sigmaPx * sigmaPx) / (s * s) * exp(-d2px / (2.0 * s * s));
}
static inline float flare(float t, float attack, float decay) {
    return t <= 0.0 ? 0.0 : smoothstep(0.0, attack, t) * exp(-max(t - attack, 0.0) / decay);
}

// Kick: every onset lights a FRESH random subset of kick stars (hash of star and onset serial),
// clustered round the onset's two epicentres and lit outwards like a ripple (1.4 H/s).
// `starSky`: the star in the bright stars' sky frame, where the epicentres ride.
// `r0`-`r1`: cluster radius (H); `dec`: decay range (s). Bright kick stars flare (0.05-0.26 H,
// 0.36-0.60 s); a sprinkle of dust stars closer in sparkles more briefly (0.02-0.15 H, 0.18-0.30 s).
static float kickLight(uint hs, float2 starSky, constant float4* s, float frac, float calm,
                       float r0, float r1, float2 dec, uint salt) {
    float lit = 0.0;
    for (int j = 0; j < kKickN; j++) {
        float4 e = s[ONSET + 2 * (kKick0 + j)];
        if (e.y < 0.0 || e.y > kKickLife || e.z <= 0.001) continue;
        uint k = hashu(hs ^ (uint(e.x) * 0x9E3779B9u + salt));
        float r = unitf(k);
        if (r >= frac) continue;
        float4 ep = s[ONSET + 2 * (kKick0 + j) + 1];
        float dm = min(skyDist(starSky, ep.xy), skyDist(starSky, ep.zw));
        float p = frac * (1.0 - smoothstep(r0, r1, dm));
        if (r >= p) continue;
        float member = 1.0 - smoothstep(0.8 * p, p, r);           // soft edge: continuous when calm moves p
        uint kv = hashu(k ^ 0x5bd1e995u);
        float t = e.y - dm / 1.4 - 0.03 * byteOf(kv, 0);          // the ripple: 1.4 H/s outwards
        float atk = mix(0.09, 0.30, calm);
        float d = mix(dec.x, dec.y, byteOf(kv, 1)) * mix(1.0, 1.6, calm);
        float env = flare(t, atk, d) * (1.0 - smoothstep(0.63, kKickLife, e.y)) * member;
        lit = max(lit, env * e.z * mix(0.55, 1.0, byteOf(kv, 2)));
    }
    return lit;
}
// Snare: a crackle scattered over the sky; each star waits 0-120 ms, leaning to the snare's side.
static float snareLight(uint hs, float2 starP, float aspect, constant float4* s, float frac, float calm) {
    float lit = 0.0;
    for (int j = 0; j < kSnareN; j++) {
        float4 e = s[ONSET + 2 * (kSnare0 + j)];
        if (e.y < 0.0 || e.y > kSnareLife || e.z <= 0.001) continue;
        uint k = hashu(hs ^ (uint(e.x) * 0x9E3779B9u + 0xC2B2AE35u));
        float side = clamp((starP.x / aspect - 0.5) * 2.3, -1.0, 1.0);
        float p = frac * clamp(1.0 + 0.9 * e.w * side, 0.15, 1.85);
        float r = unitf(k);
        if (r >= p) continue;
        uint kv = hashu(k ^ 0x27d4eb2du);
        float t = e.y - 0.12 * byteOf(kv, 0);                     // the crackle: each star waits 0-120 ms
        float atk = mix(0.08, 0.27, calm);
        float dec = mix(0.22, 0.38, byteOf(kv, 1)) * mix(1.0, 1.6, calm);
        float env = flare(t, atk, dec) * (1.0 - smoothstep(0.55, kSnareLife, e.y)) * (1.0 - smoothstep(0.8 * p, p, r));
        lit = max(lit, env * e.z * mix(0.55, 1.0, byteOf(kv, 2)));
    }
    return lit;
}
// Hat: glitter in the dust layer; high sounds sit high (where the star was when the hat landed).
// `starSky`: the star in the dust layer's sky frame; `f`: the flight axis.
static float hatLight(uint hs, float2 starSky, float2 f, constant float4* s, float frac, float calm) {
    float lit = 0.0;
    for (int j = 0; j < kHatN; j++) {
        float4 e = s[ONSET + 2 * (kHat0 + j)];
        if (e.y < 0.0 || e.y > kHatLife || e.z <= 0.001) continue;
        uint k = hashu(hs ^ (uint(e.x) * 0x9E3779B9u + 0x165667B1u));
        float r = unitf(k);
        if (r >= frac * 1.7) continue;
        float along = starSky.y - s[ONSET + 2 * (kHat0 + j) + 1].x;   // along the axis at the onset (H)
        along -= kWorld * rint(along / kWorld);
        float yAt = toScreen(float2(starSky.x, along), f).y;          // screen y at the onset, 0..1
        float p = frac * (0.30 + 1.40 * smoothstep(0.25, 0.85, yAt));
        if (r >= p) continue;
        uint kv = hashu(k ^ 0x85ebca6bu);
        float t = e.y - 0.02 * byteOf(kv, 0);
        float atk = mix(0.08, 0.24, calm);
        float dec = mix(0.11, 0.20, byteOf(kv, 1)) * mix(1.0, 1.6, calm);
        float env = flare(t, atk, dec) * (1.0 - smoothstep(0.30, kHatLife, e.y)) * (1.0 - smoothstep(0.8 * p, p, r));
        lit = max(lit, env * e.z * mix(0.55, 1.0, byteOf(kv, 2)));
    }
    return lit;
}

// Thin four-point glint, energy-conserving; `diag` turns it into an x.
static inline float glint(float2 dpx, float len, bool diag) {
    float2 a = diag ? float2(dpx.x + dpx.y, dpx.x - dpx.y) * 0.7071 : dpx;
    a = abs(a);
    return exp(-a.y * a.y / 0.35 - a.x / len) + exp(-a.x * a.x / 0.35 - a.y / len);
}

struct StarCtx {
    float2 p; float aspect; float resY; float scale;    // scale = resY / 1000
    float2 ps; float2 f; float skyB; float skyD;        // sky frame position, flight axis, bright and dust scrolls
    float3 dust;                                        // the dust stars' tint (SKY + 10)
    float stage; float cloud; float calm; float gain;
    float twClock; float pad; float bass; float vocal; float vocalPhase;
    float2 lead;
    float liveKick; float liveSnare; float liveHat;
};

// Dust layer: 100 cells per H, 80% occupied, one cell lookup (stars kept 1.7 px inside the cell):
// about 14,000 faint stars on a 16:9 screen, drifting a little slower than the bright stars.
// Hats light a fresh subset of them (silver glitter, high in the sky); kicks sprinkle a few gold
// sparks near their epicentres.
static float3 dustLayer(StarCtx c, constant float4* s, thread float3& lit) {
    const float cells = 100.0;
    float2 g = float2(c.ps.x, wrapY(c.ps.y + c.skyD)) * cells;
    int2 ci = int2(floor(g));
    uint h = cellHash(ci.x, imod(ci.y, 25600), 0xD57u);
    float cellPx = c.resY / cells;
    float keep = saturate(cellPx * cellPx / 36.0);         // thin out on small surfaces...
    // ...and give back the thinned stars as unresolved starlight, so small renders (the settings
    // preview, the limiter's 64x36 exposure probe) keep the full frame's mean brightness.
    float3 haze = c.dust * (DUST_HAZE * (1.0 - keep));
    if (float(h & 255u) >= 204.0 * keep) return haze;
    float2 sc = float2(ci) + 0.17 + 0.66 * float2(byteOf(h, 1), byteOf(h, 2));
    float2 dpx = (g - sc) * cellPx;
    float d2 = dot(dpx, dpx);
    if (d2 > 20.0) return haze;
    float r0 = byteOf(h, 3);
    uint h2 = hashu(h ^ 0x68bc21ebU);
    float rate = 0.0981748 * float(6u + (h2 & 31u));      // 2 pi k / 64: exact under the 64 s clock wrap
    float tw = 1.0 - (0.10 + 0.22 * c.pad) * (0.5 + 0.5 * sin(c.twClock * rate + 6.2832 * byteOf(h2, 1)));
    float3 col = haze + c.dust * (DUST_A + DUST_B * r0 * r0) * tw * starCore(d2, 0.55 * c.scale);
    float2 starSky = sc / cells;
    float sharp = min(1.0, cellPx / 10.0);                 // glints only where the cell has room
    if (c.liveHat > 0.5) {
        float l = hatLight(h2, starSky, c.f, s, 0.0062 * mix(1.0, 0.55, c.calm), c.calm);
        l *= c.gain * mix(1.0, 0.6, c.stage);
        if (l > 0.002) {
            float sig = mix(0.55, 0.95, sqrt(l)) * c.scale;
            lit += kHatCore * (3.0 * l) * starCore(d2, sig);
            lit += kHatHalo * (0.9 * l) * exp(-d2 / (2.0 * 1.25 * 1.25 * c.scale * c.scale + 0.2));
            lit += kHatCore * (0.6 * l * sharp * (1.0 - c.calm)) * glint(toScreen(dpx, c.f), 0.9 * c.scale + 0.2, false);
        }
    }
    if (c.liveKick > 0.5) {
        float2 amongBright = float2(starSky.x, wrapY(starSky.y - c.skyD + c.skyB));   // where the epicentres ride
        float l = kickLight(h2, amongBright, s, 0.07 * mix(1.0, 0.55, c.calm), c.calm, 0.02, 0.15,
                            float2(0.18, 0.30), 0x3C6EF372u);
        l *= c.gain * mix(1.0, 0.35, c.stage);
        if (l > 0.002) {
            lit += float3(1.0, 0.80, 0.50) * (2.0 * l) * starCore(d2, mix(0.55, 0.95, sqrt(l)) * c.scale);
            lit += kKickHalo * (1.0 * l) * exp(-d2 / (2.0 * 1.35 * 1.35 * c.scale * c.scale + 0.2));
        }
    }
    return col;
}

// Bright layer: 34 cells per H, 77% occupied, the 2x2 nearest cells so halos up to 14 px never
// clip: about 1,580 stars. Each has a fixed sound type: kick 30%, snare 18%, vocal 12%,
// bass giants 2%, and the rest are white stars (two fifths of them pad embers).
static float3 brightLayer(StarCtx c, constant float4* s, thread float3& lit) {
    const float cells = 34.0;
    float2 g = float2(c.ps.x, wrapY(c.ps.y + c.skyB)) * cells;
    int2 base = int2(floor(g - 0.5));
    float cellPx = c.resY / cells;                          // 29.6 px on a 1006 px tall surface
    float reach = min(14.0, 0.48 * cellPx);
    float room = min(1.0, cellPx / 29.6);                   // halos shrink with the cell on small surfaces
    float3 rest = float3(0.0);
    for (int j = 0; j < 2; j++) {
        for (int i = 0; i < 2; i++) {
            int2 ci = base + int2(i, j);
            uint h = cellHash(ci.x, imod(ci.y, 8704), 0xB51u);
            if ((h & 255u) > 196u) continue;
            float2 sc = float2(ci) + 0.06 + 0.88 * float2(byteOf(h, 1), byteOf(h, 2));
            float2 dpx = (g - sc) * cellPx;
            float d2 = dot(dpx, dpx);
            if (d2 > reach * reach) continue;
            float r0 = byteOf(h, 3);
            uint h2 = hashu(h ^ 0x02e5be93U);
            uint type = (h2 >> 8) % 100u;
            float mag = BRIGHT_A + BRIGHT_B * r0 * r0 * r0;
            if (type >= 60u && type < 62u) mag = 0.80;         // bass giants are bright at rest
            float rate = 0.0981748 * float(4u + (h2 & 31u));
            float tw = 1.0 - (0.08 + 0.20 * c.pad) * (0.5 + 0.5 * sin(c.twClock * rate + 6.2832 * byteOf(h2, 3)));
            float sigRest = (0.55 + 0.20 * mag) * c.scale;
            rest += kStarWhite * mag * tw * starCore(d2, sigRest);

            float2 starSky = sc / cells;                          // sky position (H), y in [0, 256)
            float2 dscr = toScreen(dpx, c.f);                     // the same offset on the screen (px)
            float2 starP = c.p - dscr / c.resY;                   // screen position (H)
            float l = 0.0;
            float3 coreT = float3(1.0), halo = float3(0.0);
            float sigLit = 1.2, haloSig = 2.4, haloAmt = 0.9, gainCore = 1.6, glen = 0.0, stageDim = 0.5;
            bool diag = false;
            if (type < 30u) {                       // kick: gold flares rippling out of the epicentres
                if (c.liveKick > 0.5) l = kickLight(h2, starSky, s, 0.75 * mix(1.0, 0.55, c.calm), c.calm,
                                                    0.05, 0.26, float2(0.36, 0.60), 0x632BE5ABu);
                coreT = kKickCore; halo = kKickHalo; sigLit = 1.6; haloSig = 3.8; haloAmt = 1.0; gainCore = 2.2;
                glen = byteOf(h2, 2) > 0.55 ? 7.0 : 0.0; stageDim = 0.35;
            } else if (type < 48u) {                // snare: an ice-cyan crackle with x glints
                if (c.liveSnare > 0.5) l = snareLight(h2, starP, c.aspect, s, 0.18 * mix(1.0, 0.55, c.calm), c.calm);
                coreT = kSnareCore; halo = kSnareHalo; sigLit = 1.4; haloSig = 2.6; haloAmt = 1.1; gainCore = 2.4;
                glen = byteOf(h2, 2) > 0.35 ? 6.0 : 0.0; diag = true; stageDim = 0.45;
            } else if (type < 60u) {                // vocal: around the lead craft, shimmering while sung
                if (c.vocal > 0.04) {
                    float lo = mix(0.10, 0.70, byteOf(h2, 2));
                    float near = 1.0 - smoothstep(0.10, 0.60, length(starP - c.lead));
                    float f = 1.0 + 0.5 * floor(byteOf(h2, 1) * 3.999);    // 1, 1.5, 2, 2.5: exact under the 4 pi wrap
                    float shimmer = 0.55 + 0.45 * sin(c.vocalPhase * f + 6.2832 * byteOf(h2, 0));
                    l = smoothstep(lo, lo + 0.2, c.vocal) * shimmer * (0.06 + 0.94 * near) * mix(1.0, 0.6, c.calm);
                }
                coreT = kVocalCore; halo = kVocalHalo; sigLit = 1.55; haloSig = 3.2; haloAmt = 1.25; gainCore = 2.4;
                stageDim = 0.5;
            } else if (type < 62u) {                // bass giant: a threshold ladder
                float lo = mix(0.22, 0.85, byteOf(h2, 2));
                float shim = 0.0981748 * float(7u + ((h2 >> 20) & 7u));     // 0.7-1.4 rad/s, exact under the 64 s wrap
                l = smoothstep(lo, lo + 0.16, c.bass) * (0.9 + 0.1 * sin(c.twClock * shim + byteOf(h2, 0) * 6.2832));
                l *= (0.45 + 0.55 * (1.0 - smoothstep(0.25, 0.85, starP.y))) * mix(1.0, 0.6, c.calm);
                coreT = kBassCore; halo = kBassHalo; sigLit = 2.2; haloSig = 5.5; haloAmt = 0.6; gainCore = 1.2;
                stageDim = 0.35;
            } else if (byteOf(h2, 2) < 0.40) {      // pad ember: a soft lavender glow inside the clouds
                float slow = 0.0981748 * float(3u + ((h2 >> 23) & 3u));     // 0.3-0.6 rad/s, exact under the 64 s wrap
                l = c.pad * c.cloud * (0.5 + 0.5 * sin(c.twClock * slow + byteOf(h2, 0) * 6.2832)) * mix(1.0, 0.6, c.calm);
                coreT = float3(0.95, 0.90, 1.0); halo = kPadHalo; sigLit = 0.9; haloSig = 5.0; haloAmt = 0.35; gainCore = 0.6;
                stageDim = 0.5;
            }
            l *= c.gain * mix(1.0, stageDim, c.stage);
            if (l > 0.002) {
                float sl = sqrt(saturate(l));
                float sig = mix(sigRest, sigLit * c.scale, sl);
                lit += coreT * (gainCore * l) * starCore(d2, sig);
                float hs = haloSig * min(room, c.scale);
                float win = 1.0 - smoothstep(0.6 * reach, reach, sqrt(d2));
                lit += halo * (haloAmt * l * win) * exp(-d2 / (2.0 * hs * hs));
                if (glen > 0.0 && c.calm < 0.999) {
                    lit += mix(float3(1.0), halo, 0.55) * (0.6 * l * win * (1.0 - c.calm)) * glint(dscr, glen * min(room, c.scale) * (0.4 + 0.6 * sl), diag);
                }
            }
        }
    }
    return rest;
}

// ------------------------------------------------------------ trails
struct TrailHit { float core; float glow; float bright; float hot; float lead; };

// Each trail is the craft's lateral history as a graph over the flight axis: 56 samples at
// uniform axis distance, one Catmull-Rom lookup per pixel, no segment loop.
static TrailHit trail(constant float4* s, int k, float sAxis, float cAxis, float resY) {
    TrailHit h; h.core = 0.0; h.glow = 0.0; h.bright = 1.0; h.hot = 0.0; h.lead = 0.0;
    float s0 = s[HDR1].x, ds = s[HDR1].y;
    float fi = (sAxis - s0) / ds;
    if (fi < 0.0 || fi > float(kTrailN - 1)) return h;
    int i1 = min(int(floor(fi)), kTrailN - 2);
    float u = fi - float(i1);
    int b = TRAIL + k * kTrailN;
    float4 p0 = s[b + max(i1 - 1, 0)], p1 = s[b + i1], p2 = s[b + i1 + 1], p3 = s[b + min(i1 + 2, kTrailN - 1)];
    float u2 = u * u, u3 = u2 * u;
    float cc = 0.5 * ((2.0 * p1.x) + (-p0.x + p2.x) * u + (2.0 * p0.x - 5.0 * p1.x + 4.0 * p2.x - p3.x) * u2
                + (-p0.x + 3.0 * p1.x - 3.0 * p2.x + p3.x) * u3);
    float dc = 0.5 * ((-p0.x + p2.x) + 2.0 * (2.0 * p0.x - 5.0 * p1.x + 4.0 * p2.x - p3.x) * u
                + 3.0 * (-p0.x + 3.0 * p1.x - 3.0 * p2.x + p3.x) * u2) / ds;
    float4 m = mix(p1, p2, u);
    float dist = abs(cAxis - cc) * rsqrt(1.0 + dc * dc);
    if (dist > 0.05) return h;
    float headS = s[CRAFT + 2 * k + 1].y;
    float headMask = smoothstep(headS + 0.004, headS + 0.020, sAxis);
    float age = max(sAxis - headS, 0.0) * s[CLK].w;              // the history drifts at a constant speed
    float hw = 0.0045 * m.y * (1.0 - 0.20 * saturate(age));     // 13 px at 1440p near the head, 10.4 px after 1 s
    float aa = 0.75 / resY;
    h.core = (1.0 - smoothstep(hw - aa, hw + aa, dist)) * headMask;
    h.glow = exp(-max(dist - hw, 0.0) / (0.0037 * (1.0 + 0.8 * saturate(m.z - 1.0)))) * headMask;   // e-fold 5.3 px at 1440p
    h.bright = m.z;                                              // glow: sung phrases, bass notes, kick packets
    h.hot = m.w;                                                 // kick packets whiten the core
    h.lead = saturate((m.y - 1.0) / 0.35);                       // the trail's own sound (voice or bass)
    return h;
}

// ------------------------------------------------------------ craft (original stylised dart)
static float sdSegmentRound(float2 p, float2 a, float2 b, float ra, float rb) {
    float2 pa = p - a, ba = b - a;
    float h = saturate(dot(pa, ba) / dot(ba, ba));
    return length(pa - ba * h) - mix(ra, rb, h);
}
static float sdTri(float2 p, float2 a, float2 b, float2 c) {
    float2 e0 = b - a, e1 = c - b, e2 = a - c;
    float2 v0 = p - a, v1 = p - b, v2 = p - c;
    float2 pq0 = v0 - e0 * saturate(dot(v0, e0) / dot(e0, e0));
    float2 pq1 = v1 - e1 * saturate(dot(v1, e1) / dot(e1, e1));
    float2 pq2 = v2 - e2 * saturate(dot(v2, e2) / dot(e2, e2));
    float sgn = sign(e0.x * e2.y - e0.y * e2.x);
    float2 d = min(min(float2(dot(pq0, pq0), sgn * (v0.x * e0.y - v0.y * e0.x)),
                       float2(dot(pq1, pq1), sgn * (v1.x * e1.y - v1.y * e1.x))),
                       float2(dot(pq2, pq2), sgn * (v2.x * e2.y - v2.y * e2.x)));
    return -sqrt(d.x) * sign(d.y);
}
// The craft's light: `under` (beneath the hull) is the engine glow, which the trail's own sound
// swells, and a kick's bloom round it; `over` (on top of the hull) is a snare's ice sparkle at
// the nose. A snare also lights the hull's rim. fx: (engine glow, head s, snare sparkle, kick bloom).
static float4 craftSprite(float2 p, float4 craft, float4 fx, int k, float4 trailColour,
                          float resY, thread float3& under, thread float3& over) {
    const float L = 0.074;                       // craft length, H (110 px at 1440p)
    float2 d = p - craft.xy;
    under = float3(0.0); over = float3(0.0);
    // The bloom's reach: 1.36 L from the centre is at least 0.91 L from the engine (0.45 L
    // back), where the bloom is below 1e-4, so its edge never shows.
    if (dot(d, d) > L * L * 1.85) return float4(0.0);
    float ch = cos(craft.z), sh = sin(craft.z);
    float2 lp = float2(dot(d, float2(ch, sh)), dot(d, float2(-sh, ch))) / L;   // x forward, y left
    float2 ep = lp - float2(-0.45, 0.0);
    float r2 = dot(ep, ep);
    under = trailColour.rgb * (exp(-r2 / 0.006) * (0.9 * fx.x + 1.4 * fx.w) + 0.65 * fx.w * exp(-r2 / 0.09));
    if (fx.z > 0.002) {
        float2 dpx = (d - L * 0.53 * float2(ch, sh)) * resY;     // pixels from just past the nose
        float px = resY / 1000.0;
        float core = exp(-dot(dpx, dpx) / (2.0 * 1.6 * 1.6 * px * px + 0.3));
        over = kSnareRim * fx.z * (1.3 * core + 0.75 * glint(dpx, 6.0 * px + 0.5, true));
    }
    if (dot(d, d) > L * L * 0.6) return float4(0.0);
    float bank = craft.w;
    float squash = max(0.45, cos(bank));
    float2 bp = float2(lp.x, lp.y / squash);
    float2 ap = float2(bp.x, abs(bp.y));
    float fus = sdSegmentRound(bp, float2(-0.40, 0.0), float2(0.40, 0.0), 0.085, 0.014);
    fus = min(fus, sdSegmentRound(bp, float2(0.40, 0.0), float2(0.50, 0.0), 0.014, 0.003));
    float wing = sdTri(ap, float2(0.10, 0.03), float2(-0.28, 0.33), float2(-0.24, 0.03));
    wing = min(wing, sdTri(ap, float2(-0.24, 0.03), float2(-0.28, 0.33), float2(-0.36, 0.28)));
    float fin = sdTri(ap, float2(-0.26, 0.02), float2(-0.47, 0.19), float2(-0.43, 0.02));
    float body = min(fus, min(wing, fin));
    float aa = 1.0 / (resY * L);
    float alpha = 1.0 - smoothstep(-aa, aa, body);
    float3 hull = k == 0 ? float3(0.80, 0.24, 0.32) : float3(0.33, 0.48, 0.90);
    float3 dark = k == 0 ? float3(0.36, 0.08, 0.15) : float3(0.12, 0.17, 0.42);
    float3 hi   = k == 0 ? float3(1.00, 0.62, 0.66) : float3(0.74, 0.84, 1.00);
    hull = mix(hull, trailColour.rgb * 0.86, trailColour.w);
    dark = mix(dark, trailColour.rgb * 0.35, trailColour.w);
    hi = mix(hi, mix(trailColour.rgb, float3(1.0), 0.45), trailColour.w);
    float2 lightLocal = float2(dot(float2(-0.6, 0.8), float2(ch, sh)), dot(float2(-0.6, 0.8), float2(-sh, ch)));
    float side = sign(bp.y) * sign(lightLocal.y) * (0.6 + 0.4 * sin(bank) * sign(bp.y));
    float3 colr = mix(dark, hull, 0.55 + 0.45 * clamp(side, -1.0, 1.0));
    colr = mix(colr, dark, smoothstep(0.0, -0.02, wing) * (1.0 - smoothstep(-0.02, -0.005, fus)) * 0.35);
    float spine = exp(-bp.y * bp.y / 0.0012) * smoothstep(-0.40, 0.2, bp.x);
    colr = mix(colr, hi, 0.45 * spine);
    float canopy = 1.0 - smoothstep(-aa, aa, length((bp - float2(0.13, 0.0)) / float2(0.11, 0.032)) - 1.0);
    colr = mix(colr, k == 0 ? float3(0.20, 0.05, 0.12) : float3(0.05, 0.08, 0.22), canopy * 0.85);
    colr += canopy * 0.35 * exp(-dot(bp - float2(0.16, 0.01), bp - float2(0.16, 0.01)) / 0.0006);
    float edge = smoothstep(-3.0 * aa, -aa, body) * alpha;
    colr = mix(colr, dark, 0.5 * edge);
    colr = mix(colr, kSnareRim, saturate(0.6 * fx.z * edge + 0.15 * fx.z * spine));    // a snare's cool rim light
    return float4(colr, alpha);
}

// ------------------------------------------------------------ asteroids (lumpy, bump-shaded, lit from the upper left)
static float4 asteroidSprite(float2 p, float4 a0, float4 a1, float resY) {
    float2 rel = p - a0.xy;
    float R = a0.z;
    if (a1.z <= 0.001 || dot(rel, rel) > R * R * 1.9) return float4(0.0);
    float cs = cos(a0.w), sn = sin(a0.w);
    float2 l = float2(cs * rel.x + sn * rel.y, -sn * rel.x + cs * rel.y) / R;
    float th = atan2(l.y, l.x);
    float ph = a1.x * 0.61;
    float edge = 1.0 + 0.11 * sin(2.0 * th + ph) + 0.07 * sin(3.0 * th + 1.7 * ph) + 0.045 * sin(5.0 * th + 2.3 * ph)
               + 0.03 * sin(9.0 * th + ph);
    float sd = (length(l) - edge) * R;
    float blur = a1.y;
    float aa = mix(0.9 / resY, R * 0.40, blur);
    float alpha = 1.0 - smoothstep(-aa, aa, sd);
    if (alpha <= 0.0) return float4(0.0);
    uint salt = 91u + uint(a1.x);
    float2 nl = l / max(edge, 0.5);
    float nz = sqrt(max(0.0, 1.0 - dot(nl, nl)));
    float bump = fbmL(l * 2.6 + float2(a1.x * 0.37, 1.3), salt, 3);
    float bump2 = fbmL(l * 2.6 + 7.0, salt + 6u, 2);
    float2 nScreen = float2(cs * nl.x - sn * nl.y, sn * nl.x + cs * nl.y);
    float3 n = normalize(float3(nScreen + 0.9 * float2(bump, bump2), nz + 0.15));
    float lambert = saturate(dot(n, normalize(float3(-0.55, 0.6, 0.58))));
    float crater = smoothstep(0.08, 0.20, fbmL(l * 1.9 + float2(5.0, a1.x), salt + 2u, 2));
    float3 c = mix(float3(0.13, 0.115, 0.17), float3(0.60, 0.57, 0.69), lambert) * (1.0 - 0.35 * crater) * (0.90 + 0.30 * bump);
    c += float3(0.18, 0.09, 0.24) * pow(1.0 - nz, 3.0) * 0.6;          // violet sky light on the rim
    c += float3(1.0, 0.62, 0.20) * a1.w * pow(1.0 - nz, 2.0) * 0.45;   // a kick catches the rim (gold)
    c = mix(c, float3(0.30, 0.26, 0.38), blur * 0.45);
    return float4(c, alpha * a1.z * (1.0 - 0.25 * blur));
}


} // namespace st

fragment float4 starTrailsFragment(FSQuadOut in [[stage_in]], constant VizUniforms& u [[buffer(0)]],
                                   constant float4* album [[buffer(4)]],
                                   constant float* exposure [[buffer(5)]],
                                   constant float4* s [[buffer(6)]]) {
    using namespace st;
    float resY = max(1.0, u.resolution.y);
    float aspect = u.resolution.x / resY;
    float2 p = float2(in.uv.x * aspect, in.uv.y);           // H units, (0,0) bottom-left
    float4 hdr0 = s[HDR0], hdr1 = s[HDR1], hdr2 = s[HDR2], env0 = s[ENV0], env1 = s[ENV1];
    float calm = saturate(hdr2.z);                          // CPU-ramped over 0.5 s, so a toggle never pops

    // Lyric stage: a soft ellipse (uv) where busy detail steps back.
    float4 stg = s[STAGE];
    float stage = 1.0 - smoothstep(0.7, 1.2, length((in.uv - stg.xy) / max(stg.zw, float2(0.01))));

    // The flight axis f (the craft's mean heading) and the sky frame built on it: the scenery
    // drifts along -f, so it always moves against the craft's flight.
    float ang = hdr0.z;
    float2 f = float2(cos(ang), sin(ang)), n = float2(-f.y, f.x);
    float2 ps = float2(dot(p, float2(f.y, -f.x)), dot(p, f));
    float4 clk = s[CLK];

    float2 mA = float2(cos(hdr2.x), sin(hdr2.x)), mB = float2(cos(hdr2.y), sin(hdr2.y));
    const float4 cal = float4(-0.015, 0.02, 1.0, 0.0);
    float albumMix = saturate(s[SKYTINT].w);
    Nebula neb = nebula(s, ps, clk.z, mA, mB, stage, env1.y, env1.x, env1.w, cal, albumMix);
    float3 col = neb.colour;
    if (albumMix > 0.0001) {
        col = mix(col, albumNebula(s, col, in.uv + 0.035 * mA), albumMix);
    }

    StarCtx c;
    c.p = p; c.aspect = aspect; c.resY = resY; c.scale = resY / 1000.0;
    c.ps = ps; c.f = f; c.skyB = hdr1.z; c.skyD = hdr1.w; c.dust = s[SKY + 10].rgb;
    c.stage = stage; c.cloud = neb.cloud; c.calm = calm; c.gain = hdr2.w;
    c.twClock = clk.x; c.pad = env1.y; c.bass = env0.w; c.vocal = env1.x; c.vocalPhase = clk.y;
    c.lead = s[CRAFT].xy;
    float4 live = s[LIVE];
    c.liveKick = live.x; c.liveSnare = live.y; c.liveHat = live.z;
    float3 soundLight = float3(0.0);
    float3 stars = dustLayer(c, s, soundLight);
    stars += brightLayer(c, s, soundLight);
    stars *= mix(1.0, 0.88, stage);                          // resting stars step back a little behind the lyrics
    stars += soundLight;                                     // sound colours keep their meaning on any cover

    // Asteroids behind the trails (far) and in front (near, blurred): cull with one compare each.
    float4 back = float4(0.0), front = float4(0.0);
    int hit0 = -1, hit1 = -1;
    int nAst = min(int(live.w), kAst);
    for (int i = 0; i < nAst; i++) {
        float4 a0 = s[AST + 2 * i];
        float2 d = p - a0.xy;
        if (dot(d, d) < a0.z * a0.z * 1.9) { if (hit0 < 0) hit0 = i; else if (hit1 < 0) hit1 = i; }
    }
    for (int hI = 0; hI < 2; hI++) {
        int i = hI == 0 ? hit0 : hit1;
        if (i < 0) break;
        float4 a1 = s[AST + 2 * i + 1];
        float4 sp = asteroidSprite(p, s[AST + 2 * i], a1, resY);
        if (a1.y > 0.45) front = float4(mix(front.rgb, sp.rgb, sp.a), max(front.a, sp.a));
        else back = float4(mix(back.rgb, sp.rgb, sp.a), max(back.a, sp.a));
    }
    col += stars * (1.0 - back.a);
    col = mix(col, back.rgb, back.a);

    // Trails: glows add light, cores cover what is behind them; crossing cores add up to white.
    float2 rel = p - hdr0.xy;
    float sAxis = -dot(rel, f), cAxis = dot(rel, n);
    TrailHit ta = trail(s, 0, sAxis, cAxis, resY);
    TrailHit tb = trail(s, 1, sAxis, cAxis, resY);
    float glowGain = kTrailGlow * mix(1.0, 0.52, stage);       // glows step back behind the lyrics
    float4 trailA = s[TRAILCOLOUR], trailB = s[TRAILCOLOUR + 1];
    float3 warm = mix(kPinkWarmCore, mix(trailA.rgb, float3(1.0, 0.91, 0.80), 0.20), trailA.w);
    float3 glowA = mix(mix(kPinkGlow, kPinkWarmGlow, 0.7 * ta.lead), mix(trailA.rgb, warm, 0.5 * ta.lead) * 0.54, trailA.w);
    float3 glowB = mix(kLimeGlow, trailB.rgb * 0.50, trailB.w);
    col += glowA * ta.glow * (1.0 - ta.core) * min(2.6, ta.bright) * glowGain;
    col += glowB * tb.glow * (1.0 - tb.core) * min(2.6, tb.bright) * glowGain;
    float3 coreA = mix(mix(trailA.rgb, warm, 0.45 * ta.lead), float3(1.0, 0.92, 1.0), 0.7 * ta.hot);   // kick packets:
    float3 coreB = mix(trailB.rgb, float3(0.97, 1.0, 0.90), 0.7 * tb.hot);                              // a white-hot bead
    float cover = max(ta.core, tb.core);
    col = col * (1.0 - cover) + coreA * ta.core + coreB * tb.core;

    for (int k = 0; k < 2; k++) {
        float3 under, over;
        float4 cs = craftSprite(p, s[CRAFT + 2 * k], s[CRAFT + 2 * k + 1], k, s[TRAILCOLOUR + k], resY, under, over);
        col += under * (1.0 - cs.a);
        col = mix(col, cs.rgb, cs.a) + over;
    }
    col = mix(col, front.rgb, front.a);

    col = select(col, 0.85 + 0.15 * tanh((col - 0.85) / 0.15), col > 0.85);   // soft shoulder where glows overlap
    col *= smoothstep(0.0, 1.0, u.intro);
    col = richness(col, u.p[15]);
    return limitedExposure(saturate(col), exposure);
}
