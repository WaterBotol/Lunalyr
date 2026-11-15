// Ported from Lunalyr for Mac's AudioCaptureRecovery.swift (AudioCaptureState, AudioCaptureProblem, AudioPlayback,
// AudioCaptureHealth, AudioCaptureGate): Lunalyr's own lines. The Mac's AudioCaptureRecovery struct itself (the switch
// from a failing system-audio tap to Screen Recording capture) traces back to the Verso import and has no Windows
// counterpart: Windows has one capture method, WASAPI loopback. Sentences name this PC and Windows' own causes.

namespace Lunalyr.Core.Visualisers.Capture;

/// <summary>What a fault in System audio is.</summary>
public enum AudioCaptureProblem
{
    /// <summary>Switched on, but no real audio arrives while the player plays here.</summary>
    NotReceiving,
    /// <summary>The stream stopped delivering samples.</summary>
    Stalled,
    /// <summary>Startup is taking unusually long.</summary>
    SlowStart,
    /// <summary>Capture could not start, or stopped with an error.</summary>
    Failed,
}

/// <summary>What System audio is doing, for the settings page and the tray. The controller's status carries the sentence.</summary>
public readonly record struct AudioCaptureState
{
    private AudioCaptureState(AudioCaptureStateKind kind, AudioCaptureProblem? problem = null)
    {
        Kind = kind;
        Problem = problem;
    }

    public AudioCaptureStateKind Kind { get; }

    /// <summary>What needs attention, for <see cref="AudioCaptureStateKind.Attention"/>.</summary>
    public AudioCaptureProblem? Problem { get; }

    /// <summary>Switched off.</summary>
    public static AudioCaptureState Off { get; } = new(AudioCaptureStateKind.Off);
    /// <summary>Switched on, but the wallpaper type, power settings, sleep, Pause wallpaper or nothing on screen pause it.</summary>
    public static AudioCaptureState Paused { get; } = new(AudioCaptureStateKind.Paused);
    /// <summary>Switched on; waiting for something to play.</summary>
    public static AudioCaptureState Standby { get; } = new(AudioCaptureStateKind.Standby);
    public static AudioCaptureState Connecting { get; } = new(AudioCaptureStateKind.Connecting);
    public static AudioCaptureState Receiving { get; } = new(AudioCaptureStateKind.Receiving);
    /// <summary>Samples arrive, but nothing audible is playing.</summary>
    public static AudioCaptureState Quiet { get; } = new(AudioCaptureStateKind.Quiet);

    /// <summary>The user can fix something; the status says what.</summary>
    public static AudioCaptureState Attention(AudioCaptureProblem problem) => new(AudioCaptureStateKind.Attention, problem);

    public bool NeedsAttention => Kind == AudioCaptureStateKind.Attention;

    public override string ToString() => Problem is { } problem ? $"attention({problem})" : Kind.ToString();
}

public enum AudioCaptureStateKind { Off, Paused, Standby, Connecting, Receiving, Quiet, Attention }

/// <summary>
/// What playback means for capture: whether anything is playing on this PC. The followed player can report "playing"
/// with none of its sound here (Spotify Connect to a speaker, say), and loopback then rightly hears silence. That is not
/// a fault, so it is never blamed on Lunalyr and never treated as a broken stream.
/// </summary>
public readonly record struct AudioPlayback(bool PlayerPlaying = false, string? PlayerName = null, bool? PlayerOutputHere = null,
    bool? AnyOutput = null)
{
    /// <summary>The player plays, but nothing of it on this PC.</summary>
    public bool PlayingElsewhere => PlayerPlaying && PlayerOutputHere == false;
    /// <summary>The player plays on this PC, as far as is known.</summary>
    public bool PlayingHere => PlayerPlaying && PlayerOutputHere != false;
    /// <summary>Something plays on this PC. Unknown counts as playing.</summary>
    public bool OutputActive => PlayingHere || (AnyOutput ?? true);
    /// <summary>Names the player in a sentence; "music" when it is unknown.</summary>
    public string PlayerTitle => PlayerName ?? "music";

    /// <summary>While the player plays somewhere else.</summary>
    public string ElsewhereMessage => $"Nothing from {PlayerTitle} is playing on this PC. The visualiser responds once it plays here.";

    /// <summary>How long the output meter must read silence before a playing player counts as playing elsewhere.</summary>
    public const double ElsewhereAfter = 2.0;

    /// <summary>
    /// Playback as Windows can observe it: the followed player's state, and the default output's peak meter
    /// (<paramref name="outputActive"/>; null when it can't be read), silent for <paramref name="outputIdleFor"/>
    /// seconds. Windows can't say which app a sound comes from, so a player that reports playing while the output has
    /// been silent for <see cref="ElsewhereAfter"/> seconds plays somewhere else (Spotify Connect, another output
    /// device, an exclusive-mode app): never a fault. A silent bar shorter than that changes nothing.
    /// </summary>
    public static AudioPlayback Observed(bool playerPlaying, string? playerName, bool? outputActive, double outputIdleFor) =>
        outputActive switch
        {
            null => new AudioPlayback(playerPlaying, playerName),
            true => new AudioPlayback(playerPlaying, playerName, playerPlaying ? true : null, true),
            false => new AudioPlayback(playerPlaying, playerName, outputIdleFor >= ElsewhereAfter ? false : null, false),
        };
}

/// <summary>What delivered samples say about a running capture.</summary>
public enum AudioCaptureHealth
{
    /// <summary>Connected; the first samples have not arrived yet.</summary>
    Waiting,
    Receiving,
    /// <summary>Samples arrive but are quiet; nothing suggests a fault.</summary>
    Quiet,
    /// <summary>Packets never arrived although something plays.</summary>
    NoSamples,
    /// <summary>The stream stopped delivering samples although something plays.</summary>
    Stalled,
    /// <summary>Exact digital silence while the player plays on this PC.</summary>
    NotReceiving,
}

public static class AudioCaptureHealthRules
{
    public const double NoSamplesAfter = 3.0;
    public const double NotReceivingAfter = 8.0;

    public static bool NeedsAttention(this AudioCaptureHealth health) =>
        health is AudioCaptureHealth.NoSamples or AudioCaptureHealth.Stalled or AudioCaptureHealth.NotReceiving;

    /// <summary>
    /// <paramref name="outputActive"/>: anything is playing on this PC. <paramref name="musicPlaying"/>: the player plays
    /// here. <paramref name="digitalSilenceFor"/>: how long every sample has been exactly zero while it did. Windows'
    /// loopback delivers no packets at all while nothing plays, which is never a fault.
    /// </summary>
    public static AudioCaptureHealth Evaluate(double elapsed, int packets, double packetAge, double digitalSilenceFor, bool quiet,
        bool musicPlaying, bool outputActive)
    {
        if (packets == 0) return elapsed > NoSamplesAfter && outputActive ? AudioCaptureHealth.NoSamples : AudioCaptureHealth.Waiting;
        if (packetAge > 1.5) return outputActive ? AudioCaptureHealth.Stalled : AudioCaptureHealth.Quiet;
        if (musicPlaying && digitalSilenceFor >= NotReceivingAfter) return AudioCaptureHealth.NotReceiving;
        return quiet ? AudioCaptureHealth.Quiet : AudioCaptureHealth.Receiving;
    }

    /// <summary>What the status says about a running capture. <paramref name="note"/> explains a reconnection.</summary>
    public static (AudioCaptureState State, string Message) Status(this AudioCaptureHealth health, AudioPlayback playback,
        string methodLabel, string note)
    {
        var idle = playback.PlayingElsewhere ? playback.ElsewhereMessage : "Listening. Play something to see the visualiser respond.";
        return health switch
        {
            AudioCaptureHealth.Waiting => (AudioCaptureState.Connecting,
                note.Length > 0 ? note : playback.OutputActive ? "Connected; waiting for audio samples…" : idle),
            AudioCaptureHealth.Receiving => (AudioCaptureState.Receiving,
                $"Receiving audio · {methodLabel}. Analysed locally; no recording is saved."),
            // A silent player somewhere else explains the silence, even while another app holds a quiet stream open here.
            AudioCaptureHealth.Quiet => (AudioCaptureState.Quiet, playback.OutputActive && !playback.PlayingElsewhere
                ? "Samples are arriving, but they’re silent. If music is audible, it may be playing on another output device; choose Reconnect."
                : idle),
            AudioCaptureHealth.NoSamples => (AudioCaptureState.Attention(AudioCaptureProblem.NotReceiving),
                "No audio samples arrived from the output device. Reconnect to try again."),
            AudioCaptureHealth.Stalled => (AudioCaptureState.Attention(AudioCaptureProblem.Stalled),
                "The audio stream stopped delivering samples. Reconnect to restart it."),
            _ => (AudioCaptureState.Attention(AudioCaptureProblem.NotReceiving),
                $"Not receiving audio while {playback.PlayerTitle} plays. It may be playing on another output device, or an app may be using the device exclusively; Reconnect to try again."),
        };
    }
}

/// <summary>
/// When capture should actually run: a visualiser that is drawing, and audio that is being played. Loopback keeps
/// the capture stream and its thread busy, so capture follows real demand.
/// </summary>
public static class AudioCaptureGate
{
    /// <summary>Short gaps between tracks or brief window changes do not restart capture.</summary>
    public const double OutputHangTime = 10.0;
    public const double HiddenHangTime = 10.0;
    /// <summary>A start the user asked for runs straight away for this long, even before anything plays.</summary>
    public const double UserStartGrace = 60.0;
    public const string StandbyMessageText = "Standing by: capture starts when audio plays.";

    /// <summary>Standing by, perhaps because the player plays somewhere else.</summary>
    public static string StandbyMessage(AudioPlayback playback) => playback.PlayingElsewhere
        ? $"Standing by: nothing from {playback.PlayerTitle} is playing on this PC. Capture starts when it plays here."
        : StandbyMessageText;

    public enum PauseKind
    {
        /// <summary>The chosen wallpaper is not a visualiser.</summary>
        Wallpaper,
        /// <summary>Power settings replaced the visualiser; the sentence explains how.</summary>
        Power,
        /// <summary>The displays or the PC are asleep, or the session is locked or switched away.</summary>
        DisplaysAsleep,
        /// <summary>No surface that could show a visualiser is drawing.</summary>
        Hidden,
        /// <summary>Pause wallpaper froze every wallpaper, and no settings preview shows the visualiser instead.</summary>
        WallpaperPaused,
    }

    public readonly record struct PauseReason(PauseKind Kind, string? PowerMessage = null)
    {
        public static PauseReason Wallpaper => new(PauseKind.Wallpaper);
        public static PauseReason DisplaysAsleep => new(PauseKind.DisplaysAsleep);
        public static PauseReason Hidden => new(PauseKind.Hidden);
        public static PauseReason WallpaperPaused => new(PauseKind.WallpaperPaused);
        public static PauseReason Power(string message) => new(PauseKind.Power, message);

        public string Message => Kind switch
        {
            PauseKind.Wallpaper => "Paused: system audio drives the Visualisers wallpaper only.",
            PauseKind.Power => PowerMessage ?? "",
            PauseKind.DisplaysAsleep => "Paused while the displays sleep.",
            PauseKind.Hidden => "Paused while no visualiser is on screen.",
            _ => "Paused while the wallpaper is paused. It resumes with the wallpaper.",
        };
    }

    public enum DecisionKind { Off, Paused, Standby, Capture }

    public readonly record struct Decision(DecisionKind Kind, PauseReason? Reason = null, double? RecheckAfter = null)
    {
        public static Decision Off => new(DecisionKind.Off);
        public static Decision Standby => new(DecisionKind.Standby);
        public static Decision Paused(PauseReason reason) => new(DecisionKind.Paused, reason);
        public static Decision Capture(double? recheckAfter) => new(DecisionKind.Capture, null, recheckAfter);

        /// <summary>
        /// Whether playback decides what happens next: capture runs, stands by, or waits behind covered or paused
        /// wallpapers, which may come back at any moment. Playback and the output route are observed only then.
        /// </summary>
        public bool FollowsPlayback => Kind switch
        {
            DecisionKind.Capture or DecisionKind.Standby => true,
            DecisionKind.Paused => Reason?.Kind is PauseKind.Hidden or PauseKind.WallpaperPaused,
            _ => false,
        };
    }

    public readonly record struct Inputs
    {
        public bool Enabled { get; init; }
        public bool VisualiserChosen { get; init; }
        public string? PowerPause { get; init; }
        public bool DisplaysAwake { get; init; } = true;
        /// <summary>Seconds since every visualiser surface stopped drawing; null while one draws.</summary>
        public double? SurfacesHiddenFor { get; init; }
        /// <summary>Pause wallpaper left nothing drawing.</summary>
        public bool SurfacesFrozen { get; init; }
        /// <summary>Seconds since audio stopped playing; null while it plays, infinite if nothing has played since capture was armed.</summary>
        public double? OutputIdleFor { get; init; }
        /// <summary>Seconds since the user last started or reconnected capture.</summary>
        public double? SinceUserStart { get; init; }

        public Inputs()
        {
        }
    }

    public static Decision Decide(Inputs inputs)
    {
        if (!inputs.Enabled) return Decision.Off;
        if (!inputs.VisualiserChosen) return Decision.Paused(PauseReason.Wallpaper);
        if (inputs.PowerPause is { } reason) return Decision.Paused(PauseReason.Power(reason));
        if (!inputs.DisplaysAwake) return Decision.Paused(PauseReason.DisplaysAsleep);
        // The user's own pause, with nothing left to show what capture hears until the wallpaper resumes.
        if (inputs.SurfacesFrozen) return Decision.Paused(PauseReason.WallpaperPaused);
        // A user start runs straight away, even before anything plays, so the result can be checked.
        if (inputs.SinceUserStart is { } since && since >= 0 && since < UserStartGrace) return Decision.Capture(UserStartGrace - since);
        var hidden = inputs.SurfacesHiddenFor is { } h ? Math.Max(0, h) : (double?)null;
        var idle = inputs.OutputIdleFor is { } i ? Math.Max(0, i) : (double?)null;
        if (hidden >= HiddenHangTime) return Decision.Paused(PauseReason.Hidden);
        if (idle >= OutputHangTime) return Decision.Standby;
        // A hang time that has started, even this instant, needs a re-evaluation when it runs out.
        double? pending = null;
        if (hidden is { } hh) pending = HiddenHangTime - hh;
        if (idle is { } ii) pending = pending is { } p ? Math.Min(p, OutputHangTime - ii) : OutputHangTime - ii;
        return Decision.Capture(pending);
    }

    /// <summary>
    /// Why power settings replaced the chosen visualiser, or null if they did not. <paramref name="shownTitle"/> names
    /// what is shown instead ("line patterns", "colour only").
    /// </summary>
    public static string? PowerPause(bool visualiserChosen, bool visualiserShown, string shownTitle, bool lowPowerVisuals,
        bool energySaver, bool onBattery)
    {
        if (!visualiserChosen || visualiserShown) return null;
        var shown = shownTitle.ToLowerInvariant();
        var cause = lowPowerVisuals ? $"“Lighter wallpaper” is showing {shown}"
            : energySaver ? $"Energy saver is showing {shown}"
            : onBattery ? $"on battery, Lunalyr is showing {shown}"
            : $"power settings are showing {shown}";
        return $"Paused: {cause}. System audio resumes with the visualiser.";
    }

    /// <summary>Whether anything that could show a visualiser is drawing. Unknown (no surfaces yet) counts as drawing.</summary>
    public static bool AnySurfaceDrawing(IReadOnlyCollection<SurfaceDrawing> surfaces) =>
        surfaces.Count == 0 || surfaces.Contains(SurfaceDrawing.Drawing);

    /// <summary>Whether Pause wallpaper left nothing drawing: it is on, and every surface there is is one it froze.</summary>
    public static bool Frozen(IReadOnlyCollection<SurfaceDrawing> surfaces, bool wallpaperPaused) =>
        wallpaperPaused && surfaces.All(s => s == SurfaceDrawing.Frozen);
}

/// <summary>What one surface that could show a visualiser is doing, by the rule its own frame clock follows.</summary>
public enum SurfaceDrawing { Drawing, Hidden, Frozen }
