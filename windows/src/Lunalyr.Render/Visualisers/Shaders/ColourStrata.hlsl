// Colour Strata, ported from Lunalyr for Mac's ColourStrata.metal (Lunalyr's own scene).
#include "Common.hlsli"

float4 main(FSQuadOut i) : SV_Target
{
    float2 uv = i.uv;
    float x = uv.x - 0.5;
    float y = uv.y;
    float t = time * speed;
    float3 energy = max(float3(bass, mid, treble), 0.0) * sensitivity;
    float3 response = energy / (0.56 + energy);

    // Each register owns a different edge. Their boundaries share a slow drift, with enough overlap to read as a
    // continuous dyed horizon.
    float lowEdge = 0.20 + 0.21 * response.x + 0.095 * sin(x * 3.2 + t * 0.38) + 0.031 * cos(x * 8.0 - t * 0.57);
    float middleEdge = 0.50 + 0.065 * sin(x * 4.7 - t * 0.29) + (0.018 + 0.14 * response.y) * cos(x * 5.6 + t * 0.47);
    float topEdge = 0.73 + 0.09 * cos(x * 3.5 + t * 0.36) + 0.026 * sin(x * 7.3 - t * 0.61);
    float topFeather = 0.065 + 0.19 * response.z;

    float lower = 1.0 - smoothstep(lowEdge - 0.105, lowEdge + 0.13, y);
    float middle = 1.0 - smoothstep(middleEdge - 0.084, middleEdge + 0.108, y);
    float upper = smoothstep(topEdge - topFeather, topEdge + topFeather, y);
    float middleLight = middle * (1.0 - lower);
    float edgeGlow = 1.0 - smoothstep(0.025, 0.19, abs(y - middleEdge));
    float2 centre = float2(x * 1.65, (y - 0.5) * 1.55);
    float contrast = lerp(0.61, 1.0, smoothstep(0.06, 0.72, length(centre)));

    float3 colour = float3(0.067, 0.088, 0.135);
    colour += float3(0.22, 0.092, 0.21) * lower;
    colour += float3(0.20, 0.25, 0.30) * middleLight;
    colour += float3(0.080, 0.17, 0.22) * upper;
    colour += float3(0.088, 0.091, 0.10) * edgeGlow;
    // Colour richness applies with or without artwork.
    return limitedExposure(albumTint(richness(colour * contrast, opt(15)), uv));
}
