using System.Numerics;
using LunaShoppingList.Core;

namespace LunaShoppingList.Core.Tests;

/// <summary>
/// Test oracle for the plan frontier, and the random book generators the exact-frontier tests share.
/// Only for tests, and only for books of up to about 22 worlds (it tries every subset of worlds) whose items each sell at most 16 stacks.
/// Ported 2026-11-25 from the measurement scratch tree (validated there against the brute force of <c>PlanFrontierTests</c> on 355 comparisons).
/// </summary>
internal static class FrontierOracle
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static WorldOffer W(uint id, params (int Price, int Qty)[] steps)
        => new(id, "W" + id, steps
            .Select((s, n) => new PriceStep(s.Price, s.Qty, false, T0.AddMinutes(-n)))
            .OrderBy(p => p.UnitPrice).ThenByDescending(p => p.LastReview).ToList());

    private static OfferBook Book(IEnumerable<ItemOffers> items)
        => new(items.OrderBy(i => i.ItemId).ToList(), new List<SkippedItem>());

    /// <summary>4 to 8 items, each on up to 5 worlds with 1 to 3 stacks; every world is anchored to some item so the book spans about <paramref name="worlds"/> worlds.</summary>
    public static OfferBook MediumBook(Random rng, int worlds)
    {
        var items = new List<ItemOffers>();
        var itemCount = rng.Next(4, 9);
        var anchors = Enumerable.Range(0, worlds).OrderBy(_ => rng.Next()).ToList();
        for (var i = 0; i < itemCount; i++)
        {
            var on = new HashSet<int>();
            for (var a = i; a < worlds; a += itemCount)
                if (on.Count < 5)
                    on.Add(anchors[a]);
            var extra = rng.Next(0, 5 - on.Count + 1);
            while (extra-- > 0)
                on.Add(rng.Next(worlds));
            var offers = on.Take(5).Select(w => W((uint)(300 + w), Enumerable.Range(0, rng.Next(1, 4))
                .Select(_ => (rng.Next(1, 60), rng.Next(1, 9))).ToArray())).ToArray();
            items.Add(new ItemOffers((uint)(1 + i), rng.Next(1, 16), offers.ToList()));
        }
        return Book(items);
    }

    /// <summary>6 to 10 items, each on 4 to 7 worlds with 1 or 2 stacks.</summary>
    public static OfferBook DenseBook(Random rng, int worlds)
    {
        var itemCount = rng.Next(6, 11);
        var anchors = Enumerable.Range(0, worlds).OrderBy(_ => rng.Next()).ToList();
        var items = new List<ItemOffers>();
        for (var i = 0; i < itemCount; i++)
        {
            var on = new HashSet<int>();
            for (var a = i; a < worlds && on.Count < 7; a += itemCount)
                on.Add(anchors[a]);
            var want = Math.Min(rng.Next(4, 8), worlds);
            while (on.Count < want)
                on.Add(rng.Next(worlds));
            var offers = on.Select(w => W((uint)(300 + w), Enumerable.Range(0, rng.Next(1, 3))
                .Select(_ => (rng.Next(1, 60), rng.Next(1, 9))).ToArray())).ToArray();
            items.Add(new ItemOffers((uint)(1 + i), rng.Next(1, 20), offers.ToList()));
        }
        return Book(items);
    }

    private static ItemOffers It(uint id, int needed, params WorldOffer[] worlds) => new(id, needed, worlds.ToList());

    /// <summary>
    /// A book shaped like a real multi-data-center list (ported from the measurement scratch tree): worlds grouped into data centers with
    /// a price level each, items with a log-uniform base price, 1 to 12 stacks per world, needs from 1 to 999. Too big for the oracle
    /// (items sell more than 16 stacks); for timing and invariants only.
    /// </summary>
    public static OfferBook AllDcBook(int seed, int[] worldsPerDc, int itemCount)
    {
        var rng = new Random(seed);
        var worldDc = new List<int>();
        var worldMult = new List<double>();
        for (var d = 0; d < worldsPerDc.Length; d++)
            for (var w = 0; w < worldsPerDc[d]; w++)
            {
                worldDc.Add(d);
                worldMult.Add(0.92 + rng.NextDouble() * 0.20);
            }
        var items = new List<ItemOffers>();
        for (var i = 0; i < itemCount; i++)
        {
            var basePrice = Math.Exp(Math.Log(50) + rng.NextDouble() * (Math.Log(20000) - Math.Log(50)));
            var dcMult = Enumerable.Range(0, worldsPerDc.Length).Select(_ => 0.75 + rng.NextDouble() * 0.65).ToArray();
            var fraction = 0.3 + rng.NextDouble() * 0.6;
            var r = rng.NextDouble();
            var need = r < 0.5 ? rng.Next(1, 21) : r < 0.8 ? rng.Next(20, 201) : rng.Next(200, 1000);
            var offers = new List<WorldOffer>();
            for (var w = 0; w < worldDc.Count; w++)
            {
                if (rng.NextDouble() >= fraction)
                    continue;
                var stacks = rng.Next(1, 13);
                var steps = new List<(int, int)>();
                for (var s = 0; s < stacks; s++)
                {
                    var qty = rng.NextDouble() < 0.9
                        ? (rng.NextDouble() < 0.4 ? rng.Next(1, 11) : rng.Next(1, 100))
                        : rng.Next(100, 251);
                    var price = basePrice * dcMult[worldDc[w]] * worldMult[w] * (1 + s * 0.03) * (0.95 + rng.NextDouble() * 0.10);
                    steps.Add((Math.Max(1, (int)Math.Round(price)), qty));
                }
                offers.Add(W((uint)(1000 + w), steps.ToArray()));
            }
            if (offers.Count == 0)
                offers.Add(W((uint)(1000 + rng.Next(worldDc.Count)), (Math.Max(1, (int)basePrice), 50)));
            items.Add(It((uint)(1 + i), need, offers.ToArray()));
        }
        return Book(items);
    }

    /// <summary>
    /// <c>result[k]</c> is the true cheapest total over every set of at most k worlds that sells the target of every item
    /// (<c>long.MaxValue</c> when no such set exists), index 0 to the number of worlds in the book.
    /// Per item: the cheapest whole-stack cover for each set of its own worlds actually used, closed under supersets.
    /// </summary>
    public static long[] ExactAtMost(OfferBook book)
    {
        var (worlds, perItem) = Tables(book);
        var bySize = new long[worlds.Count + 1];
        Array.Fill(bySize, long.MaxValue);
        for (var mask = 0; mask < 1 << worlds.Count; mask++)
        {
            var total = CostOfMask(perItem, mask);
            var size = BitOperations.PopCount((uint)mask);
            if (total != long.MaxValue && total < bySize[size])
                bySize[size] = total;
        }
        for (var k = 1; k < bySize.Length; k++)
            bySize[k] = Math.Min(bySize[k], bySize[k - 1]);
        return bySize;
    }

    /// <summary>
    /// The true cheapest cost of supplying every item (each to its target: the lesser of its need and its supply in the whole book) from
    /// exactly the worlds in <paramref name="worldIds"/> (an item may leave some of them unused); <c>long.MaxValue</c> when some item cannot reach its target there.
    /// </summary>
    public static long CostOf(OfferBook book, IReadOnlyCollection<uint> worldIds)
    {
        var (worlds, perItem) = Tables(book);
        var mask = 0;
        for (var w = 0; w < worlds.Count; w++)
            if (worldIds.Contains(worlds[w]))
                mask |= 1 << w;
        return CostOfMask(perItem, mask);
    }

    private static long CostOfMask(List<(int[] GlobalBit, long[] CostByLocalMask)> perItem, int mask)
    {
        long total = 0;
        foreach (var (bits, cost) in perItem)
        {
            var local = 0;
            for (var l = 0; l < bits.Length; l++)
                if ((mask & (1 << bits[l])) != 0)
                    local |= 1 << l;
            if (cost[local] == long.MaxValue)
                return long.MaxValue;
            total += cost[local];
        }
        return total;
    }

    private static (List<uint> Worlds, List<(int[] GlobalBit, long[] CostByLocalMask)> PerItem) Tables(OfferBook book)
    {
        var worlds = book.Items.SelectMany(i => i.Worlds).Select(w => w.WorldId).Distinct().OrderBy(x => x).ToList();
        var perItem = new List<(int[] GlobalBit, long[] CostByLocalMask)>();
        foreach (var item in book.Items)
        {
            var offers = item.Worlds.GroupBy(w => w.WorldId).Select(g => g.First()).ToList();
            var stacks = new List<PriceStep>();
            var stackLocal = new List<int>();
            for (var l = 0; l < offers.Count; l++)
                foreach (var s in offers[l].Steps)
                {
                    stacks.Add(s);
                    stackLocal.Add(l);
                }
            long supply = stacks.Sum(s => (long)s.Quantity);
            var target = Math.Min(item.Needed, supply);
            if (target <= 0)
                continue;
            var nl = offers.Count;
            var cost = new long[1 << nl];
            Array.Fill(cost, long.MaxValue);
            foreach (var sub in StackBruteForce.All(stacks))
            {
                if (sub.Units < target)
                    continue;
                var used = 0;
                for (var i = 0; i < stacks.Count; i++)
                    if ((sub.Mask & (1 << i)) != 0)
                        used |= 1 << stackLocal[i];
                if (sub.Cost < cost[used])
                    cost[used] = sub.Cost;
            }
            for (var b = 0; b < nl; b++)
                for (var m = 0; m < cost.Length; m++)
                    if ((m & (1 << b)) != 0 && cost[m ^ (1 << b)] < cost[m])
                        cost[m] = cost[m ^ (1 << b)];
            perItem.Add((offers.Select(o => worlds.IndexOf(o.WorldId)).ToArray(), cost));
        }

        return (worlds, perItem);
    }
}
