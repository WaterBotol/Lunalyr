// Which line patterns are drawn by a Direct2D painter (the others by Shaders/Patterns/<id>.hlsl). Windows' own.

using Lunalyr.Render.Backgrounds.Gpu.Painters;

namespace Lunalyr.Render.Backgrounds.Gpu;

internal static partial class LinePainters
{
    private static partial ILinePainter? Create(string patternId) => patternId switch
    {
        "ripples" => new RipplesPainter(),
        "bubbles" => new BubblesPainter(),
        "orbits" => new OrbitsPainter(),
        "starfield" => new StarfieldPainter(),
        "petals" => new PetalsPainter(),
        "constellation" => new ConstellationPainter(),
        "fractalTree" => new FractalTreePainter(),
        "spiralArms" => new SpiralArmsPainter(),
        "diagonalRain" => new DiagonalRainPainter(),
        "circuitry" => new CircuitryPainter(),
        _ => null,
    };
}
