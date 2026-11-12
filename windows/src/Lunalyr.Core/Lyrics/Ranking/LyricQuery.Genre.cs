namespace Lunalyr.Core.Lyrics;

public sealed partial record LyricQuery
{
    /// <summary>
    /// The player's genre ("J-Pop"), a hint at the language sung (<see cref="Ranking.LyricLanguage"/>), or null. Not
    /// part of <see cref="RecordingKey"/>. Mac: <c>LyricQuery.genre</c>.
    /// </summary>
    public string? Genre { get; init; }
}
