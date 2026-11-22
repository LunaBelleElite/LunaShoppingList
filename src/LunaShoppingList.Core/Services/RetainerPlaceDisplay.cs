namespace LunaShoppingList.Core;

/// <summary>One line of a retainer's per-place summary: what to call the place and how many are there.</summary>
public sealed record PlaceCount(string Label, long Count);

/// <summary>
/// Turns a retainer's per-container counts into what a player can tell apart. The game's inventory tabs do not map reliably onto
/// the internal containers, so all inventory pages are shown as one "Inventory" entry; crystals and market listings stay separate.
/// </summary>
public static class RetainerPlaceDisplay
{
    /// <summary>Merges the inventory pages into one entry; order is Inventory, Crystals, Market listings; empty places are left out.</summary>
    public static IReadOnlyList<PlaceCount> Merge(IReadOnlyDictionary<RetainerPlace, int> places)
    {
        ArgumentNullException.ThrowIfNull(places);
        long inventory = 0, crystals = 0, market = 0;
        foreach (var (place, count) in places)
        {
            if (count <= 0)
                continue;
            switch (place)
            {
                case RetainerPlace.Crystals: crystals += count; break;
                case RetainerPlace.Market: market += count; break;
                default: inventory += count; break;
            }
        }
        var merged = new List<PlaceCount>();
        if (inventory > 0) merged.Add(new PlaceCount("Inventory", inventory));
        if (crystals > 0) merged.Add(new PlaceCount("Crystals", crystals));
        if (market > 0) merged.Add(new PlaceCount("Market listings", market));
        return merged;
    }
}
