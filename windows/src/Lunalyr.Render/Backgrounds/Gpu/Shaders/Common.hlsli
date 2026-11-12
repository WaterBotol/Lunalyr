// Shared by every GPU effect and line pattern. Two kinds of code live here:
//
//  * Ported faithfully from Lunalyr's own lines of the Mac's Shaders.metal (commits aa22c91 and 4765504, "direct
//    rendering"): the frame constants' meaning, the grade (album gradient in OKLab, colour blend, colour richness) and
//    the ambient breathing. See docs/PROVENANCE.md, "GPU effects and line patterns".
//  * Written fresh for Windows: coordinates, hashing, noise and the small maths helpers the effects share. Nothing
//    here is translated from the Verso-derived parts of Shaders.metal.

#ifndef LUNALYR_COMMON
#define LUNALYR_COMMON

// The frame's constants (EffectConstants / PatternConstants in C#; 16-byte rows).
cbuffer Frame : register(b0)
{
    float2 Size;        // the surface in device-independent pixels (the Mac's points)
    float2 PixelScale;  // canvas pixels per device-independent pixel
    float Time;         // the steady clock (breathing), seconds since this layer's time base
    float Flow;         // the flow clock the shapes move along
    float Vividness;    // colour richness, 0...1.8
    float Strength;     // patterns: line opacity (0.85 × pattern opacity); effects: unused
    float4 TintA;       // the album's first stop (top-left of the cover), sRGB
    float4 TintB;       // the album's third stop (the centre), sRGB
    float4 Stops[5];    // the five stops in OKLab, corner to corner
    float4 Extra;       // patterns: x = seed fraction, y = second seed fraction, z = DPI scale, w = unused
};

static const float Tau = 6.28318530718;

// ---------------------------------------------------------------------------------------------------------------
// Ported from Lunalyr's own Metal lines (4765504): a slow, faint breathing on its own steady clock.
float AmbientLight(float time) { return 0.98 + 0.025 * sin(time * 0.21); }

// Ported from Lunalyr's own Metal lines (aa22c91): the grade that tints an effect with the album gradient.
float BlendLum(float3 c) { return dot(c, float3(0.3, 0.59, 0.11)); }

float3 ClipColour(float3 c)
{
    float l = BlendLum(c);
    float n = min(c.r, min(c.g, c.b));
    float x = max(c.r, max(c.g, c.b));
    if (n < 0.0) c = l + (c - l) * l / max(l - n, 1e-5);
    if (x > 1.0) c = l + (c - l) * (1.0 - l) / max(x - l, 1e-5);
    return c;
}

float3 EncodeSrgb(float3 l)
{
    float3 curve = 1.055 * pow(max(l, 0.0), 1.0 / 2.4) - 0.055;
    return l <= 0.0031308 ? l * 12.92 : curve;
}

float3 FromOkLab(float3 c)
{
    float l = c.x + 0.3963377774 * c.y + 0.2158037573 * c.z;
    float m = c.x - 0.1055613458 * c.y - 0.0638541728 * c.z;
    float s = c.x - 0.0894841775 * c.y - 1.2914855480 * c.z;
    l = l * l * l; m = m * m * m; s = s * s * s;
    float3 linearRgb = float3( 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
                              -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
                              -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
    return EncodeSrgb(saturate(linearRgb));
}

// The album gradient, top-left to bottom-right through the five stops, interpolated in OKLab.
float3 AlbumGradient(float2 position)
{
    float t = saturate(dot(position, Size) / max(dot(Size, Size), 1e-3)) * 4.0;
    int index = min((int)t, 3);
    float4 a = Stops[0], b = Stops[1];
    // Constant-buffer arrays indexed by a variable cost a little on some drivers; four stops make this cheap.
    if (index == 1) { a = Stops[1]; b = Stops[2]; }
    else if (index == 2) { a = Stops[2]; b = Stops[3]; }
    else if (index == 3) { a = Stops[3]; b = Stops[4]; }
    return FromOkLab(lerp(a.xyz, b.xyz, t - (float)index));
}

// The colour blend (the gradient's hue and saturation, the effect's luminosity), then colour richness about grey.
float3 Grade(float3 effect, float2 position)
{
    float3 lit = saturate(effect);
    float3 album = AlbumGradient(position);
    float3 tinted = ClipColour(album + (BlendLum(lit) - BlendLum(album)));
    float grey = dot(tinted, float3(0.2126, 0.7152, 0.0722));
    return saturate(lerp(grey.xxx, tinted, Vividness));
}

// ---------------------------------------------------------------------------------------------------------------
// Windows' own helpers.

// Centred coordinates: the shorter side spans -0.5...0.5, y down as on screen.
float2 Centred(float2 p) { return (p - 0.5 * Size) / min(Size.x, Size.y); }

float2 Turn(float2 v, float angle)
{
    float s, c;
    sincos(angle, s, c);
    return float2(c * v.x - s * v.y, s * v.x + c * v.y);
}

// Integer hashing (a PCG-style permutation): stable, no sine, no visible lattice.
uint2 Permute(uint2 v)
{
    v = v * 1664525u + 1013904223u;
    v.x += v.y * 1664525u;
    v.y += v.x * 1664525u;
    v ^= v >> 16u;
    v.x += v.y * 1664525u;
    v.y += v.x * 1664525u;
    v ^= v >> 16u;
    return v;
}

// Two independent values in 0...1 for a lattice cell.
float2 Random2(float2 cell)
{
    uint2 h = Permute(asuint((int2)floor(cell) + int2(4096, 4096)));
    return float2(h) * (1.0 / 4294967296.0);
}

float Random1(float2 cell) { return Random2(cell).x; }

// A value in 0...1 for a whole number (an index, a seed).
float RandomOf(float n, float salt) { return Random2(float2(n, salt * 7.0 + 131.0)).y; }

// Value noise with a quintic fade, 0...1.
float Noise(float2 p)
{
    float2 cell = floor(p);
    float2 f = p - cell;
    float2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float a = Random1(cell), b = Random1(cell + float2(1, 0));
    float c = Random1(cell + float2(0, 1)), d = Random1(cell + float2(1, 1));
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

// Layered noise: each octave turned and shrunk so the layers never line up. 0...~1.
float Layered(float2 p, int octaves)
{
    float sum = 0.0, weight = 0.55, total = 0.0;
    [loop] for (int i = 0; i < octaves; i++)
    {
        sum += weight * Noise(p);
        total += weight;
        p = Turn(p, 0.6) * 2.07 + float2(5.3, -2.1);
        weight *= 0.52;
    }
    return sum / total;
}

// Soft mix of the two tints along t.
float3 Tints(float t) { return lerp(TintA.rgb, TintB.rgb, saturate(t)); }

// ---------------------------------------------------------------------------------------------------------------
// For the shader-drawn line patterns (PatternMain.hlsli). Positions there are in device-independent pixels (the Mac's
// points); the canvas is at native resolution.

// The song's seed as two stable fractions (0...1), for a pattern's random details.
float SeedA() { return Extra.x; }
float SeedB() { return Extra.y; }

// Physical pixels per device-independent pixel.
float DpiScale() { return max(Extra.z, 0.5); }

// Anti-aliased coverage of a line `widthDip` wide at `distanceDip` from its centre. Lines thinner than a physical
// pixel keep a one-pixel footprint and dim instead, so they never shimmer.
float Hairline(float distanceDip, float widthDip)
{
    float scale = DpiScale();
    float width = widthDip * scale;
    float d = abs(distanceDip) * scale;
    float drawn = max(width, 1.0);
    return saturate(0.5 * drawn + 0.5 - d) * min(width, 1.0);
}

#endif
