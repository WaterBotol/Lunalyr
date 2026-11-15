// Low Poly: a triangle mesh over a jittered grid. Each vertex sits at its own random offset and drifts on a slow small
// circle; each grid quad is split along a random diagonal. The pixel's triangle is found among the eight around the
// nearest vertex; its edges are hairlines and its face gets its own faint fill.
#include "../Common.hlsli"
#include "../PatternLines.hlsli"

static const float Cell = 96.0;   // DIP

// A cheap scatter for per-vertex and per-cell choices: good enough that no regular pattern shows.
float Scatter(float2 v) { return frac(52.9829189 * frac(dot(v, float2(0.06711056, 0.00583715)))); }

float2 Vertex(float2 index, float t)
{
    float2 at = index + float2(SeedA() * 97.0, SeedB() * 61.0);
    float2 h = float2(Scatter(at), Scatter(at.yx * 1.731 + 17.0));
    float turn = t * (0.25 + 0.25 * h.y) + h.x * Tau;
    return (index + (h - 0.5) * 0.40 + 0.08 * float2(cos(turn), sin(turn))) * Cell;
}

float Side(float2 a, float2 b, float2 p) { return (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x); }

bool Inside(float2 a, float2 b, float2 c, float2 p)
{
    float s0 = Side(a, b, p), s1 = Side(b, c, p), s2 = Side(c, a, p);
    return (s0 >= 0.0 && s1 >= 0.0 && s2 >= 0.0) || (s0 <= 0.0 && s1 <= 0.0 && s2 <= 0.0);
}

float Lines(float2 p)
{
    float t = Flow;
    float2 centre = floor(p / Cell + 0.5);

    float2 v00 = Vertex(centre + float2(-1, -1), t), v10 = Vertex(centre + float2(0, -1), t), v20 = Vertex(centre + float2(1, -1), t);
    float2 v01 = Vertex(centre + float2(-1, 0), t), v11 = Vertex(centre, t), v21 = Vertex(centre + float2(1, 0), t);
    float2 v02 = Vertex(centre + float2(-1, 1), t), v12 = Vertex(centre + float2(0, 1), t), v22 = Vertex(centre + float2(1, 1), t);

    float2 A = v11, B = v11, C = v11;
    float id = 0.0;
    bool found = false;
    [unroll] for (int k = 0; k < 4; k++)
    {
        // The quad (x, y)...(x+1, y+1) in the 3×3 block.
        float2 tl = k == 0 ? v00 : k == 1 ? v10 : k == 2 ? v01 : v11;
        float2 tr = k == 0 ? v10 : k == 1 ? v20 : k == 2 ? v11 : v21;
        float2 bl = k == 0 ? v01 : k == 1 ? v11 : k == 2 ? v02 : v12;
        float2 br = k == 0 ? v11 : k == 1 ? v21 : k == 2 ? v12 : v22;
        float2 cell = centre + float2(k == 1 || k == 3 ? 0.0 : -1.0, k >= 2 ? 0.0 : -1.0);
        float pick = Scatter(cell * 1.37 + float2(SeedB() * 211.0, 5.0));
        float2 d0 = pick < 0.5 ? tl : tr;   // the diagonal runs tl-br or tr-bl
        float2 d1 = pick < 0.5 ? br : bl;
        float2 o0 = pick < 0.5 ? tr : tl;
        float2 o1 = pick < 0.5 ? bl : br;
        if (!found && Inside(d0, d1, o0, p)) { A = d0; B = d1; C = o0; id = Scatter(cell * 2.0 + float2(SeedA() * 97.0, 0.5)); found = true; }
        if (!found && Inside(d0, d1, o1, p)) { A = d0; B = d1; C = o1; id = Scatter(cell * 2.0 + float2(SeedA() * 97.0 + 1.0, 0.5)); found = true; }
    }

    float d = min(SegmentDistance(p, A, B), min(SegmentDistance(p, B, C), SegmentDistance(p, C, A)));
    float face = id;
    float fill = 0.006 + 0.034 * face * face;
    return found ? Hairline(d, 0.8) * 0.11 + fill : 0.0;
}

#include "../PatternMain.hlsli"
