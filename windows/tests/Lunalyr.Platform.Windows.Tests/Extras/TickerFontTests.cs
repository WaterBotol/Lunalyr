using Lunalyr.Core.Extras;
using Lunalyr.Platform.Extras;

namespace Lunalyr.Platform.Windows.Tests.Extras;

/// <summary>The tray lyric strip's measurements with the real font (DirectWrite).</summary>
public class TickerFontTests
{
    // Ported from MenuBarTickerTests.swift: testWideScriptsAreMeasuredNotCounted
    [Fact]
    public void WideScriptsAreMeasuredNotCounted()
    {
        using var font = new TickerFont();
        var latin = font.Measure("abcdefghij");
        var japanese = font.Measure("あいうえおかきくけこ");
        Assert.True(japanese > latin * 1.4, $"ten kana are far wider than ten letters ({japanese} vs {latin})");
        Assert.True(TrayLyricTicker.MaxWidth(36, font.Measure) > TrayLyricTicker.MaxWidth(16, font.Measure));
    }
}
