using LunaShoppingList.Core;

namespace LunaShoppingList.Core.Tests;

/// <summary>
/// Test oracle: enumerates every subset of a small set of whole stacks. Only for tests, and only for a handful of stacks (2^n subsets).
/// </summary>
internal static class StackBruteForce
{
    public readonly record struct Subset(long Units, long Cost, DateTimeOffset Oldest, int Mask);

    /// <summary>Every subset of <paramref name="stacks"/> with its units, cost and oldest review (MaxValue for the empty set).</summary>
    public static List<Subset> All(IReadOnlyList<PriceStep> stacks)
    {
        if (stacks.Count > 16)
            throw new ArgumentException("brute force is for small stack sets only");
        var all = new List<Subset>();
        for (var mask = 0; mask < 1 << stacks.Count; mask++)
        {
            long units = 0, cost = 0;
            var oldest = DateTimeOffset.MaxValue;
            for (var i = 0; i < stacks.Count; i++)
            {
                if ((mask & (1 << i)) == 0)
                    continue;
                units += stacks[i].Quantity;
                cost += (long)stacks[i].Quantity * stacks[i].UnitPrice;
                if (stacks[i].LastReview < oldest)
                    oldest = stacks[i].LastReview;
            }
            all.Add(new Subset(units, cost, oldest, mask));
        }
        return all;
    }

    /// <summary>Cheapest subset with exactly <paramref name="total"/> units (ties: newer oldest review); null when none.</summary>
    public static Subset? Exact(List<Subset> all, long total)
    {
        Subset? best = null;
        foreach (var s in all.Where(s => s.Units == total))
            if (best is null || s.Cost < best.Value.Cost || (s.Cost == best.Value.Cost && s.Oldest > best.Value.Oldest))
                best = s;
        return best;
    }

    /// <summary>
    /// Cheapest subset with at least <paramref name="need"/> units (ties: fewer units, then newer oldest review). When no subset reaches
    /// the need, the full set (buy everything). Null only for a need of zero or less handled by the caller.
    /// </summary>
    public static Subset Cover(List<Subset> all, long need)
    {
        Subset? best = null;
        foreach (var s in all.Where(s => s.Units >= need))
            if (best is null || s.Cost < best.Value.Cost
                || (s.Cost == best.Value.Cost && s.Units < best.Value.Units)
                || (s.Cost == best.Value.Cost && s.Units == best.Value.Units && s.Oldest > best.Value.Oldest))
                best = s;
        return best ?? all.MaxBy(s => s.Units);
    }

    public static PriceStep RandomStep(Random rng, DateTimeOffset t0, int maxQty = 12, int maxPrice = 40)
        => new(rng.Next(1, maxPrice + 1), rng.Next(1, maxQty + 1), rng.Next(2) == 0, t0.AddDays(-rng.Next(0, 4)));
}
