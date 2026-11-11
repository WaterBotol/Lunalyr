namespace Lunalyr.Platform.Settings;

/// <summary>Crash-safe file replacement: write a temp file, flush it to disk, then swap it in keeping a backup.</summary>
internal static class AtomicFile
{
    private const int Attempts = 3;

    public static string TempPath(string path) => path + ".tmp";

    public static string BackupPath(string path) => path + ".bak";

    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="path"/>. At every instant either the old or the new
    /// content is at <paramref name="path"/> (or, between the two steps of a first-time move, in the temp file).
    /// </summary>
    public static void Write(string path, ReadOnlySpan<byte> bytes, TimeSpan retryDelay, Action<string>? beforeCommit = null)
    {
        var tmp = TempPath(path);
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        beforeCommit?.Invoke(tmp);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Replace(tmp, path, BackupPath(path), ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tmp, path, overwrite: true);
                }
                return;
            }
            catch (Exception ex) when (attempt < Attempts && ex is IOException or UnauthorizedAccessException)
            {
                // Usually a scanner or indexer holding the file for a moment.
                Thread.Sleep(retryDelay);
            }
        }
    }

    /// <summary>Reads a whole file, retrying briefly on sharing violations. Missing files throw <see cref="FileNotFoundException"/>.</summary>
    public static byte[] ReadAllBytes(string path, TimeSpan retryDelay)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (Exception ex) when (attempt < Attempts
                && ex is IOException or UnauthorizedAccessException
                && ex is not FileNotFoundException and not DirectoryNotFoundException)
            {
                Thread.Sleep(retryDelay);
            }
        }
    }
}
