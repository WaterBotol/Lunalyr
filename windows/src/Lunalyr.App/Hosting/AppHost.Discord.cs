// Discord status and the tray lyric (milestone 7): built and dropped as their settings change, fed by the controller.

using Lunalyr.App.Settings;
using Lunalyr.App.Ticker;
using Lunalyr.Core.Extras;
using Lunalyr.Core.Lyrics.Sources;
using Lunalyr.Platform.Discord;
using Lunalyr.Shell;
using Lunalyr.Shell.Extras;
using Lunalyr.Shell.Settings;

namespace Lunalyr.App.Hosting;

internal sealed partial class AppHost
{
    /// <summary>
    /// Discord's own IPC pipes (<c>\\.\pipe\discord-ipc-0</c> … <c>9</c>). Named here and nowhere else, so nothing but
    /// the running app can reach the real Discord; <c>--discord-pipe</c> swaps in a pipe of one's own for checks.
    /// </summary>
    private const string DiscordPipePrefix = "discord-ipc-";

    private readonly Lock _extrasGate = new();
    private DiscordPresenceService? _discord;
    private CatalogueMatches? _catalogue;
    private TrayTickerWindow? _ticker;
    private event Action? DiscordStatusChanged;

    /// <summary>Why Discord status can't run this time, or null: <c>--demo</c> and <c>--no-network</c> send nothing to Discord.</summary>
    public string? DiscordOffReason => _options.DiscordPipe is not null ? null
        : _options.Demo ? "--demo"
        : _options.NoNetwork ? "--no-network"
        : null;

    /// <summary>What Settings › General's Discord rows need.</summary>
    internal DiscordHooks DiscordHooks => new(
        () => _discord?.Status ?? new DiscordConnectionStatus.Off(),
        changed => DiscordStatusChanged += changed,
        changed => DiscordStatusChanged -= changed,
        () => DiscordOffReason);

    private void StartDiscordAndTicker(AppController controller)
    {
        controller.StatusChanged += (_, _) =>
        {
            _discord?.Update();
            _ticker?.Refresh();
        };
        _settings.Changed += (_, e) => ApplyExtras(controller, e.NewValue);
        ApplyExtras(controller, _settings.Current);
    }

    private void ApplyExtras(AppController controller, AppSettings settings)
    {
        if (_quitting) return;
        DiscordPresenceService? stopping = null;
        lock (_extrasGate)
        {
            if (settings.DiscordPresence && DiscordOffReason is null)
            {
                if (_discord is null)
                {
                    _discord = CreateDiscord(controller);
                    _discord.Start();
                }
                else
                {
                    _discord.Update();
                }
            }
            else if (_discord is not null)
            {
                stopping = _discord;
                _discord = null;
            }

            if (settings.TrayLyric)
            {
                if (_ticker is null)
                {
                    // --player-debug ticker-unpaused (checks, only with --quiet-windows): the strip shows the sung line
                    // while the check's wallpaper stays paused, so the lyric states can be captured without drawing a
                    // wallpaper over the desktop.
                    Func<ExtrasNow> read = _options.QuietWindows && _options.PlayerDebug.Contains("ticker-unpaused")
                        ? () => controller.ReadExtras() with { WallpaperPaused = false }
                        : controller.ReadExtras;
                    _ticker = new TrayTickerWindow(read, () => _settings.Current.TrayLyricLength, () => ReduceMotion, Log, _options.QuietWindows);
                    _ticker.Start();
                    _log.Info("Tray lyric is on.");
                }
                else
                {
                    _ticker.Refresh();
                }
            }
            else if (_ticker is not null)
            {
                _ticker.Stop();
                _ticker = null;
                _log.Info("Tray lyric is off.");
            }
        }

        if (stopping is not null)
        {
            _ = stopping.DisposeAsync().AsTask();
            DiscordStatusChanged?.Invoke();
        }
    }

    private DiscordPresenceService CreateDiscord(AppController controller)
    {
        var prefix = _options.DiscordPipe ?? DiscordPipePrefix;
        if (_options.DiscordPipe is not null) _log.Info($"Discord status dials the pipes {prefix}0…9 (--discord-pipe), not Discord's.");
        var transport = new DiscordTransport(new DiscordPipeDialer(prefix).Dialer, DiscordPresence.ApplicationId, logger: Log);
        _catalogue ??= new CatalogueMatches(CatalogueSearch());
        var service = new DiscordPresenceService(controller.ReadExtras, () => _settings.Current.DiscordOptions(), transport, _catalogue, logger: Log);
        service.StatusChanged += () => DiscordStatusChanged?.Invoke();
        return service;
    }

    /// <summary>Apple's public search for Discord's cover and song link: the title and artist only. None under --demo or --no-network.</summary>
    private CatalogueSearch? CatalogueSearch()
    {
        if (_options.Demo || _options.NoNetwork) return null;
        var http = new LyricHttpClient(LyricHttpClient.CreateDefaultHttpClient(), new LyricHttpOptions { UserAgent = UserAgent }, Log);
        return async (title, artist, ct) =>
        {
            try
            {
                var reply = await http.GetAsync("Apple’s catalogue", DiscordPresence.SearchUri(title, artist), TimeSpan.FromSeconds(6), null, ct)
                    .ConfigureAwait(false);
                return reply.IsOk && reply.Body.Length <= ItunesCatalogue.MaxReplyBytes ? reply.Body : null;
            }
            catch (LyricSourceException)
            {
                return null;
            }
        };
    }

    /// <summary>On quitting: the Discord activity cleared (bounded), the strip closed, and <c>--ticker-capture</c> written.</summary>
    private async Task StopDiscordAndTickerAsync()
    {
        DiscordPresenceService? discord;
        TrayTickerWindow? ticker;
        lock (_extrasGate)
        {
            discord = _discord;
            ticker = _ticker;
            _discord = null;
            _ticker = null;
        }

        if (_options.TickerCapturePath is { } path) WriteTickerCapture(ticker, path);
        ticker?.Stop();
        if (discord is not null)
        {
            try
            {
                await discord.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _log.Warn("Discord status: clearing the activity took too long.");
            }
        }
    }

    /// <summary><c>--ticker-capture</c>: the strip's last frame as a PNG, and its styles in the log (read only).</summary>
    private void WriteTickerCapture(TrayTickerWindow? ticker, string path)
    {
        if (ticker is null)
        {
            _log.Info("Tray lyric capture: the tray lyric is off.");
            return;
        }
        nint hwnd = ticker.Hwnd;
        if (hwnd != 0)
        {
            long ex = TickerNative.GetWindowLongPtr(hwnd, -20), style = TickerNative.GetWindowLongPtr(hwnd, -16);
            _log.Info($"Tray lyric window {hwnd:X}: style 0x{style:X8}, ex-style 0x{ex:X8}, visible {TickerNative.IsWindowVisible(hwnd)}, "
                + $"foreground is it: {TickerNative.GetForegroundWindow() == hwnd}.");
        }
        if (ticker.LastFrame() is not { } frame)
        {
            _log.Info("Tray lyric capture: nothing was drawn.");
            return;
        }
        try
        {
            var png = TickerPng.FromBgra(frame.Bgra, frame.Width, frame.Height);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, png);
            _log.Info($"Tray lyric captured to {path} ({frame.Width}x{frame.Height}).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Warn($"Couldn't capture the tray lyric to {path}.", ex);
        }
    }
}
