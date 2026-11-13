// Settings › Music players › Player (the Mac's PlayerOptions, Lunalyr's own lines): which player Lunalyr follows.

using Lunalyr.Core.NowPlaying;

namespace Lunalyr.Shell.Settings;

public sealed partial record AppSettings
{
    /// <summary>
    /// The player followed (Mac <c>custom.player</c>): <c>automatic</c> (the session Windows calls current, the
    /// default), the Mac's raw value where the player is the same (<c>appleMusic</c>, <c>spotify</c>, <c>edge</c>,
    /// <c>chrome</c>, <c>brave</c>, <c>arc</c>), Windows' own (<c>firefox</c>, <c>opera</c>, <c>vivaldi</c>,
    /// <c>mediaPlayer</c>) or <c>app:</c> and an app's AppUserModelID. Anything else reads as <c>automatic</c>.
    /// </summary>
    public string Player { get; init; } = PlayerChoice.Automatic;
}
