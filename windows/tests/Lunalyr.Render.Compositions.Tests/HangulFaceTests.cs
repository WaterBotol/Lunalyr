using Lunalyr.Tools.CompositionPreview;

namespace Lunalyr.Render.Compositions.Tests;

/// <summary>
/// One face for Hangul (the Mac's HangulWordTests.swift: testEditorialNeverSetsHangulInAFaceWithoutHangul and
/// testCharterHasNoHangulOfItsOwn, dd7bc1e and 000abbf): Editorial's contrasting face (Georgia on Windows) and the
/// randomised faces have no Hangul, so a Korean word set in them would fall back on its own; it keeps the main face. Kana,
/// Han and Latin keep the contrast.
/// </summary>
[Collection(GpuCollection.Name)]
public sealed class HangulFaceTests : IDisposable
{
    private readonly Offscreen _stage = new(64, 64, warp: true);
    private readonly TextKit _kit;

    public HangulFaceTests()
    {
        _kit = new TextKit(_stage.Dc, _stage.DWrite);
        _kit.SetFamily("");
    }

    public void Dispose()
    {
        _kit.Dispose();
        _stage.Dispose();
    }

    [Fact]
    public void Editorial_never_sets_Hangul_in_a_face_without_Hangul()
    {
        Assert.Equal((Face.Regular, -1), _kit.KeepHangulFace(Face.Contrast, -1, "우리의"));
        Assert.Equal((Face.Contrast, -1), _kit.KeepHangulFace(Face.Contrast, -1, "dream, oh-oh"));
        Assert.Equal((Face.Contrast, -1), _kit.KeepHangulFace(Face.Contrast, -1, "夕焼けの"));
        Assert.Equal((Face.Contrast, -1), _kit.KeepHangulFace(Face.Contrast, -1, "我们一起"));
        // The main face is never passed over.
        Assert.Equal((Face.Heavy, -1), _kit.KeepHangulFace(Face.Heavy, -1, "우리의"));
    }

    [Fact]
    public void Randomised_faces_without_Hangul_give_way_to_the_main_face()
    {
        for (var mix = 0; mix < TextKit.MixFaces; mix++)
        {
            Assert.Equal((Face.Heavy, -1), _kit.KeepHangulFace(Face.Heavy, mix, "세상이"));
            Assert.Equal((Face.Heavy, mix), _kit.KeepHangulFace(Face.Heavy, mix, "dream"));
            // A randomised face passed over goes to the main face, not the contrasting one (as on the Mac).
            Assert.Equal((Face.Regular, -1), _kit.KeepHangulFace(Face.Contrast, mix, "세상이"));
        }
    }

    [Fact]
    public void A_reading_takes_its_words_face()
    {
        // The reading is Latin, but its word is Korean: the reading is set in the main face's family too.
        using var contrast = _kit.Layout("biroso", Face.Contrast, 30, "en-us", false, faceText: "dream");
        using var kept = _kit.Layout("biroso", Face.Contrast, 30, "en-us", false, faceText: "비로소");
        Assert.NotEqual(Family(contrast), Family(kept));
        Assert.Equal(Family(_kit.Layout("biroso", Face.Regular, 30, "en-us", false)), Family(kept));
    }

    private static string Family(Vortice.DirectWrite.IDWriteTextLayout layout) => layout.FontFamilyName;
}
