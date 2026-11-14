using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Shell.Cards;
using Lunalyr.Shell.LyricTools;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Updates;

namespace Lunalyr.Shell.Tests;

/// <summary>The Settings window's visual sweep (14 Nov 2026, docs/sweep/settings.md): what a test can hold.</summary>
public sealed class SettingsSweepTests
{
    // ---- The window's opening size ----

    [Theory]
    [InlineData(1.0, 1920, 1032)]
    [InlineData(1.25, 2560, 1392)]
    [InlineData(1.5, 2560, 1392)]
    [InlineData(2.0, 3200, 1952)]
    public void TheWindowOpensAtTheSameSizeInDipsAtEveryScaleThatFitsIt(double scale, int workWidth, int workHeight)
    {
        var (width, height) = SettingsWindowSize.Pixels(scale, workWidth, workHeight);
        Assert.Equal(SettingsWindowSize.Width, width / scale, 0);
        Assert.Equal(SettingsWindowSize.Height, height / scale, 0);
    }

    [Fact]
    public void ItKeepsTheOwnersSizeAt125Percent()
    {
        // 1400 × 1040 physical pixels was the fixed size before; at 125 % it is unchanged.
        Assert.Equal((1400, 1040), SettingsWindowSize.Pixels(1.25, 2560, 1392));
    }

    [Theory]
    [InlineData(1.5, 1920, 1032)] // a 1080p laptop at 150 %: 1280 × 688 DIPs of work area
    [InlineData(2.0, 2560, 1392)] // a 1440p display at 200 %
    [InlineData(1.0, 1024, 728)]
    public void ASmallerDisplayGetsItsWorkAreaLessTheMargin(double scale, int workWidth, int workHeight)
    {
        var (width, height) = SettingsWindowSize.Pixels(scale, workWidth, workHeight);
        Assert.True(width <= workWidth - 2 * SettingsWindowSize.Margin * scale + 1, $"{width} wide");
        Assert.True(height <= workHeight - 2 * SettingsWindowSize.Margin * scale + 1, $"{height} tall");
        // Never the old 200 % squeeze: the pages keep room beside the 240-DIP pane.
        Assert.True(width / scale >= 640, $"{width / scale} DIPs wide");
    }

    [Fact]
    public void ANonsenseScaleCountsAsOneHundredPercent()
    {
        Assert.Equal(SettingsWindowSize.Pixels(1, 1920, 1032), SettingsWindowSize.Pixels(double.NaN, 1920, 1032));
        Assert.Equal(SettingsWindowSize.Pixels(1, 1920, 1032), SettingsWindowSize.Pixels(0, 1920, 1032));
    }

    // ---- Wording ----

    public static TheoryData<string> Buttons() =>
    [
        UpdateCopy.Button(new UpdateState(UpdatePhase.Idle, "")),
        UpdateCopy.Button(new UpdateState(UpdatePhase.Available, "")),
        UpdateCopy.Button(new UpdateState(UpdatePhase.Ready, "")),
        LyricCardCopy.OpenTitle,
        LyricTapSyncController.MenuTitle,
        LyricTapSyncController.StopTitle,
        LyricToolsText.UsePlayingSong,
        LyricToolsText.UseForThisSong,
        LyricToolsText.ReturnToAutomatic,
        LyricToolsText.UndoTapToSync,
    ];

    [Theory]
    [MemberData(nameof(Buttons))]
    public void ButtonsAreInSentenceCase(string text)
    {
        string[] names = ["Lunalyr", "YouTube", "Discord", "Windows", "Spotify", "Apple", "Music", "LRC", "TTML"];
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words.Skip(1))
        {
            var bare = word.TrimEnd('…', '.', ',');
            Assert.True(bare.Length == 0 || !char.IsUpper(bare[0]) || names.Contains(bare), $"\"{text}\": {word}");
        }
    }

    [Fact]
    public void CaptionsCarryNoLinksOrKeys()
    {
        // The Hide lyrics caption used to end "A lunalyr://toggle link does the same." (DESIGN.md: no code breadcrumbs).
        Assert.DoesNotContain("://", LyricsOnScreenCopy.HideLyricsDescription, StringComparison.Ordinal);
        // Windows' words for the Mac's Reduce Motion.
        Assert.DoesNotContain("Reduce Motion", LyricStyleCopy.ColourBacking, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLyricCardRowsCaptionIsOneLineThatDoesntRepeatItsHelp()
    {
        // The row shows RowCaption; RowMore is its tooltip. Both start "Pick", so showing both read twice.
        Assert.DoesNotContain(LyricCardCopy.RowMore, LyricCardCopy.RowCaption, StringComparison.Ordinal);
        Assert.Single(LyricCardCopy.RowCaption.Split(". ", StringSplitOptions.RemoveEmptyEntries));
    }
}
