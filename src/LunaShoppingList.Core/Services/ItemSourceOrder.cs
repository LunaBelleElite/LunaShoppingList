namespace LunaShoppingList.Core;

/// <summary>Ordering of items by their vendors, for the Vendor column's sort.</summary>
public static class ItemSourceOrder
{
    /// <summary>
    /// Compares two items by their vendor sources. An item with at least one source sorts before one with none. Between two
    /// items with sources, each is judged by its best source (the first in <see cref="ItemSourceSummary.Ordered"/> order, the one the
    /// column shows): gil vendors before currency vendors, cheapest gil first; currency vendors by currency name (ignoring case), then cost.
    /// </summary>
    /// <param name="a">Sources of the first item; may be empty.</param>
    /// <param name="b">Sources of the second item; may be empty.</param>
    /// <returns>Negative when <paramref name="a"/> sorts first, positive when <paramref name="b"/> does, zero when they tie.</returns>
    public static int Compare(IReadOnlyList<ItemSource> a, IReadOnlyList<ItemSource> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count == 0 || b.Count == 0)
            return (a.Count == 0 ? 1 : 0) - (b.Count == 0 ? 1 : 0);

        var x = ItemSourceSummary.Ordered(a)[0];
        var y = ItemSourceSummary.Ordered(b)[0];
        var kind = (x.Kind == ItemSourceKind.GilVendor ? 0 : 1) - (y.Kind == ItemSourceKind.GilVendor ? 0 : 1);
        if (kind != 0)
            return kind;
        var currency = string.Compare(x.CurrencyName, y.CurrencyName, StringComparison.OrdinalIgnoreCase);
        return currency != 0 ? currency : x.Cost.CompareTo(y.Cost);
    }
}
