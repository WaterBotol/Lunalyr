using System.Globalization;
using System.Text.Json;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>
/// Lenient reads of service JSON, where a field may be a string one day and a number the next. Missing or mistyped
/// fields read as null, never throw.
/// </summary>
internal static class JsonRead
{
    public static JsonElement? Get(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    public static JsonElement? Get(this JsonElement? element, string name) => element is { } e ? e.Get(name) : null;

    /// <summary>A string field, or a number written as text.</summary>
    public static string? Str(this JsonElement? element, string name) => element.Get(name).AsString();

    public static string? Str(this JsonElement element, string name) => element.Get(name).AsString();

    public static string? AsString(this JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.String => value.Value.GetString(),
        JsonValueKind.Number => value.Value.GetRawText(),
        _ => null,
    };

    /// <summary>A number field, or a string holding one.</summary>
    public static double? Num(this JsonElement element, string name) => element.Get(name).AsNumber();

    public static double? AsNumber(this JsonElement? value)
    {
        switch (value?.ValueKind)
        {
            case JsonValueKind.Number when value.Value.TryGetDouble(out var number) && double.IsFinite(number):
                return number;
            case JsonValueKind.String when double.TryParse(value.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                                           && double.IsFinite(parsed):
                return parsed;
            default:
                return null;
        }
    }

    public static bool? Bool(this JsonElement element, string name) => element.Get(name)?.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    /// <summary>The items of an array field; empty when missing or not an array.</summary>
    public static IEnumerable<JsonElement> Items(this JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    public static IEnumerable<JsonElement> Items(this JsonElement element, string name) => element.Get(name).Items();
}
