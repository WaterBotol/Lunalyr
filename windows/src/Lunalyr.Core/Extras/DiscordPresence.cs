// Discord Rich Presence's rules, ported from the Mac's DiscordPresence.swift (Sources/Extras, Lunalyr's own lines; the
// blame against a99fd65 finds no Verso line in it): the frame format, Discord's field and button limits, the activity
// it is shown, the "Play this song" link, the Apple catalogue match, the rate limit and the line read ahead of the
// music. Listen-along's party, join secret and join events are left out: Windows has no listen-along.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.MusicVideo;

namespace Lunalyr.Core.Extras;

/// <summary>What the small status under the user's name shows (the Mac's <c>DiscordStatusText</c>).</summary>
public enum DiscordStatusText
{
    Lyric,
    Song,
    Artist,
}

public static class DiscordStatusTexts
{
    public static IReadOnlyList<DiscordStatusText> All { get; } = [DiscordStatusText.Lyric, DiscordStatusText.Song, DiscordStatusText.Artist];

    /// <summary>The stored value: the Mac's raw value.</summary>
    public static string Raw(this DiscordStatusText text) => text switch
    {
        DiscordStatusText.Song => "song",
        DiscordStatusText.Artist => "artist",
        _ => "lyric",
    };

    public static DiscordStatusText? Parse(string? raw) => raw switch
    {
        "lyric" => DiscordStatusText.Lyric,
        "song" => DiscordStatusText.Song,
        "artist" => DiscordStatusText.Artist,
        _ => null,
    };

    public static string Title(this DiscordStatusText text) => text switch
    {
        DiscordStatusText.Song => "Song",
        DiscordStatusText.Artist => "Artist",
        _ => "Lyric line",
    };
}

/// <summary>What the user chose to share. Part of the activity, so a change sends it again.</summary>
public sealed record DiscordOptions
{
    public DiscordStatusText StatusText { get; init; } = DiscordStatusText.Lyric;
    public bool ShowsLyric { get; init; } = true;
    public bool ShowsCover { get; init; } = true;
    public bool ShowsButton { get; init; } = true;

    /// <summary>A "Play this song" button: a song.link page with the song on every service.</summary>
    public bool ShowsSongButton { get; init; } = true;

    public bool ShowsProgress { get; init; } = true;
    public bool ShowsWhenPaused { get; init; }

    /// <summary>How far ahead of the music the line is read, so it lands as it is sung.</summary>
    public double LyricLead { get; init; } = 2.5;
}

/// <summary>What is shown on Discord: the song, the line, the bar's timestamps and what the user chose to share.</summary>
public sealed record DiscordPlaying
{
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public string? Album { get; init; }

    /// <summary>The line being sung, or null between lines and without timed lyrics.</summary>
    public string? Line { get; init; }

    /// <summary>When the song would have started, in Unix seconds.</summary>
    public long Start { get; init; }

    /// <summary>When it ends, in Unix seconds.</summary>
    public long? End { get; init; }

    public bool Paused { get; init; }
    public string? CoverUrl { get; init; }

    /// <summary>A page that opens the song on any service, when it was found.</summary>
    public string? SongUrl { get; init; }

    public DiscordOptions Options { get; init; } = new();
}

/// <summary>Discord events Lunalyr acts on.</summary>
public abstract record DiscordEvent
{
    /// <summary>The handshake's reply, with the user's display name.</summary>
    public sealed record Ready(string? UserName) : DiscordEvent;

    public sealed record Error(int Code, string Message) : DiscordEvent;

    /// <summary>The connection to Discord closed (Discord quit or restarted).</summary>
    public sealed record Disconnected : DiscordEvent;
}

/// <summary>Whether Discord is there to show the presence, for status lines.</summary>
public abstract record DiscordConnectionStatus
{
    /// <summary>Discord presence is off.</summary>
    public sealed record Off : DiscordConnectionStatus;

    /// <summary>On, but Discord isn't running or hasn't answered yet.</summary>
    public sealed record WaitingForDiscord : DiscordConnectionStatus;

    /// <summary>Discord answered, as this user (when it said who).</summary>
    public sealed record Connected(string? Name) : DiscordConnectionStatus;
}

/// <summary>The Apple catalogue's best match for a song: its cover (512 px) and its catalogue ID.</summary>
public sealed record CatalogueMatch(string? Cover, long? TrackId);

/// <summary>Discord Rich Presence's pure rules (the Mac's <c>DiscordPresence</c> statics).</summary>
public static class DiscordPresence
{
    /// <summary>
    /// The Discord application the presence is shown as: the Mac's own ("Lunalyr" in the Developer Portal). Rich Presence
    /// over the local pipe needs only this public ID, no secret and no sign-in.
    /// </summary>
    public const string ApplicationId = "1568919234367717506";

    public const string GetLunalyrUrl = "https://github.com/WaterBotol/Lunalyr/releases/latest";
    public const string IconUrl = "https://raw.githubusercontent.com/WaterBotol/Lunalyr/main/Sources/Assets.xcassets/AppIcon.appiconset/icon_256.png";
    public const string PlaySongLabel = "Play this song";
    public const string GetLunalyrLabel = "Get Lunalyr";

    /// <summary>What is shown for a break in the singing.</summary>
    public const string InstrumentalText = "♪ Instrumental";

    /// <summary>How often the line is checked while a song plays.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(0.5);

    /// <summary>How often it is checked while paused or with nothing playing: only a backstop.</summary>
    public static readonly TimeSpan IdleTickInterval = TimeSpan.FromSeconds(5);

    /// <summary>How often a missing Discord is looked for again.</summary>
    public static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(20);

    /// <summary>The longest lead the settings allow (the Mac's <c>discordLeadRange</c>, 0…6 s).</summary>
    public const double LeadMax = 6;

    /// <summary>Fast while a song plays or an update is held back for the rate limit; slow otherwise.</summary>
    public static TimeSpan TickFor(bool playing, bool heldBack) => playing || heldBack ? TickInterval : IdleTickInterval;

    // ---- Protocol ----

    public enum Opcode : uint
    {
        Handshake = 0,
        Frame = 1,
        Close = 2,
        Ping = 3,
        Pong = 4,
    }

    /// <summary>One IPC packet: opcode and payload length (little endian), then JSON.</summary>
    public static byte[] Frame(Opcode opcode, JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var json = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var data = new byte[8 + json.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)json.Length);
        json.CopyTo(data, 8);
        return data;
    }

    /// <summary>
    /// Discord wants 2…128 characters in each text field and refuses the whole activity otherwise. It counts UTF-16
    /// units, so a line is measured that way and cut between whole characters (never inside a cluster or an emoji).
    /// </summary>
    public static string Field(string text)
    {
        var clipped = (text ?? "").Trim();
        if (clipped.Length > 128)
        {
            // The longest run of whole characters that leaves room for the ellipsis (Swift's removeLast() until it fits).
            var cut = 0;
            var rest = clipped.AsSpan();
            while (!rest.IsEmpty)
            {
                var length = StringInfo.GetNextTextElementLength(rest);
                if (length <= 0 || cut + length > 127) break;
                cut += length;
                rest = rest[length..];
            }
            clipped = clipped[..cut] + "…";
        }
        return clipped.Length < 2 ? clipped + " ♪" : clipped;
    }

    /// <summary>
    /// Discord refuses the whole activity over one bad button: the label must be 1…32 characters and the link http(s),
    /// well formed and at most 512.
    /// </summary>
    public static bool IsValidButton(string label, string url)
    {
        if (label is null || url is null || label.Length is < 1 or > 32 || url.Length > 512) return false;
        if (url.Contains(' ', StringComparison.Ordinal) || !Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;
        return (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp) && !string.IsNullOrEmpty(parsed.Host);
    }

    /// <summary>
    /// song.link for a song: from Spotify's own ID when Spotify gave one, else from Apple's catalogue ID. Null for local
    /// files and unknown songs. Windows' media sessions never carry a Spotify track ID, so on Windows the link always
    /// comes from Apple's catalogue.
    /// </summary>
    public static string? SongLink(string? spotifyUri, long? appleTrackId)
    {
        const string prefix = "spotify:track:";
        if (spotifyUri is not null && spotifyUri.StartsWith(prefix, StringComparison.Ordinal))
        {
            var id = spotifyUri[prefix.Length..];
            // Spotify IDs are base62; anything else would make a link Discord refuses.
            if (id.Length > 0 && id.All(char.IsAsciiLetterOrDigit)) return "https://song.link/s/" + id;
        }
        return appleTrackId is > 0 ? "https://song.link/i/" + appleTrackId.Value.ToString(CultureInfo.InvariantCulture) : null;
    }

    /// <summary>The activity object for SET_ACTIVITY.</summary>
    public static JsonObject Activity(DiscordPlaying playing)
    {
        ArgumentNullException.ThrowIfNull(playing);
        var options = playing.Options;
        var hover = playing.Artist;
        if (!string.IsNullOrEmpty(playing.Album)) hover += " — " + playing.Album;
        var song = playing.Artist.Length == 0 ? playing.Title : $"{playing.Title} · {playing.Artist}";
        var fallback = playing.Artist.Length == 0 ? playing.Title : playing.Artist;
        var lyric = options.ShowsLyric ? playing.Line : null;

        var state = lyric ?? fallback;
        var displayType = 1; // 0 name, 1 state, 2 details
        switch (options.StatusText)
        {
            case DiscordStatusText.Song:
                displayType = 2;
                break;
            case DiscordStatusText.Artist:
                state = fallback;
                break;
        }

        var assets = new JsonObject { ["large_text"] = Field(hover) };
        if (options.ShowsCover && playing.CoverUrl is { } cover)
        {
            assets["large_image"] = cover;
            assets["small_image"] = IconUrl;
            assets["small_text"] = "Lunalyr";
        }
        else
        {
            assets["large_image"] = IconUrl;
        }

        var activity = new JsonObject
        {
            ["type"] = 2, // Listening
            ["status_display_type"] = displayType,
            ["details"] = Field(playing.Paused ? "Paused · " + song : song),
            ["state"] = Field(playing.Paused ? fallback : state),
            ["assets"] = assets,
        };

        var buttons = new List<(string Label, string Url)>();
        if (options.ShowsSongButton && playing.SongUrl is { } songUrl) buttons.Add((PlaySongLabel, songUrl));
        if (options.ShowsButton) buttons.Add((GetLunalyrLabel, GetLunalyrUrl));
        buttons.RemoveAll(button => !IsValidButton(button.Label, button.Url));
        if (buttons.Count > 0)
        {
            var array = new JsonArray();
            foreach (var (label, url) in buttons) array.Add((JsonNode)new JsonObject { ["label"] = label, ["url"] = url });
            activity["buttons"] = array;
        }

        if (options.ShowsProgress && !playing.Paused)
        {
            var timestamps = new JsonObject { ["start"] = playing.Start };
            if (playing.End is { } end) timestamps["end"] = end;
            activity["timestamps"] = timestamps;
        }
        return activity;
    }

    /// <summary>SET_ACTIVITY with the activity, or with none to clear it.</summary>
    public static JsonObject SetActivityPayload(JsonObject? activity, int pid, string nonce)
    {
        var args = new JsonObject { ["pid"] = pid };
        if (activity is not null) args["activity"] = activity;
        return new JsonObject { ["cmd"] = "SET_ACTIVITY", ["args"] = args, ["nonce"] = nonce };
    }

    /// <summary>The handshake: protocol version 1 and the application.</summary>
    public static JsonObject HandshakePayload(string clientId) => new() { ["v"] = 1, ["client_id"] = clientId };

    /// <summary>The event in a frame from Discord, or null for anything Lunalyr doesn't act on.</summary>
    public static DiscordEvent? EventFromFrame(ReadOnlySpan<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
            var evt = root.TryGetProperty("evt", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            switch (evt)
            {
                case "READY":
                    return new DiscordEvent.Ready(data.ValueKind == JsonValueKind.Object && data.TryGetProperty("user", out var user) ? Name(user) : null);
                case "ERROR":
                    var code = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
                        && c.TryGetInt32(out var number) ? number : 0;
                    var message = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                        ? m.GetString() ?? "" : "";
                    return new DiscordEvent.Error(code, message);
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        static string? Name(JsonElement user)
        {
            if (user.ValueKind != JsonValueKind.Object) return null;
            foreach (var key in (ReadOnlySpan<string>)["global_name", "username"])
                if (user.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { } text && text.Trim().Length > 0) return text;
            return null;
        }
    }

    // ---- The line ----

    /// <summary>
    /// The line being sung at <paramref name="time"/> (the lyric clock, the lead already added), <see cref="InstrumentalText"/>
    /// in an intro or a break, or null without timed lyrics. <paramref name="replacements"/> are readings shown in place
    /// of the lines, by line index.
    /// </summary>
    public static string? Line(IReadOnlyList<LyricLine>? lines, double time, IReadOnlyDictionary<int, string>? replacements = null)
    {
        if (lines is not { Count: > 0 } || !double.IsFinite(time)) return null;
        var first = lines[0];
        if (new LyricLineIndex(lines).LineAt(time) is not { } index || index < 0 || index >= lines.Count)
            return time < first.Start ? InstrumentalText : null;
        var line = lines[index];
        var text = (replacements is not null && replacements.TryGetValue(index, out var reading) && reading.Length > 0 ? reading : line.Text).Trim();
        var finished = time > line.End + 1.5 && index + 1 < lines.Count;
        return line.IsInstrumental || finished || text.Length == 0 ? InstrumentalText : text;
    }

    /// <summary>
    /// Whether Apple's search is asked at all: for the cover, or for the link of a song Spotify has not given one for.
    /// With the cover off the song button is off too unless the user turned it on themselves.
    /// </summary>
    public static bool NeedsAppleSearch(DiscordOptions options, string? spotifyUri)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.ShowsCover || (options.ShowsSongButton && spotifyUri is null);
    }

    /// <summary>
    /// A change that should not wait behind lyric lines: the first update (<paramref name="sentBefore"/> false), a new
    /// song, play or pause, or the presence being cleared.
    /// </summary>
    public static bool IsUrgent(bool sentBefore, DiscordPlaying? last, DiscordPlaying? next)
    {
        if (!sentBefore || last is null || next is null) return true;
        return last.Title != next.Title || last.Artist != next.Artist || last.Paused != next.Paused;
    }

    // ---- Apple's catalogue: the cover and the song link ----

    /// <summary>The iTunes search the Mac asks for a song: "artist title", songs only, at most 8.</summary>
    public static Uri SearchUri(string title, string artist) =>
        new("https://itunes.apple.com/search?term=" + Uri.EscapeDataString($"{artist} {title}") + "&media=music&entity=song&limit=8");

    /// <summary>The cover of the best match in an Apple search reply, at a size Discord shows well, or null.</summary>
    public static string? ArtworkUrl(ReadOnlySpan<byte> reply, string title, string artist) => SearchMatch(reply, title, artist)?.Cover;

    /// <summary>
    /// The best match's cover and Apple catalogue ID, or null if no result is the song. A loose match is close enough
    /// for the cover; the ID becomes a public "Play this song" link, so it is taken only from a result whose title and
    /// primary artist are the song's own, once normalised.
    /// </summary>
    public static CatalogueMatch? SearchMatch(ReadOnlySpan<byte> reply, string title, string artist)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(reply.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return null;
            var wantTitle = title.ToLowerInvariant();
            var wantArtist = artist.ToLowerInvariant();
            var exactTitle = MusicVideoTitleMatch.Normal(title);
            var exactArtist = PrimaryArtist(artist);
            string? cover = null;
            long? trackId = null;
            foreach (var result in results.EnumerateArray())
            {
                if (result.ValueKind != JsonValueKind.Object) continue;
                var rawName = Text(result, "trackName");
                var rawBy = Text(result, "artistName");
                var name = rawName.ToLowerInvariant();
                var by = rawBy.ToLowerInvariant();
                if (name.Length == 0) continue;
                var exact = exactTitle.Length > 0 && exactArtist.Length > 0
                    && MusicVideoTitleMatch.Normal(rawName) == exactTitle && PrimaryArtist(rawBy) == exactArtist;
                var loose = (name.Contains(wantTitle, StringComparison.Ordinal) || wantTitle.Contains(name, StringComparison.Ordinal))
                    && (wantArtist.Length == 0 || by.Contains(wantArtist, StringComparison.Ordinal) || wantArtist.Contains(by, StringComparison.Ordinal));
                if (!exact && !loose) continue;
                if (cover is null && result.TryGetProperty("artworkUrl100", out var art) && art.ValueKind == JsonValueKind.String)
                    cover = art.GetString()?.Replace("100x100bb", "512x512bb", StringComparison.Ordinal);
                if (trackId is null && exact && result.TryGetProperty("trackId", out var id) && id.ValueKind == JsonValueKind.Number
                    && id.TryGetInt64(out var value)) trackId = value;
                if (cover is not null && trackId is not null) break;
            }
            return cover is null && trackId is null ? null : new CatalogueMatch(cover, trackId);
        }

        static string Text(JsonElement result, string key) =>
            result.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }

    private static readonly string[] ArtistSeparators =
        [",", ";", "/", " & ", " + ", " x ", " × ", " and ", " with ", " feat.", " feat ", " ft.", " ft ", " featuring "];

    /// <summary>
    /// The first-named artist, normalised: "Calvin Harris feat. Rihanna", "Calvin Harris &amp; Rihanna" and "calvin
    /// harris" all give "calvinharris".
    /// </summary>
    public static string PrimaryArtist(string artist)
    {
        var name = $" {(artist ?? "").ToLowerInvariant()} ";
        foreach (var separator in ArtistSeparators)
        {
            var at = name.IndexOf(separator, StringComparison.Ordinal);
            if (at >= 0) name = name[..at];
        }
        return MusicVideoTitleMatch.Normal(name);
    }
}

/// <summary>
/// Discord takes five activity updates in twenty seconds and silently drops the rest. Lines are spaced at least
/// <see cref="LineGap"/> apart and use four of the five; the fifth is kept for an urgent change (a new song, a pause, a
/// clear).
/// </summary>
public sealed class DiscordUpdateBudget
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(20);
    public const int Limit = 5;
    public static readonly TimeSpan LineGap = TimeSpan.FromSeconds(5);

    private readonly List<DateTimeOffset> _sent = [];

    public IReadOnlyList<DateTimeOffset> Sent => _sent;

    public bool Allows(bool urgent, DateTimeOffset now)
    {
        var recent = _sent.Where(sent => now - sent < Window).ToList();
        if (urgent) return recent.Count < Limit;
        if (recent.Count > 0 && now - recent[^1] < LineGap) return false;
        return recent.Count < Limit - 1;
    }

    public void Record(DateTimeOffset now)
    {
        _sent.RemoveAll(sent => now - sent >= Window);
        _sent.Add(now);
    }
}

/// <summary>A small map that keeps the <c>capacity</c> most recently used entries (the Mac's <c>RecentCache</c>).</summary>
public sealed class RecentCache<TValue>
{
    private readonly Dictionary<string, TValue> _values = new(StringComparer.Ordinal);

    /// <summary>Keys from least to most recently used.</summary>
    private readonly List<string> _order = [];

    public RecentCache(int capacity) => Capacity = Math.Max(1, capacity);

    public int Capacity { get; }

    public int Count => _values.Count;

    public bool TryGet(string key, out TValue value)
    {
        if (!_values.TryGetValue(key, out value!)) return false;
        Touch(key);
        return true;
    }

    public void Set(string key, TValue value)
    {
        _values[key] = value;
        Touch(key);
        while (_order.Count > Capacity)
        {
            _values.Remove(_order[0]);
            _order.RemoveAt(0);
        }
    }

    public void Remove(string key)
    {
        _values.Remove(key);
        _order.Remove(key);
    }

    private void Touch(string key)
    {
        _order.Remove(key);
        _order.Add(key);
    }
}
