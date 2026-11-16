// Ported from the Mac's Sources/UI/LyricColour.swift ("Director") and LyricHighlightSwatchModel from
// Sources/UI/LyricHighlightColourControls.swift. Lunalyr's own code, translated faithfully.

using System.Numerics;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Colour;

/// <summary>Live audio levels, when a visualiser is capturing them (the Mac's <c>VCAudioLevels</c>).</summary>
public readonly record struct AudioLevels(double Rms, double Bass, double Mid, double Treble, double Pulse);

/// <summary>What the lyric colours are worked out from, each frame.</summary>
public sealed record LyricInkInputs(LyricColourMode Mode)
{
    public double Speed { get; init; } = 1;
    public double Strength { get; init; } = 1;
    public IReadOnlyList<OkLab> Custom { get; init; } = [];

    /// <summary>Album colours, sRGB (the nine artwork stops, as the stage passes them).</summary>
    public IReadOnlyList<Vector3> Album { get; init; } = [];

    public double Backdrop { get; init; } = 0.08;

    /// <summary>
    /// The luminance behind the lyric stage before the automatic text backing (a backing set by hand counts), which
    /// decides light or dark lyrics; null where the lyrics have a shade of their own (floating), so they stay light.
    /// </summary>
    public double? Unbacked { get; init; }

    /// <summary>Live audio, when a visualiser is capturing it.</summary>
    public AudioLevels? Audio { get; init; }

    /// <summary>Timing-derived vocal intensity, used when there is no audio.</summary>
    public double Vocal { get; init; }

    public bool ReduceMotion { get; init; }

    /// <summary>The system's high-contrast preference: 4.5:1 and brighter unsung words.</summary>
    public bool IncreaseContrast { get; init; }

    /// <summary>The album accent the layouts highlighted with before; it breaks ties for "Album accent".</summary>
    public OkLab? Accent { get; init; }

    /// <summary>Where highlight tiles take their colour from.</summary>
    public LyricHighlightMode Highlight { get; init; } = LyricHighlightMode.Complement;

    /// <summary>The user's own highlight colour, for <see cref="LyricHighlightMode.Custom"/>.</summary>
    public OkLab? HighlightCustom { get; init; }

    /// <summary>
    /// Colours eased towards pastel (<see cref="LyricInk.Pastel"/>) before they are made readable; false keeps the
    /// chosen colours' own lightness and chroma (<see cref="LyricColourOptions.PastelApplies"/>). The contrast floors
    /// hold either way (the Mac's cab0212).
    /// </summary>
    public bool Pastel { get; init; } = true;
}

/// <summary>
/// Builds each frame's ink. Every target colour goes through critically damped followers with a hard rate limit on
/// lightness, so a change of song, mode or a burst of audio is a fade, never a flash (pairs of opposing luminance
/// changes faster than three a second). One per lyric surface; call from one thread.
/// </summary>
public sealed class LyricInkDirector
{
    /// <summary>Lightness may change by at most this much per second (OKLab L).</summary>
    public const double LightnessRate = 0.5;

    public const double ChromaRate = 0.6;

    private static readonly InkStops FallbackPastels = new(
        OkLab.FromLch(0.84, 0.14, 1.25), OkLab.FromLch(0.84, 0.15, 3.12),
        OkLab.FromLch(0.80, 0.16, 4.85), OkLab.FromLch(0.82, 0.16, 5.82));

    /// <summary>Every channel starts at white, so the first colour fades in.</summary>
    private readonly Follower[] _channels = MakeChannels();

    private Follower _backdrop;
    private Follower _level, _balance;
    private double _phase;
    private double? _lastWall;
    private LyricInk? _last;
    private Vector3[] _albumKey = [];
    private InkStops _albumStops = FallbackPastels;

    /// <summary>The cover's own colours, unpastelled, for <see cref="LyricInkInputs.Pastel"/> off.</summary>
    private InkStops _albumRawStops = FallbackPastels;
    private OkLab _vivid = FallbackPastels.S2;
    private OkLab _reactiveAccent = FallbackPastels.S3;

    /// <summary>The cover's colours in OKLab, for "Album accent".</summary>
    private OkLab[] _albumColours = [];

    private double? _backgroundHue;

    /// <summary>Static white's ink, worked out again only when what it depends on changes.</summary>
    private (OkLab Tile, double Backdrop, bool IncreaseContrast)? _whiteKey;

    private LyricInk _whiteInk = LyricInk.White;

    /// <summary>The highlight tile eases like every other colour. It starts unset, so the first tile is simply there.</summary>
    private readonly Follower[] _highlightChannels = new Follower[3];

    private HighlightKey? _highlightKey;

    /// <summary>The tile the highlight is heading for; the drawn tile eases to it.</summary>
    public OkLab GoalTile { get; private set; } = OkLab.FromLch(0.8, 0.14, 0.4);

    private static Follower[] MakeChannels()
    {
        var channels = new Follower[12];
        for (var i = 0; i < channels.Length; i++) channels[i] = new Follower { Value = i % 3 == 0 ? 1 : 0, Valid = true };
        return channels;
    }

    public LyricInk Ink(LyricInkInputs inputs, double wall)
    {
        // The system clock stepped back: carry on from the new time rather than hold the colours until the old one
        // comes round again.
        if (_lastWall is { } previous && wall < previous - 1) _lastWall = null;
        var dt = _lastWall is { } lastWall ? Math.Min(0.25, Math.Max(0, wall - lastWall)) : 0;
        if (_lastWall is not null && dt == 0 && _last is { } last) return last;
        var firstFrame = _lastWall is null && _last is null;
        _lastWall = wall;
        RefreshAlbum(inputs.Album);
        var calm = inputs.ReduceMotion;
        // Your own colours with pastel off are kept nearer what you picked: a gentler floor, with the text's shadow and
        // backing carrying the rest (the Mac's 7957a6e and 1763cce).
        var ownColours = inputs.Mode == LyricColourMode.CustomGradient && !inputs.Pastel && inputs.Custom.Count > 0;
        var ratio = inputs.IncreaseContrast ? LyricInk.IncreasedContrast
            : ownColours ? LyricInk.OwnColourContrast : LyricInk.MinimumContrast;
        var textRatio = inputs.IncreaseContrast ? LyricHighlight.IncreasedTextContrast : LyricHighlight.TextContrast;
        var (dark, deepBackdrop, darkMoving) = DarkState(inputs, dt, firstFrame);
        if (inputs.Mode == LyricColourMode.Standard && AtWhite && dark > 0.0005)
        {
            // Static white on a light wallpaper: deep grey, through the general path below.
        }
        else if (inputs.Mode == LyricColourMode.Standard && AtWhite)
        {
            // The type is white, but the highlights have colour: white has no hue to complement, so they take the
            // album accent, or the user's own colour.
            var whiteBackdrop = Math.Min(1, Math.Max(0, inputs.Backdrop));
            var whiteGoal = HighlightGoal(inputs, null, [], whiteBackdrop, textRatio);
            var whiteTile = FollowHighlight(whiteGoal, dt, calm);
            var key = (whiteTile, inputs.Backdrop, inputs.IncreaseContrast);
            if (_whiteKey != key)
            {
                var white = LyricInk.WhiteFor(inputs.IncreaseContrast);
                white.Backdrop = whiteBackdrop;
                ApplyHighlight(ref white, whiteTile, whiteBackdrop, textRatio, white.Contrast, 1);
                _whiteKey = key;
                _whiteInk = white;
            }
            var whiteInk = _whiteInk;
            whiteInk.Settled = HighlightSettled(whiteGoal) && !darkMoving;
            _last = whiteInk;
            return whiteInk;
        }
        // Drift: about 45 s per loop at normal speed, ten times slower with Reduce Motion. Quantised below, so a
        // settled word can reuse its glyphs between steps too small to see.
        if (inputs.Mode.Drifts())
            _phase = (_phase + dt * Math.Min(2, Math.Max(0.25, inputs.Speed)) * 0.022 * (calm ? 0.1 : 1)) % 1;
        var (targets, count) = TargetStops(inputs, dt);
        var strength = LyricColourOptions.ClampedStrength(inputs.Strength);
        var goals = new InkStops(LyricInk.ApplyingStrength(strength, targets.S0), LyricInk.ApplyingStrength(strength, targets.S1),
                                 LyricInk.ApplyingStrength(strength, targets.S2), LyricInk.ApplyingStrength(strength, targets.S3));
        double lTau = calm ? 0.7 : 0.35, abTau = calm ? 0.5 : 0.25;
        OkLab Follow(int index, OkLab target)
        {
            var l = _channels[index * 3].Follow(target.L, dt, lTau, LightnessRate);
            var a = _channels[index * 3 + 1].Follow(target.A, dt, abTau, ChromaRate);
            var b = _channels[index * 3 + 2].Follow(target.B, dt, abTau, ChromaRate);
            return new OkLab(Quantised(l), Quantised(a), Quantised(b));
        }
        var s0 = Follow(0, goals.S0);
        var s1 = Follow(1, goals.S1);
        var s2 = Follow(2, goals.S2);
        var s3 = Follow(3, goals.S3);
        var followed = new InkStops(s0, s1, s2, s3);
        var behindTarget = Math.Min(1, Math.Max(0, inputs.Backdrop));
        var behind = Quantised(_backdrop.Follow(behindTarget, dt, 0.5, 0.6));
        var ink = new LyricInk
        {
            Mode = inputs.Mode,
            Settled = SettledOn(goals) && IsSettled(_backdrop, behindTarget),
            Backdrop = behind,
            Stops = followed,
            StopCount = count,
            Phase = SwiftMath.Round(_phase * 720) / 720,
            Contrast = ratio,
            Dim = inputs.IncreaseContrast ? LyricInk.IncreasedUnsungDim : LyricInk.UnsungDim,
            Dark = dark,
            DeepBackdrop = deepBackdrop,
        };
        if (darkMoving) ink.Settled = false;
        if (inputs.Mode == LyricColourMode.SungSweep)
        {
            // Unsung words deepen to a faint tint of the palette's second colour (its first when that one is grey).
            var tint = followed.S1.Chroma >= 0.02 ? followed.S1 : followed.S0;
            ink.UnsungDeep = OkLab.FromLch(0.32, 0.025, tint.Hue);
        }
        switch (inputs.Mode)
        {
            case LyricColourMode.Standard:
                // Still easing back to white after another mode (or white on a light wallpaper, deepened to grey).
                ink.Mode = LyricColourMode.AlbumDrift;
                ink.StopCount = 1;
                ink.Base = ink.Toned(Perceptual.Readable(followed.S0, behind, ratio), LyricInk.WhiteDeep);
                break;
            case LyricColourMode.SungSweep:
            {
                // Unsung white, sung the middle of the drifting palette; each word samples its own (LyricInk.Sample).
                // No underline under sung words (the owner, 14 Nov 2026): SweepUnderline is never set.
                var middle = ink.Gradient(0.5);
                var sung = Perceptual.Readable(middle, behind, ratio, keepingColour: true);
                ink.Base = ink.Toned(Perceptual.Readable(OkLab.White, behind, ratio), ink.UnsungDeep, sung, middle);
                break;
            }
            default:
            {
                var middle = ink.Gradient(0.5);
                ink.Base = ink.Toned(Perceptual.Readable(middle, behind, ratio, keepingColour: true), middle);
                break;
            }
        }
        var baseSample = ink.Base;
        baseSample.Dim = ink.Dim;
        ink.Base = baseSample;
        // Sung sweep's highlight, dots and links take the palette's first colour (the Mac's rule).
        var highlight = Perceptual.Readable(inputs.Mode == LyricColourMode.SungSweep ? followed.S0 : ink.Gradient(0.5), behind, ratio);
        // Dark lyrics: the dots take the toned highlight, the links mix less white into it.
        var toned = dark > 0.0005 ? Perceptual.Mix(highlight, Perceptual.Deep(highlight, deepBackdrop, ratio), dark) : highlight;
        ink.Dot = Perceptual.ToColour(toned);
        ink.Link = Perceptual.ToColour(Perceptual.Mix(toned, OkLab.White, 0.5 * (1 - dark) + 0.12 * dark));
        // The highlight complements the lyric colour as it is now (easing back to Static white, the one it will
        // settle on): the sung colour for Sung sweep, the middle of the line otherwise.
        OkLab? basis = inputs.Mode switch
        {
            LyricColourMode.Standard => null,
            LyricColourMode.SungSweep => followed.S0,
            _ => ink.Gradient(0.5),
        };
        var stopTotal = Math.Max(1, Math.Min(4, count));
        var stops = new OkLab[stopTotal];
        for (var i = 0; i < stopTotal; i++) stops[i] = followed[i];
        var goal = HighlightGoal(inputs, basis, stops, behind, textRatio);
        var tile = FollowHighlight(goal, dt, calm);
        ApplyHighlight(ref ink, tile, behind, textRatio, ratio, Perceptual.Luminance(highlight));
        // Cascade's held words: the deep tile colour on a light wallpaper (tiles themselves stay as they are).
        if (dark > 0.0005 && ink.Highlight is { } tone && ink.Accent is { } accent)
            ink.Accent = Perceptual.ToColour(Perceptual.Mix(Perceptual.ToOkLab(new Double3(accent.R, accent.G, accent.B)), Perceptual.Deep(tone.Tile, deepBackdrop, ratio), dark));
        ink.Settled = ink.Settled && HighlightSettled(goal);
        _last = ink;
        return ink;
    }

    // ---- Dark lyrics (the Mac's next/tester-fixes 8a52096) ----

    /// <summary>Above this luminance behind the stage (before the automatic backing) the lyrics turn dark.</summary>
    public const double DarkAbove = 0.34;

    /// <summary>Below this they turn light again (hysteresis, so a wallpaper near the line doesn't flicker).</summary>
    public const double LightBelow = 0.26;

    /// <summary>How long a switch between light and dark lyrics takes, seconds.</summary>
    public const double DarkFade = 0.6;

    private double _dark, _darkTarget;
    private Follower _deepBackdrop;

    /// <summary>
    /// Light or dark lyrics this frame, from <see cref="LyricInkInputs.Unbacked"/> (null: the floating lyrics, on their
    /// own shade, stay light): the amount, the backdrop the deep colours are measured against, and whether it is moving.
    /// </summary>
    private (double Dark, double DeepBackdrop, bool Moving) DarkState(LyricInkInputs inputs, double dt, bool firstFrame)
    {
        if (inputs.Unbacked is not { } unbacked || !double.IsFinite(unbacked))
        {
            _darkTarget = 0;
        }
        else
        {
            if (unbacked > DarkAbove) _darkTarget = 1;
            else if (unbacked < LightBelow) _darkTarget = 0;
        }
        var behind = Math.Clamp(inputs.Unbacked ?? 0, 0, 1);
        var deep = Quantised(_deepBackdrop.Follow(behind, dt, 0.5, 0.6));
        if (firstFrame) _dark = _darkTarget;
        else if (_dark < _darkTarget) _dark = Math.Min(_darkTarget, _dark + dt / DarkFade);
        else if (_dark > _darkTarget) _dark = Math.Max(_darkTarget, _dark - dt / DarkFade);
        return (_dark, deep, _dark != _darkTarget || !IsSettled(_deepBackdrop, behind));
    }

    /// <summary>The highlight from its tile: the text on it, the held words' type, and where a fading tile's text turns back.</summary>
    private static void ApplyHighlight(ref LyricInk ink, OkLab tile, double backdrop, double textRatio, double ratio, double lyric)
    {
        var tone = LyricHighlight.Tone(tile, backdrop, textRatio, ratio);
        ink.Highlight = tone;
        ink.Tile = Perceptual.ToColour(tone.Tile);
        ink.OnTile = Perceptual.ToColour(tone.Text);
        ink.Accent = Perceptual.ToColour(tone.Held);
        ink.TileTextSwitch = LyricHighlight.TextSwitch(tone.Tile, tone.Text, backdrop, lyric);
    }

    /// <summary>What the highlight goal was worked out from, quantised.</summary>
    private sealed record HighlightKey(LyricHighlightMode Mode, int[] Basis, int[] Stops, Vector3[] Album, OkLab? Accent,
                                       OkLab? Custom, int Backdrop, double Ratio)
    {
        public bool Equals(HighlightKey? other) =>
            other is not null && Mode == other.Mode && Basis.AsSpan().SequenceEqual(other.Basis)
            && Stops.AsSpan().SequenceEqual(other.Stops) && Album.AsSpan().SequenceEqual(other.Album)
            && Accent == other.Accent && Custom == other.Custom && Backdrop == other.Backdrop && Ratio.Equals(other.Ratio);

        public override int GetHashCode() => HashCode.Combine(Mode, Basis.Length, Stops.Length, Album.Length, Backdrop, Ratio);
    }

    private static int[] HueKey(OkLab colour) =>
        colour.Chroma < 0.02
            ? [0, 0, (int)SwiftMath.Round(colour.L * 50)]
            : [(int)SwiftMath.Round(LyricHighlight.Degrees(colour.Hue) / 3) % 120, (int)SwiftMath.Round(colour.Chroma * 100),
               (int)SwiftMath.Round(colour.L * 50)];

    private static OkLab? Restored(ReadOnlySpan<int> values) =>
        values.Length == 3 ? OkLab.FromLch(values[2] / 50.0, values[1] / 100.0, values[0] * 3 * Math.PI / 180) : null;

    /// <summary>
    /// The tile the highlight is heading for. Worked out again only when what it depends on changes, to within steps
    /// too small to see: a drifting lyric colour moves it every frame.
    /// </summary>
    private OkLab HighlightGoal(LyricInkInputs inputs, OkLab? basis, OkLab[] stops, double backdrop, double ratio)
    {
        var complements = inputs.Highlight == LyricHighlightMode.Complement;
        var custom = inputs.Highlight == LyricHighlightMode.Custom;
        int[] stopKey = [];
        if (complements)
        {
            stopKey = new int[stops.Length * 3];
            for (var i = 0; i < stops.Length; i++) HueKey(stops[i]).CopyTo(stopKey, i * 3);
        }
        var key = new HighlightKey(inputs.Highlight, complements && basis is { } b ? HueKey(b) : [], stopKey,
                                   custom ? [] : _albumKey, custom ? null : inputs.Accent,
                                   custom ? inputs.HighlightCustom : null, (int)SwiftMath.Round(backdrop * 256), ratio);
        if (key.Equals(_highlightKey)) return GoalTile;
        // Worked out from the quantised values, so equal keys give equal tiles.
        var shade = key.Backdrop / 256.0;
        OkLab Album() => LyricHighlight.Album(_albumColours, shade, inputs.Accent, _backgroundHue, ratio);
        switch (inputs.Highlight)
        {
            case LyricHighlightMode.Custom:
                GoalTile = LyricHighlight.Custom(inputs.HighlightCustom ?? LyricHighlight.Coral, shade, ratio);
                break;
            case LyricHighlightMode.Album:
                GoalTile = Album();
                break;
            default:
            {
                var others = new List<OkLab>();
                for (var i = 0; i + 3 <= key.Stops.Length; i += 3)
                    if (Restored(key.Stops.AsSpan(i, 3)) is { } other) others.Add(other);
                OkLab? previous = _highlightKey?.Mode == LyricHighlightMode.Complement ? GoalTile : null;
                OkLab? complement = Restored(key.Basis) is { } lyric
                    ? LyricHighlight.Complement(lyric, shade, others, previous, _backgroundHue, ratio)
                    : null;
                GoalTile = complement ?? Album();
                break;
            }
        }
        _highlightKey = key;
        return GoalTile;
    }

    /// <summary>
    /// Quicker than the text colours: a tile changing hue changes little in lightness, which stays rate limited, and
    /// should not linger part way between two clean colours.
    /// </summary>
    private OkLab FollowHighlight(OkLab goal, double dt, bool calm)
    {
        double lTau = calm ? 0.7 : 0.25, abTau = calm ? 0.5 : 0.12;
        var l = _highlightChannels[0].Follow(goal.L, dt, lTau, LightnessRate);
        var a = _highlightChannels[1].Follow(goal.A, dt, abTau, ChromaRate);
        var b = _highlightChannels[2].Follow(goal.B, dt, abTau, ChromaRate);
        return new OkLab(Quantised(l), Quantised(a), Quantised(b));
    }

    private bool HighlightSettled(OkLab goal) =>
        IsSettled(_highlightChannels[0], goal.L) && IsSettled(_highlightChannels[1], goal.A) && IsSettled(_highlightChannels[2], goal.B);

    private bool AtWhite
    {
        get
        {
            for (var index = 0; index < 4; index++)
            {
                if (Math.Abs(_channels[index * 3].Value - 1) > 1e-4 || Math.Abs(_channels[index * 3 + 1].Value) > 1e-4
                    || Math.Abs(_channels[index * 3 + 2].Value) > 1e-4) return false;
            }
            return true;
        }
    }

    /// <summary>Whether every colour channel has arrived, to within a step too small to see.</summary>
    private bool SettledOn(InkStops targets)
    {
        for (var index = 0; index < 4; index++)
        {
            var stop = targets[index];
            if (!IsSettled(_channels[index * 3], stop.L) || !IsSettled(_channels[index * 3 + 1], stop.A)
                || !IsSettled(_channels[index * 3 + 2], stop.B)) return false;
        }
        return true;
    }

    private static bool IsSettled(Follower follower, double target) =>
        Math.Abs(follower.Value - target) < 1.0 / 1024 && Math.Abs(follower.Velocity) < 1e-3;

    private static double Quantised(double value) => SwiftMath.Round(value * 512) / 512;

    /// <summary>Cover colours, prepared once per palette rather than every frame.</summary>
    private void RefreshAlbum(IReadOnlyList<Vector3> album)
    {
        if (SameAlbum(album)) return;
        _albumKey = [.. album];
        var colours = new OkLab[album.Count];
        for (var i = 0; i < colours.Length; i++) colours[i] = Perceptual.ToOkLab(Double3.From(album[i]));
        _albumColours = colours;
        _backgroundHue = new AlbumColourHarmony(colours).BackgroundHue;
        // White, black and grey have no hue. A faint cover tint still supplies a hue worth keeping and can be
        // strengthened for type.
        var chromatic = colours.Where(c => c.Chroma >= 0.012).OrderByDescending(c => c.Chroma).ToList();
        if (chromatic.Count == 0)
        {
            _vivid = FallbackPastels.S2;
            _reactiveAccent = FallbackPastels.S3;
            _albumStops = FallbackPastels;
            _albumRawStops = FallbackPastels;
            return;
        }
        var strongest = chromatic[0];
        var backdropHue = _backgroundHue ?? strongest.Hue;
        double ContrastScore(OkLab colour) => colour.Chroma + 0.18 * LyricHighlight.HueDistance(colour.Hue, backdropHue) / Math.PI;
        var vivid = chromatic[0];
        foreach (var colour in chromatic) if (ContrastScore(vivid) < ContrastScore(colour)) vivid = colour;
        _vivid = vivid;
        // Keep distinct sampled hues. Start with the cover accent that best sets off its main colour so it survives
        // the stop limit.
        var picks = new List<OkLab> { vivid };
        foreach (var colour in chromatic)
        {
            var matches = picks.Exists(pick =>
            {
                var difference = Math.Abs(pick.Hue - colour.Hue);
                return Math.Min(difference, 2 * Math.PI - difference) < 0.28;
            });
            if (!matches) picks.Add(colour);
            if (picks.Count == 4) break;
        }
        _reactiveAccent = vivid;
        for (var i = 1; i < picks.Count; i++)
        {
            if (LyricHighlight.HueDistance(picks[i].Hue, vivid.Hue) < Math.PI * 0.60) { _reactiveAccent = picks[i]; break; }
        }
        var lyric = picks.Select(pick => LyricInk.Pastel(pick, pick.Hue)).ToList();
        if (lyric.Count == 1)
        {
            var tint = lyric[0];
            lyric =
            [
                OkLab.FromLch(Math.Min(0.91, tint.L + 0.07), tint.Chroma * 0.65, tint.Hue),
                tint,
                OkLab.FromLch(Math.Max(0.70, tint.L - 0.05), tint.Chroma * 1.18, tint.Hue),
                tint,
            ];
        }
        lyric = [.. lyric.OrderBy(c => c.Hue)];
        OkLab At(int index) => lyric.Count == 0 ? OkLab.White : lyric[index % lyric.Count];
        _albumStops = new InkStops(At(0), At(1), At(2), At(3));
        var raw = picks.OrderBy(c => c.Hue).ToList();
        OkLab Raw(int index) => raw.Count == 0 ? OkLab.White : raw[index % raw.Count];
        _albumRawStops = new InkStops(Raw(0), Raw(1), Raw(2), Raw(3));
    }

    private bool SameAlbum(IReadOnlyList<Vector3> album)
    {
        if (album.Count != _albumKey.Length) return false;
        for (var i = 0; i < album.Count; i++) if (album[i] != _albumKey[i]) return false;
        return true;
    }

    /// <summary>The colours each mode is heading for this frame.</summary>
    private (InkStops Stops, int Count) TargetStops(LyricInkInputs inputs, double dt)
    {
        switch (inputs.Mode)
        {
            case LyricColourMode.Standard:
                return (InkStops.AllWhite, 1);
            case LyricColourMode.AlbumDrift:
                // Keep the gradient phase in the same slot when a new cover has a different number of colours.
                return (inputs.Pastel ? _albumStops : _albumRawStops, 4);
            case LyricColourMode.SungSweep:
            {
                // The cover's four lyric colours, each made pastel then strengthened ×1.35 (the Mac's
                // next/tester-fixes, 15 Nov 2026). Not the custom gradient's colours: those always exist (there are
                // defaults), so they took over every song's sweep and it never followed the cover (the Mac's 1763cce).
                var album = inputs.Pastel ? _albumStops : _albumRawStops;
                OkLab Cover(int index) => inputs.Pastel ? LyricInk.ApplyingStrength(1.35, LyricInk.Pastel(album[index], _vivid.Hue)) : album[index];
                return (new InkStops(Cover(0), Cover(1), Cover(2), Cover(3)), 4);
            }
            case LyricColourMode.MusicReactive:
            {
                var calm = inputs.ReduceMotion;
                var loud = inputs.Audio is { } audio ? Math.Min(1, audio.Rms * 2.4 + audio.Bass * 0.6) : inputs.Vocal;
                var tilt = inputs.Audio is { } levels ? Math.Min(1, Math.Max(-1, (levels.Treble - levels.Bass) * 2.5)) : 0;
                var intensity = _level.Follow(calm ? 0.3 : Math.Min(1, Math.Max(0, loud)), dt, 0.3, 1.5);
                var shift = _balance.Follow(calm ? 0 : tilt, dt, 0.4, 1.5);
                double chroma = 0.12 + 0.09 * intensity, lightness = 0.77 + 0.09 * intensity;
                var emphasis = shift * 0.04;
                var first = OkLab.FromLch(lightness, chroma - emphasis, _vivid.Hue);
                var second = OkLab.FromLch(lightness, chroma * 0.9 + emphasis, _reactiveAccent.Hue);
                return (new InkStops(first, second, first, second), 2);
            }
            default:
            {
                if (inputs.Custom.Count == 0) return (inputs.Pastel ? _albumStops : _albumRawStops, 4);
                var count = Math.Min(4, inputs.Custom.Count);
                OkLab At(int index) => inputs.Pastel ? LyricInk.Pastel(inputs.Custom[index % count]) : inputs.Custom[index % count];
                return (new InkStops(At(0), At(1), At(2), At(3)), count);
            }
        }
    }
}

/// <summary>
/// Keeps a preview's colours on their own clock: settled at once when a setting changes, then drifting in real time
/// (the Mac's <c>LyricHighlightSwatchModel</c>). Also how a still palette is taken from the director.
/// </summary>
public sealed class LyricInkPreview
{
    private readonly LyricInkDirector _director = new();
    private LyricInkInputs? _key;
    private double _clock;
    private double? _lastNow;

    public LyricInkDirector Director => _director;

    public LyricInk Ink(LyricInkInputs inputs, double now)
    {
        var step = _lastNow is { } last ? Math.Min(0.25, Math.Max(0, now - last)) : 0;
        _lastNow = now;
        if (!SameSettings(inputs, _key))
        {
            // A setting changed: show where the colours settle, not the fade.
            _key = inputs;
            for (var i = 0; i < 24; i++)
            {
                _clock += 0.25;
                _director.Ink(inputs, _clock);
            }
        }
        _clock += Math.Max(step, 0.001);
        return _director.Ink(inputs, _clock);
    }

    /// <summary>The swatch's key: everything but the backdrop, audio, vocal level and accent.</summary>
    private static bool SameSettings(LyricInkInputs a, LyricInkInputs? b) =>
        b is not null && a.Mode == b.Mode && a.Speed.Equals(b.Speed) && a.Strength.Equals(b.Strength)
        && a.Custom.SequenceEqual(b.Custom) && a.Album.SequenceEqual(b.Album) && a.Highlight == b.Highlight
        && a.HighlightCustom == b.HighlightCustom && a.ReduceMotion == b.ReduceMotion && a.IncreaseContrast == b.IncreaseContrast;
}
