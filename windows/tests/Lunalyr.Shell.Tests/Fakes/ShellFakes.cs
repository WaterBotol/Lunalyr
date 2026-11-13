using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Render.Scene;
using Lunalyr.Shell.Lyrics;
using Lunalyr.Shell.Rendering;

namespace Lunalyr.Shell.Tests.Fakes;

/// <summary>A lyric lookup that answers from a script. It never touches the network.</summary>
internal sealed class FakeLyricLookup : ILyricLookup
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Func<LyricQuery, Func<LyricLookupReport, ValueTask>?, CancellationToken, Task<LyricLookupReport>>> _byTitle = [];
    private readonly List<(LyricQuery Query, CancellationToken Token)> _calls = [];

    public string? DisabledReason { get; set; }

    public WordDistributor? WordDistributor => null;

    public IReadOnlyList<LyricQuery> Queries
    {
        get { lock (_gate) return [.. _calls.Select(c => c.Query)]; }
    }

    public IReadOnlyList<CancellationToken> Tokens
    {
        get { lock (_gate) return [.. _calls.Select(c => c.Token)]; }
    }

    /// <summary>Answers lookups for <paramref name="title"/> with <paramref name="report"/>.</summary>
    public void Answer(string title, LyricLookupReport report) =>
        Script(title, (_, _, _) => Task.FromResult(report));

    /// <summary>Lookups for <paramref name="title"/> wait until the returned source is completed.</summary>
    public TaskCompletionSource<LyricLookupReport> Hold(string title)
    {
        var held = new TaskCompletionSource<LyricLookupReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        Script(title, (_, _, _) => held.Task);
        return held;
    }

    public void Script(string title, Func<LyricQuery, Func<LyricLookupReport, ValueTask>?, CancellationToken, Task<LyricLookupReport>> answer)
    {
        lock (_gate) _byTitle[title] = answer;
    }

    public Task<LyricLookupReport> LookupAsync(LyricQuery query, LyricSourceSnapshot options,
        Func<LyricLookupReport, ValueTask>? onUpdate, CancellationToken cancellationToken)
    {
        Func<LyricQuery, Func<LyricLookupReport, ValueTask>?, CancellationToken, Task<LyricLookupReport>>? answer;
        lock (_gate)
        {
            _calls.Add((query, cancellationToken));
            _byTitle.TryGetValue(query.Title, out answer);
        }

        return answer?.Invoke(query, onUpdate, cancellationToken) ?? Task.FromResult(new LyricLookupReport());
    }
}

/// <summary>A renderer that remembers every call.</summary>
internal sealed class FakeRenderer : IWallpaperRenderer
{
    private readonly Lock _gate = new();
    private readonly List<SceneSnapshot> _scenes = [];

    public int Starts { get; private set; }

    public int Stops { get; private set; }

    public bool IsRunning { get; private set; }

    public (string Background, string Composition) Layers { get; private set; }

    public int FrameRate { get; private set; } = -1;

    public Func<(double Seconds, bool Playing)>? Clock { get; private set; }

    public IReadOnlyList<SceneSnapshot> Scenes
    {
        get { lock (_gate) return [.. _scenes]; }
    }

    public SceneSnapshot? Last
    {
        get { lock (_gate) return _scenes.Count == 0 ? null : _scenes[^1]; }
    }

    public void Start()
    {
        Starts++;
        IsRunning = true;
    }

    public void Stop()
    {
        Stops++;
        IsRunning = false;
    }

    public void SetScene(SceneSnapshot scene)
    {
        lock (_gate) _scenes.Add(scene);
    }

    public void SetLayers(string background, string composition) => Layers = (background, composition);

    private Lunalyr.Platform.Desktop.FloatingLyricsOptions _floating = Lunalyr.Platform.Desktop.FloatingLyricsOptions.Off;

    /// <summary>The floating lyrics' options last handed over.</summary>
    public Lunalyr.Platform.Desktop.FloatingLyricsOptions Floating
    {
        get { lock (_gate) return _floating; }
    }

    public void SetFloating(Lunalyr.Platform.Desktop.FloatingLyricsOptions options)
    {
        lock (_gate) _floating = options;
    }

    /// <summary>The layers the fake says it has (empty: the controller assumes editorial and spoken).</summary>
    public IReadOnlyCollection<string> LayerIds { get; set; } = [];

    public void SetClock(Func<(double Seconds, bool Playing)> clock) => Clock = clock;

    public void SetFrameRate(int framesPerSecond) => FrameRate = framesPerSecond;

    /// <summary>The layers handed over with ReplaceLayer, oldest first.</summary>
    public List<IRenderLayerFactory> Replaced { get; } = [];

    public void ReplaceLayer(IRenderLayerFactory factory) => Replaced.Add(factory);

    public void Dispose() => Stop();
}

/// <summary>Connectivity the test switches by hand.</summary>
internal sealed class FakeConnectivity : ILyricConnectivity
{
    public bool IsOnline { get; set; } = true;

    public event EventHandler? Restored;

    public void Restore()
    {
        IsOnline = true;
        Restored?.Invoke(this, EventArgs.Empty);
    }
}

internal static class TestLyrics
{
    public static LyricResult.Synced Synced(string source, params string[] lines) =>
        new([.. lines.Select((text, i) => new LyricLine
        {
            Start = 10 + i * 4,
            End = 13.5 + i * 4,
            Text = text,
            Words = [new LyricWord(text, 10 + i * 4, 13.5 + i * 4)],
        })], new LyricProvenance("Line-synced", source));

    public static LyricLookupReport Found(LyricResult result) => new() { Result = result, Revision = 1 };
}

/// <summary>A fresh folder under the temp directory, deleted on dispose.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lunalyr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
