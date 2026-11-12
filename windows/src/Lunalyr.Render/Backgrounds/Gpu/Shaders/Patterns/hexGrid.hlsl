// Hex Grid: a honeycomb of hexagon outlines drifting slowly sideways; a broad soft wave of brightness passes over it.
#include "../Common.hlsli"
#include "../PatternLines.hlsli"

static const float Cell = 30.0;   // DIP: the hexagons' inradius is half of this

float2 Wrap(float2 v, float2 period) { return v - period * floor(v / period); }

float Lines(float2 p)
{
    float t = Flow;
    float2 u = (p + float2(t * 7.0 + SeedA() * 500.0, SeedB() * 300.0)) / Cell;

    // Two offset rectangular lattices; the nearer centre is this point's hexagon.
    static const float2 Period = float2(1.0, 1.7320508);
    float2 a = Wrap(u, Period) - 0.5 * Period;
    float2 b = Wrap(u - 0.5 * Period, Period) - 0.5 * Period;
    float2 local = dot(a, a) < dot(b, b) ? a : b;

    float2 m = abs(local);
    float hex = max(dot(m, float2(0.5, 0.8660254)), m.x);   // 0.5 on the edges (pointy tops, flat sides left and right)
    float edge = (0.5 - hex) * Cell;

    float wave = 0.5 + 0.5 * sin(dot(p, float2(0.8, 0.6)) / 420.0 * Tau - t * 0.7);
    return Hairline(edge, 0.8) * (0.045 + 0.10 * wave * wave);
}

#include "../PatternMain.hlsli"
