namespace LunaShoppingList.Core;

/// <summary>Builds the cheapest purchase plan from requirements and listings.</summary>
public static class ShoppingPlanner
{
    /// <summary>
    /// Fills each requirement from the cheapest listings upward (NQ or HQ), restricted to allowed worlds,
    /// grouped by world. Excluded items go to Skipped; unfillable quantities go to Shortfalls.
    /// </summary>
    /// <param name="requirements">Quantities needed.</param>
    /// <param name="listings">Fetched listings per item.</param>
    /// <param name="allowedWorldIds">World ids the user may buy on.</param>
    /// <param name="exclusions">Items the user chose not to buy.</param>
    /// <returns>The shopping plan.</returns>
    public static ShoppingPlan Plan(IReadOnlyList<Requirement> requirements, IReadOnlyList<ItemListings> listings, IReadOnlySet<uint> allowedWorldIds, ExclusionState exclusions)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(listings);
        ArgumentNullException.ThrowIfNull(allowedWorldIds);
        ArgumentNullException.ThrowIfNull(exclusions);

        // Merge duplicate requirements (sum as long, clamp to int), ignoring non-positive quantities.
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

        var skipped = new List<SkippedItem>();
        var shortfalls = new List<ShortfallItem>();
        var purchases = new List<Purchase>();

        foreach (var (itemId, total) in needed)
        {
            var qty = (int)Math.Min(total, int.MaxValue);
            if (exclusions.IsExcluded(itemId))
            {
                skipped.Add(new SkippedItem(itemId, qty));
                continue;
            }

            var remaining = qty;
            if (listingsByItem.TryGetValue(itemId, out var candidates))
            {
                var ordered = candidates
                    .OrderBy(l => l.PricePerUnit)
                    .ThenByDescending(l => l.LastReview)
                    .ThenBy(l => l.WorldId)
                    .ThenBy(l => l.Hq)
                    .ThenBy(l => l.Quantity);
                foreach (var l in ordered)
                {
                    if (remaining == 0) break;
                    var take = Math.Min(remaining, l.Quantity);
                    purchases.Add(new Purchase(itemId, l.WorldId, l.WorldName, take, l.PricePerUnit, l.Hq, l.LastReview));
                    remaining -= take;
                }
            }

            if (remaining > 0) shortfalls.Add(new ShortfallItem(itemId, remaining));
        }

        var worlds = purchases
            .GroupBy(p => p.WorldId)
            .Select(g =>
            {
                var rows = g.OrderBy(p => p.ItemId).ThenBy(p => p.PricePerUnit)
                    .ThenBy(p => p.Hq).ThenByDescending(p => p.LastReview).ThenBy(p => p.Quantity).ToList();
                var name = rows.Select(p => p.WorldName).OrderBy(n => n, StringComparer.Ordinal).First();
                return new WorldShoppingGroup(g.Key, name, rows, rows.Sum(p => (long)p.Quantity * p.PricePerUnit));
            })
            .OrderBy(w => w.WorldName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.WorldId)
            .ToList();

        return new ShoppingPlan(worlds, skipped, shortfalls);
    }
}
