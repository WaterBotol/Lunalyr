// Adapted from Synesthia by Jon Jaques, MIT licence.
// Pinned source and local changes: ThirdParty/Synesthia/SOURCE.md.
// Copyright (c) 2026 Jon Jaques. See Resources/Synesthia-LICENSE.txt.

#include <metal_stdlib>
using namespace metal;
#include "VisualizerShaderCommon.h"

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

// hash31, vnoise3, fbm3 and fbmSharp (the 3D value noise standing in for
// Shadertoy's noise texture) are in VisualizerShaderCommon.h.

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
    float e = visualEnergy(bandAt(bands, tunnelFold(angle)), u.sensitivity);
    // spiral ridges spinning past: rate from the Twist slider, depth from
    // the low-mids
    float arms = sin(angle * 3.0 - pos.z * 0.4 + u.time * u.speed * (0.5 + 1.0 * u.p[kTunnelTwist]))
               * (0.05 + 0.25 * visualEnergy(u.lowMid, u.sensitivity));
    // power-shaped noise carves terraced crags into the rock
    float crags = pow(fbmSharp(pos * 0.55) * 3.3, 1.3);
    // the bore's own slow, irregular breathing: a drifting noise field, so
    // caverns open and close instead of repeating like a sine would
    float cavern = vnoise3(float3(1.7, 2.9, pos.z * 0.06) + float3(0.0, u.time * 0.05, 0.0)) - 0.5;
    float r = 1.1
        + 0.5 * cavern
        + 0.45 * visualEnergy(u.bass, u.sensitivity)         // bass opens the bore without clipping at high gain
        + u.p[kTunnelRipple] * (0.42 * crags - 0.30                       // crags (Ripple slider)
                  + 0.08 * visualEnergy(waveAt(wave, fract(pos.z * 0.10)), u.sensitivity))
        + arms
        - 0.48 * e * e;                                     // each angular frequency lane shapes its wall
    // Floor the bore radius so a worst-case pile-up of carvings can never
    // close the tunnel over the camera (which rides the centerline).
    r = max(r, 0.30);
    return r - length(xy);
}

fragment float4 tunnelFragment(FSQuadOut in [[stage_in]],
                               constant VizUniforms& u [[buffer(0)]],
                               constant float4* album [[buffer(4)]],
                               constant float* bands [[buffer(1)]],
                               constant float* wave [[buffer(2)]],
                               constant float* exposure [[buffer(5)]]) {
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
    float e = visualEnergy(bandAt(bands, fold), u.sensitivity);

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
         * (0.08 + 1.0 * visualEnergy(u.highMid, u.sensitivity)) * glow;

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
    col = max(mix(float3(luma), col, 1.5 * u.p[15]), 0.0);
    col = col / (1.0 + col);
    col = col * col * (3.0 - 2.0 * col);
    return limitedExposure(albumTint(col, in.uv, album), exposure);
}
