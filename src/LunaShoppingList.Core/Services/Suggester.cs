namespace LunaShoppingList.Core;

/// <summary>Chooses a starting buying plan from an <see cref="OfferBook"/>.</summary>
public static class Suggester
{
    /// <summary>
    /// Returns the allocation for <paramref name="mode"/>, ordered by item id then world id, zero-quantity lines omitted.
    /// <see cref="SuggestionMode.Cheapest"/>: each item's needed units come from the cheapest units in scope (the same units the
    /// existing planner would buy), possibly split across worlds. <see cref="SuggestionMode.FewestWorlds"/>: a deterministic greedy
    /// cover that repeatedly picks the world able to supply the most still-unsupplied items (ties: lowest cost, then world name, then
    /// id), assigns those items there, and repeats, then moves each fully supplied item to the cheapest chosen world that can supply it; it never visits more worlds than the cheapest plan, and supplies every item fully
    /// whenever the cheapest plan does. An item that cannot be fully supplied gets as many units as can be found, cheapest first.
    /// </summary>
    public static IReadOnlyList<AllocationLine> Suggest(OfferBook book, SuggestionMode mode)
    {
        ArgumentNullException.ThrowIfNull(book);
        if (mode != SuggestionMode.Cheapest && mode != SuggestionMode.FewestWorlds)
            throw new ArgumentOutOfRangeException(nameof(mode));

        var items = book.Items
            .Where(i => i.Needed > 0 && i.Worlds is { Count: > 0 })
            .OrderBy(i => i.ItemId)
            .ToList();

        var cheapest = AllocateCheapest(items);
        if (mode == SuggestionMode.Cheapest)
            return cheapest;

        var greedy = AllocateGreedy(items);

        // Never worse than Cheapest: same or fewer units supplied is not acceptable, more worlds is not
        // acceptable, and the same worlds at a higher price is pointless.
        if (SuppliesLess(greedy, cheapest, items))
            return cheapest;
        var greedyWorlds = WorldsVisited(greedy);
        var cheapestWorlds = WorldsVisited(cheapest);
        if (greedyWorlds > cheapestWorlds)
            return cheapest;
        if (greedyWorlds == cheapestWorlds && TotalCost(greedy, items) > TotalCost(cheapest, items))
            return cheapest;
        return greedy;
    }

    private static int WorldsVisited(IReadOnlyList<AllocationLine> lines)
        => lines.Select(l => l.WorldId).Distinct().Count();

    private static long TotalCost(IReadOnlyList<AllocationLine> lines, IReadOnlyList<ItemOffers> items)
    {
        long total = 0;
        foreach (var line in lines)
        {
            var offer = items.First(i => i.ItemId == line.ItemId).Worlds.First(w => w.WorldId == line.WorldId);
            total += offer.CostFor(line.Quantity);
        }
        return total;
    }

    private static bool SuppliesLess(IReadOnlyList<AllocationLine> greedy, IReadOnlyList<AllocationLine> cheapest, IReadOnlyList<ItemOffers> items)
    {
        foreach (var item in items)
        {
            var g = greedy.Where(l => l.ItemId == item.ItemId).Sum(l => (long)l.Quantity);
            var c = cheapest.Where(l => l.ItemId == item.ItemId).Sum(l => (long)l.Quantity);
            if (g < c)
                return true;
        }
        return false;
    }

    private static List<AllocationLine> AllocateCheapest(IReadOnlyList<ItemOffers> items)
    {
        var lines = new List<AllocationLine>();
        foreach (var item in items)
            TakeCheapest(item, item.Needed, _ => true, lines);
        return Sorted(lines);
    }

    /// <summary>Takes up to <paramref name="need"/> units of one item, cheapest unit first across every world passing <paramref name="filter"/>; returns the units still missing.</summary>
    private static int TakeCheapest(ItemOffers item, int need, Func<WorldOffer, bool> filter, List<AllocationLine> lines)
    {
        var units = item.Worlds
            .Where(filter)
            .SelectMany(w => w.Steps.Where(s => s.Quantity > 0 && s.UnitPrice > 0).Select(s => (World: w, Step: s)))
            .OrderBy(u => u.Step.UnitPrice)
            .ThenByDescending(u => u.Step.LastReview)
            .ThenBy(u => u.World.WorldId);

        var perWorld = new SortedDictionary<uint, int>();
        foreach (var (world, step) in units)
        {
            if (need <= 0)
                break;
            var take = Math.Min(need, step.Quantity);
            perWorld[world.WorldId] = perWorld.GetValueOrDefault(world.WorldId) + take;
            need -= take;
        }
        foreach (var (worldId, qty) in perWorld)
            lines.Add(new AllocationLine(item.ItemId, worldId, qty));
        return need;
    }

    private static List<AllocationLine> AllocateGreedy(IReadOnlyList<ItemOffers> items)
    {
        var lines = new List<AllocationLine>();
        var full = new List<(ItemOffers Item, uint WorldId)>();
        var remaining = items.ToList();
        var chosen = new HashSet<uint>();

        while (remaining.Count > 0)
        {
            var best = PickWorld(remaining);
            if (best is null)
                break;

            var (worldId, covered) = best.Value;
            chosen.Add(worldId);
            foreach (var item in covered)
                full.Add((item, worldId));
            remaining.RemoveAll(i => covered.Contains(i));
        }

        // No world can supply any remaining item in full: take what exists, preferring worlds already visited.
        foreach (var item in remaining)
        {
            var itemLines = new List<AllocationLine>();
            var missing = TakeCheapest(item, item.Needed, w => chosen.Contains(w.WorldId), itemLines);
            if (missing > 0)
            {
                var more = new List<AllocationLine>();
                TakeCheapest(item, missing, w => !chosen.Contains(w.WorldId), more);
                itemLines.AddRange(more);
            }
            foreach (var l in itemLines)
                chosen.Add(l.WorldId);
            lines.AddRange(itemLines);
        }

        // An item was fixed to the first world that could supply it, which may be dearer than a world chosen later. Move each
        // fully supplied item to the cheapest chosen world that can supply it in full; no world is added, and a world left with
        // nothing simply drops out.
        foreach (var (item, firstWorld) in full)
        {
            var target = item.Worlds
                .Where(w => chosen.Contains(w.WorldId) && w.UnitsForSale >= item.Needed)
                .OrderBy(w => w.CostFor(item.Needed))
                .ThenBy(w => w.WorldName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(w => w.WorldId)
                .FirstOrDefault();
            lines.Add(new AllocationLine(item.ItemId, target?.WorldId ?? firstWorld, item.Needed));
        }

        return Sorted(lines);
    }

    /// <summary>The world that can supply the most remaining items in full (ties: lowest summed cost, then name, then id), with those items; null when no world can supply any.</summary>
    private static (uint WorldId, List<ItemOffers> Items)? PickWorld(IReadOnlyList<ItemOffers> remaining)
    {
        var candidates = remaining
            .SelectMany(item => item.Worlds
                .Where(w => w.UnitsForSale >= item.Needed)
                .Select(w => (World: w, Item: item)))
            .GroupBy(x => x.World.WorldId)
            .Select(g => (
                WorldId: g.Key,
                Name: g.First().World.WorldName,
                Items: g.Select(x => x.Item).ToList(),
                Cost: g.Sum(x => x.World.CostFor(x.Item.Needed))))
            .ToList();
        if (candidates.Count == 0)
            return null;

        var winner = candidates
            .OrderByDescending(c => c.Items.Count)
            .ThenBy(c => c.Cost)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.WorldId)
            .First();
        return (winner.WorldId, winner.Items);
    }

    private static List<AllocationLine> Sorted(List<AllocationLine> lines)
        => lines.Where(l => l.Quantity > 0).OrderBy(l => l.ItemId).ThenBy(l => l.WorldId).ToList();
}
