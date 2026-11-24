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
/// <param name="DataCenter">Data center of the world, or null when unknown or no lookup was given.</param>
public record WorldRoute(uint WorldId, string WorldName, IReadOnlyList<RouteLine> Lines, long Total, string? DataCenter = null);

/// <summary>The worlds of one data center on the route.</summary>
/// <param name="Name">Data center name, or null for worlds whose data center is unknown.</param>
/// <param name="Worlds">Worlds in this group, in route order.</param>
/// <param name="Subtotal">Gil over the worlds in this group.</param>
public record DataCenterRoute(string? Name, IReadOnlyList<WorldRoute> Worlds, long Subtotal);

/// <summary>The route that follows from an allocation: worlds to visit, what to buy on each, and what is still missing.</summary>
/// <param name="Worlds">Worlds to visit, ordered by world name (ordinal ignore case) then id; when a data center lookup was given, by data center name (ordinal) first, unknown data center last.</param>
/// <param name="GrandTotal">Gil over all worlds.</param>
/// <param name="Unmet">Items whose allocated units fall short of the units needed, with the missing amount, ordered by item id.</param>
/// <param name="DataCentersOrNull">Data center groups, filled only when a lookup was given to the summarizer.</param>
public record RouteSummary(IReadOnlyList<WorldRoute> Worlds, long GrandTotal, IReadOnlyList<ShortfallItem> Unmet, IReadOnlyList<DataCenterRoute>? DataCentersOrNull = null)
{
    /// <summary>Number of worlds to visit.</summary>
    public int WorldCount => Worlds.Count;

    /// <summary>Worlds grouped by data center, in the same order as <see cref="Worlds"/>, each with a gil subtotal. Empty when no data center lookup was given or the route is empty; worlds with an unknown data center form a final group with a null name.</summary>
    public IReadOnlyList<DataCenterRoute> DataCenters => DataCentersOrNull ?? Array.Empty<DataCenterRoute>();
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
