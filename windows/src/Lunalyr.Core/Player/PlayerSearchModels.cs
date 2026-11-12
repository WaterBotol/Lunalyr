namespace Lunalyr.Core.Player;

// Ported from the Mac's Sources/Player/PlayerSearch.swift (PlayerSearchResult, PlayerQueueItem, PlayerQueueSnapshot,
// QueueOutcome, QueueSolo). Lunalyr's own code: no lines from a99fd65. The Mac's targets (a Music library track, a
// music:// address, a Spotify search page) become one Windows target, a Spotify URI with its album.

/// <summary>What a search result is.</summary>
public enum PlayerSearchResultKind
{
    Song = 0,
    Album,
}

/// <summary>
/// What playing a result takes. On Windows the only player with a search is Spotify (Web API), so a target is a
/// Spotify URI (<c>spotify:track:…</c> or <c>spotify:album:…</c>) and, for a song, the album it is on, so the album can
/// carry on after it.
/// </summary>
/// <param name="Uri">The item to play.</param>
/// <param name="AlbumUri">A song's album, or null (a song with no album plays on its own).</param>
/// <param name="TrackNumber">The song's place on its disc, one-based; 0 when unknown.</param>
/// <param name="DiscNumber">The song's disc, one-based; 0 when unknown.</param>
/// <param name="AlbumTracks">Songs on the album, all discs; 0 when unknown.</param>
public sealed record PlayerSearchTarget(string Uri, string? AlbumUri = null, int TrackNumber = 0, int DiscNumber = 0, int AlbumTracks = 0)
{
    /// <summary>
    /// Whether nothing comes after the song on its album: the last song of a one-disc album. On a later disc it can't be
    /// told (the album's songs per disc aren't in a search result), so it is never assumed.
    /// </summary>
    public bool IsLastOnAlbum => AlbumTracks > 0 && DiscNumber <= 1 && TrackNumber >= AlbumTracks;
}

/// <summary>One thing the desktop player's search can find.</summary>
/// <param name="Id">Unique within one search's results.</param>
/// <param name="InLibrary">Whether it is in the user's own library (the Mac's Music library). Always false for Spotify.</param>
public sealed record PlayerSearchResult(
    string Id,
    PlayerSearchResultKind Kind,
    string Title,
    string Artist,
    string Album,
    Uri? ArtworkUri,
    PlayerSearchTarget Target,
    bool InLibrary = false);

/// <summary>A track that plays after the current one.</summary>
/// <param name="Index">Position in what the player lists, one-based.</param>
/// <param name="IsCurrent">The song that is playing now.</param>
/// <param name="PersistentId">
/// The player's id for the song, when it has one (Spotify: its URI; the Mac: the Music library's persistent ID). A song
/// without one (a local file Spotify can't start from another app, a stream) can't be found again.
/// </param>
public sealed record PlayerQueueItem(
    string Id,
    string Title,
    string Artist,
    int Index,
    bool IsCurrent = false,
    string? PersistentId = null)
{
    /// <summary>The album, when the player says (Windows addition, for the card).</summary>
    public string Album { get; init; } = "";

    /// <summary>The cover, when the player gives one (Windows addition, for the card).</summary>
    public Uri? ArtworkUri { get; init; }

    /// <summary>
    /// Whether clicking it does anything: the song playing pauses or plays, any other needs its id to be found again.
    /// A song that played from a stream or the radio has none.
    /// </summary>
    public bool IsPlayable => IsCurrent || PersistentId is not null;
}

/// <summary>
/// What a player says is queued: the song playing and what comes after it, and whether shuffle is on (when it is,
/// that order is not the play order).
/// </summary>
public sealed record PlayerQueueSnapshot(IReadOnlyList<PlayerQueueItem> Items, bool Shuffle = false)
{
    public static PlayerQueueSnapshot Empty { get; } = new([]);

    /// <summary>What the player is playing from (Spotify's context: an album, playlist or artist URI), or null.</summary>
    public string? ContextUri { get; init; }
}

/// <summary>Why a song plays on its own.</summary>
public enum QueueSolo
{
    /// <summary>What is playing can't be copied: radio, a CD, a folder, a stream. (Mac: Music only.)</summary>
    Source = 0,

    /// <summary>The song isn't in the library, so nothing can be copied after it. (Mac: Music only.)</summary>
    NotInLibrary,

    /// <summary>A song with no album.</summary>
    Single,

    /// <summary>Nothing comes after it on its album in the library. (Mac: Music only; see <see cref="LastOnAlbum"/>.)</summary>
    Album,

    /// <summary>What comes next couldn't be lined up, so the song plays by itself.</summary>
    Rescued,

    /// <summary>Windows (Spotify): it is the last song on its album.</summary>
    LastOnAlbum,
}

/// <summary>What kind of <see cref="PlayOutcome"/>.</summary>
public enum PlayOutcomeKind
{
    /// <summary>The song plays and what follows it carries on.</summary>
    Queued = 0,

    /// <summary>The song plays on its own.</summary>
    Alone,

    /// <summary>A newer click came first; this one played nothing.</summary>
    Superseded,

    /// <summary>Windows: nothing needed sending (the song already playing was clicked; play/pause is the transport's job).</summary>
    Unchanged,
}

/// <summary>How a click on a song went, for the card's note and the log (the Mac's <c>QueueOutcome</c>).</summary>
public sealed record PlayOutcome
{
    private PlayOutcome(PlayOutcomeKind kind, int songs, int wanted, bool fromAlbum, QueueSolo solo)
    {
        Kind = kind;
        Songs = songs;
        Wanted = wanted;
        FromAlbum = fromAlbum;
        Solo = solo;
    }

    public PlayOutcomeKind Kind { get; }

    /// <summary>For <see cref="PlayOutcomeKind.Queued"/>: songs lined up of those wanted.</summary>
    public int Songs { get; }

    public int Wanted { get; }

    /// <summary>For <see cref="PlayOutcomeKind.Queued"/>: the rest of the song's album follows.</summary>
    public bool FromAlbum { get; }

    /// <summary>For <see cref="PlayOutcomeKind.Alone"/>: why.</summary>
    public QueueSolo Solo { get; }

    public static PlayOutcome Queued(int songs, int wanted, bool fromAlbum) => new(PlayOutcomeKind.Queued, songs, wanted, fromAlbum, default);

    public static PlayOutcome Alone(QueueSolo why) => new(PlayOutcomeKind.Alone, 0, 0, false, why);

    public static PlayOutcome Superseded { get; } = new(PlayOutcomeKind.Superseded, 0, 0, false, default);

    public static PlayOutcome Unchanged { get; } = new(PlayOutcomeKind.Unchanged, 0, 0, false, default);

    /// <summary>In plain words, when there is something to say.</summary>
    public string? Note => Kind switch
    {
        PlayOutcomeKind.Queued when Songs < Wanted => "Some songs after it couldn’t be added, so they’re left out.",
        PlayOutcomeKind.Queued => FromAlbum ? "Then the rest of its album." : null,
        PlayOutcomeKind.Alone => SoloNote(Solo),
        _ => null,
    };

    /// <summary>Why a song plays on its own, short enough for the card's strip.</summary>
    public static string SoloNote(QueueSolo why) => why switch
    {
        QueueSolo.Source => "Plays this song only: Lunalyr can keep only your library and your own playlists going.",
        QueueSolo.NotInLibrary => "Plays this song only: it isn’t in your library, so the playlist can’t carry on from it.",
        QueueSolo.Single => "Plays this song only.",
        QueueSolo.Album => "Plays this song only: nothing after it on its album is in your library.",
        QueueSolo.Rescued => "Couldn’t line up what comes next, so this song plays on its own.",
        QueueSolo.LastOnAlbum => "Plays this song only: it’s the last song on its album.",
        _ => "Plays this song only.",
    };
}
