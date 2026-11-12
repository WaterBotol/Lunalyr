// Perspective Grid: a floor grid seen from just above. Lines along the floor converge on a vanishing point on the
// horizon; lines across it scroll toward the viewer and recycle at the horizon. Nearer lines are brighter; lines
// thin into the distance and fade before they crowd.
#include "../Common.hlsli"
#include "../PatternLines.hlsli"

float Lines(float2 p)
{
    float horizon = Size.y * 0.40;
    float focal = Size.y * 0.55;
    float below = max(p.y - horizon, 0.5);
    float depth = focal / below;                       // floor distance, camera one unit up
    float across = (p.x - 0.5 * Size.x) / focal * depth;
    float along = depth + Flow * 0.9;

    float gAcross = GradientPerDip(across);
    float gAlong = GradientPerDip(along);
    float spacing = 0.55;
    float lines = max(LevelLine(across, gAcross, spacing, 0.8), LevelLine(along, gAlong, spacing, 0.8));

    float nearness = saturate(below / (Size.y - horizon));
    float shown = smoothstep(0.0, 24.0, p.y - horizon);
    return lines * shown * (0.04 + 0.15 * nearness);
}

#include "../PatternMain.hlsli"
