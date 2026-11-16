using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Floating;

namespace Lunalyr.Core.Tests.Colour;

/// <summary>
/// Over a music video the lyrics stay light (the Mac's MusicVideoLyricToneTests.swift, c1c33d69 and a6f7a6d8): the
/// footage isn't measured, and guessing it light turned the words dark and muddy with a pale glow over a dimmed video
/// whenever Darkening was under about 133%. On Windows the video layers report this estimate for the lyrics' light or
/// dark switch, and the floating lyrics take it over a music video wallpaper.
/// </summary>
public class MusicVideoLyricToneTests
{
    private static readonly Vector3[] Palette = [new(0.8f, 0.6f, 0.3f), new(0.5f, 0.3f, 0.2f), new(0.9f, 0.8f, 0.6f)];

    private static IEnumerable<double> Steps(double from, double to, double by)
    {
        for (var value = from; value <= to + 1e-9; value += by) yield return value;
    }

    // Ported from MusicVideoLyricToneTests.swift: testUnmeasuredVideoNeverAsksForDarkText
    [Fact]
    public void Unmeasured_video_never_asks_for_dark_text()
    {
        foreach (var dim in Steps(0, 0.85, 0.05))
            foreach (var scrim in Steps(0, 2, 0.1))
            {
                var unbacked = LyricBackdrop.Luminance(WallpaperBackgroundMode.MusicVideo, false, Palette, scrim, null, 0, videoDim: dim);
                Assert.True(unbacked < LyricInkDirector.LightBelow, $"dim {dim}, darkening {scrim}: {unbacked} would turn the lyrics dark");
                // What the video layers report for the switch (before any backing).
                var reported = LyricBackdrop.BeforeBacking(WallpaperBackgroundMode.MusicVideo, false, [], scrim, null, false, dim);
                Assert.True(reported < LyricInkDirector.LightBelow, $"dim {dim}, darkening {scrim}: reported {reported}");
            }
    }

    // Ported from MusicVideoLyricToneTests.swift: testTheVideoDimmingDarkensTheBackdrop
    [Fact]
    public void The_video_dimming_darkens_the_backdrop()
    {
        var plain = LyricBackdrop.BeforeBacking(WallpaperBackgroundMode.MusicVideo, false, Palette, 0, 0.6, false, 0);
        var dimmed = LyricBackdrop.BeforeBacking(WallpaperBackgroundMode.MusicVideo, false, Palette, 0, 0.6, false, 0.63);
        Assert.True(dimmed < plain * 0.5);
    }

    // Ported from MusicVideoLyricToneTests.swift: testFloatingLyricsOverAMusicVideoStayLight
    [Fact]
    public void Floating_lyrics_over_a_music_video_stay_light()
    {
        foreach (var strength in Steps(0, 1, 0.05))
            foreach (var dim in Steps(0, 0.85, 0.05))
                foreach (var scrim in Steps(0, 2, 0.25))
                {
                    var behind = FloatingBackdrop.Behind(musicVideo: true, scrim, dim);
                    foreach (var reduceTransparency in new[] { false, true })
                    {
                        var backdrop = FloatingBackdrop.Luminance(strength, reduceTransparency, behind);
                        Assert.True(backdrop < LyricInkDirector.LightBelow,
                                    $"shade {strength}, dim {dim}, darkening {scrim}: {backdrop} would turn the floating lyrics dark");
                    }
                }
    }

    // Ported from MusicVideoLyricToneTests.swift: testFloatingLyricsOverAMusicVideoFollowTheDesktopRule
    [Fact]
    public void Floating_lyrics_over_a_music_video_follow_the_desktop_rule()
    {
        var behind = FloatingBackdrop.Behind(musicVideo: true, 0.4, 0.3);
        var desktop = LyricBackdrop.Luminance(WallpaperBackgroundMode.MusicVideo, false, Palette, 0.4, null, 0, videoDim: 0.3);
        Assert.Equal(desktop, behind, 4);
        Assert.Equal(behind, FloatingBackdrop.Luminance(0, false, behind), 4);
    }

    // Ported from MusicVideoLyricToneTests.swift: testFloatingLyricsElsewhereStillJudgeAnUnknownApp
    [Fact]
    public void Floating_lyrics_elsewhere_still_judge_an_unknown_app()
    {
        Assert.Equal(LyricLegibility.UnknownBackdropLuminance, FloatingBackdrop.Behind(musicVideo: false, 1, 0.5));
        Assert.Equal(FloatingBackdrop.Luminance(0.4, false), FloatingBackdrop.Luminance(0.4, false, FloatingBackdrop.Behind(false, 1, 0.5)));
    }
}
