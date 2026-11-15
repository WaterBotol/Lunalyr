// Ported from the Mac's Sources/UI/Design/PlayerTokens.swift and the player's durations in MotionTokens.swift and
// DesignTokens.swift (Lunalyr's own code, no Verso lines; the four smoothstep lines MotionTokens.swift shares with the
// Verso import have no counterpart here). docs/DESIGN.md ("Desktop player") is the spec. The Mac's field token (its
// search box) is left out: Windows has no player search.

using Lunalyr.Core.Colour;

namespace Lunalyr.Core.Player;

/// <summary>The four levels of ink on the card, and nothing in between.</summary>
public enum PlayerInk
{
    /// <summary>The title, the lit row, values in capsules.</summary>
    Primary,

    /// <summary>Artist, row subtitles, icon glyphs, the player's name.</summary>
    Secondary,

    /// <summary>Album, times, tags, section names, placeholders.</summary>
    Tertiary,

    /// <summary>Disabled glyphs and decoration only, never words to read.</summary>
    Quaternary,
}

/// <summary>A type token: a size in DIPs and a weight (400 regular, 500 medium, 600 semibold; never bold).</summary>
public readonly record struct PlayerType(double Size, int Weight, bool TabularDigits = false);

/// <summary>
/// The desktop player's tokens. The card lives on the wallpaper, so it has its own small set beside the windows'
/// Fluent resources: every value the painter draws with comes from here, never a number of its own.
/// </summary>
public static class PlayerTokens
{
    /// <summary>Ink (text and glyphs on the card): the ink colour at one of four opacities.</summary>
    public static class Ink
    {
        /// <summary>
        /// The ink's opacity, normal and under Increase Contrast (the Mac's; a Windows contrast theme draws in the
        /// theme's own colours instead, with no opacity at all).
        /// </summary>
        public static double Opacity(PlayerInk ink, bool increaseContrast = false) => ink switch
        {
            PlayerInk.Primary => 1,
            PlayerInk.Secondary => increaseContrast ? 0.94 : 0.84,
            PlayerInk.Tertiary => increaseContrast ? 0.88 : 0.72,
            _ => increaseContrast ? 0.6 : 0.4,
        };
    }

    /// <summary>Fills on the card, as opacities.</summary>
    public static class Fill
    {
        /// <summary>Under the pointer: an icon button's halo, a row.</summary>
        public const double Hover = 0.08;

        /// <summary>Selected or pressed: the lit row, an open pane's button.</summary>
        public const double Selected = 0.16;

        /// <summary>A recessed well (black on the dark card): the search field, the player switch's track.</summary>
        public const double Well = 0.24;

        /// <summary>The card's veil (black on the dark card), so its ink always reads.</summary>
        public const double Veil = 0.24;

        /// <summary>The album tint over the veil (<see cref="ArtworkPalette.CardTint"/>), flat.</summary>
        public const double Tint = 0.92;

        /// <summary>A placeholder tile while a cover loads.</summary>
        public const double Placeholder = 0.1;

        /// <summary>A capsule over the card (the album's deep shade): a piece of the card lifted, not a second accent.</summary>
        public const double Capsule = 0.96;
    }

    /// <summary>
    /// Lines. Edges are drawn only under Increase Contrast (on Windows, a contrast theme): the card, capsules and fields
    /// are separated by fill, not by borders.
    /// </summary>
    public static class Line
    {
        /// <summary>The hairline's opacity under Increase Contrast (none otherwise).</summary>
        public const double Hairline = 0.55;

        /// <summary>Keyboard focus (the search field holding the keyboard): this opacity, 1 under Increase Contrast.</summary>
        public const double Focus = 0.7;

        public const double Width = 1;
        public const double FocusWidth = 1.5;
    }

    /// <summary>Type: three weights only, sentence case everywhere, default tracking.</summary>
    public static class TypeScale
    {
        /// <summary>The song's title at rest.</summary>
        public static readonly PlayerType Title = new(24, 600);

        /// <summary>A title too long for its line is set down to this share of its size before it is cut short.</summary>
        public const double TitleMinimumScale = 0.8;

        public static readonly PlayerType Artist = new(15, 400);

        /// <summary>The album, status lines.</summary>
        public static readonly PlayerType Meta = new(12, 400);

        /// <summary>The player's name over the song, the player switch, Reset.</summary>
        public static readonly PlayerType Label = new(12, 500);

        /// <summary>A row's title in search and the queue; the song under the cover; the queue's heading.</summary>
        public static readonly PlayerType RowTitle = new(13, 600);

        public static readonly PlayerType RowSubtitle = new(11, 400);

        /// <summary>"Library", "Album", section names, "Live", the drag's speed.</summary>
        public static readonly PlayerType Tag = new(11, 500);

        /// <summary>Times under the bar and counts.</summary>
        public static readonly PlayerType Time = new(11, 400, TabularDigits: true);

        /// <summary>The time in a capsule.</summary>
        public static readonly PlayerType TimeStrong = new(11, 600, TabularDigits: true);

        /// <summary>The lyric shown over the bar while scrubbing.</summary>
        public static readonly PlayerType CapsuleLyric = new(15, 600);

        /// <summary>A note over the card ("Controls are off for this run").</summary>
        public static readonly PlayerType Note = new(12, 500);
    }

    /// <summary>Corner radii in DIPs.</summary>
    public static class Radius
    {
        /// <summary>The card: concentric with the cover (cover radius + padding). DWM rounds the Windows card at 8.</summary>
        public const double Card = 28;

        public const double Cover = 8;

        /// <summary>Search field, capsules, the note.</summary>
        public const double Control = 10;

        /// <summary>A lit row in search or the queue.</summary>
        public const double Row = 8;

        /// <summary>Small covers in rows.</summary>
        public const double Thumb = 5;
    }

    /// <summary>Glyphs.</summary>
    public static class Glyph
    {
        /// <summary>Icon buttons (search, queue, film) and the × that closes a pane.</summary>
        public const double Icon = 13;

        /// <summary>The halo behind an icon button, and its hit target.</summary>
        public const double Button = 30;
    }

    /// <summary>Depth: the one soft shadow under the card, and the cover's on it.</summary>
    public static class Shadow
    {
        public const double CardOpacity = 0.32, CardBlur = 28, CardY = 12;
        public const double CoverOpacity = 0.35, CoverBlur = 12, CoverY = 6;

        /// <summary>A capsule lifted over the card.</summary>
        public const double CapsuleOpacity = 0.24, CapsuleBlur = 8, CapsuleY = 3;
    }

    /// <summary>
    /// The player's motion, in seconds, from the one timing scale: bounce-free, exits shorter than entrances, a short
    /// fade under Reduce Motion (on Windows, Animation effects off).
    /// </summary>
    public static class Motion
    {
        /// <summary>A control pressed in.</summary>
        public const double Press = 0.10;

        /// <summary>A capsule or highlight leaving.</summary>
        public const double QuickExit = 0.12;

        /// <summary>Hover, selection, a capsule or note arriving.</summary>
        public const double Quick = 0.16;

        /// <summary>What every movement becomes under Reduce Motion.</summary>
        public const double Reduced = 0.18;

        /// <summary>Panels and the player leaving.</summary>
        public const double Exit = 0.20;

        /// <summary>A control let go: settles back with no overshoot.</summary>
        public const double Release = 0.24;

        /// <summary>Panes, the player arriving (with <see cref="Rise"/>).</summary>
        public const double Standard = 0.28;

        /// <summary>The record sliding in and out of the sleeve.</summary>
        public const double Emphasised = 0.45;

        /// <summary>Hover feedback, selection, a capsule or note arriving.</summary>
        public const double Feedback = Quick;

        /// <summary>A capsule or highlight leaving.</summary>
        public const double FeedbackOut = QuickExit;

        public const double Record = Emphasised;

        /// <summary>The rise, in DIPs, as the card appears.</summary>
        public const double Rise = 8;

        /// <summary>How far a button sinks when pressed.</summary>
        public const double PressScale = 0.94;

        /// <summary>How far the cover sinks when pressed.</summary>
        public const double CoverPressScale = 0.97;

        /// <summary>The card appearing (with the rise) and leaving (a plain fade); a short fade both ways under Reduce Motion.</summary>
        public static double Reveal(bool shown, bool reduceMotion) => reduceMotion ? Reduced : shown ? Standard : Exit;

        /// <summary>Song ↔ search ↔ queue.</summary>
        public static double Pane(bool reduceMotion) => reduceMotion ? Reduced : Standard;
    }
}

/// <summary>
/// The card's ground: what its ink sits on. Dark: the veil (black) and the album's flat tint over whatever shows through
/// (the Mac's system material; on Windows, the wallpaper itself). Light (Windows only: the Mac's card is always dark):
/// a white veil and a light tint of the album's hue, with near-black ink.
/// </summary>
public static class PlayerCardColours
{
    /// <summary>The light card's ink: near-black.</summary>
    public static readonly ArtworkPalette.Rgb LightInk = new(0.07, 0.07, 0.09);

    /// <summary>Near-black ink on the light tint reaches at least this, before the veil and the wallpaper.</summary>
    public const double LightContrast = 12;

    /// <summary>The light card's flat tint: the album's accent mixed far towards white, lightened further if need be.</summary>
    public static ArtworkPalette.Rgb LightTint(ArtworkPalette palette)
    {
        var white = new ArtworkPalette.Rgb(1, 1, 1);
        var result = palette.Accent.Mixed(white, palette.FromArtwork ? 0.8 : 0.86);
        for (var i = 0; i < 60 && Contrast(LightInk, result) < LightContrast; i++)
            result = result.Mixed(white, 0.1);
        return result;
    }

    /// <summary>
    /// The card's colour over <paramref name="behind"/> (what the wallpaper shows through it): the veil, then the tint
    /// at <see cref="PlayerTokens.Fill.Tint"/>.
    /// </summary>
    public static ArtworkPalette.Rgb Ground(ArtworkPalette palette, bool light, ArtworkPalette.Rgb behind)
    {
        var veil = light ? new ArtworkPalette.Rgb(1, 1, 1) : new ArtworkPalette.Rgb(0, 0, 0);
        var tint = light ? LightTint(palette) : palette.CardTint;
        return behind.Mixed(veil, PlayerTokens.Fill.Veil).Mixed(tint, PlayerTokens.Fill.Tint);
    }

    /// <summary>The worst wallpaper for the card's ink: white behind the dark card, black behind the light one.</summary>
    public static ArtworkPalette.Rgb WorstGround(ArtworkPalette palette, bool light) =>
        Ground(palette, light, light ? new ArtworkPalette.Rgb(0, 0, 0) : new ArtworkPalette.Rgb(1, 1, 1));

    /// <summary>The ink colour at <paramref name="level"/> over <paramref name="ground"/>, as drawn.</summary>
    public static ArtworkPalette.Rgb InkOn(ArtworkPalette.Rgb ground, bool light, PlayerInk level, bool increaseContrast = false) =>
        ground.Mixed(light ? LightInk : new ArtworkPalette.Rgb(1, 1, 1), PlayerTokens.Ink.Opacity(level, increaseContrast));

    /// <summary>WCAG contrast between two colours.</summary>
    public static double Contrast(ArtworkPalette.Rgb a, ArtworkPalette.Rgb b)
    {
        var (la, lb) = (a.Luminance, b.Luminance);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
