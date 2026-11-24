namespace LunaShoppingList.Core;

/// <summary>
/// The whole-stack search shared by <see cref="WorldOffer"/> and <see cref="StackCoverSolver"/>. A stack is bought whole or not
/// at all, so the totals a buyer can reach are the subset sums of the stack quantities: a 0/1 knapsack over totals.
/// <para>
/// Stacks are given in a priority order. Among subsets with the same total the cheapest wins; equal cost goes to the newer oldest
/// review (the oldest <see cref="PriceStep.LastReview"/> in the subset, the later the better); a full tie goes to the subset that
/// avoids stacks later in the priority order (the dynamic programme only replaces a choice on a strict improvement, so a later
/// stack is only used when it is strictly better).
/// </para>
/// </summary>
internal static class StackKnapsack
{
    /// <summary>One stack: quantity, unit price and review time (UTC ticks).</summary>
    internal readonly record struct Stack(int Quantity, int UnitPrice, long ReviewTicks)
    {
        public long Cost => (long)Quantity * UnitPrice;
    }

    /// <summary>
    /// True when an exact table over totals 0..<paramref name="cap"/> for <paramref name="stackCount"/> stacks stays inside both
    /// bounds: at most <see cref="StackCoverSolver.MaxDenseTotals"/> totals and at most <see cref="StackCoverSolver.MaxCellUpdates"/>
    /// cell updates (stacks times totals).
    /// </summary>
    internal static bool Fits(int stackCount, long cap)
        => cap >= 0
           && cap + 1 <= StackCoverSolver.MaxDenseTotals
           && (long)stackCount * (cap + 1) <= StackCoverSolver.MaxCellUpdates;

    /// <summary>
    /// The exact table: for every total 0..Cap the cheapest subset cost (-1 when no subset sums to it) and, per stack, a bit saying
    /// whether the best subset for that total at that layer took the stack, so any total's subset can be rebuilt. Immutable.
    /// </summary>
    internal sealed class Table
    {
        private readonly long[] _cost;
        private readonly ulong[] _take;
        private readonly int _words;
        private readonly int[] _quantities;

        private Table(int cap, long[] cost, ulong[] take, int words, int[] quantities)
        {
            Cap = cap;
            _cost = cost;
            _take = take;
            _words = words;
            _quantities = quantities;
        }

        /// <summary>Largest total this table answers.</summary>
        public int Cap { get; }

        /// <summary>Cheapest cost of exactly <paramref name="total"/> units; -1 when not reachable or outside 0..Cap.</summary>
        public long CostAt(long total) => total < 0 || total > Cap ? -1 : _cost[total];

        /// <summary>
        /// Builds the table. Cost: stacks times (cap + 1) cell updates, with <see cref="Fits"/> checked by the caller. Memory:
        /// 8 bytes per total plus one bit per stack per total.
        /// </summary>
        public static Table Build(IReadOnlyList<Stack> stacks, int cap)
        {
            var m = stacks.Count;
            var cost = new long[cap + 1];
            var review = new long[cap + 1];
            Array.Fill(cost, -1L);
            cost[0] = 0;
            review[0] = long.MaxValue;
            var words = (cap + 64) / 64;
            var take = new ulong[(long)m * words];
            var quantities = new int[m];
            for (var i = 0; i < m; i++)
            {
                var s = stacks[i];
                quantities[i] = s.Quantity;
                if (s.Quantity > cap)
                    continue;
                var q = s.Quantity;
                var c = s.Cost;
                var r = s.ReviewTicks;
                var row = (long)i * words;
                for (var t = cap; t >= q; t--)
                {
                    var prev = cost[t - q];
                    if (prev < 0)
                        continue;
                    var nc = prev + c;
                    var nr = Math.Min(review[t - q], r);
                    var cur = cost[t];
                    if (cur < 0 || nc < cur || (nc == cur && nr > review[t]))
                    {
                        cost[t] = nc;
                        review[t] = nr;
                        take[row + (t >> 6)] |= 1UL << (t & 63);
                    }
                }
            }
            return new Table(cap, cost, take, words, quantities);
        }

        /// <summary>Indices (ascending) of the best subset for exactly <paramref name="total"/> units; null when not reachable.</summary>
        public int[]? Subset(long total)
        {
            if (CostAt(total) < 0)
                return null;
            var t = (int)total;
            var picked = new List<int>();
            for (var i = _quantities.Length - 1; i >= 0 && t > 0; i--)
            {
                if ((_take[(long)i * _words + (t >> 6)] & (1UL << (t & 63))) == 0)
                    continue;
                picked.Add(i);
                t -= _quantities[i];
            }
            picked.Reverse();
            return picked.ToArray();
        }

        /// <summary>
        /// The cheapest total in <paramref name="need"/>..<paramref name="upTo"/> (clamped to Cap); a lower total wins a tie on cost
        /// (less surplus). Returns -1 when none is reachable.
        /// </summary>
        public long BestCoverTotal(long need, long upTo)
        {
            long best = -1, bestCost = -1;
            var last = Math.Min(upTo, Cap);
            for (var t = Math.Max(need, 0); t <= last; t++)
            {
                var c = _cost[t];
                if (c < 0)
                    continue;
                if (best < 0 || c < bestCost)
                {
                    best = t;
                    bestCost = c;
                }
            }
            return best;
        }
    }

    /// <summary>
    /// The exact table a cover of <paramref name="need"/> needs (2026-11-24): totals <c>0..Cap</c> over the <c>Count</c> stacks no larger
    /// than Cap, where Cap is <c>min(supply, need + largest stack below the need - 1)</c>. A stack of at least the need covers it
    /// alone and is considered on its own (<see cref="Cover"/>), so only the stacks below the need decide how far the table reaches.
    /// <c>Table</c> is false when no table is needed: the stacks below the need together sell less than it, so the answer is one
    /// stack. Expects <c>supply &gt; need &gt; 0</c>.
    /// </summary>
    internal static (bool Table, long Cap, int Count) ExactSize(IReadOnlyList<Stack> stacks, long need)
    {
        long supply = 0, smallSupply = 0;
        var maxSmall = 0;
        foreach (var s in stacks)
        {
            supply += s.Quantity;
            if (s.Quantity < need)
            {
                smallSupply += s.Quantity;
                maxSmall = Math.Max(maxSmall, s.Quantity);
            }
        }
        if (smallSupply < need)
            return (false, 0, 0);
        var cap = Math.Min(supply, need + maxSmall - 1);
        var count = 0;
        foreach (var s in stacks)
            if (s.Quantity <= cap)
                count++;
        return (true, cap, count);
    }

    /// <summary>
    /// Exact cheapest cover of <paramref name="need"/> (at least 1) from <paramref name="stacks"/>, or the greedy fallback when the
    /// exact table would break the bounds. When the stacks cannot reach the need, every stack is taken. Returns the chosen indices
    /// (ascending) and whether the greedy fallback was used. <paramref name="table"/>, when given, is a table over all of
    /// <paramref name="stacks"/> (in this order) reaching at least the cap of <see cref="ExactSize"/>; otherwise one is built.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A cheapest cover never exceeds <c>need + largest stack - 1</c> units: with more, dropping any one stack still covers the need
    /// and costs less (unit prices are above zero). Since 2026-11-24 the table reaches less far: a stack of at least the need covers
    /// it alone, so a cheapest cover containing one is that stack alone (any other stack only adds cost). Every cover that is not a
    /// single such stack is made of stacks below the need and, by the same argument, never exceeds <c>need + largest stack below
    /// the need - 1</c> units (the cap of <see cref="ExactSize"/>). So the answer is the better of the table's best total up to that
    /// cap and the best single stack above it.
    /// </para>
    /// <para>
    /// The answer is the same, stack for stack, as the full table to <c>need + largest stack - 1</c> gave: the table's entries up to a
    /// total never depend on how far it reaches (each total is built from smaller ones, and a stack larger than the cap changes no
    /// entry within it), a total above the cap can only be the cheapest if its best subset is one stack (anything else costs more
    /// than one of its own stacks alone, or than a smaller set of stacks below the need), and the full table's tie-breaks between
    /// totals (lower total) and between single stacks of one size (newer review, then the earlier stack) are applied unchanged.
    /// When the stacks below the need cannot reach it together no table is built at all.
    /// </para>
    /// <para>
    /// Past the size guard the greedy fallback runs and the result is flagged; the cheapest single stack that covers the need alone
    /// replaces the greedy set when it is cheaper (ties: fewer units, then newer review).
    /// </para>
    /// </remarks>
    internal static (int[] Indices, bool Approximate) Cover(IReadOnlyList<Stack> stacks, long need, Table? table = null)
    {
        long supply = 0;
        foreach (var s in stacks)
            supply += s.Quantity;
        if (need <= 0)
            return (Array.Empty<int>(), false);
        if (supply <= need)
            return (Enumerable.Range(0, stacks.Count).ToArray(), false);

        var (useTable, cap, count) = ExactSize(stacks, need);
        // The best single stack above the cap (every stack covers alone there, since cap >= need - 1): cheapest, then fewer units,
        // then newer review, then the earlier stack (the full table only replaced a choice on a strict improvement).
        var single = -1;
        for (var i = 0; i < stacks.Count; i++)
        {
            var s = stacks[i];
            if (s.Quantity < need || (useTable && s.Quantity <= cap))
                continue;
            if (single < 0 || Better(s, stacks[single]))
                single = i;
        }

        if (!useTable)
            return ([single], false);

        if (table is null || table.Cap < cap)
        {
            if (!Fits(count, cap))
            {
                var greedy = Greedy(stacks, need);
                var best = BestSingle(stacks, need);
                return best >= 0 && BeatsGreedy(stacks[best], stacks, greedy) ? ([best], true) : (greedy, true);
            }
            if (count == stacks.Count)
                table = Table.Build(stacks, (int)cap);
            else
            {
                // Stacks larger than the cap change nothing in the table: leave them out, keeping the order, and map back.
                var kept = new List<Stack>(count);
                var map = new int[count];
                for (var i = 0; i < stacks.Count; i++)
                    if (stacks[i].Quantity <= cap)
                    {
                        map[kept.Count] = i;
                        kept.Add(stacks[i]);
                    }
                var small = Table.Build(kept, (int)cap);
                var smallTotal = small.BestCoverTotal(need, cap);
                if (single >= 0 && (smallTotal < 0 || stacks[single].Cost < small.CostAt(smallTotal)))
                    return ([single], false);
                return (small.Subset(smallTotal)!.Select(k => map[k]).ToArray(), false);
            }
        }
        var total = table.BestCoverTotal(need, cap);
        if (single >= 0 && (total < 0 || stacks[single].Cost < table.CostAt(total)))
            return ([single], false);
        return (table.Subset(total)!, false);
    }

    /// <summary>Single-stack order: cheaper, then fewer units, then newer review (an equal stack later in the order never wins).</summary>
    private static bool Better(Stack a, Stack b)
        => a.Cost != b.Cost ? a.Cost < b.Cost
            : a.Quantity != b.Quantity ? a.Quantity < b.Quantity
            : a.ReviewTicks > b.ReviewTicks;

    /// <summary>The best stack that covers <paramref name="need"/> alone (<see cref="Better"/>), or -1.</summary>
    private static int BestSingle(IReadOnlyList<Stack> stacks, long need)
    {
        var best = -1;
        for (var i = 0; i < stacks.Count; i++)
            if (stacks[i].Quantity >= need && (best < 0 || Better(stacks[i], stacks[best])))
                best = i;
        return best;
    }

    /// <summary>True when <paramref name="single"/> beats the greedy set: cheaper, then fewer units, then a newer oldest review.</summary>
    private static bool BeatsGreedy(Stack single, IReadOnlyList<Stack> stacks, int[] greedy)
    {
        long cost = 0, units = 0;
        var review = long.MaxValue;
        foreach (var i in greedy)
        {
            cost += stacks[i].Cost;
            units += stacks[i].Quantity;
            review = Math.Min(review, stacks[i].ReviewTicks);
        }
        return single.Cost != cost ? single.Cost < cost
            : single.Quantity != units ? single.Quantity < units
            : single.ReviewTicks > review;
    }

    /// <summary>The largest cap whose exact table fits the bounds for <paramref name="stackCount"/> stacks (never above <paramref name="supply"/>).</summary>
    internal static long ExactLimit(int stackCount, long supply)
    {
        if (stackCount == 0)
            return 0;
        var byCells = StackCoverSolver.MaxCellUpdates / stackCount - 1;
        return Math.Max(0, Math.Min(supply, Math.Min(StackCoverSolver.MaxDenseTotals - 1, byCells)));
    }

    /// <summary>
    /// Greedy fallback cover (approximate): stacks by unit price ascending, then newer review, then priority order, taken until the
    /// need is reached; then any chosen stack that is no longer needed is dropped, dearest stack (quantity times price) first, ties
    /// to the later stack in priority order. Deterministic. Only used when the need can be reached.
    /// </summary>
    internal static int[] Greedy(IReadOnlyList<Stack> stacks, long need)
    {
        var order = Enumerable.Range(0, stacks.Count)
            .OrderBy(i => stacks[i].UnitPrice)
            .ThenByDescending(i => stacks[i].ReviewTicks)
            .ThenBy(i => i);
        var chosen = new List<int>();
        long total = 0;
        foreach (var i in order)
        {
            if (total >= need)
                break;
            chosen.Add(i);
            total += stacks[i].Quantity;
        }
        foreach (var i in chosen.OrderByDescending(i => stacks[i].Cost).ThenByDescending(i => i).ToList())
        {
            if (total - stacks[i].Quantity >= need)
            {
                chosen.Remove(i);
                total -= stacks[i].Quantity;
            }
        }
        chosen.Sort();
        return chosen.ToArray();
    }
}
