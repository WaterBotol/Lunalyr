// The welcome's recordings and its glass logo, in WinUI: a muted MediaPlayer shown by a MediaPlayerElement, made only
// while it may play (OnboardingClips.Plays, WelcomeGlass.ShouldPlay, SettingsLoader.ShouldShow) and disposed as soon as it
// may not. The Mac's AVPlayer views (OnboardingClip.swift, WelcomeGlassIntro.swift, SettingsLoader.swift, Lunalyr's own
// lines) re-made with Windows' player; nothing here is translated from Verso-attributed lines.
//
// No sound, ever: the files have no audio track, and every player is muted with its volume at 0 before a source is set.
// No media session either: a MediaPlayer joins Windows' media controls (the GSMTC sessions Lunalyr itself follows) unless
// its command manager is off, so it is switched off first, with the player's own transport controls.

using Lunalyr.Shell.Onboarding;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Lunalyr.App.Onboarding;

/// <summary>Windows' "Animation effects" (Settings › Accessibility › Visual effects), read each time, never changed.</summary>
internal static class WindowsMotion
{
    public static bool AnimationsEnabled()
    {
        try
        {
            return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>A muted, session-less player for one of the welcome's files.</summary>
internal sealed class MutedVideo : IDisposable
{
    private readonly MediaPlayer _player;
    private bool _disposed;

    /// <param name="path">The file, relative to Lunalyr.exe's folder.</param>
    /// <param name="loop">Loop it (the recordings) or play it once (the glass logo).</param>
    /// <param name="rate">Playback speed (the Settings loader plays the logo at 2×).</param>
    public MutedVideo(string path, bool loop, double rate = 1)
    {
        _player = new MediaPlayer();
        // Out of Windows' media controls before anything else: no session, no media keys.
        _player.CommandManager.IsEnabled = false;
        _player.SystemMediaTransportControls.IsEnabled = false;
        _player.IsMuted = true;
        _player.Volume = 0;
        _player.AutoPlay = false;
        _player.IsLoopingEnabled = loop;
        _player.AudioCategory = MediaPlayerAudioCategory.SoundEffects;
        _player.MediaEnded += (_, _) => Ended?.Invoke();
        _player.MediaFailed += (_, _) => Failed?.Invoke();
        _player.Source = MediaSource.CreateFromUri(new Uri(Path.Combine(AppContext.BaseDirectory, path)));
        _player.PlaybackSession.PlaybackRate = rate;
        Element = new MediaPlayerElement { AreTransportControlsEnabled = false, Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
        Element.SetMediaPlayer(_player);
        AutomationProperties.SetAccessibilityView(Element, AccessibilityView.Raw);
    }

    public MediaPlayerElement Element { get; }

    /// <summary>Where playback is, and its state (for the capture's log line).</summary>
    public string Describe()
    {
        var session = _player.PlaybackSession;
        return $"{session.PlaybackState} at {session.Position.TotalSeconds:0.00} s of {session.NaturalDuration.TotalSeconds:0.00} s";
    }

    /// <summary>The video played to its end (off the UI thread; looping players never say so).</summary>
    public event Action? Ended;

    /// <summary>The file couldn't be played (off the UI thread).</summary>
    public event Action? Failed;

    /// <summary>Whether the file is next to Lunalyr.exe.</summary>
    public static bool Exists(string path) => File.Exists(Path.Combine(AppContext.BaseDirectory, path));

    public void Play(double rate = 1)
    {
        if (_disposed) return;
        // Muted by construction (and the files have no audio track); checked again before playing.
        _player.IsMuted = true;
        _player.Volume = 0;
        _player.Play();
        if (rate != 1) _player.PlaybackSession.PlaybackRate = rate;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _player.Pause();
            Element.SetMediaPlayer(null);
            _player.Source = null;
        }
        finally
        {
            _player.Dispose();
        }
    }
}

/// <summary>
/// A recording in a rounded frame (the Mac's <c>OnboardingClipView</c>): the poster, and over it the playing video while
/// it may play, both framed as <see cref="OnboardingClips.Frame"/> says. Hidden from Narrator: the step's words say what
/// it shows.
/// </summary>
internal sealed class OnboardingClipView
{
    private readonly OnboardingClip _clip;
    private readonly Canvas _canvas = new();
    private readonly BoxRect _frame;
    private MutedVideo? _video;

    public OnboardingClipView(OnboardingClip clip, double width, double height, double cornerRadius)
    {
        _clip = clip;
        _frame = clip.Frame(width, height);
        var poster = new Image { Stretch = Stretch.Fill, Width = _frame.Width, Height = _frame.Height };
        try
        {
            poster.Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, clip.PosterPath())));
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            // No poster: the black frame shows.
        }
        Place(poster);
        _canvas.Children.Add(poster);
        _canvas.Width = width;
        _canvas.Height = height;
        _canvas.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, width, height) };
        View = new Border
        {
            Style = Settings.SettingsKit.Resource<Style>("OnboardingPicture"),
            Width = width, Height = height, CornerRadius = new CornerRadius(cornerRadius), Child = _canvas,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Black),
        };
        AutomationProperties.SetAccessibilityView(View, AccessibilityView.Raw);
    }

    public Border View { get; }

    public bool Playing => _video is not null;

    /// <summary>Plays (muted, looping) or holds the poster, as <paramref name="plays"/> says.</summary>
    public void Set(bool plays)
    {
        if (plays == Playing) return;
        if (!plays)
        {
            Stop();
            return;
        }
        if (!_clip.HasVideo() || !MutedVideo.Exists(_clip.VideoPath())) return;
        var video = new MutedVideo(_clip.VideoPath(), loop: true);
        video.Element.Width = _frame.Width;
        video.Element.Height = _frame.Height;
        Place(video.Element);
        _canvas.Children.Add(video.Element);
        video.Play();
        _video = video;
    }

    public void Stop()
    {
        if (_video is not { } video) return;
        _video = null;
        _canvas.Children.Remove(video.Element);
        video.Dispose();
    }

    private void Place(FrameworkElement element)
    {
        Canvas.SetLeft(element, _frame.X);
        Canvas.SetTop(element, _frame.Y);
    }
}
