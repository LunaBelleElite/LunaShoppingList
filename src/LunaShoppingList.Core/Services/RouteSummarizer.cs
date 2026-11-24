namespace LunaShoppingList.Core;

/// <summary>Computes the route that follows from an allocation.</summary>
public static class RouteSummarizer
{
    /// <summary>
    /// Prices every allocation line from the offer's price ladder (cheapest units first, quantity clamped to the units for sale),
    /// groups the lines per world and reports what is still missing. Lines for unknown items or worlds, and zero or negative
    /// quantities, are ignored; lines for the same item and world are summed first. Ordering as documented on the records.
    /// </summary>
    public static RouteSummary Summarize(OfferBook book, IReadOnlyList<AllocationLine> lines, Func<uint, string?>? dataCenterOf = null)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(lines);

        var items = new Dictionary<uint, ItemOffers>();
        foreach (var item in book.Items)
            items.TryAdd(item.ItemId, item);

        var summed = new Dictionary<(uint Item, uint World), long>();
        foreach (var line in lines)
        {
            if (line.Quantity <= 0 || !items.TryGetValue(line.ItemId, out var offers) || FindWorld(offers, line.WorldId) is null)
                continue;
            var key = (line.ItemId, line.WorldId);
            summed[key] = summed.GetValueOrDefault(key) + line.Quantity;
        }

        var allocated = new Dictionary<uint, long>();
        var perWorld = new Dictionary<uint, (string Name, List<RouteLine> Lines)>();
        foreach (var ((itemId, worldId), requested) in summed)
        {
            var offer = FindWorld(items[itemId], worldId)!;
            var quantity = (int)Math.Min(requested, offer.UnitsForSale);
            if (quantity <= 0)
                continue;

            allocated[itemId] = allocated.GetValueOrDefault(itemId) + quantity;
            if (!perWorld.TryGetValue(worldId, out var world))
            {
                world = (offer.WorldName, new List<RouteLine>());
                perWorld[worldId] = world;
            }
            world.Lines.Add(new RouteLine(itemId, quantity, offer.CostFor(quantity),
                offer.MinUnitPrice, offer.MaxUnitPriceFor(quantity), offer.OldestReviewFor(quantity)));
        }

        var worlds = perWorld
            .Select(kv =>
            {
                var ordered = kv.Value.Lines.OrderBy(l => l.ItemId).ToList();
                return new WorldRoute(kv.Key, kv.Value.Name, ordered, ordered.Sum(l => l.Cost), dataCenterOf?.Invoke(kv.Key));
            })
            .OrderBy(w => dataCenterOf is not null && w.DataCenter is null ? 1 : 0)
            .ThenBy(w => dataCenterOf is null ? null : w.DataCenter, StringComparer.Ordinal)
            .ThenBy(w => w.WorldName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.WorldId)
            .ToList();

        var unmet = new List<ShortfallItem>();
        foreach (var item in items.Values.OrderBy(i => i.ItemId))
        {
            var have = allocated.GetValueOrDefault(item.ItemId);
            if (have < item.Needed)
                unmet.Add(new ShortfallItem(item.ItemId, (int)(item.Needed - have)));
        }

        List<DataCenterRoute>? groups = null;
        if (dataCenterOf is not null)
            groups = worlds
                .GroupBy(w => w.DataCenter)
                .Select(g => new DataCenterRoute(g.Key, g.ToList(), g.Sum(w => w.Total)))
                .ToList();

        return new RouteSummary(worlds, worlds.Sum(w => w.Total), unmet, groups);
    }

    /// <summary>Summarizes the two plans and pairs them so the UI can show what the fewest-worlds plan costs extra and saves.</summary>
    public static SuggestionComparison Compare(
        OfferBook book,
        IReadOnlyList<AllocationLine> cheapest,
        IReadOnlyList<AllocationLine> fewestWorlds)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(cheapest);
        ArgumentNullException.ThrowIfNull(fewestWorlds);
        return new SuggestionComparison(Summarize(book, cheapest), Summarize(book, fewestWorlds));
    }

    private static WorldOffer? FindWorld(ItemOffers offers, uint worldId)
    {
        foreach (var w in offers.Worlds)
            if (w.WorldId == worldId)
                return w;
        return null;
    }
}
