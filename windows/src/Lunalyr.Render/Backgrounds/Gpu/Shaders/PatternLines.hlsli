// Small helpers for the shader-drawn line patterns (Patterns/*.hlsl). Windows' own.
#ifndef LUNALYR_PATTERN_LINES
#define LUNALYR_PATTERN_LINES

// Distance (in the field's own units) from f to the nearest multiple of `spacing`.
float LevelOffset(float f, float spacing)
{
    return abs(frac(f / spacing + 0.5) - 0.5) * spacing;
}

// A hairline along every level f = k × spacing of a smooth field. `gradient` is the field's change per DIP
// (|grad f|); lines thinner than the gap they sit in are faded out so dense regions never turn into a grey wash.
float LevelLine(float f, float gradient, float spacing, float widthDip)
{
    float g = max(gradient, 1e-5);
    float distanceDip = LevelOffset(f, spacing) / g;
    float gapDip = spacing / g;
    float dense = saturate(gapDip * DpiScale() / 5.0 - 0.6);
    return Hairline(distanceDip, widthDip) * dense;
}

// |grad f| per DIP from screen-space derivatives (call at top level, never inside a loop or branch).
float GradientPerDip(float f)
{
    return length(float2(ddx(f), ddy(f))) * DpiScale();
}

// Distance from p to the segment a-b.
float SegmentDistance(float2 p, float2 a, float2 b)
{
    float2 ab = b - a;
    float h = saturate(dot(p - a, ab) / max(dot(ab, ab), 1e-6));
    return length(p - a - ab * h);
}

#endif
