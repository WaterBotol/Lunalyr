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
    CardPart Focus)
{
    /// <summary>The parts Narrator lists, in reading order (the same order as Tab), with what it reads for each.</summary>
    public IReadOnlyList<AutomationItem> Items { get; init; } = [];

    public AutomationItem? Item(CardPart part)
    {
        foreach (var item in Items)
            if (item.Part == part) return item;
        return null;
    }

    public int IndexOf(CardPart part)
    {
        for (var i = 0; i < Items.Count; i++)
            if (Items[i].Part == part) return i;
        return -1;
    }
}

/// <summary>One part as UI Automation reads it.</summary>
/// <param name="ControlType">UIA's control type id (Button 50000, Edit 50004, ListItem 50007, Slider 50015).</param>
/// <param name="Rect">Where it is on the card, in DIPs.</param>
/// <param name="Status">ItemStatus ("Selected", "Now playing", "Up next"…), or empty.</param>
internal sealed record AutomationItem(CardPart Part, string Name, string Help, int ControlType, RectF Rect, bool Enabled, string Status);

internal sealed unsafe partial class PlayerAutomation : IDisposable
{
    private const int UiaRootObjectId = -25;

    private readonly DesktopPlayer _player;
    private readonly nint _hwnd;
    private readonly RootProvider _root;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<CardPart, PartProvider> _parts = [];
    private volatile AutomationSnapshot _snapshot;
    private AutomationSnapshot? _announced;

    public PlayerAutomation(DesktopPlayer player, nint hwnd)
    {
        _player = player;
        _hwnd = hwnd;
        _snapshot = new AutomationSnapshot(false, default, 1, "Nothing playing", "Cover", false, false, false, 0, 0, "", "", CardPart.PlayPause);
        _root = new RootProvider(this);
    }

    private PartProvider PartFor(CardPart part) => _parts.GetOrAdd(part, p => new PartProvider(this, p));

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
        var items = Items(m, next);
        if (next == _snapshot && items.SequenceEqual(_snapshot.Items)) return;
        var old = _snapshot;
        _snapshot = next with { Items = items };
        if (!UiaClientsAreListening()) return;
        if (old.Playing != next.Playing) RaiseProperty(PartFor(CardPart.PlayPause), 30005);
        // Rows came or went (a search answered, the queue was read): the card's children changed.
        if (!old.Items.Select(i => i.Part).SequenceEqual(items.Select(i => i.Part))) RaiseStructure(_root);
    }

    /// <summary>What Narrator reads for each part, in the order Tab walks them.</summary>
    private List<AutomationItem> Items(CardModel m, AutomationSnapshot s)
    {
        var items = new List<AutomationItem>();
        foreach (var part in _player.FocusOrder())
        {
            var rect = _player.PartRectDip(part);
            var (name, help, type, status) = Describe(m, s, part);
            items.Add(new AutomationItem(part, name, help, type, rect,
                part is CardPart.PlayPause or CardPart.TimeLabel || s.HasSong || part is not (CardPart.Cover or CardPart.Previous or CardPart.Next), status));
        }

        return items;
    }

    private static (string Name, string Help, int ControlType, string Status) Describe(CardModel m, AutomationSnapshot s, CardPart part)
    {
        const int Button = 50000, Edit = 50004, ListItem = 50007, Slider = 50015;
        switch (part)
        {
            case CardPart.Cover: return (s.CoverName, s.HasSong ? "Plays or pauses. Double-click brings the player forward" : "", Button, "");
            case CardPart.Previous: return ("Previous track", "", Button, "");
            case CardPart.PlayPause: return (s.Playing ? "Pause" : "Play", "", Button, "");
            case CardPart.Next: return ("Next track", "", Button, "");
            case CardPart.SeekBar: return ("Playback position", "", Slider, s.PositionText);
            case CardPart.TimeLabel: return (s.TimeLabelName, "", Button, "");
            case CardPart.SearchButton: return ("Search", "Search and play a song (Ctrl+F)", Button, m.Pane == PlayerPane.Search ? "Open" : "");
            case CardPart.QueueButton: return ("Queue", "What played and what plays next", Button, m.Pane == PlayerPane.Queue ? "Open" : "");
            case CardPart.PlayerLabel: return ($"Playing in {m.PlayerName}", "Opens search, where you choose the player to search", Button, "");
            case CardPart.PaneClose: return (m.Pane == PlayerPane.Queue ? "Close queue" : "Close search", "Esc", Button, "");
            case CardPart.ClearSearch: return ("Clear search", "", Button, "");
            case CardPart.PromptAction: return (m.Prompt?.ActionLabel ?? "", m.Prompt?.Text ?? "", Button, "");
            case CardPart.SearchField:
                return (m.SearchPlaceholder, m.Status.Kind == PlayerSearchStatusKind.Idle ? "" : m.Status.Text, Edit, "");
        }

        if (CardParts.IsPicker(part, out var picker) && picker < m.Pickers.Count)
            return ($"Search {m.Pickers[picker]}", "Chooses the player search talks to", Button, picker == m.PickerSelected ? "Selected" : "");
        if (CardParts.IsResult(part, out var index) && index < m.Results.Count)
        {
            var r = m.Results[index];
            var kind = r.Kind == PlayerSearchResultKind.Album ? "album" : "song";
            var by = r.Artist.Length == 0 ? r.Title : $"{r.Title} by {r.Artist}";
            return ($"{by}, {kind}", index == m.Selection ? "Selected. Enter plays it" : "", ListItem, index == m.Selection ? "Selected" : "");
        }

        if (CardParts.IsQueueRow(part, out var row) && row < m.Queue.Count)
        {
            var q = m.Queue[row];
            var current = m.Queue.ToList().FindIndex(x => x.IsCurrent);
            var place = row == current ? "Now playing" : current >= 0 && row < current ? "Played earlier" : "Up next";
            return (q.Artist.Length == 0 ? q.Title : $"{q.Title} by {q.Artist}", row == current ? "Plays or pauses" : "Plays it, then what is listed after it", ListItem, place);
        }

        if (DesktopPlayer.DescribeAlign(m, part) is { } align) return align;
        return ("", "", Button, "");
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
        if (UiaClientsAreListening() && part != CardPart.None) RaiseEvent(PartFor(part), 20005);
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
        part == CardPart.None ? 0 : (nint)ComInterfaceMarshaller<IRawElementProviderFragment>.ConvertToUnmanaged(PartFor(part));

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

    private static void RaiseStructure(RootProvider root)
    {
        var provider = (nint)ComInterfaceMarshaller<IRawElementProviderSimple>.ConvertToUnmanaged(root);
        try
        {
            // StructureChangeType_ChildrenInvalidated, with no runtime id.
            UiaRaiseStructureChangedEvent(provider, 1, 0, 0);
        }
        finally
        {
            Marshal.Release(provider);
        }
    }

    internal static UiaRect PartRect(AutomationSnapshot s, CardPart part)
    {
        var r = s.Item(part)?.Rect ?? default;
        return new UiaRect
        {
            Left = s.Card.Left + r.X * s.Scale, Top = s.Card.Top + r.Y * s.Scale, Width = r.Width * s.Scale, Height = r.Height * s.Scale,
        };
    }

    [LibraryImport("uiautomationcore")]
    private static partial nint UiaReturnRawElementProvider(nint hwnd, nuint wParam, nint lParam, nint provider);

    [LibraryImport("uiautomationcore")]
    private static partial int UiaRaiseStructureChangedEvent(nint provider, int structureChangeType, nint runtimeId, int runtimeIdLength);

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

        public nint Navigate(int direction)
        {
            var items = owner.Snapshot.Items;
            if (items.Count == 0) return 0;
            return direction switch
            {
                3 => owner.Provider(items[0].Part), // FirstChild
                4 => owner.Provider(items[^1].Part), // LastChild
                _ => 0,
            };
        }

        public nint GetRuntimeId() => 0; // the host window's id stands for the card

        public UiaRect get_BoundingRectangle() => default; // the host window's

        public nint GetEmbeddedFragmentRoots() => 0;

        public void SetFocus() => owner.Post(() => owner.Player.FocusPart(CardPart.PlayPause));

        public nint get_FragmentRoot() => owner.RootFragment();

        public nint ElementProviderFromPoint(double x, double y)
        {
            // From the snapshot (UIA calls on its own threads; the card's hit list is the UI thread's).
            var s = owner.Snapshot;
            for (var i = s.Items.Count - 1; i >= 0; i--)
            {
                var r = PartRect(s, s.Items[i].Part);
                if (r.Width > 0 && x >= r.Left && x < r.Left + r.Width && y >= r.Top && y < r.Top + r.Height) return owner.Provider(s.Items[i].Part);
            }

            return owner.RootFragment();
        }

        public nint GetFocus() => owner.Provider(owner.Snapshot.Focus);
    }

    // ---- Its parts ----

    [GeneratedComClass]
    internal sealed partial class PartProvider(PlayerAutomation owner, CardPart part) : IRawElementProviderSimple,
        IRawElementProviderFragment, IInvokeProvider, IRangeValueProvider, IValueProvider
    {
        private bool IsBar => part == CardPart.SeekBar;

        private bool IsField => part == CardPart.SearchField;

        public int get_ProviderOptions() => 1;

        public nint GetPatternProvider(int patternId)
        {
            // Invoke for the buttons and rows; RangeValue and Value (the spoken position) for the seek bar; Value (what is
            // typed) for the search box.
            return patternId switch
            {
                10000 when !IsBar && !IsField => (nint)ComInterfaceMarshaller<IInvokeProvider>.ConvertToUnmanaged(this),
                10003 when IsBar => (nint)ComInterfaceMarshaller<IRangeValueProvider>.ConvertToUnmanaged(this),
                10002 when IsBar || IsField => (nint)ComInterfaceMarshaller<IValueProvider>.ConvertToUnmanaged(this),
                _ => 0,
            };
        }

        public UiaVariant GetPropertyValue(int propertyId)
        {
            var s = owner.Snapshot;
            var item = s.Item(part);
            return propertyId switch
            {
                30003 => UiaVariant.Of(item?.ControlType ?? (IsBar ? 50015 : 50000)), // ControlType
                30005 => UiaVariant.Of(item?.Name ?? ""),
                30011 => UiaVariant.Of($"LunalyrPlayer.{part}"),
                30009 => UiaVariant.Of(true), // IsKeyboardFocusable
                30008 => UiaVariant.Of(s.Focus == part && owner.Player.HasKeyboard), // HasKeyboardFocus
                30010 => UiaVariant.Of(item?.Enabled ?? false), // IsEnabled
                30013 => UiaVariant.Of(item?.Help ?? ""), // HelpText
                30026 => UiaVariant.Of(item?.Status ?? ""), // ItemStatus
                30017 or 30016 => UiaVariant.Of(true), // IsContentElement, IsControlElement
                30019 => UiaVariant.Of(!s.Shown || item is null), // IsOffscreen
                _ => default,
            };
        }

        public nint get_HostRawElementProvider() => 0;

        public nint Navigate(int direction)
        {
            var s = owner.Snapshot;
            var index = s.IndexOf(part);
            return direction switch
            {
                0 => owner.RootFragment(), // Parent
                1 => index >= 0 && index + 1 < s.Items.Count ? owner.Provider(s.Items[index + 1].Part) : 0, // NextSibling
                2 => index > 0 ? owner.Provider(s.Items[index - 1].Part) : 0, // PreviousSibling
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
            // The spoken position is read-only; the search box takes what Narrator (or another client) types.
            if (IsField) owner.Post(() => owner.Player.SetQueryFromAutomation(value ?? ""));
        }

        string IValueProvider.get_Value() => IsField ? owner.Player.QueryText : owner.Snapshot.PositionText;

        int IValueProvider.get_IsReadOnly() => IsField ? 0 : 1;
    }
}
