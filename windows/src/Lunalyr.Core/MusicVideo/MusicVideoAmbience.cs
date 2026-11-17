// Ported from the Mac's MusicVideoAmbience.swift (7ef2908, 82f7a84; Lunalyr's own code): the ambient glow around a video
// shown whole. About once a second a small copy of the playing picture is taken, blurred heavily and cross-faded in
// behind the video, stretched to fill the screen. The glow takes the scene's colours without a second decode; it lags a cut
// a little, which reads as light spilling rather than flicker. The Mac's Core Image filters (CIColorControls,
// CIGaussianBlur on a clamped extent) become a small CPU pass here, as MusicVideoBackdrop's blur does, so it is testable
// without a GPU. The sampling and the drawing are the renderer's and the YouTube host's.

using Lunalyr.Core.Colour;

namespace Lunalyr.Core.MusicVideo;

/// <summary>The ambient glow's rules and its picture (the Mac's MusicVideoAmbience).</summary>
public static class MusicVideoAmbience
{
    /// <summary>How often the picture is sampled, in seconds.</summary>
    public const double Interval = 1;

    /// <summary>Low power (Battery saver): half as often.</summary>
    public const double LowPowerInterval = 2;

    /// <summary>The cross-fade from one sample to the next, in seconds.</summary>
    public const double Fade = 1.2;

    /// <summary>Reduce Motion: a slower, gentler cross-fade.</summary>
    public const double ReducedFade = 2.4;

    /// <summary>The width a sample is taken and blurred at.</summary>
    public const int SampleWidth = 160;

    /// <summary>The blur's radius at that width: heavy, so only colour and light are left.</summary>
    public const double BlurRadius = 12;

    /// <summary>The glow's own darkening, under Settings' dimming.</summary>
    public const double Darkening = 0.3;

    /// <summary>A little more colour, so the glow does not look grey once blurred.</summary>
    public const double Saturation = 1.25;

    /// <summary>How long a cross-fade takes.</summary>
    public static double FadeFor(bool reduceMotion) => reduceMotion ? ReducedFade : Fade;

    /// <summary>How often the picture is sampled.</summary>
    public static double IntervalFor(bool lowPower) => lowPower ? LowPowerInterval : Interval;

    /// <summary>
    /// The glow drawn from <paramref name="image"/> (premultiplied BGRA): reduced to at most <see cref="SampleWidth"/> wide,
    /// a little richer in colour, blurred with its edges kept. Null for an empty picture.
    /// </summary>
    public static CoverArt? Glow(CoverArt image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0) return null;
        var scale = Math.Min(1.0, (double)SampleWidth / image.Width);
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        var small = MusicVideoBackdrop.Reduce(image, width, height);
        Saturate(small, Saturation);
        var kernel = MusicVideoBackdrop.Kernel(BlurRadius);
        var pass = new float[small.Length];
        MusicVideoBackdrop.Blur(small, pass, width, height, kernel, horizontal: true);
        MusicVideoBackdrop.Blur(pass, small, width, height, kernel, horizontal: false);
        var pixels = new byte[small.Length];
        for (var i = 0; i < small.Length; i++) pixels[i] = (byte)Math.Clamp((int)Math.Round(small[i]), 0, 255);
        return new CoverArt(width, height, pixels);
    }

    /// <summary>
    /// The picture is (near) black all over, a blank page or a player not yet drawing (the Mac's
    /// MusicVideoFrozenFrame.looksBlank): reduced to 8 × 8, no pixel has a channel over <paramref name="threshold"/>.
    /// </summary>
    public static bool LooksBlank(CoverArt image, byte threshold = 6)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0) return true;
        const int side = 8;
        var small = MusicVideoBackdrop.Reduce(image, Math.Min(side, image.Width), Math.Min(side, image.Height));
        for (var i = 0; i < small.Length; i += 4)
            if (Math.Max(small[i], Math.Max(small[i + 1], small[i + 2])) > threshold) return false;
        return true;
    }

    /// <summary>Core Image's colour controls' saturation: each pixel pushed away from its luma (Rec. 709 weights).</summary>
    private static void Saturate(float[] pixels, double amount)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            float b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
            var luma = 0.0721 * b + 0.7154 * g + 0.2125 * r;
            pixels[i] = (float)Math.Clamp(luma + (b - luma) * amount, 0, a);
            pixels[i + 1] = (float)Math.Clamp(luma + (g - luma) * amount, 0, a);
            pixels[i + 2] = (float)Math.Clamp(luma + (r - luma) * amount, 0, a);
        }
    }
}
