using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Lunalyr.Core.Player;

namespace Lunalyr.App.Player;

// UI Automation for the card, so Narrator (and every other screen reader and automation client) can read and use it:
// the card is a pane of buttons (the cover, previous, play/pause, next, the time label) and a slider (the seek bar,
// with the Mac's VoiceOver value "1:23 of 3:33, line: …"). Source-generated COM (no reflection, Native AOT safe).
// UIA calls in on its own threads: they read an immutable snapshot, and anything they do is posted to the UI thread.

[GeneratedComInterface, Guid("d6dd68d1-86fd-4332-8666-9abedea2d24c")]
internal partial interface IRawElementProviderSimple
{
    int get_ProviderOptions();

    nint GetPatternProvider(int patternId);

    UiaVariant GetPropertyValue(int propertyId);

    nint get_HostRawElementProvider();
}

[GeneratedComInterface, Guid("f7063da8-8359-439c-9297-bbc5299a7d87")]
internal partial interface IRawElementProviderFragment
{
    nint Navigate(int direction);

    nint GetRuntimeId();

    UiaRect get_BoundingRectangle();

    nint GetEmbeddedFragmentRoots();

    void SetFocus();

    nint get_FragmentRoot();
}

[GeneratedComInterface, Guid("620ce2a5-ab8f-40a9-86cb-de3c75599b58")]
internal partial interface IRawElementProviderFragmentRoot
{
    nint ElementProviderFromPoint(double x, double y);

    nint GetFocus();
}

[GeneratedComInterface, Guid("54fcb24b-e18e-47a2-b4d3-eccbe77599a2")]
internal partial interface IInvokeProvider
{
    void Invoke();
}

[GeneratedComInterface, Guid("36dc7aef-33e6-4691-afe1-2be7274b3d33")]
internal partial interface IRangeValueProvider
{
    void SetValue(double value);

    double get_Value();

    int get_IsReadOnly();

    double get_Maximum();

    double get_Minimum();

    double get_LargeChange();

    double get_SmallChange();
}

[GeneratedComInterface, Guid("c7935180-6fb3-4201-b174-7df73adbf64a")]
internal partial interface IValueProvider
{
    void SetValue([MarshalAs(UnmanagedType.LPWStr)] string value);

    [return: MarshalAs(UnmanagedType.BStr)]
    string get_Value();

    int get_IsReadOnly();
}

[StructLayout(LayoutKind.Sequential)]
internal struct UiaRect
{
    public double Left, Top, Width, Height;
}

/// <summary>
/// A COM VARIANT (24 bytes on x64), blittable so the source generators pass it as is. Only the kinds UIA properties
/// need: empty, a 32-bit integer, a bool and a BSTR (which the caller frees, as an out VARIANT's owner does).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct UiaVariant
{
    public ushort Vt;
    public ushort Reserved1, Reserved2, Reserved3;
    public long Data;
    public long Data2;

    public static UiaVariant Of(int value) => new() { Vt = 3, Data = value }; // VT_I4

    public static UiaVariant Of(bool value) => new() { Vt = 11, Data = value ? 0xFFFF : 0 }; // VT_BOOL (VARIANT_TRUE)

    public static UiaVariant Of(string value) => new() { Vt = 8, Data = Marshal.StringToBSTR(value) }; // VT_BSTR
}

/// <summary>What the providers read: replaced whole on the UI thread, read on UIA's threads.</summary>
internal sealed record AutomationSnapshot(
    bool Shown,
    PlayerNative.RECT Card,
    double Scale,
    string Song,
    string CoverName,
    bool Playing,
    bool HasSong,
    bool Seekable,
    double Elapsed,
    double Duration,
    string PositionText,
    string TimeLabelName,
    CardPart Focus);

internal sealed unsafe partial class PlayerAutomation : IDisposable
{
    private const int UiaRootObjectId = -25;
    private static readonly CardPart[] Parts = [CardPart.Cover, CardPart.Previous, CardPart.PlayPause, CardPart.Next, CardPart.SeekBar, CardPart.TimeLabel];

    private readonly DesktopPlayer _player;
    private readonly nint _hwnd;
    private readonly RootProvider _root;
    private readonly Dictionary<CardPart, PartProvider> _parts = [];
    private volatile AutomationSnapshot _snapshot;
    private AutomationSnapshot? _announced;

    public PlayerAutomation(DesktopPlayer player, nint hwnd)
    {
        _player = player;
        _hwnd = hwnd;
        _snapshot = new AutomationSnapshot(false, default, 1, "Nothing playing", "Cover", false, false, false, 0, 0, "", "", CardPart.PlayPause);
        _root = new RootProvider(this);
        foreach (var part in Parts) _parts[part] = new PartProvider(this, part);
    }

    internal AutomationSnapshot Snapshot => _snapshot;

    internal nint Hwnd => _hwnd;

    internal void Post(Action action) => _player.PostFromAutomation(action);

    internal DesktopPlayer Player => _player;

    /// <summary>The card's state for the providers (cheap: a new snapshot only when what they read changed).</summary>
    public void Update(CardModel m)
    {
        var card = _player.ScreenRect();
        var position = m.RowKind == ScrubRowKind.Seekable || m.RowKind == ScrubRowKind.ReadOnly
            ? ScrubMath.AccessibilityValue(m.Elapsed, m.Duration, LineAt(m))
            : "";
        var song = m.HasSong ? (m.Artist is { Length: > 0 } a ? $"{m.Title}, {a}" : m.Title) : m.Title;
        var next = new AutomationSnapshot(
            _snapshot.Shown, card, _player.Scale, song,
            m.HasSong ? (m.Artist is { Length: > 0 } artist ? $"Cover, {m.Title} by {artist}" : $"Cover, {m.Title}") : "Nothing playing",
            m.DisplayedPlaying, m.HasSong, m.RowKind == ScrubRowKind.Seekable, Math.Floor(m.Elapsed), m.Duration,
            position,
            m.ShowsRemaining ? $"Time remaining {ScrubMath.Timestamp(Math.Max(0, m.Duration - m.Elapsed))}" : $"Song length {ScrubMath.Timestamp(m.Duration)}",
            _snapshot.Focus);
        if (next == _snapshot) return;
        var old = _snapshot;
        _snapshot = next;
        if (old.Playing != next.Playing && UiaClientsAreListening()) RaiseProperty(_parts[CardPart.PlayPause], 30005);
    }

    private static string? LineAt(CardModel m)
    {
        if (!m.Scrub.HasLyrics) return null;
        var at = ScrubMath.Lyric(m.Elapsed, m.Scrub.Lines);
        return at.Kind == ScrubLyricKind.Line ? m.Scrub.Lines[at.Index].Text : null;
    }

    public void Shown(bool shown)
    {
        _snapshot = _snapshot with { Shown = shown };
        if (shown && UiaClientsAreListening() && !ReferenceEquals(_announced, _snapshot))
        {
            _announced = _snapshot;
            Notify(_snapshot.HasSong ? $"Lunalyr player: {_snapshot.Song}" : "Lunalyr player");
        }
    }

    /// <summary>Keyboard focus moved to <paramref name="part"/> (a click, or Tab).</summary>
    public void Focus(CardPart part)
    {
        if (_snapshot.Focus == part) return;
        _snapshot = _snapshot with { Focus = part };
        if (UiaClientsAreListening() && _parts.TryGetValue(part, out var provider)) RaiseEvent(provider, 20005);
    }

    /// <summary>A note the card shows (a refused command) is read out too.</summary>
    public void Announce(string text)
    {
        if (UiaClientsAreListening()) Notify(text);
    }

    public nint? HandleGetObject(nuint wParam, nint lParam)
    {
        if ((int)(long)lParam != UiaRootObjectId) return null;
        var provider = (nint)ComInterfaceMarshaller<IRawElementProviderSimple>.ConvertToUnmanaged(_root);
        try
        {
            return UiaReturnRawElementProvider(_hwnd, wParam, lParam, provider);
        }
        finally
        {
            Marshal.Release(provider);
        }
    }

    public void Dispose()
    {
        // Tell UIA the providers are going, so no client keeps calling into them.
        UiaReturnRawElementProvider(_hwnd, 0, 0, 0);
        UiaDisconnectAllProviders();
    }

    internal nint Provider(CardPart part) =>
        part == CardPart.None || !_parts.TryGetValue(part, out var provider) ? 0
            : (nint)ComInterfaceMarshaller<IRawElementProviderFragment>.ConvertToUnmanaged(provider);

    internal nint RootFragment() => (nint)ComInterfaceMarshaller<IRawElementProviderFragment>.ConvertToUnmanaged(_root);

    private void Notify(string text)
    {
        var provider = (nint)ComInterfaceMarshaller<IRawElementProviderSimple>.ConvertToUnmanaged(_root);
        var display = Marshal.StringToBSTR(text);
        var activity = Marshal.StringToBSTR("LunalyrPlayer");
        try
        {
            // NotificationKind_ActionCompleted, NotificationProcessing_MostRecent.
            UiaRaiseNotificationEvent(provider, 2, 3, display, activity);
        }
        catch (EntryPointNotFoundException)
        {
            // Windows before 1709: no notifications.
        }
        finally
        {
            Marshal.FreeBSTR(display);
            Marshal.FreeBSTR(activity);
            Marshal.Release(provider);
        }
    }

    private static void RaiseEvent(PartProvider part, int eventId)
    {
        var provider = (nint)ComInterfaceMarshaller<IRawElementProviderSimple>.ConvertToUnmanaged(part);
        try
        {
            UiaRaiseAutomationEvent(provider, eventId);
        }
        finally
        {
            Marshal.Release(provider);
        }
    }

    private static void RaiseProperty(PartProvider part, int propertyId)
    {
        var provider = (nint)ComInterfaceMarshaller<IRawElementProviderSimple>.ConvertToUnmanaged(part);
        try
        {
            UiaRaiseAutomationPropertyChangedEvent(provider, propertyId, default, default);
        }
        finally
        {
            Marshal.Release(provider);
        }
    }

    internal static UiaRect PartRect(AutomationSnapshot s, CardPart part)
    {
        var r = part switch
        {
            CardPart.Cover => CardLayout.CoverRect,
            CardPart.Previous => CardLayout.PreviousRect,
            CardPart.PlayPause => CardLayout.PlayRect,
            CardPart.Next => CardLayout.NextRect,
            CardPart.SeekBar => new RectF(CardLayout.ColumnLeft, CardLayout.SeekRowTop, CardLayout.ColumnWidth, CardLayout.BarBand),
            CardPart.TimeLabel => new RectF(CardLayout.TimesRect.Right - 64, CardLayout.TimesRect.Y, 64, CardLayout.TimesHeight),
            _ => new RectF(0, 0, 0, 0),
        };
        return new UiaRect
        {
            Left = s.Card.Left + r.X * s.Scale, Top = s.Card.Top + r.Y * s.Scale, Width = r.Width * s.Scale, Height = r.Height * s.Scale,
        };
    }

    [LibraryImport("uiautomationcore")]
    private static partial nint UiaReturnRawElementProvider(nint hwnd, nuint wParam, nint lParam, nint provider);

    [LibraryImport("uiautomationcore")]
    internal static partial int UiaHostProviderFromHwnd(nint hwnd, out nint provider);

    [LibraryImport("uiautomationcore")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UiaClientsAreListening();

    [LibraryImport("uiautomationcore")]
    private static partial int UiaRaiseAutomationEvent(nint provider, int eventId);

    [LibraryImport("uiautomationcore")]
    private static partial int UiaRaiseAutomationPropertyChangedEvent(nint provider, int propertyId, UiaVariant oldValue, UiaVariant newValue);

    [LibraryImport("uiautomationcore")]
    private static partial int UiaRaiseNotificationEvent(nint provider, int kind, int processing, nint displayString, nint activityId);

    [LibraryImport("uiautomationcore")]
    private static partial int UiaDisconnectAllProviders();

    [LibraryImport("oleaut32")]
    internal static partial nint SafeArrayCreateVector(ushort vt, int lowerBound, uint count);

    [LibraryImport("oleaut32")]
    internal static unsafe partial int SafeArrayPutElement(nint array, int* index, void* value);

    // ---- The card ----

    [GeneratedComClass]
    internal sealed partial class RootProvider(PlayerAutomation owner) : IRawElementProviderSimple, IRawElementProviderFragment,
        IRawElementProviderFragmentRoot
    {
        public int get_ProviderOptions() => 1; // ProviderOptions_ServerSideProvider

        public nint GetPatternProvider(int patternId) => 0;

        public UiaVariant GetPropertyValue(int propertyId)
        {
            var s = owner.Snapshot;
            return propertyId switch
            {
                30003 => UiaVariant.Of(50033), // ControlType: Pane
                30005 => UiaVariant.Of("Lunalyr player"),
                30013 => UiaVariant.Of(s.HasSong ? $"Playing {s.Song}" : "Nothing is playing"), // HelpText
                30011 => UiaVariant.Of("LunalyrPlayer"), // AutomationId
                30012 => UiaVariant.Of("LunalyrPlayerCard"), // ClassName
                30024 => UiaVariant.Of("Lunalyr"), // FrameworkId
                _ => default,
            };
        }

        public nint get_HostRawElementProvider() => UiaHostProviderFromHwnd(owner.Hwnd, out var host) == 0 ? host : 0;

        public nint Navigate(int direction) => direction switch
        {
            3 => owner.Provider(Parts[0]), // FirstChild
            4 => owner.Provider(Parts[^1]), // LastChild
            _ => 0,
        };

        public nint GetRuntimeId() => 0; // the host window's id stands for the card

        public UiaRect get_BoundingRectangle() => default; // the host window's

        public nint GetEmbeddedFragmentRoots() => 0;

        public void SetFocus() => owner.Post(() => owner.Player.FocusPart(CardPart.PlayPause));

        public nint get_FragmentRoot() => owner.RootFragment();

        public nint ElementProviderFromPoint(double x, double y)
        {
            var s = owner.Snapshot;
            var part = owner.Player.PartAtScreen(x, y);
            return part == CardPart.None ? owner.RootFragment() : owner.Provider(part);
        }

        public nint GetFocus() => owner.Provider(owner.Snapshot.Focus);
    }

    // ---- Its parts ----

    [GeneratedComClass]
    internal sealed partial class PartProvider(PlayerAutomation owner, CardPart part) : IRawElementProviderSimple,
        IRawElementProviderFragment, IInvokeProvider, IRangeValueProvider, IValueProvider
    {
        private bool IsBar => part == CardPart.SeekBar;

        public int get_ProviderOptions() => 1;

        public nint GetPatternProvider(int patternId)
        {
            // Invoke for the buttons; RangeValue and Value (the spoken position) for the seek bar.
            return patternId switch
            {
                10000 when !IsBar => (nint)ComInterfaceMarshaller<IInvokeProvider>.ConvertToUnmanaged(this),
                10003 when IsBar => (nint)ComInterfaceMarshaller<IRangeValueProvider>.ConvertToUnmanaged(this),
                10002 when IsBar => (nint)ComInterfaceMarshaller<IValueProvider>.ConvertToUnmanaged(this),
                _ => 0,
            };
        }

        public UiaVariant GetPropertyValue(int propertyId)
        {
            var s = owner.Snapshot;
            return propertyId switch
            {
                30003 => UiaVariant.Of(IsBar ? 50015 : 50000), // Slider or Button
                30005 => UiaVariant.Of(Name(s)),
                30011 => UiaVariant.Of($"LunalyrPlayer.{part}"),
                30009 => UiaVariant.Of(true), // IsKeyboardFocusable
                30008 => UiaVariant.Of(s.Focus == part && owner.Player.HasKeyboard), // HasKeyboardFocus
                30010 => UiaVariant.Of(part is CardPart.PlayPause or CardPart.TimeLabel || s.HasSong), // IsEnabled
                30013 => UiaVariant.Of(Help(s)), // HelpText
                30026 when IsBar => UiaVariant.Of(s.PositionText), // ItemStatus
                30017 or 30016 => UiaVariant.Of(true), // IsContentElement, IsControlElement
                30019 => UiaVariant.Of(!s.Shown), // IsOffscreen
                _ => default,
            };
        }

        private string Name(AutomationSnapshot s) => part switch
        {
            CardPart.Cover => s.CoverName,
            CardPart.Previous => "Previous track",
            CardPart.PlayPause => s.Playing ? "Pause" : "Play",
            CardPart.Next => "Next track",
            CardPart.SeekBar => "Playback position",
            CardPart.TimeLabel => s.TimeLabelName,
            _ => "",
        };

        private static string Help(AutomationSnapshot s) => "";

        public nint get_HostRawElementProvider() => 0;

        public nint Navigate(int direction)
        {
            var index = Array.IndexOf(Parts, part);
            return direction switch
            {
                0 => owner.RootFragment(), // Parent
                1 => index + 1 < Parts.Length ? owner.Provider(Parts[index + 1]) : 0, // NextSibling
                2 => index > 0 ? owner.Provider(Parts[index - 1]) : 0, // PreviousSibling
                _ => 0,
            };
        }

        public unsafe nint GetRuntimeId()
        {
            var array = SafeArrayCreateVector(3, 0, 2); // VT_I4
            int index = 0, value = 3; // UiaAppendRuntimeId
            SafeArrayPutElement(array, &index, &value);
            index = 1;
            value = (int)part;
            SafeArrayPutElement(array, &index, &value);
            return array;
        }

        public UiaRect get_BoundingRectangle() => owner.Snapshot.Shown ? PartRect(owner.Snapshot, part) : default;

        public nint GetEmbeddedFragmentRoots() => 0;

        public void SetFocus() => owner.Post(() => owner.Player.FocusPart(part));

        public nint get_FragmentRoot() => owner.RootFragment();

        public void Invoke() => owner.Post(() => owner.Player.Click(part));

        // The seek bar.
        public void SetValue(double value) => owner.Post(() => owner.Player.SeekFromAutomation(value));

        public double get_Value() => owner.Snapshot.Elapsed;

        int IRangeValueProvider.get_IsReadOnly() => owner.Snapshot.Seekable ? 0 : 1;

        public double get_Maximum() => owner.Snapshot.Duration;

        public double get_Minimum() => 0;

        public double get_LargeChange() => PlayerKeyMap.LineFallbackStep;

        public double get_SmallChange() => 5;

        public void SetValue(string value)
        {
            // The spoken position is read-only.
        }

        string IValueProvider.get_Value() => owner.Snapshot.PositionText;

        int IValueProvider.get_IsReadOnly() => 1;
    }
}
