// What the tray lyric shows and when it next wakes, ported from the Mac's MenuBarLyricTicker.refresh
// (MenuBarLyricTicker.swift, Lunalyr's own lines) without AppKit: the strip that draws it is the app's.

using Lunalyr.Core.Extras;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Shell.Extras;

/// <summary>What the strip shows: the text, the pause symbol beside a paused wallpaper's note, dimmed while the music is paused.</summary>
public sealed record TickerShown(string Text, bool PauseSymbol, bool Dimmed);

/// <summary>One refresh's answer: what to show (null: nothing), its width in points, and when to look again.</summary>
public readonly record struct TickerStep(TickerShown? Shown, double Width, bool Changed, bool TextChanged, double? Wait);

/// <summary>
/// The tray lyric's state between refreshes: the song's measured layout (its width is set once per song, from its
/// widest line), what is shown, and the short wait before it leaves when nothing plays. Not thread-safe: the strip's
/// thread owns it.
/// </summary>
public sealed class TrayTickerModel(Func<string, double> measure)
{
    /// <summary>The symbol's room beside the paused note, in points.</summary>
    public const double SymbolWidth = 20;

    private TrayLyricTicker.Layout? _layout;
    private (object? Lines, object? Readings, double MaxWidth) _layoutKey;
    private TickerShown? _shown;
    private double _width;
    private double? _hideAt;

    public TickerShown? Shown => _shown;

    public double Width => _width;

    /// <summary>
    /// Reads what to show at <paramref name="clock"/> (monotonic seconds). <paramref name="force"/> re-measures the song
    /// and reports the line as changed even when it isn't.
    /// </summary>
    public TickerStep Refresh(ExtrasNow now, double length, bool screensAsleep, double clock, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(now);
        if (force) _layout = null;
        var display = TrayLyricTicker.DisplayFor(now.WallpaperPaused, now.HasTrack, lyricsVisible: !now.LyricsHidden);
        var maxWidth = TrayLyricTicker.MaxWidth((int)length, measure);
        TickerShown next;
        double width;
        double? eventIn = null;
        switch (display)
        {
            case TrayLyricTicker.Display.None:
                // Between songs: hold a quiet ♪ for a moment before leaving.
                if (_shown is null) return Hidden();
                _hideAt ??= clock + TrayLyricTicker.HideDelay;
                if (clock >= _hideAt)
                {
                    _hideAt = null;
                    _shown = null;
                    return new TickerStep(null, _width, Changed: true, TextChanged: true, Wait: null);
                }
                next = new TickerShown("♪", false, Dimmed: true);
                width = _width > 0 ? _width : measure("♪") + TrayLyricTicker.WidthPadding;
                return Commit(next, width, force, _hideAt.Value - clock);
            case TrayLyricTicker.Display.Note note:
                var symbol = note.Text == TrayLyricTicker.PausedText;
                next = new TickerShown(note.Text, symbol, Dimmed: false);
                width = Math.Min(maxWidth, measure(note.Text) + (symbol ? SymbolWidth : 0)) + TrayLyricTicker.WidthPadding;
                break;
            default:
                var lines = now.Lines ?? [];
                var layout = LayoutFor(now, lines, maxWidth);
                var time = now.LyricTime;
                var index = lines.Count > 0 && double.IsFinite(time) ? new LyricLineIndex(lines).LineAt(time) : null;
                var state = TrayLyricTicker.LineStateAt(lines, index, layout.PageSwitches, double.IsFinite(time) ? time : 0);
                if (state.NextEvent is { } at && double.IsFinite(time)) eventIn = at - time;
                string? text = null;
                if (state.Line is { } l && l < layout.Pages.Count && layout.Pages[l].Count > 0)
                    text = layout.Pages[l][Math.Min(state.Page, layout.Pages[l].Count - 1)];
                // The music paused: the line stays, quieter, so it reads as stopped.
                next = new TickerShown(text ?? "♪", false, Dimmed: !now.Playing);
                // While the new song's lyrics are being found, keep the width: it changes once, when they arrive.
                var waiting = lines.Count == 0 && now.LyricsLoading && _width > 0;
                width = waiting ? _width : layout.Width;
                break;
        }

        _hideAt = null;
        var interval = TrayLyricTicker.TickInterval(now.HasTrack, now.Playing, hasTimedLines: now.Lines is { Count: > 0 },
            lyricsVisible: !now.LyricsHidden, paused: now.WallpaperPaused, screensAsleep: screensAsleep);
        double? wait = interval is { } ceiling ? (now.Playing ? TrayLyricTicker.Wait(eventIn, ceiling) : ceiling) : null;
        return Commit(next, width, force, wait);
    }

    /// <summary>Forgets what is shown (the strip was switched off).</summary>
    public void Reset()
    {
        _shown = null;
        _layout = null;
        _width = 0;
        _hideAt = null;
    }

    private TickerStep Hidden()
    {
        _hideAt = null;
        return new TickerStep(null, _width, Changed: false, TextChanged: false, Wait: null);
    }

    private TickerStep Commit(TickerShown next, double width, bool force, double? wait)
    {
        if (Math.Abs(_width - width) > 0.5) _width = width;
        var changed = force || next != _shown;
        var textChanged = next.Text != _shown?.Text || next.PauseSymbol != _shown?.PauseSymbol;
        _shown = next;
        return new TickerStep(next, _width, changed, textChanged, wait);
    }

    /// <summary>The song's lines measured once, again only when the lines, their readings or the length change.</summary>
    private TrayLyricTicker.Layout LayoutFor(ExtrasNow now, IReadOnlyList<LyricLine> lines, double maxWidth)
    {
        var key = ((object?)now.Lines, (object?)now.Replacements, maxWidth);
        if (_layout is not null && ReferenceEquals(key.Item1, _layoutKey.Lines) && ReferenceEquals(key.Item2, _layoutKey.Readings)
            && key.maxWidth == _layoutKey.MaxWidth) return _layout;
        _layoutKey = key;
        return _layout = TrayLyricTicker.LayoutFor(lines, Readings(now, lines), maxWidth, measure);
    }

    /// <summary>Each line's text as shown: its reading when readings replace the lyrics, with the words that time it.</summary>
    public static IReadOnlyList<TrayLyricTicker.LineText> Readings(ExtrasNow now, IReadOnlyList<LyricLine> lines)
    {
        ArgumentNullException.ThrowIfNull(now);
        var texts = new List<TrayLyricTicker.LineText>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            if (now.Replacements is { } readings && readings.TryGetValue(i, out var reading) && reading.Length > 0)
            {
                IReadOnlyList<LyricWord> words = now.ReplacementWords is { } byLine && byLine.TryGetValue(i, out var w) ? w : [];
                texts.Add(new TrayLyricTicker.LineText(reading, words));
            }
            else
            {
                texts.Add(new TrayLyricTicker.LineText(lines[i].Text, lines[i].Words));
            }
        }
        return texts;
    }
}
