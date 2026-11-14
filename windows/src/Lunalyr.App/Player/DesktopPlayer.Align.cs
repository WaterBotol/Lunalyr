using Lunalyr.App.MusicVideo;
using Lunalyr.Core.MusicVideo;
using Lunalyr.Core.Player;

namespace Lunalyr.App.Player;

// Align video on the card (the Mac's VideoAlignRow.swift: LiveAlignButton, VideoAlignEvents, and PlayerKeyboard's align
// keys; Lunalyr's own code). While the wallpaper is a music video, the film button beside search turns the seek row into
// the video's own bar: dragging it moves only the video (the song plays on) until the picture matches the music, and the
// release keeps that offset for the video. ← → move it a tenth (Shift: a second), Enter keeps it, Esc cancels. The rules
// are the controller's (MusicVideoController's align members, ported with their tests); this only draws and forwards.
internal sealed unsafe partial class DesktopPlayer
{
    private bool _alignDragging;

    private static MusicVideoController? Video => MusicVideoUi.Controller;

    /// <summary><c>--player-debug align</c>: align mode as it looks mid-song, for captures (nothing is changed).</summary>
    private bool DebugAlign => _debug.Contains("align");

    /// <summary>The film button and align mode, from the controller, each frame.</summary>
    private void UpdateAlign(double now)
    {
        var m = _model;
        if (DebugAlign)
        {
            m.MusicVideoOffered = true;
            m.AlignReason = null;
            m.Align = new MusicVideoAlignDisplay(215, 200, 3.4, Math.Max(0, Elapsed(now)), MusicVideoOffsetOrigin.Manual);
            m.TitleAlpha = Math.Min(m.TitleAlpha, (float)PlayerCard.TitleOpacity(true));
            return;
        }
        var video = Video;
        var aligning = video?.IsAligning ?? false;
        var offered = video is not null && m.Pane == PlayerPane.Song && _settings().BackgroundMode == "musicVideo"
            && video.Status is not MusicVideoStatus.Off
            && (aligning || m.RowKind is ScrubRowKind.Seekable or ScrubRowKind.ReadOnly or ScrubRowKind.ElapsedOnly);
        var reason = offered && !aligning ? video!.AlignAvailability.Reason : null;
        var display = offered && aligning ? video!.AlignDisplay() : null;
        if (offered != m.MusicVideoOffered || reason != m.AlignReason || display != m.Align) Invalidate();
        m.MusicVideoOffered = offered;
        m.AlignReason = reason;
        m.Align = display;
        // The offset's capsule sits over the song: it steps aside, as for the seek bar's capsules.
        if (display is not null) m.TitleAlpha = Math.Min(m.TitleAlpha, (float)PlayerCard.TitleOpacity(true));
    }

    /// <summary>Align mode keeps the card drawing (the knob moves with the song) and the card up.</summary>
    private bool Aligning => _model.Align is not null && !DebugAlign || (Video?.IsAligning ?? false);

    /// <summary>The align parts under the pointer; null when align has nothing there (the card's own parts decide).</summary>
    private CardPart? AlignPartAt(float x, float y)
    {
        var m = _model;
        if (m.Pane != PlayerPane.Song) return null;
        // While aligning, Done ends it (the film button would sit on Cancel).
        if (m.MusicVideoOffered && m.Align is null && CardLayout.AlignButtonRect.Contains(x, y)) return CardPart.AlignButton;
        if (m.Align is not { } display) return null;
        if (CardLayout.BarHitRect.Contains(x, y)) return CardPart.AlignBar;
        if (display.Origin == MusicVideoOffsetOrigin.Manual && CardLayout.AlignResetRect.Contains(x, y)) return CardPart.AlignReset;
        if (CardLayout.AlignCancelRect.Contains(x, y)) return CardPart.AlignCancel;
        if (CardLayout.AlignDoneRect.Contains(x, y)) return CardPart.AlignDone;
        // The transport and the times give way to the row: nothing else is pressed there.
        if (x >= CardLayout.ColumnLeft && y >= CardLayout.SeekRowTop - CardLayout.BarSlop) return CardPart.None;
        return null;
    }

    /// <summary>A press on the video's bar.</summary>
    private void AlignPress(float x, float y)
    {
        if (Video is not { } video || DebugAlign) return;
        var (bx, by) = BarLocal(x, y);
        video.AlignMouseDown(bx, by, CardLayout.ColumnWidth, fine: Alt);
        _alignDragging = true;
        _interaction.Dragging = true;
    }

    /// <summary>The pointer moved with the bar pressed: true when it was the bar's.</summary>
    private bool AlignDragMove(float x, float y)
    {
        if (!_alignDragging || Video is not { } video) return false;
        var (bx, by) = BarLocal(x, y);
        video.AlignMouseDragged(bx, by, fine: Alt);
        Invalidate();
        StartFrames(16);
        return true;
    }

    /// <summary>The release keeps the offset under the hand; true when it was the bar's press.</summary>
    private bool AlignRelease(float x, float y)
    {
        if (!_alignDragging) return false;
        _alignDragging = false;
        _interaction.Dragging = false;
        _interaction.NoteClick(Now());
        if (Video is { } video)
        {
            var (bx, by) = BarLocal(x, y);
            video.AlignMouseUp(bx, by, fine: Alt);
        }
        Invalidate();
        return true;
    }

    /// <summary>A click (or Narrator's Invoke) on the film button or the align row's buttons.</summary>
    private void ClickAlign(CardPart part)
    {
        if (Video is not { } video || DebugAlign) return;
        switch (part)
        {
            case CardPart.AlignButton:
                if (video.IsAligning) video.AlignDone();
                else if (video.AlignAvailability.Reason is { } reason) ShowNote(reason);
                else if (!video.BeginAlign() && video.AlignAvailability.Reason is { } later) ShowNote(later);
                else _automation?.Announce("Drag the video’s bar until the picture matches the music.");
                break;
            case CardPart.AlignReset: video.AlignReset(); break;
            case CardPart.AlignCancel: video.AlignCancel(); break;
            case CardPart.AlignDone: video.AlignDone(); break;
        }
        Invalidate();
        StartFrames(16);
    }

    /// <summary>The align keys while the video is being lined up; true when the key was align's.</summary>
    private bool AlignKey(ushort vk, bool ctrl, bool alt, bool system)
    {
        if (Video is not { } video || !video.IsAligning || _scrub.IsDragging) return false;
        if (vk is PlayerNative.VK_CONTROL or PlayerNative.VK_MENU or PlayerNative.VK_SHIFT)
        {
            if (_alignDragging) video.AlignModifiersChanged(fine: alt);
            return false;
        }
        var shift = PlayerNative.KeyDown(PlayerNative.VK_SHIFT);
        if (MusicVideoAlignKey.Action(vk, shift, alt, ctrl || system, editingText: false) is not { } key) return false;
        switch (key.Kind)
        {
            case MusicVideoAlignKey.KeyKind.Nudge: video.AlignNudge(key.Delta); break;
            case MusicVideoAlignKey.KeyKind.Done: video.AlignDone(); break;
            case MusicVideoAlignKey.KeyKind.Cancel:
                if (_alignDragging)
                {
                    _alignDragging = false;
                    _interaction.Dragging = false;
                }
                video.AlignCancel();
                break;
        }
        Invalidate();
        StartFrames(16);
        return true;
    }

    /// <summary>The align parts in Tab and Narrator order (after the song's row).</summary>
    private void AddAlignFocus(List<CardPart> order)
    {
        var m = _model;
        if (m.Pane != PlayerPane.Song) return;
        if (m.Align is { } display)
        {
            order.Remove(CardPart.Previous);
            order.Remove(CardPart.PlayPause);
            order.Remove(CardPart.Next);
            order.Remove(CardPart.SeekBar);
            order.Remove(CardPart.TimeLabel);
            order.Add(CardPart.AlignBar);
            if (display.Origin == MusicVideoOffsetOrigin.Manual) order.Add(CardPart.AlignReset);
            order.Add(CardPart.AlignCancel);
            order.Add(CardPart.AlignDone);
        }
        else if (m.MusicVideoOffered) order.Add(CardPart.AlignButton);
    }

    /// <summary>What Narrator reads for an align part, or null for another part.</summary>
    internal static (string Name, string Help, int ControlType, string Status)? DescribeAlign(CardModel m, CardPart part)
    {
        const int Button = 50000;
        return part switch
        {
            CardPart.AlignButton => m.Align is not null
                ? ("Done aligning video", "Keeps the video’s timing", Button, "")
                : ("Align video", m.AlignReason ?? "Turns the seek bar into the video’s own bar", Button, m.AlignReason is null ? "" : "Unavailable"),
            CardPart.AlignBar => ("Music video position", "Left and right arrows move the video a tenth of a second, with Shift a second. Enter keeps it, Esc cancels",
                Button, m.Align is { } d ? MusicVideoAlignBar.Spoken(d.Offset) : ""),
            CardPart.AlignReset => ("Reset", "Forget your timing for this video and go back to the estimate", Button, ""),
            CardPart.AlignCancel => ("Cancel", "Put the video’s timing back as it was (Esc)", Button, ""),
            CardPart.AlignDone => ("Done", "Keep this timing for the video (Enter)", Button, ""),
            _ => null,
        };
    }
}
