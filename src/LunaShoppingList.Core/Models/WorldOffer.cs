namespace LunaShoppingList.Core;

/// <summary>
/// What one world can supply of one item: its listings as a price ladder, cheapest first.
/// </summary>
/// <param name="WorldId">World row id.</param>
/// <param name="WorldName">World name.</param>
/// <param name="Steps">Price ladder sorted by unit price ascending (ties: newer review first); the builder guarantees the order.</param>
public record WorldOffer(uint WorldId, string WorldName, IReadOnlyList<PriceStep> Steps)
{
    // The total is computed once per Steps list and remembered. The cache remembers which list it was computed from, so an
    // instance made with "with { Steps = ... }" recomputes. It is held in one reference so a concurrent reader never sees a
    // list paired with another list's total, and it is left out of equality below.
    private sealed record UnitsCache(IReadOnlyList<PriceStep> Source, int Units);

    private UnitsCache? _unitsCache;

    /// <summary>Total units for sale on this world (clamped to <see cref="int.MaxValue"/>).</summary>
    /// <remarks>Supply only (every stack together), not a quantity any purchase is priced at: see <see cref="Cover"/> and <see cref="ExactCost"/>.</remarks>
    public int UnitsForSale
    {
        get
        {
            var cache = _unitsCache;
            if (cache is not null && ReferenceEquals(cache.Source, Steps))
                return cache.Units;
            long total = 0;
            foreach (var s in Steps)
                total += s.Quantity;
            var units = (int)Math.Min(total, int.MaxValue);
            _unitsCache = new UnitsCache(Steps, units);
            return units;
        }
    }

    /// <summary>Two offers are equal when their world id, world name and steps list are equal; the remembered total is not compared.</summary>
    public virtual bool Equals(WorldOffer? other)
        => other is not null
            && EqualityComparer<uint>.Default.Equals(WorldId, other.WorldId)
            && EqualityComparer<string>.Default.Equals(WorldName, other.WorldName)
            && EqualityComparer<IReadOnlyList<PriceStep>>.Default.Equals(Steps, other.Steps);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(
            EqualityComparer<uint>.Default.GetHashCode(WorldId),
            EqualityComparer<string>.Default.GetHashCode(WorldName),
            EqualityComparer<IReadOnlyList<PriceStep>>.Default.GetHashCode(Steps));

    /// <summary>Gil to buy <paramref name="quantity"/> units cheapest-first; a quantity above <see cref="UnitsForSale"/> is clamped, zero or less costs nothing.</summary>
    /// <remarks>
    /// Part of a stack counted as buyable, so this is NOT a purchase price (a listing is bought whole: see <see cref="Cover"/> and
    /// <see cref="ExactCost"/>). Kept only as the sort key <see cref="OfferBookBuilder"/> orders an item's worlds by.
    /// </remarks>
    public long CostFor(int quantity)
    {
        long cost = 0;
        var remaining = Math.Min(quantity, UnitsForSale);
        foreach (var s in Steps)
        {
            if (remaining <= 0)
                break;
            var take = Math.Min(remaining, s.Quantity);
            cost += (long)take * s.UnitPrice;
            remaining -= take;
        }
        return cost;
    }

    /// <summary>Unit price of the cheapest unit on this world, or 0 when nothing is for sale.</summary>
    /// <remarks>Display only (the world's lowest listed price), not the price of any purchase: see <see cref="Cover"/>.</remarks>
    public int MinUnitPrice => Steps.Count == 0 ? 0 : Steps[0].UnitPrice;

    // 2026-11-24: MaxUnitPriceFor and OldestReviewFor (partial-stack, cheapest-first) were removed once nothing but their own tests
    // used them; LinePricer / Cover report the prices and review of the stacks actually chosen.

    // ---- Whole stacks (release 0.16.0.0) ----
    //
    // A market listing (one PriceStep) can only be bought as a whole stack. The members below answer questions under that rule;
    // CostFor above still assumes part of a stack can be bought and only orders worlds in the offer book.
    //
    // The stack table is computed lazily and remembered in one reference, keyed by the Steps list it came from (the same pattern as
    // the UnitsForSale cache): a "with { Steps = ... }" copy recomputes, a concurrent reader never pairs one list with another
    // list's table, and equality ignores it. The table only ever grows (to the largest total asked for, at least doubling, never
    // past ExactTotalsLimit); answers never depend on what was asked first, because the best subset for a total does not depend on
    // how far the table reaches. Memory: 8 bytes per total plus one bit per stack per total.
    //
    // Steps whose quantity or unit price is zero or less are not stacks (the builder never makes them); stack indices always
    // refer to positions in Steps.

    private sealed record StackCache(
        IReadOnlyList<PriceStep> Source,
        StackKnapsack.Stack[] Stacks,
        int[] StepIndex,
        long Supply,
        int MaxStack,
        long Limit,
        StackKnapsack.Table? Table);

    private StackCache? _stackCache;

    private StackCache StackInfo()
    {
        var cache = _stackCache;
        if (cache is not null && ReferenceEquals(cache.Source, Steps))
            return cache;
        var stacks = new List<StackKnapsack.Stack>();
        var index = new List<int>();
        long supply = 0;
        var maxStack = 0;
        for (var i = 0; i < Steps.Count; i++)
        {
            var s = Steps[i];
            if (s.Quantity <= 0 || s.UnitPrice <= 0)
                continue;
            stacks.Add(new StackKnapsack.Stack(s.Quantity, s.UnitPrice, s.LastReview.UtcTicks));
            index.Add(i);
            supply += s.Quantity;
            maxStack = Math.Max(maxStack, s.Quantity);
        }
        cache = new StackCache(Steps, stacks.ToArray(), index.ToArray(), supply, maxStack,
            StackKnapsack.ExactLimit(stacks.Count, supply), null);
        _stackCache = cache;
        return cache;
    }

    /// <summary>The exact table reaching at least <paramref name="cap"/>; the caller guarantees cap is at most the limit.</summary>
    private StackKnapsack.Table TableFor(StackCache info, long cap)
    {
        var table = info.Table;
        if (table is not null && table.Cap >= cap)
            return table;
        var current = _stackCache;
        if (current is not null && ReferenceEquals(current.Source, info.Source) && current.Table is { } shared && shared.Cap >= cap)
            return shared;
        var newCap = Math.Min(info.Limit, Math.Max(cap, table is null ? cap : 2L * table.Cap));
        table = StackKnapsack.Table.Build(info.Stacks, (int)newCap);
        _stackCache = info with { Table = table };
        return table;
    }

    private static int ClampInt(long value) => (int)Math.Min(value, int.MaxValue);

    /// <summary>
    /// Every total at or below this is answered exactly by <see cref="ExactCost"/>, <see cref="Stacks"/>, <see cref="LegalTotals"/>,
    /// <see cref="SnapUp"/>, <see cref="SnapDown"/> and <see cref="Cover"/>. It is the units for sale when the whole world fits the
    /// size guard, otherwise the largest total whose exact table stays within <see cref="StackCoverSolver.MaxDenseTotals"/> totals
    /// and <see cref="StackCoverSolver.MaxCellUpdates"/> cell updates (stacks times totals). 0 with no stacks. Above it only the full
    /// supply is known for certain to be legal; see each member.
    /// </summary>
    public int ExactTotalsLimit => ClampInt(StackInfo().Limit);

    /// <summary>
    /// The totals above zero, up to <paramref name="cap"/>, that whole stacks can make (the subset sums of the stack quantities),
    /// ascending. Beyond <see cref="ExactTotalsLimit"/> only the full supply is listed.
    /// </summary>
    public IReadOnlyList<int> LegalTotals(int cap)
    {
        var info = StackInfo();
        var result = new List<int>();
        var exactCap = Math.Min(cap, info.Limit);
        if (exactCap > 0)
        {
            var table = TableFor(info, exactCap);
            for (var t = 1; t <= exactCap; t++)
                if (table.CostAt(t) >= 0)
                    result.Add(t);
        }
        if (info.Supply > info.Limit && info.Supply <= cap)
            result.Add((int)info.Supply);
        return result;
    }

    /// <summary>
    /// Gil for exactly <paramref name="total"/> units bought as whole stacks, the cheapest way; null when no set of stacks sums to it.
    /// 0 for a total of 0. Ties on cost go to the set whose oldest review is newest, then to the set that avoids stacks later in
    /// <see cref="Steps"/>. Above <see cref="ExactTotalsLimit"/> only the full supply is known: every other total there returns null
    /// (unknown, reported as not legal).
    /// </summary>
    public long? ExactCost(int total)
    {
        var info = StackInfo();
        if (total < 0 || total > info.Supply)
            return null;
        if (total == 0)
            return 0;
        if (total == info.Supply)
            return info.Stacks.Sum(s => s.Cost);
        if (total > info.Limit)
            return null;
        var cost = TableFor(info, total).CostAt(total);
        return cost < 0 ? null : cost;
    }

    /// <summary>
    /// Indices into <see cref="Steps"/> (ascending) of the stacks <see cref="ExactCost"/> prices for <paramref name="total"/>;
    /// empty for 0; null when no set sums to it (or, above <see cref="ExactTotalsLimit"/>, when it is not the full supply).
    /// </summary>
    public IReadOnlyList<int>? Stacks(int total)
    {
        var info = StackInfo();
        if (total < 0 || total > info.Supply)
            return null;
        if (total == 0)
            return Array.Empty<int>();
        if (total == info.Supply)
            return info.StepIndex.ToArray();
        if (total > info.Limit)
            return null;
        var subset = TableFor(info, total).Subset(total);
        return subset?.Select(i => info.StepIndex[i]).ToArray();
    }

    /// <summary>
    /// The cheapest whole stacks covering <paramref name="need"/>: the lowest cost over every set of stacks with at least that many
    /// units. Ties: fewer units (less surplus), then the newest oldest review, then the set avoiding stacks later in
    /// <see cref="Steps"/>. A world with fewer units than the need sells everything it has (every stack). A need of zero or less buys
    /// nothing. A stack of at least the need covers it alone and is weighed on its own, so the exact table only has to reach
    /// <c>need + largest stack below the need - 1</c> (2026-11-24; see <see cref="StackCoverSolver.Cover"/>). When even that table
    /// would pass the size guard the greedy fallback is used (or the cheapest single stack that covers alone, when cheaper) and
    /// <see cref="StackCover.Approximate"/> is true (the need is still covered with whole stacks, not provably cheapest).
    /// </summary>
    public StackCover Cover(int need)
    {
        var info = StackInfo();
        int[] chosen;
        var approximate = false;
        if (need <= 0)
            chosen = Array.Empty<int>();
        else
        {
            // The remembered full table answers whenever it can reach the cap the cover needs (see StackKnapsack.ExactSize); past
            // the exact limit the cover builds its own, smaller table (stacks no larger than the cap only), or goes greedy.
            StackKnapsack.Table? table = null;
            if (info.Supply > need && StackKnapsack.ExactSize(info.Stacks, need) is { Table: true, Cap: var cap } && cap <= info.Limit)
                table = TableFor(info, cap);
            (chosen, approximate) = StackKnapsack.Cover(info.Stacks, need, table);
        }

        long units = 0, cost = 0;
        int min = 0, max = 0;
        var oldest = DateTimeOffset.MaxValue;
        var indices = new int[chosen.Length];
        for (var k = 0; k < chosen.Length; k++)
        {
            var stepIndex = info.StepIndex[chosen[k]];
            indices[k] = stepIndex;
            var step = Steps[stepIndex];
            units += step.Quantity;
            cost += (long)step.Quantity * step.UnitPrice;
            min = k == 0 ? step.UnitPrice : Math.Min(min, step.UnitPrice);
            max = Math.Max(max, step.UnitPrice);
            if (step.LastReview < oldest)
                oldest = step.LastReview;
        }
        return new StackCover(ClampInt(units), cost, indices, min, max, oldest, approximate);
    }

    /// <summary>
    /// The smallest total whole stacks can make that is at least <paramref name="need"/>; the world's full supply when the need is
    /// more than it sells; 0 with no stacks or a need of zero or less. When that answer would lie above
    /// <see cref="ExactTotalsLimit"/>, the smaller of the greedy <see cref="Cover"/> units and the full supply (a legal total at least
    /// the need, not necessarily the smallest).
    /// </summary>
    public int SnapUp(int need)
    {
        var info = StackInfo();
        if (need <= 0 || info.Supply == 0)
            return 0;
        if (need >= info.Supply)
            return ClampInt(info.Supply);
        // Some legal total lies in need..need + largest stack - 1 (drop stacks from the full set while the need stays covered).
        var hi = Math.Min(info.Supply, (long)need + info.MaxStack - 1);
        var scanTo = Math.Min(hi, info.Limit);
        if (need <= scanTo)
        {
            var table = TableFor(info, scanTo);
            for (long t = need; t <= scanTo; t++)
                if (table.CostAt(t) >= 0)
                    return (int)t;
        }
        return ClampInt(Math.Min(Cover(need).Units, info.Supply));
    }

    /// <summary>
    /// The largest total whole stacks can make that is at most <paramref name="quantity"/>; 0 when none (or for zero or less); the
    /// full supply when the quantity is at least that. Above <see cref="ExactTotalsLimit"/> the largest legal total at or below the
    /// limit (not necessarily the largest below the quantity).
    /// </summary>
    public int SnapDown(int quantity)
    {
        var info = StackInfo();
        if (quantity <= 0)
            return 0;
        if (quantity >= info.Supply)
            return ClampInt(info.Supply);
        var top = Math.Min(quantity, info.Limit);
        if (top <= 0)
            return 0;
        var table = TableFor(info, top);
        for (var t = top; t > 0; t--)
            if (table.CostAt(t) >= 0)
                return (int)t;
        return 0;
    }
}
