// Adapted from Wavefield by Niko Dellic, MIT. See ThirdParty/Wavefield/SOURCE.md.
#include <metal_stdlib>
using namespace metal;

#include "VisualizerShaderCommon.h"

struct WavefieldMode {
    float4 shape;   // m, n, topology, band index
    float4 motion;  // phase, coherence, log frequency, layer
    float4 drive;   // excitation, pulse
};

struct WavefieldSample {
    float field;
    float gradient;
    float energy;
};

constant float wavefieldPi = 3.141592653589793;

// Default template values from src/templates/default.json.
constant float nodeWidth = 0.25;
constant float softness = 1.0;
constant float interference = 0.50;
constant float harmonicMix = 0.42;
constant float warpAmount = 0.38;
constant float warpScale = 0.78;
constant float driftAmount = 0.18;
constant float edgeFade = 0.12;
constant float densityScale = 1.20;
constant float brightness = 1.35;
constant float opacity = 1.10;
constant float sphereRadius = 1.35;

static float wavefieldHash(float2 p) {
    return fract(sin(dot(p, float2(127.1, 311.7))) * 43758.5453123);
}

static float wavefieldNoise(float2 p) {
    float2 i = floor(p);
    float2 f = fract(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return mix(
        mix(wavefieldHash(i), wavefieldHash(i + float2(1, 0)), u.x),
        mix(wavefieldHash(i + float2(0, 1)), wavefieldHash(i + float2(1, 1)), u.x), u.y);
}

static float wavefieldFBM(float2 p) {
    float value = 0.0, amplitude = 0.5;
    for (int octave = 0; octave < 4; ++octave) {
        value += amplitude * wavefieldNoise(p);
        p = p * 2.03 + float2(19.2, 11.4);
        amplitude *= 0.5;
    }
    return value;
}

// Wavefield's free-plate Chladni figure and analytic gradient.
static float plateValue(float m, float n, float2 p) {
    float2 angle = (p - 0.5) * (2.0 * wavefieldPi);
    float2 cm = cos(m * angle), cn = cos(n * angle);
    return cm.x * cn.y - cn.x * cm.y;
}

static float plateGradient(float m, float n, float2 p) {
    float2 angle = (p - 0.5) * (2.0 * wavefieldPi);
    float2 cm = cos(m * angle), cn = cos(n * angle);
    float2 sm = sin(m * angle), sn = sin(n * angle);
    return (2.0 * wavefieldPi) * length(float2(
        -m * sm.x * cn.y + n * sn.x * cm.y,
        -n * cm.x * sn.y + m * cn.x * sm.y));
}

static WavefieldSample samplePlate(float2 p, constant VizUniforms& u, constant WavefieldMode* modes, int count) {
    float3 bands = float3(u.p[6], u.p[7], u.p[8]);
    float3 onsets = float3(u.p[10], u.p[11], u.p[12]);
    float change = u.p[4], pulse = u.p[5], energy = u.p[13];
    float spectrumShape = clamp(dot(bands, float3(0.24, 0.38, 0.52))
        + energy * 0.44 + change * 0.38 + pulse * 0.3, 0.0, 3.0);
    float2 drift = u.time * float2(
        0.013 + driftAmount * 0.034 + spectrumShape * 0.012,
        -(0.011 + driftAmount * 0.026 + spectrumShape * 0.009));
    float2 warp = float2(
        wavefieldFBM(p * (1.8 + warpScale * 3.4) + drift),
        wavefieldFBM(p.yx * (1.5 + warpScale * 3.1) - drift.yx));
    p += (warp - 0.5) * warpAmount * (0.012 + spectrumShape * 0.008);

    WavefieldSample sample = {};
    for (int index = 0; index < count; ++index) {
        WavefieldMode mode = modes[index];
        float m = mode.shape.x, n = mode.shape.y, topology = mode.shape.z;
        int band = int(mode.shape.w);
        float excitation = mode.drive.x, modePulse = mode.drive.y;
        float localAudio = clamp(bands[band] * 1.05 + onsets[band] * 0.74
            + excitation * 1.78 + modePulse * 1.42 + energy * 0.28
            + change * (0.18 + mode.motion.w * 0.26), 0.0, 3.0);
        float phase = cos(mode.motion.x + u.time * (0.04 + mode.motion.z * 0.18
            + excitation * 0.12 + modePulse * 0.22) + modePulse * 1.7 + onsets[band] * 0.8);
        float field = plateValue(m, n, p) * (0.9 + phase * 0.1 * mode.motion.y)
            + plateValue(n, m + 1.0, p) * interference * 0.34
            + plateValue(floor(m * 1.42), floor(n * 1.34), p) * harmonicMix * 0.22;
        float weight = topology * (0.62 + topology * 0.38);
        sample.field += field * weight * (0.82 + excitation * 0.16);
        sample.gradient += plateGradient(m, n, p) * weight * (0.0018 + localAudio * 0.0007);
        sample.energy += weight * (0.5 + localAudio * 0.2 + abs(field) * 0.62)
            * (0.72 + mode.motion.y * 0.34);
    }
    return sample;
}

static float3 heatmap(float heat) {
    float3 blue = float3(0.02, 0.08, 0.42), cyan = float3(0.0, 0.78, 0.92);
    float3 yellow = float3(1.0, 0.92, 0.18), red = float3(1.0, 0.12, 0.02);
    if (heat < 0.33) { return mix(blue, cyan, smoothstep(0.0, 0.33, heat)); }
    if (heat < 0.68) { return mix(cyan, yellow, smoothstep(0.33, 0.68, heat)); }
    return mix(yellow, red, smoothstep(0.68, 1.0, heat));
}

fragment float4 wavefieldFragment(FSQuadOut in [[stage_in]],
                                   constant VizUniforms& u [[buffer(0)]],
                                   constant float4* album [[buffer(4)]],
                                   constant WavefieldMode* modes [[buffer(3)]],
                                   constant float* exposure [[buffer(5)]]) {
    int count = clamp(int(u.p[3]), 0, 12);
    if (count == 0) { return float4(0, 0, 0, 1); }

    float aspect = u.resolution.x / max(1.0, u.resolution.y);
    bool sphere = u.p[0] > 0.5;
    WavefieldSample field;
    if (sphere) {
        // Ray against the original sphere and 42° perspective camera. The
        // analytic surface gives the same triplanar normals as the proxy mesh.
        float3 origin = float3(0, 0, 3.7);
        float3 ray = normalize(float3((in.uv * 2.0 - 1.0) * float2(aspect, 1.0) * 0.383864, -1.0));
        float b = dot(origin, ray);
        float d = b * b - (dot(origin, origin) - sphereRadius * sphereRadius);
        if (d <= 0) { return float4(0, 0, 0, 1); }
        float3 normal = normalize(origin + (-b - sqrt(d)) * ray);
        float3 p = normal * 0.5 + 0.5;
        float3 weights = normal * normal;
        weights *= weights;
        weights /= weights.x + weights.y + weights.z;
        // The upstream plate projection aspect-corrects each planar sample.
        float2 plateScale = float2(aspect, 1.0);
        WavefieldSample xy = samplePlate((p.xy - 0.5) * plateScale + 0.5, u, modes, count);
        WavefieldSample yz = samplePlate((p.yz - 0.5) * plateScale + 0.5, u, modes, count);
        WavefieldSample zx = samplePlate((p.zx - 0.5) * plateScale + 0.5, u, modes, count);
        field.field = dot(float3(yz.field, zx.field, xy.field), weights);
        field.gradient = dot(float3(yz.gradient, zx.gradient, xy.gradient), weights);
        field.energy = dot(float3(yz.energy, zx.energy, xy.energy), weights);
    } else {
        float2 centred = in.uv - 0.5;
        float2 circular = centred * float2(aspect, 1.0);
        float2 bent = clamp(centred * (1.0 - 0.33 * 4.0 * dot(circular, circular)) + 0.5, 0.0, 1.0);
        field = samplePlate((bent - 0.5) * float2(aspect, 1.0) + 0.5, u, modes, count);
    }

    float modeScale = sqrt(float(count));
    float normalizedField = field.field / (sphere ? max(0.18, modeScale * 0.15) : max(0.22, modeScale * 0.18));
    float distance = abs(normalizedField);
    float rms = u.p[9], energy = u.p[13];
    float pulse = clamp(u.p[5] * 0.88 + u.p[4] * 0.3 + rms * 0.16, 0.0, 1.8);
    float width = sphere ? max(0.00035, nodeWidth * 0.055 * (0.82 + pulse * 0.34))
                         : max(0.00042, nodeWidth * 0.035 * (0.85 + pulse * 0.3));
    float core = 1.0 - smoothstep(width * 0.32, width * 1.35, distance);
    float feather = 1.0 - smoothstep(width * 1.4, width * (4.2 + softness * 4.0), distance);
    float structure = smoothstep(0.02, 0.28 + edgeFade * 0.36, field.gradient);
    float density = clamp((core * core + feather * (0.2 + softness * 0.38))
        * (0.4 + structure * 0.6) * (0.66 + field.energy * (sphere ? 0.52 : 0.42))
        * densityScale * (0.92 + energy * 0.26 + pulse * 0.14), 0.0, 1.0);
    float halo = pow(clamp(1.0 - distance, 0.0, 1.0), 4.0) * softness * field.energy * 0.1;
    float visible = sphere ? 1.0 : smoothstep(0.005, 0.04, density + halo);
    float alpha = clamp((density + halo) * (0.96 + rms * 0.22) * opacity, 0.0, 1.0);
    float heat = pow(1.0 - smoothstep(0.0, 1.0, distance / (width * (4.2 + softness * 4.0))), 0.78);
    float3 color = heatmap(clamp(max(heat, halo * (3.0 + softness * 0.9)), 0.0, 1.0));
    color *= (0.82 + density * 0.72 + field.energy * 0.24 + pulse * 0.16) * visible * brightness;
    float luma = dot(color, float3(0.299, 0.587, 0.114));
    color = clamp(mix(float3(luma), color, u.p[15]), 0.0, 1.0);
    return limitedExposure(albumTint(color * alpha, in.uv, album), exposure);
}
