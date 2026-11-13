using System.Runtime.InteropServices;

namespace Lunalyr.Setup;

/// <summary>A button on a task dialog: a common one (OK, Cancel…) by its id, or a custom one with text.</summary>
internal readonly record struct DialogButton(int Id, string Text);

/// <summary>What a task dialog shows.</summary>
internal sealed record DialogSpec
{
    public required string Title { get; init; }
    public required string Instruction { get; init; }
    public string? Content { get; init; }
    public string? Expanded { get; init; }
    public string? Footer { get; init; }
    public string? Verification { get; init; }
    public DialogIcon Icon { get; init; } = DialogIcon.App;
    public IReadOnlyList<DialogButton> Buttons { get; init; } = [];
    public int CommonButtons { get; init; }
    public int DefaultButton { get; init; }

    /// <summary>A progress bar driven by <see cref="TaskDialog.ShowProgress"/>.</summary>
    public bool Progress { get; init; }
}

internal enum DialogIcon
{
    App,
    Information,
    Warning,
    Error,
}

/// <summary>
/// Windows' task dialogs (<c>TaskDialogIndirect</c>, Common Controls 6): native, themed, high-contrast and Narrator
/// ready, with no UI framework. One dialog at a time.
/// </summary>
internal static unsafe partial class TaskDialog
{
    public const int IdOk = 1, IdCancel = 2, IdRetry = 4, IdYes = 6, IdNo = 7, IdClose = 8;
    public const int ButtonOk = 0x1, ButtonCancel = 0x8, ButtonClose = 0x20;

    private const int TdfAllowCancellation = 0x8, TdfShowProgressBar = 0x200, TdfCallbackTimer = 0x800, TdfSizeToContent = 0x01000000;
    private const uint WmUser = 0x400;
    private const uint TdmClickButton = WmUser + 102, TdmSetProgressRange = WmUser + 105, TdmSetProgressPos = WmUser + 106,
        TdmSetElementText = WmUser + 108;
    private const int TdnTimer = 4, TdnButtonClicked = 2;
    private const int TdeContent = 0;

    // State for the one progress dialog showing.
    private static Func<(double Fraction, string Step, bool Done)>? _poll;
    private static Action? _cancel;
    private static string _lastStep = "";

    /// <summary>Shows a dialog; returns the button clicked and whether the check box was ticked.</summary>
    public static (int Button, bool Checked) Show(DialogSpec spec) => ShowCore(spec, timer: false);

    /// <summary>
    /// Shows a progress dialog until <paramref name="poll"/> says done. Its Cancel button calls
    /// <paramref name="cancel"/>; the dialog stays until the work notices and finishes.
    /// </summary>
    public static void ShowProgress(DialogSpec spec, Func<(double Fraction, string Step, bool Done)> poll, Action cancel)
    {
        _poll = poll;
        _cancel = cancel;
        _lastStep = "";
        try
        {
            ShowCore(spec with { Progress = true, CommonButtons = ButtonCancel }, timer: true);
        }
        finally
        {
            _poll = null;
            _cancel = null;
        }
    }

    private static (int, bool) ShowCore(DialogSpec spec, bool timer)
    {
        var strings = new List<nint>();
        nint Str(string? s)
        {
            if (s is null) return 0;
            var p = Marshal.StringToHGlobalUni(s);
            strings.Add(p);
            return p;
        }

        var buttons = stackalloc Button[Math.Max(1, spec.Buttons.Count)];
        for (var i = 0; i < spec.Buttons.Count; i++) buttons[i] = new Button { Id = spec.Buttons[i].Id, Text = Str(spec.Buttons[i].Text) };

        try
        {
            var flags = TdfAllowCancellation | TdfSizeToContent;
            if (spec.Progress) flags |= TdfShowProgressBar;
            if (timer) flags |= TdfCallbackTimer;
            var config = new Config
            {
                Size = (uint)sizeof(Config),
                Instance = spec.Icon == DialogIcon.App ? GetModuleHandleW(null) : 0,
                Flags = flags,
                CommonButtons = spec.CommonButtons,
                WindowTitle = Str(spec.Title),
                MainIcon = spec.Icon switch
                {
                    DialogIcon.Warning => 0xFFFF,
                    DialogIcon.Error => 0xFFFE,
                    DialogIcon.Information => 0xFFFD,
                    _ => 32512, // The exe's icon resource (IDI_APPLICATION's id, which the SDK gives ApplicationIcon).
                },
                MainInstruction = Str(spec.Instruction),
                Content = Str(spec.Content),
                ButtonCount = (uint)spec.Buttons.Count,
                Buttons = spec.Buttons.Count > 0 ? (nint)buttons : 0,
                DefaultButton = spec.DefaultButton,
                VerificationText = Str(spec.Verification),
                ExpandedInformation = Str(spec.Expanded),
                Footer = Str(spec.Footer),
                FooterIcon = 0,
                Callback = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint, int>)&Callback,
            };
            int button = 0, radio = 0, verified = 0;
            var hr = TaskDialogIndirect(&config, &button, &radio, &verified);
            if (hr < 0 && spec.Icon == DialogIcon.App)
            {
                // No icon resource (a development build): show it with the information icon instead.
                foreach (var p in strings) Marshal.FreeHGlobal(p);
                strings.Clear();
                return ShowCore(spec with { Icon = DialogIcon.Information }, timer);
            }
            Marshal.ThrowExceptionForHR(hr);
            return (button, verified != 0);
        }
        finally
        {
            foreach (var p in strings) Marshal.FreeHGlobal(p);
        }
    }

    [UnmanagedCallersOnly]
    private static int Callback(nint hwnd, uint notification, nint wParam, nint lParam, nint data)
    {
        if (_poll is null) return 0;
        if (notification == TdnTimer)
        {
            var (fraction, step, done) = _poll();
            SendMessageW(hwnd, TdmSetProgressRange, 0, (nint)(1000 << 16));
            SendMessageW(hwnd, TdmSetProgressPos, (nint)Math.Clamp((int)(fraction * 1000), 0, 1000), 0);
            if (step != _lastStep)
            {
                _lastStep = step;
                var text = Marshal.StringToHGlobalUni(step);
                SendMessageW(hwnd, TdmSetElementText, TdeContent, text);
                Marshal.FreeHGlobal(text);
            }
            if (done) SendMessageW(hwnd, TdmClickButton, IdCancel, 0);
            return 0;
        }
        if (notification == TdnButtonClicked)
        {
            // The work closes the dialog itself; until then Cancel only asks it to stop.
            if (_poll().Done) return 0;
            _cancel?.Invoke();
            return 1; // S_FALSE: stay open.
        }
        return 0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Button
    {
        public int Id;
        public nint Text;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Config
    {
        public uint Size;
        public nint Parent;
        public nint Instance;
        public int Flags;
        public int CommonButtons;
        public nint WindowTitle;
        public nint MainIcon;
        public nint MainInstruction;
        public nint Content;
        public uint ButtonCount;
        public nint Buttons;
        public int DefaultButton;
        public uint RadioButtonCount;
        public nint RadioButtons;
        public int DefaultRadioButton;
        public nint VerificationText;
        public nint ExpandedInformation;
        public nint ExpandedControlText;
        public nint CollapsedControlText;
        public nint FooterIcon;
        public nint Footer;
        public nint Callback;
        public nint CallbackData;
        public uint Width;
    }

    [LibraryImport("comctl32.dll")]
    private static partial int TaskDialogIndirect(Config* config, int* button, int* radio, int* verified);

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);
}
