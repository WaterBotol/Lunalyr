// Overprint, ported from Lunalyr for Mac's Overprint.metal (Lunalyr's own scene). Overlapping matte ink plates in the
// cover's colours: bass swells a plate, mids shift the overlap, and highs bring out a fine dot screen.
#include "Common.hlsli"

float inkCoverage(float2 at, float2 centre, float2 radii, float slant)
{
    float2 local = at - centre;
    float2 bent = float2(local.x + slant * local.y, local.y + 0.15 * local.x * local.x);
    float edge = length(bent / radii);
    return 1.0 - smoothstep(0.89, 1.10, edge);
}

float3 inkColour(float3 colour, float vividness)
{
    float grey = dot(colour, float3(0.30, 0.59, 0.11));
    return saturate(lerp(float3(grey, grey, grey), colour, vividness));
}

float4 main(FSQuadOut i) : SV_Target
{
    float2 at = i.uv - 0.5;
    float t = time * speed;
    float3 energy = max(float3(bass, mid, treble), 0.0) * sensitivity;
    float3 response = energy / (0.54 + energy);

    float first = inkCoverage(at, float2(-0.37 + 0.056 * sin(t * 0.49), -0.24),
        float2(0.58 + 0.17 * response.x, 0.38 + 0.21 * response.x), -0.50);
    float second = inkCoverage(at, float2(0.38 - 0.30 * response.y, 0.18 + 0.08 * cos(t * 0.57)),
        float2(0.48, 0.43), 0.45);
    float third = inkCoverage(at, float2(0.10 + 0.055 * cos(t * 0.34), -0.55),
        float2(0.78, 0.40), 0.18);

    // Register the ink plate colours to the artwork and let overlaps darken softly, like ink on coloured stock.
    bool hasAlbum = album[0].w > 0.0;
    // Colour richness also applies to the built-in inks when there is no artwork (album[0].w carries the same value
    // when there is).
    float vividness = opt(15);
    float3 stock = hasAlbum ? album[2].rgb : float3(0.36, 0.32, 0.62);
    float3 left = inkColour(hasAlbum ? album[0].rgb : float3(0.42, 0.30, 0.71), vividness);
    float3 right = inkColour(hasAlbum ? album[4].rgb : float3(0.22, 0.60, 0.62), vividness);
    float3 bottom = inkColour(hasAlbum ? album[1].rgb : float3(0.69, 0.34, 0.46), vividness);
    float3 colour = 0.026 + stock * 0.11;
    colour += first * left * 0.33 + second * right * 0.31 + third * bottom * 0.16;
    colour *= 1.0 - 0.18 * first * second;

    // A stationary dot screen sits near the plate edges. Treble strengthens the impression without moving the dots or
    // changing the whole field.
    float aspect = resolution.x / max(1.0, resolution.y);
    float2 paperPoint = float2(at.x * aspect, at.y);
    float2 screen = float2(0.94 * paperPoint.x - 0.34 * paperPoint.y,
                           0.34 * paperPoint.x + 0.94 * paperPoint.y);
    float dotDistance = length(fract(screen * 76.0) - 0.5);
    float antialias = clamp(fwidth(dotDistance) * 0.65, 0.022, 0.20);
    float dots = 1.0 - smoothstep(0.29 - antialias, 0.29 + antialias, dotDistance);
    float edges = max(4.0 * first * (1.0 - first), 4.0 * second * (1.0 - second));
    colour *= 1.0 - edges * dots * (0.10 + 0.45 * response.z);

    float centreShade = lerp(0.63, 1.0, smoothstep(0.06, 0.60, length(at)));
    return limitedExposure(colour * centreShade);
}
