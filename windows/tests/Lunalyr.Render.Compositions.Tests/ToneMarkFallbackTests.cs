using Lunalyr.Render.Compositions;
using Lunalyr.Tools.CompositionPreview;
using Vortice.DirectWrite;

namespace Lunalyr.Render.Compositions.Tests;

/// <summary>
/// Pinyin's tone marks in a typeface without them (tester feedback on 0.9.0): the whole word is set in a family that has
/// them, never just the marked letters in another font mid-word.
/// </summary>
[Collection(GpuCollection.Name)]
public sealed class ToneMarkFallbackTests : IDisposable
{
    private readonly Offscreen _stage = new(64, 64);

    public void Dispose() => _stage.Dispose();

    /// <summary>An installed family that lacks ǐ (U+01D0), or null.</summary>
    private string? FamilyWithoutToneMarks()
    {
        using var fonts = _stage.DWrite.GetSystemFontCollection(false);
        foreach (var name in new[] { "Impact", "Bahnschrift", "Ink Free", "Gabriola", "Segoe Print", "Comic Sans MS", "Courier New", "Georgia" })
        {
            if (!fonts.FindFamilyName(name, out var index)) continue;
            using var family = fonts.GetFontFamily(index);
            using var font = family.GetFirstMatchingFont(FontWeight.Normal, FontStretch.Normal, FontStyle.Normal);
            if (!font.HasCharacter(0x01D0)) return name;
        }
        return null;
    }

    [Fact]
    public void APinyinWordIsSetWholeInOneFamily()
    {
        if (FamilyWithoutToneMarks() is not { } lacking) return; // nothing on this PC to test against
        using var kit = new TextKit(_stage.Dc, _stage.DWrite);
        kit.SetFamily(lacking);
        using var layout = kit.Layout("nǐ hǎo", Face.Quiet, 24, "en-us", false);
        var first = layout.GetFontFamilyName(0, out _);
        var marked = layout.GetFontFamilyName(1, out _);
        Assert.NotEqual(lacking, first);
        Assert.Equal(first, marked);
        // Plain Latin keeps the chosen family.
        using var plain = kit.Layout("ni hao", Face.Quiet, 24, "en-us", false);
        Assert.Equal(lacking, plain.GetFontFamilyName(0, out _));
    }
}
