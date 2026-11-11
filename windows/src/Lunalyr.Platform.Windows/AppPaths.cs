namespace Lunalyr.Platform;

/// <summary>Where the app keeps its data. Services take these folders as parameters; nothing assumes them.</summary>
public static class AppPaths
{
    /// <summary><c>%LOCALAPPDATA%\Lunalyr</c>. Only the app calls this; tests always use temp folders.</summary>
    public static string DefaultDataRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lunalyr");

    /// <summary><c>{dataRoot}\Logs</c>.</summary>
    public static string LogsFolder(string dataRoot) => Path.Combine(dataRoot, "Logs");
}
