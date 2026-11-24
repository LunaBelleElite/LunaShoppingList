namespace LunaShoppingList.Core;

/// <summary>Chooses a starting buying plan from an <see cref="OfferBook"/>.</summary>
/// <remarks>
/// Whole stacks (release 0.16.0.0): a market listing can only be bought as a whole stack, so a line's quantity is the units actually
/// bought on that world, always a sum of whole stacks there, and can exceed what the item needs (surplus).
/// </remarks>
public static class Suggester
{
    /// <summary>
    /// Returns the allocation for <paramref name="mode"/>, ordered by item id then world id, zero-quantity lines omitted. Each line's
    /// quantity is the units bought on that world (whole stacks; it can exceed the need).
    /// <see cref="SuggestionMode.Cheapest"/>: per item, the cheapest set of whole stacks across every world in scope that covers the
    /// need (see <see cref="StackCoverSolver.Cover"/>; ties: less surplus, newer review, lower world id), possibly split across
    /// worlds. <see cref="SuggestionMode.FewestWorlds"/>: a deterministic greedy cover that repeatedly picks the world able to supply
    /// the most still-unsupplied items in full (ties: lowest whole-stack cost, then world name, then id), assigns those items there,
    /// and repeats, then moves each fully supplied item to the cheapest chosen world that can supply it; it never visits more worlds
    /// than the cheapest plan, and covers every item whenever the cheapest plan does. An item that cannot be fully supplied gets
    /// every stack in scope.
    /// </summary>
    public static IReadOnlyList<AllocationLine> Suggest(OfferBook book, SuggestionMode mode) => SuggestDetailed(book, mode).Lines;

    /// <summary>
    /// The same suggestion as <see cref="Suggest"/>, plus the items whose stacks were chosen by the greedy fallback because the exact
    /// search passed the size guard (<see cref="StackCoverSolver.MaxCellUpdates"/>, <see cref="StackCoverSolver.MaxDenseTotals"/>).
    /// </summary>
    public static SuggestionResult SuggestDetailed(OfferBook book, SuggestionMode mode)
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
            return cheapest.ToResult();

        var greedy = AllocateGreedy(items);

        // Never worse than Cheapest: covering less of some item is not acceptable, more worlds is not acceptable, and the same
        // worlds at a higher price is pointless.
        if (CoversLess(greedy, cheapest, items))
            return cheapest.ToResult();
        var greedyWorlds = greedy.WorldsVisited();
        var cheapestWorlds = cheapest.WorldsVisited();
        if (greedyWorlds > cheapestWorlds)
            return cheapest.ToResult();
        if (greedyWorlds == cheapestWorlds && greedy.TotalCost() > cheapest.TotalCost())
            return cheapest.ToResult();
        return greedy.ToResult();
    }

    /// <summary>Units bought of one item on one world, and what they cost (whole stacks).</summary>
    private readonly record struct Buy(uint ItemId, uint WorldId, int Units, long Cost);

    private sealed class Plan
    {
        public List<Buy> Buys { get; } = new();

        public SortedSet<uint> Approximate { get; } = new();

        public void Add(uint itemId, ItemCover cover)
        {
            foreach (var w in cover.Worlds)
                Buys.Add(new Buy(itemId, w.WorldId, w.Units, w.Cost));
            if (cover.Approximate)
                Approximate.Add(itemId);
        }

        public int WorldsVisited() => Buys.Where(b => b.Units > 0).Select(b => b.WorldId).Distinct().Count();

        public long TotalCost() => Buys.Sum(b => b.Cost);

        public long Covered(ItemOffers item) => Math.Min(item.Needed, Buys.Where(b => b.ItemId == item.ItemId).Sum(b => (long)b.Units));

        public SuggestionResult ToResult()
            => new(
                Buys.Where(b => b.Units > 0)
                    .OrderBy(b => b.ItemId).ThenBy(b => b.WorldId)
                    .Select(b => new AllocationLine(b.ItemId, b.WorldId, b.Units))
                    .ToList(),
                Approximate.ToList());
    }

    /// <summary>True when the greedy plan covers fewer of some item's needed units than the cheapest plan (surplus does not count).</summary>
    private static bool CoversLess(Plan greedy, Plan cheapest, IReadOnlyList<ItemOffers> items)
        => items.Any(item => greedy.Covered(item) < cheapest.Covered(item));

    private static Plan AllocateCheapest(IReadOnlyList<ItemOffers> items)
    {
        var plan = new Plan();
        foreach (var item in items)
            plan.Add(item.ItemId, StackCoverSolver.Cover(item.Worlds, item.Needed));
        return plan;
    }

    private static Plan AllocateGreedy(IReadOnlyList<ItemOffers> items)
    {
        var plan = new Plan();
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

        // No world can supply any remaining item in full: cover it from the worlds already visited (cheapest whole stacks across
        // them); when they cannot cover it together they sell everything they have, and the rest is covered the same way from the
        // other worlds. Any world used joins the chosen set.
        foreach (var item in remaining)
        {
            var fromChosen = StackCoverSolver.Cover(item.Worlds.Where(w => chosen.Contains(w.WorldId)), item.Needed);
            plan.Add(item.ItemId, fromChosen);
            if (fromChosen.Units < item.Needed)
                plan.Add(item.ItemId, StackCoverSolver.Cover(item.Worlds.Where(w => !chosen.Contains(w.WorldId)), item.Needed - fromChosen.Units));
            foreach (var b in plan.Buys.Where(b => b.ItemId == item.ItemId))
                chosen.Add(b.WorldId);
        }

        // An item was fixed to the first world that could supply it, which may be dearer than a world chosen later. Move each
        // fully supplied item to the cheapest chosen world that can supply it in full (whole-stack cover cost); no world is added,
        // and a world left with nothing simply drops out.
        foreach (var (item, firstWorld) in full)
        {
            var target = item.Worlds
                .Where(w => chosen.Contains(w.WorldId) && w.UnitsForSale >= item.Needed)
                .OrderBy(w => w.Cover(item.Needed).Cost)
                .ThenBy(w => w.WorldName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(w => w.WorldId)
                .FirstOrDefault()
                ?? item.Worlds.First(w => w.WorldId == firstWorld);
            var cover = target.Cover(item.Needed);
            plan.Buys.Add(new Buy(item.ItemId, target.WorldId, cover.Units, cover.Cost));
            if (cover.Approximate)
                plan.Approximate.Add(item.ItemId);
        }

        return plan;
    }

    /// <summary>The world that can supply the most remaining items in full (ties: lowest summed whole-stack cover cost, then name, then id), with those items; null when no world can supply any.</summary>
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
                Cost: g.Sum(x => x.World.Cover(x.Item.Needed).Cost)))
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
}
