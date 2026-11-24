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
    /// An optional <paramref name="filter"/> drops listings whose review is older than <c>Now - MaxAge</c> (exactly that old is kept; an unknown review time is kept); the count per item is <see cref="ItemOffers.DroppedListings"/>.
    /// </summary>
    public static OfferBook Build(
        IReadOnlyList<Requirement> requirements,
        IReadOnlyList<ItemListings> listings,
        IReadOnlySet<uint> allowedWorldIds,
        ExclusionState exclusions,
        ListingFilter? filter = null)
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
            var dropped = 0;
            if (listingsByItem.TryGetValue(itemId, out var usable))
            {
                var candidates = usable.Where(l => !IsTooOld(l, filter)).ToList();
                dropped = usable.Count - candidates.Count;
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
            items.Add(new ItemOffers(itemId, qty, ordered) { DroppedListings = dropped });
        }

        return new OfferBook(items, skipped);
    }

    /// <summary>True when the filter has a maximum age and the listing was last reviewed before <c>Now - MaxAge</c>. A review time at or before the Unix epoch is unknown and never too old.</summary>
    private static bool IsTooOld(Listing l, ListingFilter? filter)
        => filter?.MaxAge is TimeSpan maxAge && l.LastReview > DateTimeOffset.UnixEpoch && l.LastReview < filter.Now - maxAge;

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
                // The primary key already orders by UnitsForSale, so only equal unit counts reach here: equal average
                // unit price is then equal total cost, compared exactly. (Cross-multiplying cost * units overflowed a long.)
                return a.CostFor(needed).CompareTo(b.CostFor(needed));
            }))
            .ThenBy(w => w.WorldName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.WorldId);
        return full.Concat(partial).ToList();
    }
}
