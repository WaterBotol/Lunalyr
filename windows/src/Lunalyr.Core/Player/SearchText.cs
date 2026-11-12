using System.Globalization;

namespace Lunalyr.Core.Player;

/// <summary>Editing rules for the desktop player's search box that a plain Win32 EDIT doesn't have.</summary>
public static class SearchText
{
    /// <summary>
    /// Where Ctrl+Backspace deletes back to from <paramref name="caret"/>: over any spaces before it, then the word before
    /// them (letters and digits, or a run of punctuation), as Windows' own text boxes do. Never splits a surrogate pair
    /// or a combining sequence.
    /// </summary>
    public static int WordStartBefore(string text, int caret)
    {
        ArgumentNullException.ThrowIfNull(text);
        caret = Math.Clamp(caret, 0, text.Length);
        // Text elements (what shows as one character) up to the caret.
        var starts = new List<int>();
        var enumerator = StringInfo.GetTextElementEnumerator(text[..caret]);
        while (enumerator.MoveNext()) starts.Add(enumerator.ElementIndex);
        var i = starts.Count - 1;
        while (i >= 0 && IsSpace(text, starts[i])) i--;
        if (i < 0) return 0;
        var word = IsWord(text, starts[i]);
        while (i > 0 && !IsSpace(text, starts[i - 1]) && IsWord(text, starts[i - 1]) == word) i--;
        return starts[i];
    }

    private static bool IsSpace(string text, int index) => char.IsWhiteSpace(text, index);

    private static bool IsWord(string text, int index)
    {
        var category = CharUnicodeInfo.GetUnicodeCategory(text, index);
        return char.IsLetterOrDigit(text, index) || category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.OtherLetter;
    }
}
