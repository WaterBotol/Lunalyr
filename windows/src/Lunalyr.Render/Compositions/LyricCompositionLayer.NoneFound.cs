// "When none are found" (Settings › Timing › Without synced lyrics): what the stage shows once a song turns out to have
// no lyrics. The Mac draws this on lines that trace back to the squashed Verso import (LyricLayerView's missing scene),
// so it is re-implemented here from its behaviour: a short note that holds and then fades (timed by LyricStagePresence,
// which is Lunalyr's own and ported), the song's title large in the lyric face with the artist smaller and dimmer under
// it, centred and still, or nothing at all.

using System.Numerics;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Motion;
using Lunalyr.Render.Scene;
using Vortice.Direct2D1;

namespace Lunalyr.Render.Compositions;

internal abstract partial class LyricCompositionLayer
{
    private Ink? _trackTitle;
    private Ink? _trackArtist;
    private (string Title, string Artist)? _trackNameSet;
    private double _trackTitleHeight;
    private double _trackArtistHeight;

    /// <summary>A song with no lyrics to show, drawn as <see cref="LyricStyle.NoLyrics"/> asks.</summary>
    private void DrawNoneFound(ID2D1DeviceContext dc, SceneSnapshot scene, double wall)
    {
        switch (scene.Style.NoLyrics)
        {
            case NoLyricsDisplay.Nothing:
                // An empty stage: a notice shown again later eases in again.
                _statusText = null;
                break;
            case NoLyricsDisplay.TrackName when !string.IsNullOrWhiteSpace(scene.Title):
                _statusText = null;
                // The opening title staying for the song says it already (the owner, 15 Nov 2026).
                if (!Lunalyr.Core.Compositions.OpeningTitle.Stays(_openingSchedule)) DrawTrackName(dc, scene.Title!, scene.Artist ?? "");
                break;
            default:
                DrawStatus(dc, scene.Status!, wall, brief: true);
                break;
        }
    }

    /// <summary>The song's title, with its artist under it, centred on the stage and fitted to its width.</summary>
    private void DrawTrackName(ID2D1DeviceContext dc, string title, string artist)
    {
        title = title.Trim();
        artist = artist.Trim();
        if (_trackNameSet != (title, artist) || _trackTitle is null)
        {
            DropTrackName();
            _trackNameSet = (title, artist);
            var size = BaseSize * Metrics.UserScale;
            var shadow = new ShadowSpec(0.5f, Points(10) * 0.5f, Points(2));
            using (var layout = Kit!.Layout(title, Face.Regular, (float)(size * 0.5), TextKit.LocaleFor(title),
                                            ScriptDirection.IsRightToLeft(title), (float)Stage.Width))
            {
                _trackTitleHeight = layout.Metrics.Height;
                _trackTitle = Kit.Bake(layout, TextColour with { W = TextColour.W * 0.85f }, 1, shadow);
            }
            if (artist.Length > 0)
            {
                using var layout = Kit!.Layout(artist, Face.Quiet, (float)(size * 0.26), TextKit.LocaleFor(artist),
                                               ScriptDirection.IsRightToLeft(artist), (float)Stage.Width);
                _trackArtistHeight = layout.Metrics.Height;
                _trackArtist = Kit.Bake(layout, TextColour with { W = TextColour.W * 0.6f }, 1, shadow);
            }
            else
            {
                _trackArtistHeight = 0;
            }
        }

        var gap = _trackArtist is null ? 0 : BaseSize * 0.12;
        var total = _trackTitleHeight + gap + _trackArtistHeight;
        // A long title is set smaller rather than spilling off the stage.
        var fit = total > Stage.Height ? Stage.Height / total : 1;
        var top = -total / 2;
        var titleAt = Matrix3x2.CreateTranslation(0, (float)(top + _trackTitleHeight / 2)) * Matrix3x2.CreateScale((float)fit)
            * Matrix3x2.CreateTranslation(StageCentre);
        _trackTitle!.Draw(dc, titleAt, (float)fit, 1, 0, GlowSpec.None, 1);
        if (_trackArtist is { } artistInk)
        {
            var artistAt = Matrix3x2.CreateTranslation(0, (float)(top + _trackTitleHeight + gap + _trackArtistHeight / 2))
                * Matrix3x2.CreateScale((float)fit) * Matrix3x2.CreateTranslation(StageCentre);
            artistInk.Draw(dc, artistAt, (float)fit, 1, 0, GlowSpec.None, 1);
        }
    }

    private void DropTrackName()
    {
        _trackTitle?.Dispose();
        _trackTitle = null;
        _trackArtist?.Dispose();
        _trackArtist = null;
        _trackNameSet = null;
    }
}
