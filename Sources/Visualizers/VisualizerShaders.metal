// Adapted from Synesthia by Jon Jaques, MIT licence.
// Pinned source and local changes: ThirdParty/Synesthia/SOURCE.md.
// Copyright (c) 2026 Jon Jaques. See Resources/Synesthia-LICENSE.txt.

// The shared vertex stage of the built-in visualizers. Their shaders are
// compiled at build time into the app's default Metal library (loaded via
// device.makeDefaultLibrary()), one scene per file in this folder:
//
//   SilkAurora.metal       auroraFragment (Spectral Aurora and Silk Ribbons)
//   SpectrumTunnel.metal   tunnelFragment
//   CurtainAurora.metal    curtainAuroraFragment
//   FosforaAurora.metal    fosforaAuroraFragment
//   StarTrails.metal, ColourStrata.metal, Overprint.metal, WavefieldShaders.metal
//
// Scene files include VisualizerShaderCommon.h (palette, spectrum and
// waveform sampling, noise), which brings in VisualizerShaderTypes.h, the
// layout shared with the CPU. AudioVisualizerStyle.fragment names the
// fragment function each style draws with; ExposureLimiter.metal is the
// photosensitivity limiter's kernel.
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
// MTLDevice.makeLibrary(source:), which is how these shaders used to load.

#include <metal_stdlib>
using namespace metal;

#include "VisualizerShaderTypes.h"

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
