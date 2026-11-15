// The first-run welcome (milestone 8, reworked to eight steps from the Mac's Test Build 88): its steps and what moving
// between them does. Ported from the Mac's OnboardingFlow.swift (Sources/UI/Onboarding/, Lunalyr's own lines:
// blame-checked against a99fd65, none). Windows asks
// no permission for a player (GSMTC needs none), so the Mac's "ask about undecided players on Continue" has nothing to
// do here and is left out; the order of the steps, Back, Continue and the endings are the Mac's.

namespace Lunalyr.Shell.Onboarding;

/// <summary>How the welcome ended.</summary>
public enum OnboardingEnding
{
    /// <summary>Open Settings: finish, then show Settings (first run only).</summary>
    OpenSettings,

    /// <summary>Done.</summary>
    Done,

    /// <summary>The window's close button (or Esc, or Alt+F4) before the end.</summary>
    Closed,
}

/// <summary>The eight steps, in order.</summary>
public enum OnboardingStep
{
    Welcome,
    Music,
    Look,
    Videos,
    Lyrics,
    Extras,
    Tray,
    Done,
}

/// <summary>The look step's two halves: the wallpaper, or the lyrics' layout (the Mac's <c>LookPart</c>).</summary>
public enum OnboardingLookPart
{
    Wallpaper,
    Lyrics,
}

/// <summary>The steps' names and positions.</summary>
public static class OnboardingSteps
{
    public static IReadOnlyList<OnboardingStep> All { get; } = Enum.GetValues<OnboardingStep>();

    /// <summary>The step's name, for Narrator and the captures.</summary>
    public static string Title(this OnboardingStep step) => step switch
    {
        OnboardingStep.Welcome => "Welcome",
        OnboardingStep.Music => "Your music",
        OnboardingStep.Look => "Pick a look",
        OnboardingStep.Videos => "Music videos",
        OnboardingStep.Lyrics => "Lyrics & readings",
        OnboardingStep.Extras => "Extras",
        OnboardingStep.Tray => "The tray",
        _ => "Done",
    };

    /// <summary>"Step 2 of 8": the step dots, to Narrator.</summary>
    public static string Position(this OnboardingStep step) => $"Step {(int)step + 1} of {All.Count}";

    /// <summary>The short name <c>--onboarding-step</c> takes and the capture files use: welcome, music, look, videos, lyrics, extras, tray, done.</summary>
    public static string Name(this OnboardingStep step) => step.ToString().ToLowerInvariant();

    /// <summary>
    /// A step from <c>--onboarding-step</c>: its short name, its title, or its number (1–8), in any case; null when it is
    /// none of them.
    /// </summary>
    public static OnboardingStep? Parse(string? text)
    {
        var wanted = Letters(text);
        if (wanted.Length == 0)
        {
            return int.TryParse(text?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
                && number >= 1 && number <= All.Count
                ? All[number - 1]
                : null;
        }
        foreach (var step in All)
        {
            if (Letters(step.Name()) == wanted || Letters(step.Title()) == wanted) return step;
        }
        // The Mac's name for the tray step.
        return wanted is "menubar" ? OnboardingStep.Tray : null;

        static string Letters(string? s) => new([.. (s ?? "").ToLowerInvariant().Where(char.IsAsciiLetter)]);
    }

    /// <summary>The look part's name: Wallpaper or Lyrics (the Mac's segmented control).</summary>
    public static string Title(this OnboardingLookPart part) => part == OnboardingLookPart.Wallpaper ? "Wallpaper" : "Lyrics";

    /// <summary>
    /// What <c>--onboarding-step</c> can show for a capture: a step (as <see cref="Parse"/>), <c>look-lyrics</c> (the look
    /// step on its Lyrics half) or <c>opening</c> (the glass logo opening over the welcome); null when it is none of them.
    /// </summary>
    public static OnboardingCapturePage? ParsePage(string? text)
    {
        var letters = new string([.. (text ?? "").ToLowerInvariant().Where(char.IsAsciiLetter)]);
        if (letters is "opening" or "intro") return new(OnboardingStep.Welcome, OnboardingLookPart.Wallpaper, Opening: true);
        if (letters is "looklyrics" or "lyricslayout" or "layouts") return new(OnboardingStep.Look, OnboardingLookPart.Lyrics, Opening: false);
        return Parse(text) is { } step ? new(step, OnboardingLookPart.Wallpaper, Opening: false) : null;
    }
}

/// <summary>A page of the welcome for <c>--onboarding-capture</c>: a step, the look step's half, or the opening.</summary>
public sealed record OnboardingCapturePage(OnboardingStep Step, OnboardingLookPart LookPart, bool Opening)
{
    /// <summary>The capture's name: the step's, "look-lyrics" or "opening".</summary>
    public string Name => Opening ? "opening" : Step == OnboardingStep.Look && LookPart == OnboardingLookPart.Lyrics ? "look-lyrics" : Step.Name();
}

/// <summary>Which way the step on screen changed: the window slides the new one in from that side.</summary>
public sealed record OnboardingStepChange(OnboardingStep From, OnboardingStep To)
{
    public bool Forward => To > From;
}

/// <summary>
/// The welcome as a plain model, so its order can be tested without a window:
/// <code>
/// var flow = new OnboardingFlow(firstRun: true, onFinish: ending => …);
/// flow.Advance();                          // Welcome → Your music
/// flow.Finish(OnboardingEnding.OpenSettings);
/// </code>
/// Nothing here asks Windows for anything: Lunalyr follows players without a permission prompt.
/// </summary>
public sealed class OnboardingFlow
{
    private readonly Action<OnboardingEnding> _onFinish;

    /// <param name="firstRun">First run: finishing can open Settings. Shown again from Settings › General, the same steps run and finishing just closes.</param>
    public OnboardingFlow(bool firstRun, OnboardingStep step = OnboardingStep.Welcome, Action<OnboardingEnding>? onFinish = null)
    {
        FirstRun = firstRun;
        Step = Enum.IsDefined(step) ? step : OnboardingStep.Welcome;
        _onFinish = onFinish ?? (_ => { });
    }

    public OnboardingStep Step { get; private set; }

    public bool FirstRun { get; }

    /// <summary>Which half of the look step is showing; kept here so Back returns to it (the Mac's <c>lookPart</c>).</summary>
    public OnboardingLookPart LookPart { get; set; } = OnboardingLookPart.Wallpaper;

    /// <summary>The step changed (Continue or Back).</summary>
    public event EventHandler<OnboardingStepChange>? StepChanged;

    public bool CanGoBack => Step != OnboardingStep.Welcome;

    public bool IsLastStep => Step == OnboardingStep.Done;

    /// <summary>The step Back goes to, or null on the first.</summary>
    public OnboardingStep? Previous => CanGoBack ? Step - 1 : null;

    /// <summary>The step Continue goes to, or null on the last.</summary>
    public OnboardingStep? Next => IsLastStep ? null : Step + 1;

    /// <summary>Continue. Stops at the end.</summary>
    public void Advance()
    {
        if (Next is { } next) Go(next);
    }

    public void Back()
    {
        if (Previous is { } previous) Go(previous);
    }

    /// <summary>
    /// Says how it ended. Shown again, Open Settings is just Done (Settings is open already: that is where it was shown
    /// from).
    /// </summary>
    public void Finish(OnboardingEnding ending) =>
        _onFinish(FirstRun ? ending : ending == OnboardingEnding.OpenSettings ? OnboardingEnding.Done : ending);

    /// <summary>The buttons at the foot of the step on screen, in reading order (the primary one last).</summary>
    public IReadOnlyList<OnboardingButton> Buttons
    {
        get
        {
            var buttons = new List<OnboardingButton>();
            if (CanGoBack) buttons.Add(OnboardingButton.Back);
            if (!IsLastStep) buttons.Add(OnboardingButton.Continue);
            else if (FirstRun)
            {
                buttons.Add(OnboardingButton.Done);
                buttons.Add(OnboardingButton.OpenSettings);
            }
            else buttons.Add(OnboardingButton.Done);
            return buttons;
        }
    }

    /// <summary>What Enter presses: the last of <see cref="Buttons"/>.</summary>
    public OnboardingButton DefaultButton => Buttons[^1];

    /// <summary>Presses a footer button.</summary>
    public void Press(OnboardingButton button)
    {
        switch (button)
        {
            case OnboardingButton.Back: Back(); break;
            case OnboardingButton.Continue: Advance(); break;
            case OnboardingButton.Done: Finish(OnboardingEnding.Done); break;
            case OnboardingButton.OpenSettings: Finish(OnboardingEnding.OpenSettings); break;
        }
    }

    private void Go(OnboardingStep target)
    {
        var from = Step;
        if (from == target) return;
        Step = target;
        StepChanged?.Invoke(this, new OnboardingStepChange(from, target));
    }
}

/// <summary>The footer's buttons.</summary>
public enum OnboardingButton
{
    Back,
    Continue,
    Done,
    OpenSettings,
}
