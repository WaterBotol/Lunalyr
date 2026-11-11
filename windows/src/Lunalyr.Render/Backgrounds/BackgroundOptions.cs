namespace Lunalyr.Render.Backgrounds;

/// <summary>
/// The wallpaper settings the album backgrounds read. Layers never read settings themselves (see
/// <see cref="Scene.IRenderLayer"/>), and <see cref="Scene.FrameContext"/> does not carry these, so the app hands them
/// to the layer factory; a change of setting makes a new factory and new layers. Defaults are the Mac's.
/// </summary>
public sealed record BackgroundOptions
{
    public static BackgroundOptions Default { get; } = new();

    /// <summary>Motion speed, 0.2...2 (the Mac's "Motion speed").</summary>
    public double Speed { get; init; } = 1;

    /// <summary>Colour richness, 0.4...1.8 (the Mac's "Colour richness"): saturation about each colour's own luma.</summary>
    public double Vividness { get; init; } = 1;

    /// <summary>Darkening, 0...2 (the Mac's "Darkening"): scales the scrim.</summary>
    public double ScrimStrength { get; init; } = 1;

    /// <summary>The system's reduce-motion preference: ambient motion at 15%, the album gradient still.</summary>
    public bool ReduceMotion { get; init; }

    /// <summary>The system's high-contrast preference: a stronger text backing for pastel lyrics.</summary>
    public bool IncreaseContrast { get; init; }

    /// <summary>A text backing amount set by hand (0...1), or null for the automatic amount.</summary>
    public double? ManualBacking { get; init; }

    /// <summary>Power saving: the colour field draws 60% of its usual pixels (the Mac's economical budget).</summary>
    public bool Economical { get; init; }

    /// <summary>Replaces the colour field's pixel budget; <see cref="double.PositiveInfinity"/> draws at native resolution.</summary>
    public double? PixelBudget { get; init; }

    /// <summary>A fine grain over the field, which also dithers its dark gradients on 8-bit displays.</summary>
    public bool Grain { get; init; } = true;
}
