// Which line patterns are drawn by a Direct2D painter (the others by Shaders/Patterns/<id>.hlsl). Windows' own.

namespace Lunalyr.Render.Backgrounds.Gpu;

internal static partial class LinePainters
{
    private static partial ILinePainter? Create(string patternId) => patternId switch
    {
        _ => null,
    };
}
