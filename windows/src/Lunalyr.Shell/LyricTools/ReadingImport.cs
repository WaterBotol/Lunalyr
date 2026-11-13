namespace Lunalyr.Shell.LyricTools;

/// <summary>
/// What importing a reading LRC did, for Settings › Readings to say under its button: a note, or a warning when the
/// file itself couldn't be used (the Mac's ReadingsPage shows the two the same ways).
/// </summary>
/// <param name="Message">What happened, in a sentence.</param>
/// <param name="Failed">The file couldn't be read (too large, unreadable or without timestamps).</param>
public sealed record ReadingImport(string Message, bool Failed);
