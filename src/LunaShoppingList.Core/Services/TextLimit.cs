using System.Text;

namespace LunaShoppingList.Core;

/// <summary>Cuts text to a size limit without splitting a character (or a surrogate pair).</summary>
public static class TextLimit
{
    /// <summary>
    /// Returns the longest prefix of <paramref name="text"/> whose UTF-8 size is at most <paramref name="maxBytes"/>,
    /// ending on a whole character. <paramref name="wasCut"/> is true when anything was removed.
    /// </summary>
    public static string CutToBytes(string text, int maxBytes, out bool wasCut)
    {
        wasCut = false;
        text ??= string.Empty;
        var bytes = 0;
        var chars = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (bytes + size > maxBytes)
            {
                wasCut = true;
                return text.Substring(0, chars);
            }
            bytes += size;
            chars += rune.Utf16SequenceLength;
        }
        return text;
    }

    /// <summary>Returns at most <paramref name="maxChars"/> UTF-16 characters of the text, never ending in half a surrogate pair.</summary>
    public static string CutToChars(string text, int maxChars)
    {
        text ??= string.Empty;
        if (text.Length <= maxChars)
            return text;
        var cut = Math.Max(0, maxChars);
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]) && char.IsLowSurrogate(text[cut]))
            cut--;
        return text.Substring(0, cut);
    }
}
