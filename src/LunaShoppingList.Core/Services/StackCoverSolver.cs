namespace LunaShoppingList.Core;

/// <summary>
/// Cheapest whole-stack cover of one item's need across several worlds. A market listing can only be bought as a whole stack, so
/// buying more than needed (surplus) is legal and can be cheapest.
/// </summary>
public static class StackCoverSolver
{
    /// <summary>
    /// Size guard: an exact search over <c>s</c> stacks and totals <c>0..cap</c> costs <c>s * (cap + 1)</c> cell updates. Above
    /// 20,000,000 (a fraction of a second; the table's take bits are then at most 2.5 MB) the greedy fallback is used instead and the
    /// result is flagged approximate.
    /// </summary>
    public const long MaxCellUpdates = 20_000_000;

    /// <summary>
    /// Size guard: an exact table holds at most 1,000,000 totals (8 MB of costs, plus 8 MB of review times while it is built).
    /// Above that the greedy fallback is used and the result is flagged approximate.
    /// </summary>
    public const int MaxDenseTotals = 1_000_000;

    /// <summary>
    /// The cheapest set of whole stacks from <paramref name="worlds"/> with at least <paramref name="need"/> units. Ties: fewer units
    /// (less surplus), then the newest oldest review (the oldest <see cref="PriceStep.LastReview"/> among the chosen stacks, the
    /// later the better), then the set avoiding stacks of higher world ids (stacks are ordered by world id, then by position in each
    /// world's <see cref="WorldOffer.Steps"/>, and a later stack is used only when it is strictly better). When the worlds together
    /// sell fewer units than the need, every stack is bought. A need of zero or less buys nothing.
    /// <para>
    /// Exact search (since 2026-11-24): a stack of at least the need covers it alone, and a cheapest cover that contains one is that
    /// stack alone (every other stack only adds cost), so those stacks are weighed one by one. Every other cover is made of stacks
    /// below the need and never needs more than <c>need + largest stack below the need - 1</c> units (dropping any stack from a bigger
    /// one still covers the need for less), so the 0/1 knapsack runs over totals <c>0..cap</c>, <c>cap = min(supply, that)</c>, and
    /// over the stacks no larger than cap: that many stacks times <c>cap + 1</c> cell updates and <c>8 * (cap + 1)</c> bytes plus one
    /// bit per stack per total. The answer is the cheaper of the two (a tie goes to the table's, which has fewer units), and it is
    /// stack for stack what the full table to <c>need + largest stack - 1</c> chose before. When the stacks below the need cannot
    /// reach it together, no table is built. Past <see cref="MaxCellUpdates"/> or <see cref="MaxDenseTotals"/> a deterministic greedy
    /// cover is used (cheapest unit price first, then newer review, then the order above, taken until the need is reached; then
    /// stacks no longer needed are dropped, dearest first), replaced by the cheapest single stack that covers alone when that is
    /// cheaper, and <see cref="ItemCover.Approximate"/> is true.
    /// </para>
    /// Steps whose quantity or unit price is zero or less are ignored. The worlds are expected to have distinct ids.
    /// </summary>
    public static ItemCover Cover(IEnumerable<WorldOffer> worlds, int need)
    {
        ArgumentNullException.ThrowIfNull(worlds);
        var ordered = worlds.OrderBy(w => w.WorldId).ToList();
        var stacks = new List<StackKnapsack.Stack>();
        var owner = new List<(int World, int Step)>();
        for (var w = 0; w < ordered.Count; w++)
        {
            var steps = ordered[w].Steps;
            for (var i = 0; i < steps.Count; i++)
            {
                var s = steps[i];
                if (s.Quantity <= 0 || s.UnitPrice <= 0)
                    continue;
                stacks.Add(new StackKnapsack.Stack(s.Quantity, s.UnitPrice, s.LastReview.UtcTicks));
                owner.Add((w, i));
            }
        }

        var (chosen, approximate) = StackKnapsack.Cover(stacks, need);

        var buys = new List<WorldBuy>();
        long units = 0, cost = 0;
        foreach (var group in chosen.GroupBy(k => owner[k].World).OrderBy(g => g.Key))
        {
            long u = 0, c = 0;
            var steps = new List<int>();
            foreach (var k in group)
            {
                u += stacks[k].Quantity;
                c += stacks[k].Cost;
                steps.Add(owner[k].Step);
            }
            steps.Sort();
            buys.Add(new WorldBuy(ordered[group.Key].WorldId, (int)Math.Min(u, int.MaxValue), c, steps));
            units += u;
            cost += c;
        }
        return new ItemCover(buys, (int)Math.Min(units, int.MaxValue), cost, approximate);
    }
}
