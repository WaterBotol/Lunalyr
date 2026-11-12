using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics.Readings;

namespace Lunalyr.Shell;

// The opening title's reading of the song's title (the Mac's OpeningTitleMemory.reading): worked out by the same
// readings service the lyrics use, off the caller's thread, once per title and readings settings, and added to the
// scene when ready.
public sealed partial class AppController
{
    private readonly OpeningTitleMemory _titleMemory = new();

    private void StartTitleReading(long generation, string title, RomanizationOptions options)
    {
        if (options.Display == RomanizationDisplay.Off || !LyricRomanizer.ContainsNonLatin(title))
        {
            SetTitleReading(generation, title, null);
            return;
        }

        Track(Task.Run(() =>
        {
            try
            {
                string? reading;
                // The memory keeps the last title's reading; it is not thread-safe.
                lock (_titleMemory) reading = _titleMemory.Reading(title, options, _romanizer);
                SetTitleReading(generation, title, reading);
            }
            catch (Exception ex)
            {
                _lyricLog.Warn("Couldn't work out the reading of the song's title.", ex);
            }
        }));
    }

    private void SetTitleReading(long generation, string title, string? reading)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation || _scene.Title != title || _scene.TitleReading == reading) return;
            _scene = _scene with { TitleReading = reading };
        }

        PublishScene();
    }
}
