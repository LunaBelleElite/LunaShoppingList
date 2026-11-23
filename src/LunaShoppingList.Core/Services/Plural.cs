using System.Globalization;

namespace LunaShoppingList.Core;

/// <summary>Counts with the right singular or plural noun, so a window never says "1 worlds".</summary>
public static class Plural
{
    /// <summary>The count with thousands separators and the noun, e.g. "1 world", "2 worlds", "1,200 items".</summary>
    /// <param name="count">How many.</param>
    /// <param name="singular">The noun for exactly one.</param>
    /// <param name="plural">The noun for any other count; the singular with an "s" when null.</param>
    public static string Of(long count, string singular, string? plural = null)
        => count.ToString("N0", CultureInfo.InvariantCulture) + " " + (count == 1 ? singular : plural ?? singular + "s");
}
