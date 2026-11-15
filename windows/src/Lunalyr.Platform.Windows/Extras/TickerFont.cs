using Vortice.DirectWrite;

namespace Lunalyr.Platform.Extras;

/// <summary>
/// The tray lyric strip's font, measured with DirectWrite (the Mac measures its menu-bar font): Segoe UI Variable Text
/// at 13 points, falling back as DirectWrite does. Widths are in points (DIPs). One per thread: DirectWrite text formats
/// aren't shared across threads here.
/// </summary>
public sealed class TickerFont : IDisposable
{
    public const string Family = "Segoe UI Variable Text";
    public const float Size = 13;

    private readonly IDWriteFactory _factory;
    private readonly bool _ownsFactory;

    public TickerFont(IDWriteFactory? factory = null)
    {
        _ownsFactory = factory is null;
        _factory = factory ?? DWrite.DWriteCreateFactory<IDWriteFactory>(FactoryType.Shared);
        Format = _factory.CreateTextFormat(Family, null, FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, Size, "");
        Format.WordWrapping = WordWrapping.NoWrap;
    }

    public IDWriteTextFormat Format { get; }

    /// <summary>The text's width on one line, in points, rounded up (trailing spaces included).</summary>
    public double Measure(string text)
    {
        using var layout = _factory.CreateTextLayout(text ?? "", Format, 100_000, 100);
        return Math.Ceiling(layout.Metrics.WidthIncludingTrailingWhitespace);
    }

    public void Dispose()
    {
        Format.Dispose();
        if (_ownsFactory) _factory.Dispose();
    }
}
