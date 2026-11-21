namespace LunaShoppingList.Core;

/// <summary>Turns fetched listings into the per-item, per-world offers the user chooses from.</summary>
public static class OfferBookBuilder
{
    /// <summary>
    /// Builds the offer book. Requirements are merged per item (quantities summed, zero or less ignored); excluded items go to
    /// <see cref="OfferBook.Skipped"/>; for every other item the listings on allowed worlds (price above zero, quantity above zero;
    /// NQ and HQ both) are grouped per world into a <see cref="WorldOffer"/> whose price ladder is sorted by unit price ascending
    /// (ties: newer review first, then NQ first, then larger quantity first). Worlds with no usable listing are omitted; an item
    /// with no offers anywhere still appears with an empty world list. Items are ordered by id; worlds as documented on
    /// <see cref="ItemOffers.Worlds"/>.
    /// </summary>
    public static OfferBook Build(
        IReadOnlyList<Requirement> requirements,
        IReadOnlyList<ItemListings> listings,
        IReadOnlySet<uint> allowedWorldIds,
        ExclusionState exclusions)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(listings);
        ArgumentNullException.ThrowIfNull(allowedWorldIds);
        ArgumentNullException.ThrowIfNull(exclusions);

        var needed = new SortedDictionary<uint, long>();
        foreach (var r in requirements)
        {
            if (r.Quantity <= 0) continue;
            needed[r.ItemId] = needed.GetValueOrDefault(r.ItemId) + r.Quantity;
        }

        var listingsByItem = listings
            .SelectMany(il => il.Listings)
            .Where(l => l.Quantity > 0 && l.PricePerUnit > 0 && allowedWorldIds.Contains(l.WorldId))
            .GroupBy(l => l.ItemId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var items = new List<ItemOffers>();
        var skipped = new List<SkippedItem>();
        foreach (var (itemId, total) in needed)
        {
            var qty = (int)Math.Min(total, int.MaxValue);
            if (exclusions.IsExcluded(itemId))
            {
                skipped.Add(new SkippedItem(itemId, qty));
                continue;
            }

            var worlds = new List<WorldOffer>();
            if (listingsByItem.TryGetValue(itemId, out var candidates))
            {
                foreach (var g in candidates.GroupBy(l => l.WorldId))
                {
                    var name = g.Select(l => l.WorldName).OrderBy(n => n, StringComparer.Ordinal).First();
                    var steps = g
                        .OrderBy(l => l.PricePerUnit)
                        .ThenByDescending(l => l.LastReview)
                        .ThenBy(l => l.Hq)
                        .ThenByDescending(l => l.Quantity)
                        .Select(l => new PriceStep(l.PricePerUnit, l.Quantity, l.Hq, l.LastReview))
                        .ToList();
                    worlds.Add(new WorldOffer(g.Key, name, steps));
                }
            }

            var ordered = OrderWorlds(worlds, qty);
            items.Add(new ItemOffers(itemId, qty, ordered));
        }

        return new OfferBook(items, skipped);
    }

    /// <summary>Worlds able to supply every needed unit first (cheapest total first), then the rest (most units, then lowest average unit price); name and id break ties.</summary>
    private static List<WorldOffer> OrderWorlds(List<WorldOffer> worlds, int needed)
    {
        var full = worlds
            .Where(w => w.UnitsForSale >= needed)
            .OrderBy(w => w.CostFor(needed))
            .ThenBy(w => w.WorldName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.WorldId);
        var partial = worlds
            .Where(w => w.UnitsForSale < needed)
            .OrderByDescending(w => w.UnitsForSale)
            .ThenBy(w => w, Comparer<WorldOffer>.Create((a, b) =>
            {
                // average unit price a.cost/a.units against b.cost/b.units, cross-multiplied so nothing is rounded
                var left = a.CostFor(needed) * (long)b.UnitsForSale;
                var right = b.CostFor(needed) * (long)a.UnitsForSale;
                return left.CompareTo(right);
            }))
            .ThenBy(w => w.WorldName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.WorldId);
        return full.Concat(partial).ToList();
    }
}
