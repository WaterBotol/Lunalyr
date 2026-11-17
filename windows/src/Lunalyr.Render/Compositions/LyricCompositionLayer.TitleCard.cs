// The title card's layout (the Mac's OpeningTitleCard.card, CoverBesideWords and TypeMetrics in OpeningTitle.swift,
// 11f1228, 297b2f2 and 79088c3, Lunalyr's own, ported): a cover of one size for every title, the title and its reading
// from the cover's top at the largest of a few sizes whose two lines fit, the artist and album with the last baseline on
// the cover's foot, and each line measured in its own face so the card keeps its shape in any typeface. Drawn by
// LyricCompositionLayer.OpeningTitle.cs.

using System.Numerics;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Motion;
using Vortice.DirectWrite;

namespace Lunalyr.Render.Compositions;

internal abstract partial class LyricCompositionLayer
{
    /// <summary>A line of the card, baked: its ink, glows and the room its face's evened-out line box adds above and below.</summary>
    private sealed record CardLine(Ink Ink, Ink[]? Glows, double PadTop, double PadBottom, double First, double Last, double InkTop)
    {
        public double Height => PadTop + Ink.Box.Y + PadBottom;
        public double FirstBaseline => PadTop + First;
        public double LastBaseline => PadTop + Last;
        /// <summary>Where the first line's ink starts, down from the line's top.</summary>
        public double Cap => FirstBaseline - InkTop;
    }

    private OpeningCard BuildCard(CardKey key)
    {
        var kit = Kit!;
        var compact = key.Compact;
        var floating = key.Floating;
        // The card's box on the desktop: low on the left, as wide as a title may run.
        // Where the user put it on the desktop, and how large (dragged on the Settings picture).
        var place = key.Placement;
        var box = OpeningTitle.Box(SurfaceSize.X, SurfaceSize.Y, BaseSize, Scale, place);
        // The lyric's base size on this screen; a compact card is smaller. The floating window's takes nearly its text
        // size and nearly the window's width, in its middle.
        var unit = floating ? BaseSize * 0.95 : (compact ? BaseSize * 0.8 : BaseSize) * OpeningTitle.CardScale(place.Scale);
        var type = key.Dark ? new Vector3(0.08f, 0.08f, 0.08f) : Vector3.One;
        var shadow = new ShadowSpec(0.45f, (float)(unit * 0.12 * 0.5), (float)(unit * 0.015), key.Dark ? Vector3.One : default);
        // The card glows in the album's own colour: a tight halo and a wide, soft bloom on the type, and one round the
        // cover, arriving and leaving with them (radii as the Mac's shadows', sigma half the radius).
        var glow = OpeningTitle.GlowStrength(key.Dark);
        var accent = new Vector3(key.Accent.X, key.Accent.Y, key.Accent.Z);
        ShadowSpec[] typeGlows =
        [
            new((float)(0.45 * glow * key.Accent.W), (float)(unit * 0.6 * 0.5), 0, accent),
            new((float)(0.7 * glow * key.Accent.W), (float)(unit * 0.18 * 0.5), 0, accent),
        ];

        // The title's size; the lines under it take shares of it. A long title is set smaller, but they are not. Each
        // line is sized in its own face so its letters stand as tall as the card's own face would set them.
        var nominal = unit * (compact ? 0.72 : 0.92);
        var titleMetrics = FaceMetrics(kit, key.Title, Face.Heavy);
        var artistText = key.Artist.Trim();
        var showsReading = key.Reading is not null && !compact;
        var titleSize = nominal * OpeningTitle.LengthScale(key.Title) * titleMetrics.Scale;
        var step = new OpeningTitle.Spacing(nominal);
        var width = floating ? BoxSize.X * 0.9 : box.Width;
        var centred = floating && !key.Cover;
        var side = key.Cover ? nominal * OpeningTitle.CoverShare(compact) : 0;
        var gap = step.Group;
        var column = key.Cover ? Math.Max(width * 0.4, width - side - gap) : width;

        CardLine? Detail(string text, double share, double tracking, float alpha)
        {
            var metrics = FaceMetrics(kit, text, Face.Quiet);
            var size = nominal * share * metrics.Scale;
            return BakeCardLine(kit, text, size, column, tracking, new Vector4(type, alpha), shadow, typeGlows, metrics);
        }

        var reading = showsReading ? Detail(key.Reading!, OpeningTitle.ReadingShare, 0, 0.78f) : null;
        // In the artist's own case, as the player shows it: no tracked capitals.
        var artist = artistText.Length > 0 ? Detail(artistText, OpeningTitle.ArtistShare, 0, 0.92f) : null;
        var album = key.Album is { } albumText && !compact ? Detail(albumText, OpeningTitle.AlbumShare, 0.02, 0.7f) : null;

        // The details as one block: the artist, then the album tight under it.
        var albumTop = artist is null ? 0 : artist.Height + (album is null ? 0 : step.Line);
        CardBlock? details = artist is null && album is null ? null : new CardBlock(
            Math.Max(artist?.Ink.Box.X ?? 0, album?.Ink.Box.X ?? 0),
            album is null ? artist!.Height : albumTop + album.Height,
            artist?.FirstBaseline ?? albumTop + album!.FirstBaseline,
            album is null ? artist!.LastBaseline : albumTop + album.LastBaseline,
            artist?.Cap ?? albumTop + album!.Cap);

        // The title at each of its sizes (and the last on one line, narrowed to fit), with its reading under it.
        var titleRtl = ScriptDirection.IsRightToLeft(key.Title);
        var widestWord = WidestWord(kit, key.Title);
        var steps = OpeningTitle.TitleSteps;
        var layouts = new IDWriteTextLayout[steps.Length + 1];
        var blocks = new CardBlock[steps.Length + 1];
        var pads = new (double Top, double Bottom)[steps.Length + 1];
        try
        {
            for (var i = 0; i <= steps.Length; i++)
            {
                var last = i == steps.Length;
                var size = titleSize * steps[last ? steps.Length - 1 : i];
                var layout = layouts[i] = TitleLayout(kit, key.Title, size, column, titleRtl, centred, last, titleMetrics);
                var (first, lastBaseline, lines) = Baselines(layout);
                var fits = !last && lines <= 2 && widestWord * size <= column + 0.5;
                var fontSize = FontSize(layout);
                var padTop = titleMetrics.PadTop * fontSize;
                var padBottom = titleMetrics.PadBottom * fontSize;
                pads[i] = (padTop, padBottom);
                var metrics = layout.Metrics;
                var blockHeight = padTop + metrics.Height + padBottom;
                var blockLast = padTop + lastBaseline;
                var blockWidth = (double)metrics.Width;
                if (reading is not null)
                {
                    blockLast = blockHeight + step.Reading + reading.LastBaseline;
                    blockHeight += step.Reading + reading.Height;
                    blockWidth = Math.Max(blockWidth, reading.Ink.Box.X);
                }
                blocks[i] = new CardBlock(blockWidth, blockHeight, padTop + first, blockLast, padTop + first - titleMetrics.InkTop * fontSize, fits);
            }
            var grid = key.Cover
                ? OpeningTitle.BesideCover(side, gap, column, side * OpeningTitle.BlockGap, blocks, details)
                : OpeningTitle.Stacked(blocks, details, artist is null ? step.Line : step.Group);
            var chosen = layouts[grid.Title];
            var titleGlows = GlowInks(kit, chosen, typeGlows);
            var titleInk = kit.Bake(chosen, new Vector4(type, 1), 1, shadow);
            var titlePad = pads[grid.Title];

            var height = grid.Height;
            var contentWidth = grid.Width - (key.Cover ? side + gap : 0);
            double left, top;
            if (floating)
            {
                // Centred in the window: the block (with its cover) across, the card up and down.
                left = (SurfaceSize.X - grid.Width) / 2;
                top = (SurfaceSize.Y - height) / 2;
            }
            else
            {
                // Its foot on the box's.
                left = box.X;
                top = box.Y + box.Height - height;
            }

            Ink? coverShadow = null, coverGlow = null;
            if (key.Cover)
            {
                var radius = (float)(nominal * 0.1);
                coverShadow = kit.BakeTile(new Vector2((float)side, (float)side), radius, Vector4.One, 1,
                                           new ShadowSpec(0.45f, (float)(nominal * 0.12 * 0.5), (float)(nominal * 0.04)));
                // Only its glow is drawn: the tile itself is clear.
                coverGlow = kit.BakeTile(new Vector2((float)side, (float)side), radius, Vector4.Zero, 0.5f,
                                         new ShadowSpec((float)(0.55 * glow * key.Accent.W), (float)(nominal * 0.32 * 0.5), 0, accent));
            }
            var readingTop = grid.TitleTop + titlePad.Top + titleInk.Box.Y + titlePad.Bottom + step.Reading;
            return new OpeningCard(key)
            {
                Unit = unit,
                Title = titleInk,
                TitleGlows = titleGlows,
                Reading = reading?.Ink,
                ReadingGlows = reading?.Glows,
                Artist = artist?.Ink,
                ArtistGlows = artist?.Glows,
                Album = album?.Ink,
                AlbumGlows = album?.Glows,
                CoverShadow = coverShadow,
                CoverGlow = coverGlow,
                Cover = key.Cover,
                Side = side,
                CoverRadius = nominal * 0.1,
                Left = (float)left,
                Top = (float)top,
                ColumnLeft = key.Cover ? (float)(side + gap) : 0,
                Centred = centred,
                ContentWidth = (float)contentWidth,
                CoverTop = grid.CoverTop,
                TitleTop = grid.TitleTop + titlePad.Top,
                ReadingTop = reading is null ? 0 : readingTop + reading.PadTop,
                ArtistTop = artist is null ? 0 : grid.DetailsTop + artist.PadTop,
                AlbumTop = album is null ? 0 : grid.DetailsTop + albumTop + album.PadTop,
                // Legible over a bright or busy picture: the corner the box is in darkens softly.
                ShadeCentre = new Vector2((float)(box.X + box.Width * 0.3), (float)(box.Y + box.Height * 0.6)),
            };
        }
        finally
        {
            foreach (var layout in layouts) layout?.Dispose();
        }
    }

    /// <summary>
    /// The title at <paramref name="size"/> in the lyric typeface, bold, wrapped to the column (its lines spaced as its
    /// face's evened-out line box has them), or (<paramref name="oneLine"/>) on one line, narrowed down to 60% to fit and
    /// cut short after that. Set flush with the card's left edge whatever the script, or centred.
    /// </summary>
    private static IDWriteTextLayout TitleLayout(TextKit kit, string title, double size, double column, bool rightToLeft, bool centred,
                                                 bool oneLine, TitleTypeMetrics metrics)
    {
        var locale = TextKit.LocaleFor(title);
        if (oneLine)
        {
            var natural = kit.Layout(title, Face.Heavy, (float)size, locale, rightToLeft);
            var width = natural.Metrics.WidthIncludingTrailingWhitespace;
            if (!(width > column)) return Aligned(natural);
            natural.Dispose();
            var narrowed = kit.Layout(title, Face.Heavy, (float)(size * Math.Max(0.6, column / width)), locale, rightToLeft);
            kit.Truncate(narrowed, (float)column);
            return Aligned(narrowed);
        }
        var layout = kit.Layout(title, Face.Heavy, (float)size, locale, rightToLeft, (float)column);
        var extra = metrics.LineSpacing(FontSize(layout));
        if (extra > 0.5 && layout.Metrics.LineCount > 1)
        {
            var lines = new LineMetrics[layout.Metrics.LineCount];
            layout.GetLineMetrics(lines, out _);
            layout.SetLineSpacing(LineSpacingMethod.Uniform, (float)(lines[0].Height + extra), lines[0].Baseline);
            layout.MaxHeight = MathF.Ceiling(layout.Metrics.Height + 1);
        }
        return Aligned(layout);

        IDWriteTextLayout Aligned(IDWriteTextLayout set)
        {
            set.TextAlignment = centred ? TextAlignment.Center : rightToLeft ? TextAlignment.Trailing : TextAlignment.Leading;
            return set;
        }
    }

    /// <summary>
    /// One line (reading, artist, album) with <paramref name="tracking"/> (a share of its size) after each letter, set
    /// smaller (down to 60%) rather than past the column, with its glows and its face's evened-out line box.
    /// </summary>
    private static CardLine BakeCardLine(TextKit kit, string text, double size, double columnWidth, double tracking, Vector4 colour,
                                         ShadowSpec shadow, ReadOnlySpan<ShadowSpec> glows, TitleTypeMetrics metrics)
    {
        var locale = TextKit.LocaleFor(text);
        var rightToLeft = ScriptDirection.IsRightToLeft(text);
        var scale = 1.0;
        for (var attempt = 0; ; attempt++)
        {
            var layout = kit.Layout(text, Face.Quiet, (float)(size * scale), locale, rightToLeft);
            try
            {
                if (tracking > 0)
                {
                    using var spaced = layout.QueryInterface<IDWriteTextLayout1>();
                    spaced.SetCharacterSpacing(0, (float)(tracking * size * scale), 0, new TextRange(0, (uint)text.Length));
                    layout.MaxWidth = MathF.Ceiling(layout.Metrics.WidthIncludingTrailingWhitespace + 1);
                }
                var width = layout.Metrics.WidthIncludingTrailingWhitespace;
                if (width > columnWidth + 1 && attempt == 0)
                {
                    scale = Math.Max(0.6, columnWidth / width);
                    continue;
                }
                if (width > columnWidth + 1) kit.Truncate(layout, (float)columnWidth);
                var set = size * scale;
                var (first, last, _) = Baselines(layout);
                var glowInks = GlowInks(kit, layout, glows);
                return new CardLine(kit.Bake(layout, colour, 1, shadow), glowInks, metrics.PadTop * set, metrics.PadBottom * set,
                                    first, last, metrics.InkTop * set);
            }
            finally
            {
                layout.Dispose();
            }
        }
    }

    /// <summary>The size <paramref name="layout"/>'s text is set at.</summary>
    private static double FontSize(IDWriteTextLayout layout)
    {
        layout.GetFontSize(0, out var size, out _);
        return size > 0 ? size : 1;
    }

    /// <summary>The first and last lines' baselines, down from the layout's top, and how many lines it has.</summary>
    private static (double First, double Last, int Lines) Baselines(IDWriteTextLayout layout)
    {
        var count = (int)Math.Max(1, layout.Metrics.LineCount);
        var lines = new LineMetrics[count];
        layout.GetLineMetrics(lines, out var actual);
        if (actual == 0) return (layout.Metrics.Height * 0.8, layout.Metrics.Height * 0.8, 0);
        double above = 0;
        for (var i = 0; i < actual - 1; i++) above += lines[i].Height;
        return (lines[0].Baseline, above + lines[actual - 1].Baseline, (int)actual);
    }

    /// <summary>The widest of <paramref name="title"/>'s words in the title face, per point of size.</summary>
    private static double WidestWord(TextKit kit, string title)
    {
        double widest = 0;
        foreach (var word in title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            using var layout = kit.Layout(word, Face.Heavy, 100, TextKit.LocaleFor(word), ScriptDirection.IsRightToLeft(word));
            widest = Math.Max(widest, layout.Metrics.WidthIncludingTrailingWhitespace / 100.0);
        }
        return widest;
    }

    /// <summary>
    /// How <paramref name="text"/> reads in <paramref name="face"/> (the Mac's <c>TypeMetrics</c>): measured by
    /// DirectWrite through the kit's own layouts, so a fallback face (kana in a Latin family) is measured as drawn.
    /// </summary>
    private static TitleTypeMetrics FaceMetrics(TextKit kit, string text, Face face)
    {
        if (string.IsNullOrWhiteSpace(text)) return TitleTypeMetrics.Reference;
        var h = Probe(kit, "H", face);
        var x = Probe(kit, "x", face);
        if (h is not { } capital) return TitleTypeMetrics.Reference;
        return TitleTypeMetrics.Measure(text, capital.Baseline, capital.Height - capital.Baseline,
                                        capital.Ink ?? TitleTypeMetrics.ReferenceCap, x?.Ink ?? TitleTypeMetrics.ReferenceLower,
                                        s => Probe(kit, s, face)?.Ink, s => s.Length == 0 ? 0 : Probe(kit, s, face)?.Width ?? 0);
    }

    /// <summary>
    /// <paramref name="text"/> laid out at 100 in <paramref name="face"/>, per point: how far its ink rises above the
    /// first baseline (null for none), the baseline's depth, the line's height and its advance.
    /// </summary>
    private static (double? Ink, double Baseline, double Height, double Width)? Probe(TextKit kit, string text, Face face)
    {
        const float size = 100;
        using var layout = kit.Layout(text, face, size, TextKit.LocaleFor(text), ScriptDirection.IsRightToLeft(text));
        var lines = new LineMetrics[Math.Max(1, (int)layout.Metrics.LineCount)];
        layout.GetLineMetrics(lines, out var count);
        if (count == 0) return null;
        var size1 = FontSize(layout);
        var baseline = lines[0].Baseline;
        // The ink's top is the overhang above the layout's top (negative when it lies inside).
        var ink = baseline + layout.OverhangMetrics.Top;
        return (ink > 0 ? ink / size1 : null, baseline / size1, lines[0].Height / size1,
                layout.Metrics.WidthIncludingTrailingWhitespace / size1);
    }
}
