using System.Text.Json.Serialization;

namespace Lunalyr.Core.Lyrics.Storage;

// The on-disk shape of a saved lyric entry. Kept apart from the shared lyric types so those carry no serialisation
// attributes; optional fields may be missing in older files and still decode.

internal sealed class SavedEntryDto
{
    [JsonPropertyName("result")]
    public SavedResultDto? Result { get; set; }

    [JsonPropertyName("storedAt")]
    public DateTimeOffset StoredAt { get; set; }

    /// <summary>Mac raw source identifier; present for automatic online choices.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("artist")]
    public string? Artist { get; set; }

    [JsonPropertyName("duration")]
    public double? Duration { get; set; }
}

internal sealed class SavedResultDto
{
    /// <summary>"synced" or "plain".</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("precision")]
    public string Precision { get; set; } = "";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("lines")]
    public List<SavedLineDto>? Lines { get; set; }

    [JsonPropertyName("text")]
    public List<string>? Text { get; set; }
}

internal sealed class SavedLineDto
{
    [JsonPropertyName("start")]
    public double Start { get; set; }

    [JsonPropertyName("end")]
    public double End { get; set; }

    [JsonPropertyName("endFromNextLine")]
    public bool? EndFromNextLine { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("words")]
    public List<SavedWordDto>? Words { get; set; }

    [JsonPropertyName("isInstrumental")]
    public bool IsInstrumental { get; set; }

    [JsonPropertyName("wordTimed")]
    public bool WordTimed { get; set; }

    [JsonPropertyName("romanization")]
    public string? Romanization { get; set; }

    [JsonPropertyName("translation")]
    public string? Translation { get; set; }

    [JsonPropertyName("credit")]
    public bool? Credit { get; set; }
}

internal sealed class SavedWordDto
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("start")]
    public double Start { get; set; }

    [JsonPropertyName("end")]
    public double End { get; set; }
}

internal sealed class ArtistAliasEntryDto
{
    [JsonPropertyName("names")]
    public List<string> Names { get; set; } = [];

    /// <summary>MusicBrainz's country for the artist, "" when it names none; absent (null) in entries stored before countries were kept.</summary>
    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("storedAt")]
    public DateTimeOffset StoredAt { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SavedEntryDto))]
[JsonSerializable(typeof(Dictionary<string, ArtistAliasEntryDto>))]
internal sealed partial class SavedLyricsJsonContext : JsonSerializerContext;
