using System.Runtime.InteropServices;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Player;

namespace Lunalyr.App.Player;

/// <summary>
/// The card's search box: a real Win32 EDIT control in a small pop-up owned by the card, placed exactly over the field
/// the card draws. Windows does the text: typing, IME composition (Japanese, Chinese, Korean), the caret, selection,
/// the clipboard and its context menu, and Narrator (UI Automation's own Edit proxy, named with a dynamic annotation).
/// The subclass adds what a single-line EDIT lacks: Ctrl+Backspace (delete a word), Ctrl+A, the placeholder, and the
/// player's own keys (Esc, ↑ ↓, Enter, Tab, Ctrl+F), which it hands to the card. See docs/APP-SHELL.md, "Search and
/// queue in the player".
/// </summary>
/// <remarks>
/// Not a child window: the card has no redirection bitmap (it is drawn with DirectComposition), and a child of such a
/// window, layered or not, never reaches the screen (tried on this PC, with and without
/// <c>CreateSurfaceFromHwnd</c>). An owned pop-up always sits just above its owner, moves with it here, and is layered
/// so it fades with the card. Everything runs on the card's (UI) thread.
/// </remarks>
internal sealed unsafe class SearchField : IDisposable
{
    private static SearchField? s_instance;

    private readonly CategoryLogger _log;
    private readonly nint _owner;
    private nint _previousProc;
    private nint _font, _brush;
    private uint _back = 0x202020, _ink = 0xFFFFFF, _faint = 0x909090;
    private string _placeholder = "Search";
    private int _fontPixels;
    private bool _composing;
    private bool _visible;
    private string _name = "";

    public SearchField(nint owner, CategoryLogger log)
    {
        if (s_instance is not null) throw new InvalidOperationException("Only one search field per process.");
        _log = log;
        var instance = Lunalyr.App.Interop.Win32.GetModuleHandle(null);
        _owner = owner;
        // An EDIT as an owned pop-up: no taskbar button, never in Alt+Tab, always just above the card. Notifications
        // (WM_COMMAND, WM_CTLCOLOREDIT) go to the card, its owner.
        Hwnd = PlayerNative.CreateWindowEx(PlayerNative.WS_EX_LAYERED | PlayerNative.WS_EX_TOOLWINDOW, "EDIT", "",
            PlayerNative.WS_POPUP | PlayerNative.ES_AUTOHSCROLL, 0, 0, 10, 10, owner, 0, instance, 0);
        if (Hwnd == 0) throw new InvalidOperationException($"CreateWindowEx(EDIT) failed ({Marshal.GetLastPInvokeError()}).");
        s_instance = this;
        var square = PlayerNative.DWMWCP_DONOTROUND;
        PlayerNative.DwmSetWindowAttribute(Hwnd, PlayerNative.DWMWA_WINDOW_CORNER_PREFERENCE, &square, sizeof(int));
        PlayerNative.SetLayeredWindowAttributes(Hwnd, 0, 255, PlayerNative.LWA_ALPHA);
        PlayerNative.SendMessage(Hwnd, PlayerNative.EM_LIMITTEXT, 200, 0);
        PlayerNative.SendMessage(Hwnd, PlayerNative.EM_SETMARGINS, 3, 0); // EC_LEFTMARGIN | EC_RIGHTMARGIN, both 0
        _previousProc = PlayerNative.SetWindowLongPtr(Hwnd, PlayerNative.GWLP_WNDPROC, (nint)(delegate* unmanaged<nint, uint, nuint, nint, nint>)&SubclassProc);
        _brush = PlayerNative.CreateSolidBrush(_back);
    }

    public nint Hwnd { get; private set; }

    /// <summary>The text changed (typed, pasted, cleared): the card's model takes it.</summary>
    public Action<string>? Changed { get; set; }

    /// <summary>A key the field doesn't keep (Esc, ↑ ↓, Enter, Tab, Ctrl+F…): true when the card used it.</summary>
    public Func<ushort, bool>? Key { get; set; }

    /// <summary>The field gained (true) or lost the keyboard.</summary>
    public Action<bool>? FocusChanged { get; set; }

    /// <summary>The wheel over the field: the list under it scrolls.</summary>
    public Action<int>? Wheel { get; set; }

    public bool HasFocus => Hwnd != 0 && PlayerNative.GetFocus() == Hwnd;

    public bool IsShown => _visible;

    public string Text
    {
        get
        {
            var length = PlayerNative.GetWindowTextLength(Hwnd);
            if (length <= 0) return "";
            var buffer = new char[length + 1];
            fixed (char* b = buffer)
            {
                var read = PlayerNative.GetWindowText(Hwnd, b, buffer.Length);
                return new string(b, 0, Math.Max(0, read));
            }
        }
        set
        {
            if (Text == value) return;
            PlayerNative.SetWindowText(Hwnd, value);
            PlayerNative.SendMessage(Hwnd, PlayerNative.EM_SETSEL, (nuint)value.Length, value.Length);
            PlayerNative.InvalidateRect(Hwnd, 0, true);
        }
    }

    private (int X, int Y, int W, int H) _placed;

    /// <summary>Where the text goes, in screen pixels: one line high, inside the field the card draws; just above the card.</summary>
    public void Place(int x, int y, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var above = PlayerNative.GetWindow(_owner, PlayerNative.GW_HWNDPREV);
        var moved = _placed != (x, y, width, height);
        if (!moved && above == Hwnd) return;
        var resized = _placed.W != width || _placed.H != height;
        _placed = (x, y, width, height);
        // Right above the card in the z-order (an owned window is never below its owner).
        PlayerNative.SetWindowPos(Hwnd, above == Hwnd || above == 0 ? 0 : above, x, y, width, height,
            PlayerNative.SWP_NOACTIVATE | PlayerNative.SWP_NOOWNERZORDER | (above == Hwnd || above == 0 ? PlayerNative.SWP_NOZORDER : 0));
        if (resized)
        {
            // A single-line EDIT keeps the horizontal scroll it had at its old width: scroll from the start again, back
            // to the caret.
            uint start, end;
            PlayerNative.SendMessage(Hwnd, PlayerNative.EM_GETSEL, (nuint)(&start), (nint)(&end));
            PlayerNative.SendMessage(Hwnd, PlayerNative.EM_SETSEL, 0, 0);
            PlayerNative.SendMessage(Hwnd, PlayerNative.EM_SETSEL, start, (nint)end);
        }
    }

    /// <summary>The text's size in pixels (15 DIPs at the card's scale), and the face the card uses.</summary>
    public void SetFont(int pixels)
    {
        if (pixels == _fontPixels) return;
        _fontPixels = pixels;
        var font = PlayerNative.CreateFont(-pixels, 0, 0, 0, 500, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI Variable Text");
        PlayerNative.SendMessage(Hwnd, PlayerNative.WM_SETFONT, (nuint)font, 1);
        if (_font != 0) PlayerNative.DeleteObject(_font);
        _font = font;
    }

    /// <summary>The field's colours (COLORREF): its opaque background (the card draws the same flat colour around it), the text and the placeholder.</summary>
    public void SetColours(uint back, uint ink, uint faint)
    {
        if (back == _back && ink == _ink && faint == _faint) return;
        (_back, _ink, _faint) = (back, ink, faint);
        if (_brush != 0) PlayerNative.DeleteObject(_brush);
        _brush = PlayerNative.CreateSolidBrush(back);
        PlayerNative.InvalidateRect(Hwnd, 0, true);
    }

    /// <summary>"Search Spotify": the placeholder, and the name Narrator reads.</summary>
    public void SetPlaceholder(string text)
    {
        if (text == _placeholder && _name == text) return;
        _placeholder = text;
        if (_name != text)
        {
            _name = text;
            AccessibleName.Set(Hwnd, text);
        }

        PlayerNative.InvalidateRect(Hwnd, 0, true);
    }

    /// <summary>Follows the card's fade (the field is a window of its own, so it doesn't fade with the drawing).</summary>
    public void SetAlpha(float alpha) =>
        PlayerNative.SetLayeredWindowAttributes(Hwnd, 0, (byte)Math.Clamp(Math.Round(alpha * 255), 0, 255), PlayerNative.LWA_ALPHA);

    public void Show(bool shown)
    {
        if (shown == _visible) return;
        _visible = shown;
        PlayerNative.ShowWindow(Hwnd, shown ? PlayerNative.SW_SHOWNOACTIVATE : PlayerNative.SW_HIDE);
    }

    /// <summary>Gives the field the keyboard (only ever for the user's own click or key on the card).</summary>
    public void Focus()
    {
        if (!_visible) return;
        if (PlayerNative.GetForegroundWindow() != Hwnd) PlayerNative.SetForegroundWindow(Hwnd);
        PlayerNative.SetFocus(Hwnd);
    }

    /// <summary>The parent's answer to <c>WM_CTLCOLOREDIT</c>: the field's own colours.</summary>
    public nint CtlColor(nint hdc)
    {
        PlayerNative.SetTextColor(hdc, _ink);
        PlayerNative.SetBkColor(hdc, _back);
        return _brush;
    }

    /// <summary>The parent's <c>WM_COMMAND</c> from the field.</summary>
    public bool Command(nuint wParam, nint lParam)
    {
        if (lParam != Hwnd) return false;
        switch ((uint)wParam >> 16)
        {
            case PlayerNative.EN_CHANGE:
                PlayerNative.InvalidateRect(Hwnd, 0, true);
                Changed?.Invoke(Text);
                break;
            case PlayerNative.EN_SETFOCUS:
                FocusChanged?.Invoke(true);
                break;
            case PlayerNative.EN_KILLFOCUS:
                FocusChanged?.Invoke(false);
                break;
        }

        return true;
    }

    public void Dispose()
    {
        if (Hwnd != 0)
        {
            AccessibleName.Clear(Hwnd);
            PlayerNative.SetWindowLongPtr(Hwnd, PlayerNative.GWLP_WNDPROC, _previousProc);
            PlayerNative.DestroyWindow(Hwnd);
            Hwnd = 0;
        }

        if (_font != 0) PlayerNative.DeleteObject(_font);
        if (_brush != 0) PlayerNative.DeleteObject(_brush);
        _font = _brush = 0;
        if (ReferenceEquals(s_instance, this)) s_instance = null;
    }

    [UnmanagedCallersOnly]
    private static nint SubclassProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        var self = s_instance;
        if (self is null || hwnd != self.Hwnd) return PlayerNative.DefWindowProc(hwnd, msg, wParam, lParam);
        try
        {
            if (self.Handle(msg, wParam, lParam) is { } handled) return handled;
        }
        catch (Exception ex)
        {
            try
            {
                self._log.Warn("Search field: a message couldn't be handled.", ex);
            }
            catch (Exception)
            {
                // Nothing more can be done inside a window procedure.
            }
        }

        return PlayerNative.CallWindowProc(self._previousProc, hwnd, msg, wParam, lParam);
    }

    private nint? Handle(uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case PlayerNative.WM_KEYDOWN:
            case PlayerNative.WM_SYSKEYDOWN:
                var vk = (ushort)wParam;
                if (vk is PlayerNative.VK_CONTROL or PlayerNative.VK_MENU or PlayerNative.VK_SHIFT) return null;
                if (Key?.Invoke(vk) == true) return 0;
                return null;

            case PlayerNative.WM_CHAR:
                switch ((uint)wParam)
                {
                    case 0x0D or 0x1B or 0x09 or 0x0A:
                        // Return, Esc, Tab and Ctrl+Return are the card's keys: no character, and no beep.
                        return 0;
                    case 0x7F:
                        DeleteWordBefore();
                        return 0;
                    case 0x01:
                        PlayerNative.SendMessage(Hwnd, PlayerNative.EM_SETSEL, 0, -1);
                        return 0;
                }

                return null;

            case PlayerNative.WM_SYSCHAR:
                return 0;

            case PlayerNative.WM_IME_STARTCOMPOSITION:
                _composing = true;
                PlayerNative.InvalidateRect(Hwnd, 0, true);
                return null;

            case PlayerNative.WM_IME_ENDCOMPOSITION:
                _composing = false;
                PlayerNative.InvalidateRect(Hwnd, 0, true);
                return null;

            case PlayerNative.WM_MOUSEWHEEL:
                Wheel?.Invoke((short)(((long)wParam >> 16) & 0xFFFF));
                return 0;

            case PlayerNative.WM_PAINT:
                if (_composing || PlayerNative.GetWindowTextLength(Hwnd) > 0) return null;
                var dc = PlayerNative.BeginPaint(Hwnd, out var paint);
                try
                {
                    PaintPlaceholder(dc);
                }
                finally
                {
                    PlayerNative.EndPaint(Hwnd, paint);
                }

                return 0;

            case WM_PRINTCLIENT:
                // A capture (PrintWindow) draws the same as the screen.
                if (_composing || PlayerNative.GetWindowTextLength(Hwnd) > 0) return null;
                PaintPlaceholder((nint)wParam);
                return 0;
        }

        return null;
    }

    private const uint WM_PRINTCLIENT = 0x0318;

    /// <summary>An empty field shows what it searches, faintly; the caret blinks over it.</summary>
    private void PaintPlaceholder(nint dc)
    {
        PlayerNative.GetClientRect(Hwnd, out var rect);
        PlayerNative.FillRect(dc, rect, _brush);
        var old = PlayerNative.SelectObject(dc, _font);
        PlayerNative.SetBkMode(dc, PlayerNative.TRANSPARENT);
        PlayerNative.SetTextColor(dc, _faint);
        rect.Left += 1;
        PlayerNative.DrawText(dc, _placeholder, -1, ref rect,
            PlayerNative.DT_LEFT | PlayerNative.DT_SINGLELINE | PlayerNative.DT_NOPREFIX | PlayerNative.DT_END_ELLIPSIS);
        PlayerNative.SelectObject(dc, old);
    }

    /// <summary>Ctrl+Backspace: the selection, or the word before the caret (and the spaces after it).</summary>
    private void DeleteWordBefore()
    {
        uint start, end;
        PlayerNative.SendMessage(Hwnd, PlayerNative.EM_GETSEL, (nuint)(&start), (nint)(&end));
        if (start == end)
        {
            var from = SearchText.WordStartBefore(Text, (int)start);
            if (from == start) return;
            PlayerNative.SendMessage(Hwnd, PlayerNative.EM_SETSEL, (nuint)from, (nint)start);
        }

        PlayerNative.SendMessage(Hwnd, PlayerNative.EM_REPLACESEL, 1, "");
    }
}

/// <summary>
/// The name Narrator reads for a Win32 control that has no label of its own (a dynamic annotation through
/// <c>IAccPropServices</c>, which UI Automation's Win32 proxies read).
/// </summary>
internal static unsafe class AccessibleName
{
    private static readonly Guid ClsidAccPropServices = new("b5f8350b-0548-48b1-a6ee-88bd00b4a5e7");
    private static readonly Guid IidAccPropServices = new("6e26e776-04f0-495d-80e4-3330352e3169");
    private static readonly Guid PropIdName = new("608d3df8-8128-4aa7-a428-f55e49267291");
    private const uint ObjIdClient = 0xFFFFFFFC;

    private static nint s_services;

    private static nint Services()
    {
        if (s_services != 0) return s_services;
        return PlayerNative.CoCreateInstance(ClsidAccPropServices, 0, 1, IidAccPropServices, out s_services) >= 0 ? s_services : 0;
    }

    public static void Set(nint hwnd, string name)
    {
        var services = Services();
        if (services == 0) return;
        fixed (char* text = name)
        {
            // IAccPropServices: 7 SetHwndPropStr.
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, Guid, char*, int>)Slot(services, 7))(services, hwnd, ObjIdClient, 0, PropIdName, text);
        }
    }

    public static void Clear(nint hwnd)
    {
        if (s_services == 0) return;
        var props = PropIdName;
        // IAccPropServices: 9 ClearHwndProps.
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, Guid*, int, int>)Slot(s_services, 9))(s_services, hwnd, ObjIdClient, 0, &props, 1);
    }

    private static void* Slot(nint com, int index) => (*(void***)com)[index];
}
