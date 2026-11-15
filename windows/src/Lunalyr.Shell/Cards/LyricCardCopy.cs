// The lyric card's words, wherever it is offered (the card window, Settings › General, the tray flyout): the Mac's
// LyricCardPopoutView, LyricExtrasInspector's Share section and MenuBarWallpaperControls' tile (Lunalyr's own lines).

using Lunalyr.Core.Cards;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Cards;

public static class LyricCardCopy
{
    public const string WindowTitle = "Lyric card";

    /// <summary>The action, wherever it is offered (the Mac's <c>LunalyrExtras.Titles.copyCard</c>).</summary>
    public const string OpenTitle = "Open lyric card…";

    public const string TileTitle = "Lyric card";
    public const string TileHelpPlaying = "Opens the lyric card window: pick lines, a shape and a format to share.";
    public const string TileHelpIdle = "Available while a song plays";

    public const string RowTitle = "Lyric card";
    public const string RowCaption = "Pick lines, a shape and a format for a shareable card.";
    public const string RowMore = "Pick any lines from the song, the card’s shape and whether to make an MP4, GIF or PNG. Synced lyrics play in order.";
    public const string NeedsSong = "A card can be made while a song is playing.";
    public const string LayoutTitle = "Card layout";
    public const string FollowWallpaper = "Follow wallpaper";

    public const string NothingPlaying = "Nothing playing";
    public const string NoLyrics = "No lyrics found for this song.";
    public const string PlayASong = "Play a song to make a lyric card.";
    public const string PickLines = "Pick lines to preview";
    public const string WholeSong = "Whole song";

    public const string Background = "Background";
    public const string AlbumColours = "Album colours";
    public const string BackgroundPicked = "Plays behind the lyrics and loops. PNG uses its first frame.";
    public const string BackgroundHint = "Pick or drop a picture, GIF or video.";
    public const string ClipLengthTitle = "Clip length (MP4)";
    public const string MaxSixty = "Max 60 s";
    public const string SoundTitle = "Sound (MP4 only)";
    public const string SoundCaption = "Mix as many as you like: the song, a background video’s sound, and audio files.";
    public const string TheSong = "The song";
    public const string SongLevel = "Song level";
    public const string DuckSong = "Duck the song under the video’s sound";
    public const string BackgroundVideo = "Background video";
    public const string AddSound = "Add audio or video sound…";
    public const string DropSound = "You can also drop audio files on the preview.";
    public const string GifHasNoSound = "A GIF can’t carry sound, so make an MP4 when you want it.";
    public const string TextSizeTitle = "Text size";
    public const string TextSizeCaption = "Applies to every layout.";
    public const string ShapeTitle = "Shape";
    public const string MakeTitle = "Make";
    public const string Save = "Save…";
    public const string Copy = "Copy";
    public const string Cancel = "Cancel";
    public const string Details = "Details";
    public const string ShowInExplorer = "Show in Explorer";

    /// <summary>
    /// How the song's sound is got on Windows (the Mac's caption, for Windows' way: Lunalyr never plays or moves the
    /// player, so the person plays the lines and Lunalyr records them as they play).
    /// </summary>
    public const string SongCaption =
        "Records the selected lines from what the PC plays while you play them in your music app: start the song a moment before them. "
        + "Lunalyr never plays, pauses or moves the song itself, and anything else the PC plays while it records is in the recording too.";

    /// <summary>Why the song can't be recorded in this run (<c>--demo</c>, <c>--no-playback</c>).</summary>
    public static string SongOff(string reason) => $"Recording the song is off for this run ({reason}).";
    public const string GetSong = "Get the song’s sound";
    public const string Redo = "Redo";
    public const string NotFetched = "Not fetched for these lines yet";
    public const string Ready = "Ready for these lines";

    /// <summary>The card's layout choices: "Follow wallpaper" (""), then every layout.</summary>
    public static IReadOnlyList<SettingsChoice> Layouts { get; } =
        [new("", FollowWallpaper), .. SettingsCopy.Layouts.Where(choice => choice.Value != AppSettings.AllLayoutsKey)];

    /// <summary>The PNG's limit, said when more lines are picked.</summary>
    public static string StillLimit => $"The PNG shows the first {LyricCardLayout.StillLineLimit} lines. MP4 and GIF play them all.";

    public static string LinesPicked(int picked, int total) => $"{picked} of {total} lines";

    public static string SecondsLeft(int seconds) => $"{seconds} s left";
}
