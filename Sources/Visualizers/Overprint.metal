#include <metal_stdlib>
using namespace metal;
#include "VisualizerShaderCommon.h"

static float inkCoverage(float2 point, float2 centre, float2 radii, float slant) {
    float2 local = point - centre;
    float2 bent = float2(local.x + slant * local.y, local.y + 0.15 * local.x * local.x);
    float edge = length(bent / radii);
    return 1.0 - smoothstep(0.89, 1.10, edge);
}

static float3 inkColour(float3 colour, float vividness) {
    float grey = dot(colour, float3(0.30, 0.59, 0.11));
    return clamp(mix(float3(grey), colour, vividness), 0.0, 1.0);
}

fragment float4 overprintFragment(FSQuadOut in [[stage_in]], constant VizUniforms& u [[buffer(0)]],
                                  constant float4* album [[buffer(4)]],
                                  constant float* exposure [[buffer(5)]]) {
    float2 point = in.uv - 0.5;
    float time = u.time * u.speed;
    float3 energy = max(float3(u.bass, u.mid, u.treble), float3(0.0)) * u.sensitivity;
    float3 response = energy / (0.54 + energy);

    float first = inkCoverage(point, float2(-0.37 + 0.056 * sin(time * 0.49), -0.24),
        float2(0.58 + 0.17 * response.x, 0.38 + 0.21 * response.x), -0.50);
    float second = inkCoverage(point, float2(0.38 - 0.30 * response.y, 0.18 + 0.08 * cos(time * 0.57)),
        float2(0.48, 0.43), 0.45);
    float third = inkCoverage(point, float2(0.10 + 0.055 * cos(time * 0.34), -0.55),
        float2(0.78, 0.40), 0.18);

    // Register the ink plate colours to the artwork and let overlaps darken
    // softly, like ink on coloured stock.
    bool hasAlbum = album[0].w > 0.0;
    // Colour richness also applies to the built-in inks when there is no
    // artwork (album[0].w carries the same value when there is).
    float vividness = u.p[15];
    float3 stock = hasAlbum ? album[2].rgb : float3(0.36, 0.32, 0.62);
    float3 left = inkColour(hasAlbum ? album[0].rgb : float3(0.42, 0.30, 0.71), vividness);
    float3 right = inkColour(hasAlbum ? album[4].rgb : float3(0.22, 0.60, 0.62), vividness);
    float3 bottom = inkColour(hasAlbum ? album[1].rgb : float3(0.69, 0.34, 0.46), vividness);
    float3 colour = float3(0.026) + stock * 0.11;
    colour += first * left * 0.33 + second * right * 0.31 + third * bottom * 0.16;
    colour *= 1.0 - 0.18 * first * second;

    // A stationary dot screen sits near the plate edges. Treble strengthens
    // the impression without moving the dots or changing the whole field.
    float aspect = u.resolution.x / max(1.0, u.resolution.y);
    float2 paperPoint = float2(point.x * aspect, point.y);
    float2 screen = float2(0.94 * paperPoint.x - 0.34 * paperPoint.y,
                          0.34 * paperPoint.x + 0.94 * paperPoint.y);
    float dotDistance = length(fract(screen * 76.0) - 0.5);
    float antialias = clamp(fwidth(dotDistance) * 0.65, 0.022, 0.20);
    float dots = 1.0 - smoothstep(0.29 - antialias, 0.29 + antialias, dotDistance);
    float edges = max(4.0 * first * (1.0 - first), 4.0 * second * (1.0 - second));
    colour *= 1.0 - edges * dots * (0.10 + 0.45 * response.z);

    float centreShade = mix(0.63, 1.0, smoothstep(0.06, 0.60, length(point)));
    return limitedExposure(colour * centreShade, exposure);
}
