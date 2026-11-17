namespace Lunalyr.Render.Pacing;

/// <summary>
/// A surface's background crossfade, timed from timestamps (ported from the Mac's <c>BackgroundCrossfade</c>).
/// Times are seconds on the engine's clock; <see cref="double.NegativeInfinity"/> means "long ago" (a change while
/// paused shows at once) and <see cref="double.PositiveInfinity"/> "not yet".
/// </summary>
/// <remarks>
/// The layers form a short stack, bottom first. Each fades in over the ones beneath it, which stay fully drawn until
/// something covers them, so a crossfade between two opaque layers never dims the screen at its midpoint. A change
/// that arrives mid-fade stacks on top: the layer fading in carries on from where it was, and the one beneath does
/// not vanish. A layer that reaches full opacity hides everything beneath it, and those are let go. A transparent
/// layer can't cover what is beneath it, so the layers under it fade out as it fades in. A layer with nothing to draw
/// yet holds its fade at the start, but only until it is first ready.
/// </remarks>
public sealed class LayerCrossfade<TLayer> where TLayer : notnull
{
    public readonly record struct Entry(TLayer Layer, double StartedAt, bool IsReady, bool FadesIn);

    /// <summary>A layer as drawn at a moment.</summary>
    public readonly record struct Drawn(TLayer Layer, double Opacity);

    /// <summary>The most layers drawn at once. A fourth change in quick succession lets go of the one showing least.</summary>
    public const int MaximumLayers = 3;

    /// <summary>A layer replaced before it had faded in this far was never really seen.</summary>
    public const double UnseenFade = 0.02;

    private readonly List<Entry> _entries;

    /// <summary>A surface's first layer, shown without a fade.</summary>
    public LayerCrossfade(TLayer layer, bool ready = true) =>
        _entries = [new Entry(layer, double.NegativeInfinity, ready, false)];

    /// <summary>A surface's first layer, fading in from the window beneath (black) from <paramref name="at"/>, or from when it is first ready.</summary>
    public LayerCrossfade(TLayer layer, bool ready, double fadingInAt) =>
        _entries = [new Entry(layer, fadingInAt, ready, true)];

    /// <summary>Bottom first; the last is <see cref="Current"/>.</summary>
    public IReadOnlyList<Entry> Entries => _entries;

    public TLayer Current => _entries[^1].Layer;
    public TLayer? Previous => _entries.Count > 1 ? _entries[^2].Layer : default;
    public double ChangedAt => _entries[^1].StartedAt;
    public bool IsReady => _entries[^1].IsReady;
    public bool IsHeld => !IsReady;

    /// <summary>Moves to <paramref name="next"/>. A layer that never had anything to draw, or was replaced before it showed, was never seen, so the fade carries on from what is beneath.</summary>
    public void Change(TLayer next, bool ready, double at, double duration)
    {
        Prune(at, duration);
        var top = _entries[^1];
        bool unseen = top.FadesIn && Fade(top, at, duration) < UnseenFade;
        // A first layer shown at once stays as the base even before it is ready: it is what the surface has.
        if (unseen || (!top.IsReady && _entries.Count > 1)) _entries.RemoveAt(_entries.Count - 1);
        // Back to the layer held under one that never showed (a video's stand-in held over the same video): nothing to
        // fade (the Mac's 5857455).
        if (_entries.Count > 0 && EqualityComparer<TLayer>.Default.Equals(_entries[^1].Layer, next)) return;
        while (_entries.Count >= MaximumLayers)
        {
            var weights = VisibleWeights(at, duration);
            int least = 0;
            for (int i = 1; i < weights.Length; i++) if (weights[i] < weights[least]) least = i;
            _entries.RemoveAt(least);
        }
        _entries.Add(new Entry(next, at, ready, true));
    }

    /// <summary><see cref="Current"/> has something to draw: the first time, its fade starts now. Afterwards nothing changes.</summary>
    public void BecameReady(double at)
    {
        if (_entries[^1].IsReady) return;
        _entries[^1] = _entries[^1] with { IsReady = true, StartedAt = at };
    }

    /// <summary>How far <see cref="Current"/> has faded in, 0 to 1.</summary>
    public double FadeOfCurrent(double at, double duration) => Fade(_entries[^1], at, duration);

    /// <summary>When the last fade still running ends; null when nothing fades.</summary>
    public double? FadesEnd(double duration)
    {
        double? end = null;
        foreach (var e in _entries)
            if (e.FadesIn && e.IsReady) end = Math.Max(end ?? double.NegativeInfinity, e.StartedAt + duration);
        return end;
    }

    /// <summary>
    /// The layers drawn at <paramref name="at"/>, bottom first, with the opacity each is drawn at.
    /// <see cref="Current"/> is always included; a layer something covers is not.
    /// </summary>
    public List<Drawn> Layers(double at, double duration, Func<TLayer, bool>? isOpaque = null)
    {
        var drawn = new List<Drawn>(_entries.Count);
        Layers(at, duration, drawn, isOpaque);
        return drawn;
    }

    /// <summary>As <see cref="Layers(double, double, Func{TLayer, bool}?)"/>, into a list the caller reuses (no allocation per frame).</summary>
    public void Layers(double at, double duration, List<Drawn> drawn, Func<TLayer, bool>? isOpaque = null)
    {
        drawn.Clear();
        double through = 1; // How much of what lies beneath shows through the transparent layers above.
        for (int index = _entries.Count - 1; index >= 0; index--)
        {
            var entry = _entries[index];
            double own = Fade(entry, at, duration);
            double opacity = own * through;
            if (opacity > 0 || index == _entries.Count - 1) drawn.Add(new Drawn(entry.Layer, opacity));
            if (isOpaque?.Invoke(entry.Layer) ?? true)
            {
                if (own >= 1) break;
            }
            else
            {
                through *= 1 - own;
                if (through <= 0) break;
            }
        }
        drawn.Reverse();
    }

    /// <summary>
    /// Lets go of the layers a fully shown layer covers, without waiting for the next change (Windows addition: the
    /// engine releases their GPU resources once a fade has finished).
    /// </summary>
    public void Settle(double at, double duration) => Prune(at, duration);

    /// <summary>How much of each entry reaches the screen, treating every layer as opaque.</summary>
    private double[] VisibleWeights(double at, double duration)
    {
        var weights = new double[_entries.Count];
        double covered = 1;
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            double own = Fade(_entries[i], at, duration);
            weights[i] = own * covered;
            covered *= 1 - own;
        }
        return weights;
    }

    /// <summary>Lets go of the layers a fully shown layer covers.</summary>
    private void Prune(double at, double duration)
    {
        int covering = -1;
        for (int i = _entries.Count - 1; i >= 0; i--)
            if (Fade(_entries[i], at, duration) >= 1) { covering = i; break; }
        if (covering > 0) _entries.RemoveRange(0, covering);
    }

    private static double Fade(in Entry entry, double at, double duration)
    {
        if (!entry.FadesIn) return 1;
        if (!entry.IsReady) return 0;
        double t = (at - entry.StartedAt) / duration;
        return Smoothstep(double.IsNaN(t) ? 0 : t);
    }

    internal static double Smoothstep(double x)
    {
        double c = Math.Clamp(x, 0, 1);
        return c * c * (3 - 2 * c);
    }
}

/// <summary>
/// Holds the last song's background choice across a song change until the new song's inputs have settled (or
/// <see cref="SettleLimit"/> passes), then takes the new choice once and keeps it for the rest of that recording.
/// While nothing plays the last choice stays. Ported from the Mac's <c>BackgroundChoiceHold</c>.
/// </summary>
public sealed class BackgroundChoiceHold<TTrack, TChoice>(TChoice choice)
{
    /// <summary>The longest a song change waits for its cover and lyrics, in seconds.</summary>
    public const double SettleLimit = 2.5;

    public TChoice Choice { get; private set; } = choice;
    public TTrack? Track { get; private set; }
    public TTrack? Waiting { get; private set; }
    public double? WaitingSince { get; private set; }

    /// <summary>When the waiting song is taken whatever its inputs.</summary>
    public double? Deadline => WaitingSince + SettleLimit;

    /// <summary>
    /// Follows the playing <paramref name="track"/>, whose choice would be <paramref name="next"/> now;
    /// <paramref name="settled"/> says its cover and lyrics are in. True when the choice was taken for a new
    /// recording (the moment for its single crossfade).
    /// </summary>
    public bool Update(TTrack? track, TChoice next, bool settled, double now)
    {
        var comparer = EqualityComparer<TTrack>.Default;
        if (track is null || (Track is not null && comparer.Equals(track, Track)))
        {
            Waiting = default;
            WaitingSince = null;
            return false;
        }
        if (Waiting is null || !comparer.Equals(Waiting, track) || WaitingSince is null)
        {
            Waiting = track;
            WaitingSince = now;
        }
        if (!(settled || now >= (Deadline ?? now))) return false;
        Choice = next;
        Track = track;
        Waiting = default;
        WaitingSince = null;
        return true;
    }
}
