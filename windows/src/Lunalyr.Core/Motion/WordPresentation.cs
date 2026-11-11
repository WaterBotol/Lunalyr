using System.Text;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Core.Motion;

/// <summary>
/// A timed word's identity: its text and times. The same word moved to another line window keeps it, so its motion
/// and per-word variation stay the same.
/// </summary>
public readonly record struct TimedWordKey(string Text, double Start, double End)
{
    public TimedWordKey(LyricWord word) : this(word.Text, word.Start, word.End) { }

    /// <summary>
    /// The seed for this word's motion: an FNV-1a hash of its UTF-8 text, started from its start time's bits, so a
    /// repeated word sung at another time moves differently.
    /// </summary>
    public ulong VisualSeed => SeedOf(Text, Start);

    /// <summary><see cref="VisualSeed"/> without building a key. Allocation-free.</summary>
    public static ulong SeedOf(string text, double start)
    {
        unchecked
        {
            var hash = (ulong)BitConverter.DoubleToInt64Bits(start) ^ 1469598103934665603UL;
            Span<byte> buffer = stackalloc byte[4];
            foreach (var rune in text.EnumerateRunes())
            {
                var length = rune.EncodeToUtf8(buffer);
                for (var i = 0; i < length; i++) hash = (hash ^ buffer[i]) * 1099511628211UL;
            }
            return hash;
        }
    }
}

/// <summary>How a word is shown on one frame.</summary>
/// <param name="Progress">Monotone 0…1 reveal: opacity, blur, masks.</param>
/// <param name="Arrival">The word's spring, for travel and scale. Can pass 1 and settle back.</param>
/// <param name="Opacity">Opacity, the next-word preview included.</param>
/// <param name="Preview">True while the word is shown ahead of its onset as the preview.</param>
public readonly record struct WordPresentation(double Progress, double Arrival, double Opacity, bool Preview)
{
    /// <summary>A word shown whole: when the layout does not reveal words one by one.</summary>
    public static WordPresentation Whole => new(1, 1, 1, false);

    /// <summary>
    /// How <paramref name="word"/> is shown at <paramref name="time"/>.
    /// </summary>
    /// <param name="word">The word.</param>
    /// <param name="index">Its index in its line.</param>
    /// <param name="nextIndex">The index of the line's next word to be sung (the only one that may be previewed).</param>
    /// <param name="time">Lyric time.</param>
    /// <param name="options">The style's reveal and preview settings.</param>
    /// <param name="motion">The word's motion if the caller already has it; otherwise the default for its timing.</param>
    /// <param name="eligibleSince">When this word could first be previewed: the onset of the word before it, or when its
    /// line's preview was allowed. The preview ramps in from then, so it never appears part-way up.</param>
    /// <remarks>
    /// The faint preview ramps up to a quarter and hands over, without a blink, to the word's own reveal at its onset.
    /// Only a single word is previewed (<see cref="PreviewWords.IsPreviewWord"/>), never a phrase or punctuation.
    /// </remarks>
    public static WordPresentation Make(LyricWord word, int index, int? nextIndex, double time, WordRevealOptions options,
                                        WordMotion? motion = null, double eligibleSince = double.NegativeInfinity)
    {
        ArgumentNullException.ThrowIfNull(word);
        if (!options.RevealsWords) return Whole;
        var m = motion ?? LyricMotion.ForWord(time, word.Start, word.End, word.End - word.Start, double.PositiveInfinity, 0,
                                              TimedWordKey.SeedOf(word.Text, word.Start), 0, false);
        var ahead = options.PreviewsNextWord;
        double opacity;
        if (time < word.Start)
        {
            var hint = WordReveal.Visibility(time, word.Start, word.End, ahead, index == nextIndex, options.PreviewLead)
                * Easing.Smoothstep((time - eligibleSince) / WallpaperMotion.PreviewRamp);
            opacity = hint > 0 && PreviewWords.IsPreviewWord(word.Text) ? hint : 0;
        }
        else
        {
            var carriesPreview = ahead && (m.Reveal == 1 || PreviewWords.IsPreviewWord(word.Text));
            opacity = carriesPreview ? 0.25 + 0.75 * m.Reveal : m.Reveal;
        }
        return new WordPresentation(m.Reveal, m.Enter, opacity, time < word.Start && opacity > 0);
    }
}

/// <summary>A lyric style's word settings.</summary>
/// <param name="RevealsWords">Words arrive one by one as they are sung (otherwise the line is shown whole).</param>
/// <param name="PreviewsNextWord">The next word shows faintly just before it is sung.</param>
/// <param name="PreviewLead">How many seconds ahead the preview begins (Settings "Preview lead", 0.15–1.25; 0.45 by default).</param>
public readonly record struct WordRevealOptions(bool RevealsWords = true, bool PreviewsNextWord = true, double PreviewLead = 0.45);

/// <summary>Which words the next-word preview may show.</summary>
public static class PreviewWords
{
    /// <summary>
    /// True when the text is exactly one word: a phrase sung as one timed piece is not previewed, nor is punctuation
    /// alone. Words are counted as runs of letters and digits joined by apostrophes, with each Han, kana or Hangul
    /// character its own word (an approximation of the Mac's system word breaker).
    /// </summary>
    public static bool IsPreviewWord(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var words = 0;
        var inWord = false;
        var previousWasJoiner = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (TextElements.KeepsSyllables(rune.Value))
            {
                if (++words > 1) return false;
                inWord = false;
                continue;
            }
            var wordy = Rune.IsLetterOrDigit(rune) || Rune.GetUnicodeCategory(rune) is
                System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark;
            if (wordy)
            {
                if (!inWord && !previousWasJoiner && ++words > 1) return false;
                inWord = true;
                previousWasJoiner = false;
            }
            else if (inWord && rune.Value is '\'' or '’' or '.')
            {
                // "don't", "rock'n'roll" and similar stay one word.
                previousWasJoiner = true;
                inWord = false;
            }
            else
            {
                inWord = false;
                previousWasJoiner = false;
            }
        }
        return words == 1;
    }
}
