// The song's opening title on the wallpaper (the Mac's OpeningTitleCard in OpeningTitle.swift, Lunalyr's own, ported;
// the one Verso-attributed line of that file, its Settings.shared property, has no counterpart): the title in the lyric
// typeface, the artist in its own case, the album, the title's reading and the album cover, low on the left with a
// soft corner shade, the type and the cover glowing in the album's colour. Its timing is
// Lunalyr.Core.Compositions.OpeningTitle's.

using System.Numerics;
using System.Runtime.InteropServices;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Motion;
using Lunalyr.Render.Scene;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Lunalyr.Render.Compositions;

internal abstract partial class LyricCompositionLayer
{
    private OpeningCard? _card;
    private CoverArt? _coverSource;
    private ID2D1Bitmap1? _coverBitmap;
    private ID2D1BitmapBrush1? _coverBrush;
    private ID2D1GradientStopCollection? _shadeStops;
    private ID2D1RadialGradientBrush? _shade;
    private bool _shadeDark;
    private LyricSheet? _firstSungSheet;
    private double? _firstSung;
    private (string? Album, string? Title, string? Line) _albumOf;

    /// <summary>
    /// The scene's opening title (null when it has none: the setting is off, nothing is playing, or there is no room
    /// before the first line), and the credits held back until it has gone. Called once a frame, after the scene is
    /// taken in and before anything is drawn.
    /// </summary>
    private OpeningSchedule? PrepareOpening(in FrameContext frame)
    {
        var own = OpeningScheduleFor(frame.Scene, frame.SongJoined);
        OpeningSchedule? schedule;
        if (own is { Kept: true })
        {
            // Kept for the whole song: the same card whatever the lookup does, so nothing is carried across it.
            _loadingCard = null;
            _handedCard = null;
            _staying = null;
            _yielding = null;
            _before = null;
            schedule = own;
        }
        else
        {
            schedule = Staying(Handed(own, frame.Scene.Title, frame.SongSeconds), frame.Scene.Title, frame.SongSeconds);
        }
        // Credits wait for the opening title of synced lyrics to leave (the Mac's notBefore); for a kept card, only as long
        // as for the ordinary one.
        CreditsNotBefore(Sheet is { Count: > 0 } && schedule is { } s ? s.Clears : 0);
        _openingSchedule = schedule;
        return schedule;
    }

    /// <summary>This frame's opening title, for the interlude dots in the floating window, which wait for it.</summary>
    private OpeningSchedule? _openingSchedule;

    /// <summary>
    /// The floating window's stage (its scene carries the floating text size): the opening title takes the middle,
    /// compact and without a shade of its own (the Mac's <c>OpeningTitleCard.floating</c>).
    /// </summary>
    private bool FloatingStage => SceneShown?.Style.FloatingTextSize is not null;

    /// <summary>
    /// How much the interlude dots show: in the floating window the opening title takes the middle, where the dots are,
    /// so they wait for it (the Mac's <c>OpeningTitle.yields</c>); on the wallpaper they always show.
    /// </summary>
    private double InterludeYield(double time) =>
        FloatingStage && _openingSchedule is not { Kept: true } ? OpeningTitle.Yields(_openingSchedule, time) : 1;

    private (OpeningSchedule Schedule, string? Title)? _staying;
    private OpeningSchedule? _yielding;

    /// <summary>
    /// A card staying for a song without lyrics gives way when lyrics arrive after all: it leaves from that moment with its
    /// usual exit, instead of vanishing because the new schedule's time has passed. A new song starts afresh.
    /// </summary>
    private OpeningSchedule? Staying(OpeningSchedule? schedule, string? title, double time)
    {
        if (_staying is { } held && held.Title != title)
        {
            _staying = null;
            _yielding = null;
        }
        if (_before is { } b && b.Title != title) _before = null;
        if (OpeningTitle.Stays(schedule))
        {
            // Already staying: the same card, as it entered.
            if (_staying is { } kept && _yielding is null) return kept.Schedule;
            var staying = schedule!.Value;
            // The lookup ended after the holding card had started leaving (or gone): it comes back in from now rather
            // than popping back at full strength (the Mac's dd3854e).
            if (_before is { } shown && double.IsFinite(time) && time >= shown.Schedule.Leave)
                staying = OpeningTitle.Reentering(staying, time);
            _staying = (staying, title);
            _yielding = null;
            return staying;
        }
        if (_staying is { } was && double.IsFinite(time))
        {
            _yielding ??= OpeningTitle.Yielding(was.Schedule, time);
            // Sought back to before the lyrics arrived: the lyrics are there, so the card doesn't come back over them.
            if (time < _yielding.Value.Leave - 0.05)
            {
                _staying = null;
                _yielding = null;
                return schedule;
            }
            if (time < _yielding.Value.End) return _yielding;
            _staying = null;
            _yielding = null;
        }
        if (schedule is { } s) _before = (s, title);
        return schedule;
    }

    /// <summary>What the schedule was made from this frame: looking the lyrics up (None), lyrics, plain lines or none found.</summary>
    private OpeningWords _openingWords;
    /// <summary>The card that entered while the lyrics were looked up, and the card it became when they arrived.</summary>
    private (OpeningSchedule Schedule, string? Title)? _loadingCard;
    private OpeningSchedule? _handedCard;

    /// <summary>
    /// One card across the lyrics arriving (the Mac's 5fb4a15): a card that entered while they were looked up is kept,
    /// becoming the lyrics' card or leaving in time for the first line, instead of switching size or vanishing in a frame.
    /// A new song or a replay forgets it; a song with no lyrics found keeps its staying card (Staying).
    /// </summary>
    private OpeningSchedule? Handed(OpeningSchedule? schedule, string? title, double time)
    {
        if (_loadingCard is { } card && (card.Title != title || !(time >= card.Schedule.Start)))
        {
            _loadingCard = null;
            _handedCard = null;
        }
        if (_openingWords.Kind == OpeningWordsKind.None)
        {
            // Looking up: remember the card once it has entered.
            if (schedule is { } loading && time >= loading.Start && _handedCard is null) _loadingCard = (loading, title);
            return _handedCard ?? schedule;
        }
        if (_openingWords.Kind == OpeningWordsKind.Absent || _loadingCard is not { } entered) return schedule;
        _handedCard ??= OpeningTitle.Handover(entered.Schedule, schedule, _openingWords.FirstLine, Options.PreviewLead, time);
        return _handedCard;
    }

    /// <summary>The last schedule shown while not staying, for the card coming back in when a lookup ends late.</summary>
    private (OpeningSchedule Schedule, string? Title)? _before;

    private OpeningSchedule? OpeningScheduleFor(SceneSnapshot scene, double joined)
    {
        if (!scene.Style.OpeningTitle || string.IsNullOrWhiteSpace(scene.Title))
        {
            _openingWords = new OpeningWords(OpeningWordsKind.Absent);
            return null;
        }
        OpeningWords words;
        if (Sheet is { Count: > 0 } sheet)
        {
            if (!ReferenceEquals(sheet, _firstSungSheet))
            {
                _firstSungSheet = sheet;
                _firstSung = OpeningTitle.FirstSung(sheet.Lines);
            }
            words = OpeningWords.Timed(_firstSung);
        }
        else if (scene.Lyrics is LyricResult.Plain { Lines.Count: > 0 })
        {
            words = OpeningWords.Showing;
        }
        else if (scene.NoneFound)
        {
            // No lyrics for this song: the card stays (the owner, 15 Nov 2026), in place of the centred title and artist.
            words = OpeningWords.Absent;
        }
        else
        {
            // Looking lyrics up, offline: nothing to sing along to (yet).
            words = OpeningWords.None;
        }
        _openingWords = words;
        return OpeningTitle.Schedule(words, double.IsFinite(joined) ? joined : 0, Options.PreviewLead, scene.Style.OpeningTitleKeep);
    }

    /// <summary>The card at the frame's song time, over everything else; keeps the frame clock running while it moves.</summary>
    private void DrawOpening(ID2D1DeviceContext dc, OpeningSchedule? schedule, in FrameContext frame)
    {
        var time = frame.SongSeconds;
        if (schedule is null || !double.IsFinite(time) || SceneShown is not { } scene || Kit is null || _paint is null) return;
        // The clock runs every frame while the card moves or is about to (the Mac's OpeningTitle.pace); held, the
        // picture does not change, but the wallpaper still needs a frame when it starts to leave.
        // A card that stays with no leave due needs frames only until it has eased to rest (an idle surface draws
        // nothing, so the clock runs through the hold before it); at rest it is the same picture (the Mac's pace).
        if (frame.IsPlaying && OpeningTitle.Upcoming(schedule, time) is not null
            && (!OpeningTitle.Stays(schedule) || time < schedule.Value.RestsFrom + OpeningTitle.RestFade))
            IsAnimating = true;
        if (OpeningTitle.Phase(schedule, time, scene.Style.OpeningTitleStrength) is not { } phase) return;
        var floating = FloatingStage;
        if (floating && phase.Kept)
        {
            DrawRow(dc, scene, phase, frame.WallSeconds);
            return;
        }
        var compact = floating || phase.Size == OpeningSize.Compact;
        var card = Card(dc, scene, compact, phase.Age);
        if (card is null) return;
        var coverFade = CoverFade(scene, frame.WallSeconds);

        var reduce = ReduceMotion;
        var reveal = OpeningReveal.At(phase.Age, compact ? OpeningSize.Compact : phase.Size, reduce);
        var leaving = Easing.Smoothstep(phase.Exit);
        var unit = card.Unit;
        // A staying card settles to a quieter rest after a while: a kept one at Title strength.
        var opacity = (1 - leaving) * phase.Opacity;
        var blur = reduce ? 0f : (float)(leaving * unit * 0.06);
        var lift = reduce ? 0f : (float)(-leaving * unit * 0.14);
        var rise = reduce ? 0 : unit * 0.32;

        // Legible over a bright or busy picture: the wallpaper darkens softly towards the corner the card is in, like a
        // film's grade, rather than a patch behind the words.
        if (_shade is not null && !floating)
        {
            var shade = (float)(Math.Max(reveal.Title, reveal.Rule) * opacity);
            if (shade > 0.002f)
            {
                _shade.Center = card.ShadeCentre;
                _shade.RadiusX = SurfaceSize.X * 0.5f;
                _shade.RadiusY = SurfaceSize.Y * 0.5f;
                _shade.Opacity = shade;
                dc.Transform = Matrix3x2.Identity;
                dc.FillRectangle(new RawRectF(0, 0, SurfaceSize.X, SurfaceSize.Y), _shade);
            }
        }

        // The cover: rounded, shadowed, growing a little from its lower left corner as it arrives.
        if (card.Cover && _coverBrush is not null && _coverSource is { } art && reveal.Cover * opacity * coverFade > 0.002)
        {
            var side = (float)card.Side;
            var grow = reduce ? 1f : (float)(0.9 + 0.1 * reveal.Cover);
            var world = Matrix3x2.CreateTranslation(0, -side) * Matrix3x2.CreateScale(grow)
                * Matrix3x2.CreateTranslation(card.Left, card.Top + (float)card.CoverTop + side + lift);
            var alpha = (float)(reveal.Cover * opacity * coverFade);
            // The album-colour glow round the cover, then its shadow.
            card.CoverGlow?.DrawShadow(dc, Matrix3x2.CreateTranslation(side / 2, side / 2) * world, alpha, 1);
            card.CoverShadow?.DrawShadow(dc, Matrix3x2.CreateTranslation(side / 2, side / 2) * world, alpha, 1);
            // Fill the square from the middle of the cover (a wide or tall cover is cropped, not squeezed).
            var fill = side / Math.Min(art.Width, art.Height);
            _coverBrush.Transform = Matrix3x2.CreateScale(fill)
                * Matrix3x2.CreateTranslation((side - art.Width * fill) / 2, (side - art.Height * fill) / 2);
            _coverBrush.Opacity = alpha;
            dc.Transform = world;
            var radius = (float)card.CoverRadius;
            dc.FillRoundedRectangle(new RoundedRectangle(new RawRectF(0, 0, side, side), radius, radius), _coverBrush);
        }

        var x = card.Left + card.ColumnLeft;
        // The title is wiped on, rising into place.
        var title = card.Title;
        var titleAlpha = Math.Min(1, reveal.Title * 1.6) * opacity;
        if (titleAlpha > 0.002)
        {
            var world = Matrix3x2.CreateTranslation(Mid(card, title, x), card.Top + (float)card.TitleTop + title.Box.Y / 2
                                                    + (float)((1 - reveal.Title) * rise) + lift);
            DrawWiped(dc, title, card.TitleGlows, world, (float)titleAlpha, blur, reduce ? 1 : reveal.Wipe);
        }

        if (card.Reading is { } reading && reveal.Reading * opacity > 0.002)
        {
            var world = Matrix3x2.CreateTranslation(Mid(card, reading, x), card.Top + (float)card.ReadingTop + reading.Box.Y / 2
                                                    + (float)((1 - reveal.Reading) * rise * 0.5) + lift);
            DrawGlows(dc, card.ReadingGlows, world, (float)(reveal.Reading * opacity));
            reading.Draw(dc, world, 1, (float)(reveal.Reading * opacity), blur, GlowSpec.None, 1);
        }

        if (card.Artist is { } artist && reveal.Artist * opacity > 0.002)
        {
            // In the artist's own case, as the player shows it.
            var world = Matrix3x2.CreateTranslation(Mid(card, artist, x), card.Top + (float)card.ArtistTop + artist.Box.Y / 2 + lift);
            DrawGlows(dc, card.ArtistGlows, world, (float)(reveal.Artist * opacity));
            artist.Draw(dc, world, 1, (float)(reveal.Artist * opacity), blur, GlowSpec.None, 1);
        }

        if (card.Album is { } album && reveal.Album * opacity > 0.002)
        {
            var world = Matrix3x2.CreateTranslation(Mid(card, album, x), card.Top + (float)card.AlbumTop + album.Box.Y / 2 + lift);
            DrawGlows(dc, card.AlbumGlows, world, (float)(reveal.Album * opacity));
            album.Draw(dc, world, 1, (float)(reveal.Album * opacity), blur, GlowSpec.None, 1);
        }
    }

    /// <summary>
    /// The floating window's kept title (the Mac's <c>OpeningTitleCard.titleRow</c>): the cover, the title and the artist
    /// on one line at the window's top, settling down from beyond its edge as it enters, quieter once it rests.
    /// </summary>
    private void DrawRow(ID2D1DeviceContext dc, SceneSnapshot scene, in OpeningPhase phase, double wall)
    {
        if (Card(dc, scene, compact: true, phase.Age, row: true) is not { } card) return;
        var coverFade = CoverFade(scene, wall);
        var reduce = ReduceMotion;
        var reveal = OpeningReveal.At(phase.Age, OpeningSize.Compact, reduce);
        var leaving = Easing.Smoothstep(phase.Exit);
        var arrival = reduce ? reveal.Title : Easing.Smoothstep(reveal.Title);
        var alpha = Math.Min(1, arrival * 1.4) * (1 - leaving) * phase.Opacity;
        if (!(alpha > 0.002)) return;
        // Settles in from beyond its edge (the top, or the foot where it was put), and lifts back out that way.
        var dy = reduce ? 0f : (float)((card.Key.RowPlace.Bottom ? 1 : -1) * (1 - arrival + leaving) * card.Unit * 0.18);
        var top = card.Top + dy;

        if (card.Cover && _coverBrush is not null && _coverSource is { } art && reveal.Cover * alpha * coverFade > 0.002)
        {
            var side = (float)card.Side;
            var world = Matrix3x2.CreateTranslation(card.Left, top + (float)card.CoverTop);
            var coverAlpha = (float)(reveal.Cover * alpha * coverFade);
            card.CoverShadow?.DrawShadow(dc, Matrix3x2.CreateTranslation(side / 2, side / 2) * world, coverAlpha, 1);
            var fill = side / Math.Min(art.Width, art.Height);
            _coverBrush.Transform = Matrix3x2.CreateScale(fill)
                * Matrix3x2.CreateTranslation((side - art.Width * fill) / 2, (side - art.Height * fill) / 2);
            _coverBrush.Opacity = coverAlpha;
            dc.Transform = world;
            var radius = side * 0.14f;
            dc.FillRoundedRectangle(new RoundedRectangle(new RawRectF(0, 0, side, side), radius, radius), _coverBrush);
        }

        var title = card.Title;
        var titleWorld = Matrix3x2.CreateTranslation(card.Left + card.ColumnLeft + title.Box.X / 2, top + (float)card.TitleTop + title.Box.Y / 2);
        DrawGlows(dc, card.TitleGlows, titleWorld, (float)alpha);
        title.Draw(dc, titleWorld, 1, (float)alpha, 0, GlowSpec.None, 1);
        if (card.Artist is { } artist && reveal.Artist * alpha > 0.002)
        {
            var world = Matrix3x2.CreateTranslation(card.Left + card.ArtistLeft + artist.Box.X / 2, top + (float)card.ArtistTop + artist.Box.Y / 2);
            DrawGlows(dc, card.ArtistGlows, world, (float)(reveal.Artist * alpha));
            artist.Draw(dc, world, 1, (float)(reveal.Artist * alpha), 0, GlowSpec.None, 1);
        }
    }

    /// <summary>
    /// The floating window's title row, set: the cover (or its place), then the title and the artist on one baseline,
    /// cut short with "…" rather than past 90% of the window, centred across it, the row's margin down from its top.
    /// </summary>
    private OpeningCard BuildRow(CardKey key)
    {
        var kit = Kit!;
        // The row's own size: the lyric size at the row's scale (dragged on the Settings picture).
        var size = BaseSize * OpeningTitle.RowScale(key.RowPlace.Scale);
        var side = size * OpeningTitle.RowCover;
        var type = key.Dark ? new Vector3(0.08f, 0.08f, 0.08f) : Vector3.One;
        var glow = OpeningTitle.GlowStrength(key.Dark);
        var shadow = new ShadowSpec(0.5f, (float)(size * 0.1 * 0.5), (float)(size * 0.015), key.Dark ? Vector3.One : default);
        var accent = new Vector3(key.Accent.X, key.Accent.Y, key.Accent.Z);
        ShadowSpec[] glows = [new((float)(0.45 * glow * key.Accent.W), (float)(size * 0.3 * 0.5), 0, accent)];
        var gap = size * 0.2;
        var wordsGap = size * 0.16;
        var room = SurfaceSize.X * 0.9 - (key.Cover ? side + gap : 0);

        var titleText = key.Title;
        // Each sized in its own face, as the card's lines are.
        var titleLayout = kit.Layout(titleText, Face.Heavy, (float)(size * OpeningTitle.RowTitle * FaceMetrics(kit, titleText, Face.Heavy).Scale),
                                     TextKit.LocaleFor(titleText),
                                     ScriptDirection.IsRightToLeft(titleText));
        Ink titleInk;
        Ink[]? titleGlows;
        float titleBaseline;
        try
        {
            // The title comes first (the Mac's layoutPriority): the artist takes what is left.
            kit.Truncate(titleLayout, (float)room);
            titleBaseline = FirstBaseline(titleLayout);
            titleGlows = GlowInks(kit, titleLayout, glows);
            titleInk = kit.Bake(titleLayout, new Vector4(type, 1), 1, shadow);
        }
        finally
        {
            titleLayout.Dispose();
        }

        Ink? artistInk = null;
        Ink[]? artistGlows = null;
        float artistBaseline = 0;
        var artistRoom = room - titleInk.Box.X - wordsGap;
        if (key.Artist.Trim().Length > 0 && artistRoom > size)
        {
            var text = key.Artist.Trim();
            var layout = kit.Layout(text, Face.Quiet, (float)(size * OpeningTitle.RowArtist * FaceMetrics(kit, text, Face.Quiet).Scale),
                                    TextKit.LocaleFor(text),
                                    ScriptDirection.IsRightToLeft(text));
            try
            {
                kit.Truncate(layout, (float)artistRoom);
                artistBaseline = FirstBaseline(layout);
                artistGlows = GlowInks(kit, layout, glows);
                artistInk = kit.Bake(layout, new Vector4(type, 0.78f), 1, shadow);
            }
            finally
            {
                layout.Dispose();
            }
        }

        // One baseline for the words, the row as tall as the cover; the words centred on it.
        var wordsAbove = Math.Max(titleBaseline, artistBaseline);
        var wordsBelow = Math.Max(titleInk.Box.Y - titleBaseline, artistInk is null ? 0 : artistInk.Box.Y - artistBaseline);
        var wordsHeight = wordsAbove + wordsBelow;
        var height = Math.Max(key.Cover ? side : 0, wordsHeight);
        var baseline = (height - wordsHeight) / 2 + wordsAbove;
        var width = (key.Cover ? side + gap : 0) + titleInk.Box.X + (artistInk is null ? 0 : wordsGap + artistInk.Box.X);
        var frame = OpeningTitle.RowFrame(SurfaceSize.X, SurfaceSize.Y, width, height, BaseSize, key.RowPlace.Centre, key.RowPlace.Bottom);

        Ink? coverShadow = null;
        if (key.Cover)
            coverShadow = kit.BakeTile(new Vector2((float)side, (float)side), (float)(side * 0.14), Vector4.One, 1,
                                       new ShadowSpec(0.4f, (float)(side * 0.08 * 0.5), (float)(side * 0.03)));
        return new OpeningCard(key)
        {
            Unit = size,
            Title = titleInk,
            TitleGlows = titleGlows,
            Artist = artistInk,
            ArtistGlows = artistGlows,
            CoverShadow = coverShadow,
            Cover = key.Cover,
            Side = side,
            Left = (float)frame.X,
            Top = (float)frame.Y,
            ColumnLeft = key.Cover ? (float)(side + gap) : 0,
            ArtistLeft = (float)((key.Cover ? side + gap : 0) + titleInk.Box.X + wordsGap),
            ContentWidth = (float)width,
            CoverTop = (height - side) / 2,
            TitleTop = baseline - titleBaseline,
            ArtistTop = baseline - artistBaseline,
        };
    }

    /// <summary>The first line's baseline, down from the top of <paramref name="layout"/>'s box.</summary>
    private static float FirstBaseline(IDWriteTextLayout layout)
    {
        var lines = new LineMetrics[1];
        layout.GetLineMetrics(lines, out var count);
        return count > 0 ? lines[0].Baseline : layout.Metrics.Height * 0.8f;
    }

    /// <summary>A line's album-colour glow (its tight halo and wide bloom), drawn behind it.</summary>
    private static void DrawGlows(ID2D1DeviceContext dc, Ink[]? glows, in Matrix3x2 world, float opacity, InkClip clip = default)
    {
        if (glows is null) return;
        foreach (var glow in glows) glow.DrawShadow(dc, world, opacity, 1, clip: clip);
    }

    /// <summary>The centre of a line of the card: under the column's middle when centred, else from its left edge.</summary>
    private static float Mid(OpeningCard card, Ink ink, float x) => card.Centred ? x + card.ContentWidth / 2 : x + ink.Box.X / 2;

    /// <summary>
    /// A soft left-to-right wipe over <paramref name="ink"/> (the Mac's card mask: solid up to the edge, then a feather
    /// a quarter of the title's width), drawn as a few strips of falling opacity, with the title's shadow and glow.
    /// </summary>
    private static void DrawWiped(ID2D1DeviceContext dc, Ink ink, Ink[]? glows, in Matrix3x2 world, float opacity, float blur,
                                  double progress)
    {
        const double feather = 0.25;
        var (solid, clear) = LyricSweep.Edges(progress, feather);
        if (solid >= 1)
        {
            DrawGlows(dc, glows, world, opacity);
            ink.Draw(dc, world, 1, opacity, blur, GlowSpec.None, 1);
            return;
        }
        var width = ink.Box.X;
        // The wipe runs left to right on the card whatever the script (the card is anchored on the left).
        if (solid > 0)
        {
            DrawGlows(dc, glows, world, opacity, SweepSpan(-1, solid, width, false));
            ink.Draw(dc, world, 1, opacity, blur, GlowSpec.None, 1, clip: SweepSpan(-1, solid, width, false));
        }
        const int strips = 6;
        var step = (clear - solid) / strips;
        if (!(step > 0)) return;
        for (var s = 0; s < strips; s++)
        {
            var from = solid + s * step;
            var alpha = LyricSweep.Alpha(from + step / 2, progress, feather);
            if (!(alpha > 0.002)) continue;
            DrawGlows(dc, glows, world, (float)(opacity * alpha), SweepSpan(from, from + step, width, false));
            ink.Draw(dc, world, 1, (float)(opacity * alpha), blur, GlowSpec.None, 1, clip: SweepSpan(from, from + step, width, false));
        }
    }

    private string? _fadeTrack;
    private double? _coverArrived;

    /// <summary>A cover that arrives after the card has begun fades in over this long, in wall seconds (the Mac's <c>coverFadeIn</c>).</summary>
    internal const double CoverFadeIn = 0.45;

    /// <summary>
    /// 0…1: how far the song's cover has faded in at wall time <paramref name="wall"/> (the Mac's
    /// <c>OpeningTitleMemory.coverFade</c>, 297b2f2). A cover already here when the song was first seen needs no fade of
    /// its own (the card's entrance brings it); one that arrives later fades in. Called after <see cref="Card"/>.
    /// </summary>
    private double CoverFade(SceneSnapshot scene, double wall)
    {
        var has = _coverSource is not null && _coverBrush is not null;
        if (scene.TrackKey != _fadeTrack)
        {
            _fadeTrack = scene.TrackKey;
            _coverArrived = has ? double.NegativeInfinity : null;
        }
        if (!has)
        {
            _coverArrived = null;
            return 0;
        }
        _coverArrived ??= wall;
        var fade = Easing.Smoothstep(Math.Clamp((wall - _coverArrived.Value) / CoverFadeIn, 0, 1));
        if (fade < 1) IsAnimating = true;
        return fade;
    }

    /// <summary>The card for the scene as it is now: set (laid out and baked) once, again only when what it shows changes.</summary>
    /// <param name="age">How long the card has been up: a cover still being read keeps its place for <see cref="OpeningTitle.CoverWait"/>.</param>
    private OpeningCard? Card(ID2D1DeviceContext dc, SceneSnapshot scene, bool compact, double age, bool row = false)
    {
        var art = scene.Style.OpeningTitleCover && scene.Cover is { } cover && OpeningTitle.ShowsCover(true, cover.Width, cover.Height)
            ? cover : null;
        SyncCover(dc, art);
        // The cover's place: its picture once it is here, kept empty while it is read (so the words don't move when it
        // comes), none when the song has none (the Mac's 297b2f2).
        var slot = (art is not null && _coverBrush is not null)
                   || (scene.Style.OpeningTitleCover && scene.CoverPending && OpeningTitle.ReservesCover(age));
        var dark = IsDarkInk(SettledInk.Base.Rest);
        // The album line is worked out once per song, not every frame (it normalises both names).
        if (!ReferenceEquals(scene.Album, _albumOf.Album) || !ReferenceEquals(scene.Title, _albumOf.Title))
            _albumOf = (scene.Album, scene.Title, OpeningTitle.AlbumLine(scene.Album, scene.Title));
        var key = new CardKey(scene.Title ?? "", scene.Artist ?? "", _albumOf.Line,
                              string.IsNullOrWhiteSpace(scene.TitleReading) ? null : scene.TitleReading.Trim(), compact,
                              slot, FloatingStage, scene.Style.FontFamily, SurfaceSize, Scale, BaseSize, dark,
                              SettledInk.Dot is { } dot ? LyricInks.Vector(dot) : TextColour, row,
                              scene.Style.OpeningTitlePlacement, scene.Style.OpeningTitleRow);
        if (_card is { } current && current.Key == key) return current;
        _card?.Dispose();
        _card = null;
        SyncShade(dc, dark);
        Meter.Enter();
        try
        {
            _card = key.Row ? BuildRow(key) : BuildCard(key);
        }
        finally
        {
            Meter.Exit();
        }
        return _card;
    }

    /// <summary>Dark type, for a lyric ink chosen against a light backdrop (the Mac's <c>OpeningTitleCard.isDark</c>).</summary>
    internal static bool IsDarkInk(SrgbColour colour) => 0.2126 * colour.R + 0.7152 * colour.G + 0.0722 * colour.B < 0.4;

    /// <summary>
    /// Glows of a laid-out line, baked once with the card: each only a soft image of the text in the glow's colour (the
    /// text itself is clear), at half resolution, drawn with <see cref="Ink.DrawShadow"/>. None when no glow shows.
    /// </summary>
    private static Ink[]? GlowInks(TextKit kit, IDWriteTextLayout layout, ReadOnlySpan<ShadowSpec> glows)
    {
        var count = 0;
        foreach (var glow in glows) if (glow.Alpha > 0.004f) count++;
        if (count == 0) return null;
        var result = new Ink[count];
        var k = 0;
        foreach (var glow in glows)
            if (glow.Alpha > 0.004f) result[k++] = kit.Bake(layout, Vector4.Zero, 0.5f, glow);
        return result;
    }

    /// <summary>The cover as a Direct2D bitmap and brush, made once per cover (never per frame).</summary>
    private void SyncCover(ID2D1DeviceContext dc, CoverArt? art)
    {
        if (ReferenceEquals(art, _coverSource) && (art is null || _coverBrush is not null)) return;
        DropCover();
        _coverSource = art;
        if (art is null) return;
        var pinned = GCHandle.Alloc(art.Pixels, GCHandleType.Pinned);
        try
        {
            _coverBitmap = dc.CreateBitmap(new SizeI(art.Width, art.Height), pinned.AddrOfPinnedObject(), (uint)(art.Width * 4),
                new BitmapProperties1(new PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
        }
        finally
        {
            pinned.Free();
        }
        _coverBrush = dc.CreateBitmapBrush(_coverBitmap,
            new BitmapBrushProperties1(ExtendMode.Clamp, ExtendMode.Clamp, InterpolationMode.HighQualityCubic),
            new BrushProperties(1, Matrix3x2.Identity));
    }

    /// <summary>The corner shade's brush, in black (or white behind dark type).</summary>
    private void SyncShade(ID2D1DeviceContext dc, bool dark)
    {
        if (_shade is not null && dark == _shadeDark) return;
        DropShade();
        _shadeDark = dark;
        var c = dark ? 1f : 0f;
        _shadeStops = dc.CreateGradientStopCollection(
        [
            new GradientStop(0, new Color4(c, c, c, 0.4f)),
            new GradientStop(0.3f, new Color4(c, c, c, 0.28f)),
            new GradientStop(0.65f, new Color4(c, c, c, 0.1f)),
            new GradientStop(1, new Color4(c, c, c, 0)),
        ]);
        _shade = dc.CreateRadialGradientBrush(new RadialGradientBrushProperties(new Vector2(0, 0), new Vector2(0, 0), 1, 1), _shadeStops);
    }

    private void DropCover()
    {
        _coverBrush?.Dispose();
        _coverBrush = null;
        _coverBitmap?.Dispose();
        _coverBitmap = null;
        _coverSource = null;
    }

    private void DropShade()
    {
        _shade?.Dispose();
        _shade = null;
        _shadeStops?.Dispose();
        _shadeStops = null;
    }

    /// <summary>Lets go of the card and its device resources (the device or the surface is going).</summary>
    private void ReleaseOpening()
    {
        _card?.Dispose();
        _card = null;
        DropCover();
        DropShade();
    }

    private readonly record struct CardKey(string Title, string Artist, string? Album, string? Reading, bool Compact, bool Cover,
                                           bool Floating, string Family, Vector2 Surface, float Scale, double BaseSize, bool Dark, Vector4 Accent,
                                           bool Row, OpeningTitlePlacement Placement, OpeningTitleRow RowPlace);

    /// <summary>The card as set: its baked lines and where they go, in device pixels.</summary>
    private sealed class OpeningCard(CardKey key) : IDisposable
    {
        public CardKey Key { get; } = key;
        public double Unit { get; init; }
        public required Ink Title { get; init; }
        public Ink? Reading { get; init; }
        public Ink? Artist { get; init; }
        public Ink? Album { get; init; }
        /// <summary>Each line's album-colour glows (the wide bloom, then the tight halo); null for none.</summary>
        public Ink[]? TitleGlows { get; init; }
        public Ink[]? ReadingGlows { get; init; }
        public Ink[]? ArtistGlows { get; init; }
        public Ink[]? AlbumGlows { get; init; }
        public Ink? CoverShadow { get; init; }
        /// <summary>The cover's album-colour glow (a clear tile with a coloured soft image).</summary>
        public Ink? CoverGlow { get; init; }
        public bool Cover { get; init; }
        public double Side { get; init; }
        /// <summary>The cover's corner radius.</summary>
        public double CoverRadius { get; init; }
        /// <summary>The middle of the soft corner shade behind the card (the desktop's).</summary>
        public Vector2 ShadeCentre { get; init; }
        /// <summary>The card's left edge and top.</summary>
        public float Left { get; init; }
        public float Top { get; init; }
        /// <summary>The words' column, from the card's left edge (beside the cover).</summary>
        public float ColumnLeft { get; init; }
        /// <summary>The floating row's artist, from the row's left edge (after the title).</summary>
        public float ArtistLeft { get; init; }
        /// <summary>Centred in the floating window without a cover: each line under the column's middle.</summary>
        public bool Centred { get; init; }
        /// <summary>The widest line, pixels.</summary>
        public float ContentWidth { get; init; }
        // Each piece's top, from the card's top.
        public double CoverTop { get; init; }
        public double TitleTop { get; init; }
        public double ReadingTop { get; init; }
        public double ArtistTop { get; init; }
        public double AlbumTop { get; init; }

        public void Dispose()
        {
            Title.Dispose();
            Reading?.Dispose();
            Artist?.Dispose();
            Album?.Dispose();
            CoverShadow?.Dispose();
            CoverGlow?.Dispose();
            foreach (var glows in new[] { TitleGlows, ReadingGlows, ArtistGlows, AlbumGlows })
                if (glows is not null)
                    foreach (var glow in glows) glow.Dispose();
        }
    }
}
