namespace LunaShoppingList.Core;

/// <summary>The one-line note the results window shows for items that are not sold on the market board.</summary>
public static class NotOnMarketNote
{
    /// <summary>Most item names the note lists before it says how many more there are.</summary>
    public const int MaxNames = 8;

    /// <summary>
    /// "Not sold on the market board (not priced): &lt;name&gt; x&lt;qty&gt;, ..." with at most <see cref="MaxNames"/> names, then
    /// ", +N more"; empty when <paramref name="notOnMarket"/> is empty. Quantities use invariant thousands separators.
    /// </summary>
    public static string Text(IReadOnlyList<Requirement> notOnMarket, Func<uint, string> name)
    {
        ArgumentNullException.ThrowIfNull(notOnMarket);
        ArgumentNullException.ThrowIfNull(name);
        if (notOnMarket.Count == 0)
            return string.Empty;

        var names = string.Join(", ", notOnMarket.Take(MaxNames).Select(r => $"{name(r.ItemId)} x{ItemSourceSummary.Thousands(r.Quantity)}"));
        var more = notOnMarket.Count > MaxNames ? $", +{notOnMarket.Count - MaxNames} more" : string.Empty;
        return $"Not sold on the market board (not priced): {names}{more}";
    }
}
