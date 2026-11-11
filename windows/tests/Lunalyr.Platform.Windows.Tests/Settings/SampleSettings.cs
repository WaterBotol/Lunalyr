using System.Text.Json.Serialization;
using Lunalyr.Platform.Settings;

namespace Lunalyr.Platform.Windows.Tests.Settings;

/// <summary>Stand-in settings for tests only; the real keys come from the Mac app later.</summary>
internal sealed record SampleSettings
{
    public string Theme { get; init; } = "system";
    public int FontSize { get; init; } = 32;
    public bool ShowTranslation { get; init; } = true;
    public int Counter { get; init; }
    public SampleWindow Window { get; init; } = new();
}

internal sealed record SampleWindow
{
    public int Width { get; init; } = 800;
    public int Height { get; init; } = 600;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SampleSettings))]
internal sealed partial class SampleSettingsJsonContext : JsonSerializerContext;

internal static class SampleStore
{
    public static SettingsStoreOptions<SampleSettings> Options(TempFolder temp, TimeProvider? time = null) => new()
    {
        Folder = temp.Path,
        Name = "settings",
        TypeInfo = SampleSettingsJsonContext.Default.SampleSettings,
        CreateDefaults = () => new SampleSettings(),
        TimeProvider = time ?? new ManualTimeProvider(),
        IoRetryDelay = TimeSpan.FromMilliseconds(1),
    };

    /// <summary>Opens a store, applies <paramref name="value"/>, flushes and closes it.</summary>
    public static async Task WriteAsync(TempFolder temp, SampleSettings value)
    {
        await using var store = SettingsStore<SampleSettings>.Open(Options(temp));
        store.Set(value);
        Assert.True(await store.FlushAsync());
    }

    public static Task<bool> WaitForSaveAsync<T>(SettingsStore<T> store)
        where T : class
    {
        var saved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Saved += () => saved.TrySetResult(true);
        return saved.Task;
    }
}
