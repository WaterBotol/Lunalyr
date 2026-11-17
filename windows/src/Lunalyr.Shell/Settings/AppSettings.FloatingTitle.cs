// The floating lyrics' song title card in its own window, ported from the Mac's FloatingLyrics.swift (b11a1ab3;
// Lunalyr's own lines): whether it shows, its frames set by hand per display, and where it starts beside the lyrics'
// box. The title row it replaces (floatingTitleCentre, Edge and Scale) is carried over once as that start.

using System.Text.Json.Serialization;
using Lunalyr.Core.Floating;

namespace Lunalyr.Shell.Settings;

public sealed partial record AppSettings
{
    /// <summary>The song title card floats in a window of its own beside the lyrics' box on every floating display (Mac <c>custom.floating.titleCard</c>).</summary>
    public bool FloatingTitleCard { get; init; } = true;

    /// <summary>The card's frames set by hand, per display key, relative to that display (Mac <c>custom.floating.titleFrames</c>).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<double>> FloatingTitleFrames { get; init; } = new Dictionary<string, IReadOnlyList<double>>();

    /// <summary>Where the card starts on a display with no frame of its own: [centre, below, scale]; none for the standard start (Mac <c>custom.floating.titleStart</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<double>? FloatingTitleStart { get; init; }

    /// <summary>Where the card starts beside the lyrics' box.</summary>
    public FloatingTitleStart TitleStart() => Core.Floating.FloatingTitleStart.Decode(FloatingTitleStart) ?? Core.Floating.FloatingTitleStart.Standard;

    /// <summary>The card's frame set by hand on <paramref name="display"/>, relative to it, or null.</summary>
    public FloatingRect? TitleFrame(string display) =>
        (FloatingTitleFrames ?? new Dictionary<string, IReadOnlyList<double>>()).TryGetValue(display, out var values) ? FloatingFrame.Decode(values) : null;

    /// <summary>Keeps the card where it was dragged on <paramref name="display"/>, relative to it.</summary>
    public AppSettings WithTitleFrame(FloatingRect relative, string display) => this with
    {
        FloatingTitleFrames = new Dictionary<string, IReadOnlyList<double>>(FloatingTitleFrames ?? new Dictionary<string, IReadOnlyList<double>>())
        {
            [display] = FloatingFrame.Encode(relative),
        },
    };

    /// <summary>Puts the card back beside the lyrics on every display (Reset Title Position).</summary>
    public AppSettings WithTitleFramesReset() => this with { FloatingTitleFrames = new Dictionary<string, IReadOnlyList<double>>(), FloatingTitleStart = null };

    /// <summary>The card was placed by hand somewhere, or starts away from its standard place.</summary>
    [JsonIgnore]
    public bool TitleIsPlacedByHand => (FloatingTitleFrames?.Count ?? 0) > 0 || !TitleStart().IsStandard;

    /// <summary>
    /// The title used to be a row inside the lyrics' window, placed along its top or bottom: once, that place becomes where
    /// the card starts beside the box, and the row goes back to its standard place (Windows keeps every key, so the row's
    /// standard place is its "removed"). Part of normalising, so it runs as the settings load.
    /// </summary>
    private AppSettings WithTitleRowCarriedOver()
    {
        var row = TitleRow();
        if (row.IsStandard) return this;
        var start = FloatingTitleStart is null ? Core.Floating.FloatingTitleStart.FromRow(row) : TitleStart();
        return WithTitleRow(Core.Compositions.OpeningTitleRow.Standard) with
        {
            FloatingTitleStart = start.IsStandard ? null : start.Encoded,
        };
    }
}
