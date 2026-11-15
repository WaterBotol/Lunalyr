using System.Reflection;
using System.Text.RegularExpressions;
using Lunalyr.Shell.Cards;
using Lunalyr.Shell.Extras;
using Lunalyr.Shell.Flyout;
using Lunalyr.Shell.Onboarding;

namespace Lunalyr.Shell.Tests;

/// <summary>
/// The visual sweep of the smaller surfaces (docs/sweep/surfaces.md): the tray flyout's, the welcome's and the lyric
/// card's words are in sentence case, and nothing names a feature Windows has dropped.
/// </summary>
public sealed partial class SurfacesCopyTests
{
    /// <summary>Two capitalised words in a row that aren't names (Lunalyr, Windows, Apple Music, YouTube…).</summary>
    [GeneratedRegex(@"\b(?!Custom Mix\b|Lunalyr\b|Windows\b|Apple\b|Music\b|YouTube\b|Spotify\b|SoundCloud\b|Discord\b|Settings\b|LRCLIB\b|Match\b|Chinese\b|Japanese\b|Korean\b|Start\b|Explorer\b|GIF\b|PNG\b|MP4\b)[A-Z][a-z]+ (?!Lunalyr\b|Windows\b|Music\b|YouTube\b|Settings\b|Spotify\b|Discord\b|LRCLIB\b)[A-Z][a-z]+")]
    private static partial Regex TitleCase();

    public static TheoryData<string, string> Constants()
    {
        var data = new TheoryData<string, string>();
        foreach (var type in new[] { typeof(FlyoutText), typeof(OnboardingCopy), typeof(LyricCardCopy) })
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.IsLiteral && field.GetRawConstantValue() is string text) data.Add($"{type.Name}.{field.Name}", text);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Constants))]
    public void WordsAreInSentenceCase(string name, string text)
    {
        Assert.False(TitleCase().IsMatch(text), $"{name} reads \"{text}\": sentence case everywhere.");
    }

    [Fact]
    public void TheWindowTitlesAndRowsAreInSentenceCase()
    {
        Assert.Equal("Lyric card", LyricCardCopy.WindowTitle);
        Assert.Equal("Open lyric card…", LyricCardCopy.OpenTitle);
        Assert.Equal("Choose another image or video…", OnboardingCopy.ChooseAnother);
        Assert.Equal(("Pause wallpaper", "Resume wallpaper", "Wallpaper options…"),
            (FlyoutText.PauseWallpaper, FlyoutText.ResumeWallpaper, FlyoutText.WallpaperOptions));
    }

    [Fact]
    public void TheWelcomesPlayerLineDoesNotPromiseAQueue()
    {
        // The Mac's card shows what plays next and a search; Windows' card has neither (removed 14 Nov 2026).
        Assert.DoesNotContain("next", OnboardingCopy.Player, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("search", OnboardingCopy.Player, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoNoteSaysAFeatureIsMissingFromTheBuild()
    {
        Assert.DoesNotContain("build", LunalyrExtras.LyricCardMissing, StringComparison.OrdinalIgnoreCase);
    }
}
