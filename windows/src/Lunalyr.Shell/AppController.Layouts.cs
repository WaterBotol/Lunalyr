using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell;

// "Match the music": with the layout setting on "automatic", each song gets a composition once, when its lyrics (or
// the verdict that there are none) arrive: the next in the rotation that suits the song's character, or with the
// rotation off the layout its character matches (docs/compositions/matching.md). A song keeps its layout for as long
// as the app runs -- a second lookup or a late cover never changes it mid-song -- and only compositions the renderer
// has registered are chosen, so new ones join as they are added.
public sealed partial class AppController
{
    /// <summary>The <c>lyricComposition</c> value for "Match the music".</summary>
    public const string AutomaticComposition = "automatic";

    private const int RememberedLayouts = 512;
    private static readonly string[] EveryBuildHas = ["editorial", "spoken"];

    private LayoutRotation? _rotation;
    private readonly Dictionary<string, string> _layouts = new(StringComparer.Ordinal);
    private readonly Queue<string> _layoutOrder = new();
    private string? _layout;
    private string? _layoutTrack;

    /// <summary>The composition "Match the music" chose for the song on screen, or null.</summary>
    public string? MatchedComposition
    {
        get { lock (_gate) return _layout; }
    }

    // Must hold _gate.
    private LayoutRotation RotationLocked() =>
        _rotation ??= new LayoutRotation(_settings.RotateLayoutsCursor, _settings.RotateLayoutCounts);

    /// <summary>The composition to draw for <paramref name="settings"/>: the chosen one, or the song's match.</summary>
    private string CompositionFor(AppSettings settings)
    {
        lock (_gate)
            return settings.LyricComposition == AutomaticComposition ? _layout ?? "editorial" : settings.LyricComposition;
    }

    /// <summary>The compositions the renderer has (the ones every build has when it cannot say).</summary>
    private IReadOnlyCollection<string> AvailableCompositions()
    {
        var ids = _renderer.LayerIds;
        if (ids.Count == 0) return EveryBuildHas;
        List<string> found = [.. AppSettings.Compositions.Where(id => id != AutomaticComposition && ids.Contains(id))];
        return found.Count == 0 ? ["editorial"] : found;
    }

    /// <summary>
    /// Picks the composition for the song on screen, once per song, from what is known of it now: its lyrics
    /// (<paramref name="lyrics"/>, null when there are none) and its cover's colours if they have been read.
    /// </summary>
    /// <param name="onlyWhenSettled">Only when the song's lyrics are shown or its lookup has ended: a choice made mid-lookup
    /// would know nothing of the song and would stick.</param>
    private void ChooseLayout(LyricResult? lyrics = null, bool onlyWhenSettled = false)
    {
        string pick, background, title;
        int? cursor = null;
        var available = AvailableCompositions();
        lock (_gate)
        {
            if (_disposed || _settings.LyricComposition != AutomaticComposition || _lookupTrack is not { } track) return;
            if (onlyWhenSettled && _shown is null && _lookupCts is not null) return;
            lyrics ??= _shown;
            var key = track.TrackKey;
            if (key == _layoutTrack && _layout is not null) return;
            if (!_layouts.TryGetValue(key, out var remembered) || !available.Contains(remembered))
            {
                var cover = _palettes.TryGetValue(key, out var known) ? known.Palette.ArtworkStops : [];
                var character = TrackCharacter.Of((lyrics as LyricResult.Synced)?.Lines, cover, track.Title, track.Artist);
                if (_settings.RotateLayouts)
                {
                    var rotation = RotationLocked();
                    var before = rotation.Cursor;
                    remembered = rotation.Layout(key, character, available);
                    if (rotation.Cursor != before) cursor = rotation.Cursor;
                }
                else
                {
                    remembered = LayoutMatch.For(character, available);
                }
                _layouts[key] = remembered;
                _layoutOrder.Enqueue(key);
                while (_layoutOrder.Count > RememberedLayouts) _layouts.Remove(_layoutOrder.Dequeue());
            }
            if (key == _layoutTrack && remembered == _layout) return;
            pick = remembered;
            _layout = pick;
            _layoutTrack = key;
            background = _settings.BackgroundMode;
            title = track.Title;
        }

        _app.Info($"Match the music: \"{title}\" takes the {pick} layout.");
        _renderer.SetLayers(background, pick);
        if (cursor is { } moved) _options.LayoutCursorChanged?.Invoke(moved);
    }

    /// <summary>The rotation's counts or the layout setting changed.</summary>
    private void ApplyLayoutSettings(AppSettings old, AppSettings settings)
    {
        lock (_gate)
        {
            if (!SameCounts(old.RotateLayoutCounts, settings.RotateLayoutCounts)) _rotation?.SetCounts(settings.RotateLayoutCounts);
            // Turned to "Match the music" (or its rotation switched): the song on screen is matched afresh.
            if (old.LyricComposition != settings.LyricComposition || old.RotateLayouts != settings.RotateLayouts)
            {
                _layoutTrack = null;
                _layout = null;
                _layouts.Clear();
                _layoutOrder.Clear();
            }
        }
    }

    private static bool SameCounts(IReadOnlyDictionary<string, int> a, IReadOnlyDictionary<string, int> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && value == pair.Value);
}
