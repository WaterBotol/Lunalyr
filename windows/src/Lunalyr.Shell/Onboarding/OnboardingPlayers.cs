using Lunalyr.Platform.NowPlaying;

namespace Lunalyr.Shell.Onboarding;

/// <summary>One player sharing what it plays with Windows, as the music and last steps list it.</summary>
public sealed record OnboardingPlayerRow(string Name, string Status, bool Followed);

/// <summary>
/// The players Windows' media controls know about now (the Windows counterpart of the Mac's player access list: there is
/// no access to grant, so each row says what the player is doing and which one Lunalyr follows).
/// </summary>
public static class OnboardingPlayers
{
    /// <summary>
    /// A row per session, in the order Windows lists them; the one Windows calls current is the one followed. Reads only
    /// (never a command). Players without a name read "A player".
    /// </summary>
    public static IReadOnlyList<OnboardingPlayerRow> Rows(IMediaSessionSource? sessions)
    {
        if (sessions is null) return [];
        var current = sessions.CurrentSession;
        var all = sessions.Sessions;
        // The same session object when the source keeps one per player; otherwise the first with the current one's app id.
        var followedIndex = -1;
        for (var i = 0; i < all.Count && followedIndex < 0; i++) if (ReferenceEquals(all[i], current)) followedIndex = i;
        for (var i = 0; i < all.Count && followedIndex < 0 && current is not null; i++)
            if (all[i].SourceAppUserModelId == current.SourceAppUserModelId) followedIndex = i;

        var rows = new List<OnboardingPlayerRow>();
        for (var i = 0; i < all.Count; i++)
        {
            var session = all[i];
            var name = string.IsNullOrWhiteSpace(session.SourceApp.DisplayName) ? "A player" : session.SourceApp.DisplayName;
            var followed = i == followedIndex;
            rows.Add(new OnboardingPlayerRow(name, OnboardingCopy.PlayerStatus(session.GetPlaybackInfo().Status, followed), followed));
        }
        return rows;
    }
}
