namespace LunaShoppingList.Core;

/// <summary>Stable ordering of table rows for click-to-sort column headers.</summary>
public static class TableSort
{
    /// <summary>
    /// Returns the rows ordered by <paramref name="comparison"/>. The sort is stable: rows that compare equal keep
    /// the order they had in <paramref name="rows"/>, in both directions (descending reverses the comparison, not
    /// the ties). A null comparison returns the rows in their original order, which is what an unsorted table shows.
    /// </summary>
    /// <typeparam name="T">The row type.</typeparam>
    /// <param name="rows">The rows in their natural order. Not modified.</param>
    /// <param name="comparison">How two rows compare in the chosen column, or null for no sorting.</param>
    /// <param name="ascending">True for ascending, false for descending.</param>
    /// <returns>A new list holding the same rows.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/> is null.</exception>
    public static IReadOnlyList<T> Order<T>(IReadOnlyList<T> rows, Comparison<T>? comparison, bool ascending)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (comparison is null || rows.Count < 2)
            return rows.ToList();

        // LINQ's OrderBy is a documented stable sort, so equal rows keep their order. Descending swaps the
        // arguments instead of negating the result, which would be wrong for a comparison returning int.MinValue.
        return rows
            .OrderBy(r => r, Comparer<T>.Create((a, b) => ascending ? comparison(a, b) : comparison(b, a)))
            .ToList();
    }

    /// <summary>Compares two names the way every text column sorts: ordinal, ignoring case, null as empty.</summary>
    /// <param name="a">First text.</param>
    /// <param name="b">Second text.</param>
    /// <returns>Negative, zero or positive.</returns>
    public static int CompareText(string? a, string? b)
        => string.Compare(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
}
