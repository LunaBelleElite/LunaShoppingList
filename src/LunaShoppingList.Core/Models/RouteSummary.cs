namespace LunaShoppingList.Core;

/// <summary>One item to buy on one world, priced cheapest-first from that world's listings.</summary>
/// <param name="ItemId">Item row id.</param>
/// <param name="Quantity">Units to buy there.</param>
/// <param name="Cost">Gil for those units (excluding tax).</param>
/// <param name="MinUnitPrice">Cheapest unit price used.</param>
/// <param name="MaxUnitPrice">Most expensive unit price used.</param>
/// <param name="OldestReview">Oldest review time among the listings used.</param>
public record RouteLine(uint ItemId, int Quantity, long Cost, int MinUnitPrice, int MaxUnitPrice, DateTimeOffset OldestReview);

/// <summary>Everything to buy on one world.</summary>
/// <param name="WorldId">World row id.</param>
/// <param name="WorldName">World name.</param>
/// <param name="Lines">Lines ordered by item id.</param>
/// <param name="Total">Gil for the whole world.</param>
public record WorldRoute(uint WorldId, string WorldName, IReadOnlyList<RouteLine> Lines, long Total);

/// <summary>The route that follows from an allocation: worlds to visit, what to buy on each, and what is still missing.</summary>
/// <param name="Worlds">Worlds to visit, ordered by world name (ordinal ignore case) then id.</param>
/// <param name="GrandTotal">Gil over all worlds.</param>
/// <param name="Unmet">Items whose allocated units fall short of the units needed, with the missing amount, ordered by item id.</param>
public record RouteSummary(IReadOnlyList<WorldRoute> Worlds, long GrandTotal, IReadOnlyList<ShortfallItem> Unmet)
{
    /// <summary>Number of worlds to visit.</summary>
    public int WorldCount => Worlds.Count;
}

/// <summary>The two suggested plans side by side.</summary>
/// <param name="Cheapest">Summary of the cheapest plan.</param>
/// <param name="FewestWorlds">Summary of the fewest-worlds plan.</param>
public record SuggestionComparison(RouteSummary Cheapest, RouteSummary FewestWorlds)
{
    /// <summary>Extra gil the fewest-worlds plan costs compared with the cheapest plan (can be zero, never negative for a correct suggester).</summary>
    public long ExtraGilForFewestWorlds => FewestWorlds.GrandTotal - Cheapest.GrandTotal;

    /// <summary>Worlds the fewest-worlds plan saves compared with the cheapest plan.</summary>
    public int WorldsSaved => Cheapest.WorldCount - FewestWorlds.WorldCount;
}
