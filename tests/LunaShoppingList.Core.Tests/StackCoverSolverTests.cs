using LunaShoppingList.Core;

namespace LunaShoppingList.Core.Tests;

/// <summary>Cheapest whole-stack cover across several worlds (<see cref="StackCoverSolver"/>).</summary>
public class StackCoverSolverTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static PriceStep S(int price, int qty, int ageDays = 0) => new(price, qty, false, T0.AddDays(-ageDays));

    private static WorldOffer W(uint id, params PriceStep[] steps) => new(id, "W" + id, steps);

    private static string Render(ItemCover c)
        => string.Join(";", c.Worlds.Select(w => $"{w.WorldId}x{w.Units}={w.Cost}[{string.Join(",", w.StackIndices)}]"));

    [Fact]
    public void NullWorlds_Throws() => Assert.Throws<ArgumentNullException>(() => StackCoverSolver.Cover(null!, 3));

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void NothingNeeded_BuysNothing(int need)
    {
        var c = StackCoverSolver.Cover([W(1, S(5, 2))], need);
        Assert.Empty(c.Worlds);
        Assert.Equal(0, c.Units);
        Assert.Equal(0L, c.Cost);
        Assert.False(c.Approximate);
    }

    [Fact]
    public void NoWorlds_BuysNothing() => Assert.Empty(StackCoverSolver.Cover([], 3).Worlds);

    [Fact]
    public void ShortSupply_BuysEveryStack_WorldsInIdOrder()
    {
        var c = StackCoverSolver.Cover([W(9, S(3, 2)), W(2, S(1, 1), S(4, 1))], 10);
        Assert.Equal("2x2=5[0,1];9x2=6[0]", Render(c));
        Assert.Equal(4, c.Units);
        Assert.Equal(11L, c.Cost);
    }

    [Fact]
    public void CombinesStacksAcrossWorlds_WithSurplusWhenCheaper()
    {
        // need 7: 4 @ 1 on world 1 + 4 @ 2 on world 2 = 12 gil (8 units), against 7 @ 5 on world 3 (35).
        var c = StackCoverSolver.Cover([W(1, S(1, 4)), W(2, S(2, 4)), W(3, S(5, 7))], 7);
        Assert.Equal("1x4=4[0];2x4=8[0]", Render(c));
        Assert.Equal(8, c.Units);
        Assert.Equal(12L, c.Cost);
    }

    [Fact]
    public void MatchesBruteForce_On300RandomWorldSets()
    {
        var rng = new Random(31337);
        for (var n = 0; n < 300; n++)
        {
            var worlds = Enumerable.Range(1, rng.Next(1, 5))
                .Select(w => new WorldOffer((uint)(w * 3), "W", Enumerable.Range(0, rng.Next(0, 4))
                    .Select(_ => StackBruteForce.RandomStep(rng, T0, maxQty: 10, maxPrice: 25)).ToList()))
                .ToList();
            var all = StackBruteForce.All(worlds.SelectMany(w => w.Steps).ToList());
            var supply = worlds.Sum(w => w.UnitsForSale);
            for (var need = 1; need <= supply + 1; need++)
            {
                var best = StackBruteForce.Cover(all, need);
                var c = StackCoverSolver.Cover(worlds, need);
                Assert.False(c.Approximate);
                Assert.Equal(best.Cost, c.Cost);
                Assert.Equal(best.Units, c.Units);
                var oldest = c.Worlds.SelectMany(b => b.StackIndices.Select(i => worlds.Single(w => w.WorldId == b.WorldId).Steps[i].LastReview))
                    .DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
                Assert.Equal(best.Oldest, oldest);
                foreach (var b in c.Worlds)
                {
                    var wo = worlds.Single(w => w.WorldId == b.WorldId);
                    Assert.Equal(b.Units, b.StackIndices.Sum(i => wo.Steps[i].Quantity));
                    Assert.Equal(b.Cost, b.StackIndices.Sum(i => (long)wo.Steps[i].Quantity * wo.Steps[i].UnitPrice));
                }
                Assert.Equal(c.Worlds.OrderBy(b => b.WorldId).Select(b => b.WorldId), c.Worlds.Select(b => b.WorldId));
            }
        }
    }

    // ---- size guard: both bounds, at the bound (exact) and one past it (greedy, flagged) ----

    [Fact]
    public void DenseBound_AtTheBoundIsExact()
    {
        // need 1: totals up to 999,999 (1,000,000 totals, the bound): exact, the 1-unit stack at 5 gil wins.
        var c = StackCoverSolver.Cover([W(1, S(1, 999_999)), W(2, S(5, 1))], 1);
        Assert.False(c.Approximate);
        Assert.Equal("2x1=5[0]", Render(c));
    }

    [Fact]
    public void HugeStackPastTheOldDenseBound_IsBeatenByTheOneUnitStack()
    {
        // Superseded 2026-11-24 (was DenseBound_OnePastIsGreedyAndFlagged, expecting Approximate true and "1x1000000=1000000[0]",
        // 1,000,000 gil for a need of 1). Both stacks cover the need alone, so no table is needed: the 5 gil stack, exactly.
        var c = StackCoverSolver.Cover([W(1, S(1, 1_000_000)), W(2, S(5, 1))], 1);
        Assert.False(c.Approximate);
        Assert.Equal("2x1=5[0]", Render(c));
    }

    // Stacks below the need only, so the table must reach need + largest - 1 (the bounds the size guard still applies to).

    [Fact]
    public void SmallStacks_DenseBound_AtTheBoundIsExact()
    {
        // need 500,001, stacks 499,999 @ 1 on worlds 1 and 2, 2 @ 5 on world 3: totals up to 999,999 (1,000,000 totals): exact.
        var c = StackCoverSolver.Cover([W(1, S(1, 499_999)), W(2, S(1, 499_999)), W(3, S(5, 2))], 500_001);
        Assert.False(c.Approximate);
        Assert.Equal("1x499999=499999[0];3x2=10[0]", Render(c));
    }

    [Fact]
    public void SmallStacks_DenseBound_OnePastIsGreedyAndFlagged()
    {
        var c = StackCoverSolver.Cover([W(1, S(1, 500_000)), W(2, S(1, 500_000)), W(3, S(5, 2))], 500_001);
        Assert.True(c.Approximate);
        Assert.Equal("1x500000=500000[0];2x500000=500000[0]", Render(c));
    }

    [Fact]
    public void SmallStacks_CellBound_AtTheBoundIsExact()
    {
        // 40 stacks: 249,999 @ 1 on worlds 1 and 2, 1 @ 5 on worlds 3..40. need 250,001: totals up to 250,001 + 249,999 - 1 =
        // 499,999, so 40 * 500,000 = 20,000,000 cell updates, the bound: exact, one big stack and two 1-unit stacks (250,009).
        var c = StackCoverSolver.Cover(CellBoundWorlds(249_999), 250_001);
        Assert.False(c.Approximate);
        Assert.Equal("1x249999=249999[0];3x1=5[0];4x1=5[0]", Render(c));
    }

    [Fact]
    public void SmallStacks_CellBound_OnePastIsGreedyAndFlagged()
    {
        // The same with 250,000-unit stacks and need 250,002: 40 * 500,002 cell updates, past. Greedy buys both big stacks.
        var c = StackCoverSolver.Cover(CellBoundWorlds(250_000), 250_002);
        Assert.True(c.Approximate);
        Assert.Equal("1x250000=250000[0];2x250000=250000[0]", Render(c));
    }

    private static List<WorldOffer> CellBoundWorlds(int big)
    {
        var worlds = new List<WorldOffer> { W(1, S(1, big)), W(2, S(1, big)) };
        worlds.AddRange(Enumerable.Range(3, 38).Select(i => W((uint)i, S(5, 1))));
        return worlds;
    }

    [Fact]
    public void OneHugeCheapUnitStack_LosesToStacksOnOtherWorlds()
    {
        // A 9,999 crystal stack at 2 on world 1 and 1,999 stacks of 30 at 5 on world 2, need 50: two 30 stacks (300) beat the big stack
        // (19,998), exactly. Before 2026-11-24 the table had to reach 10,048 over 2,000 stacks, past the guard, and greedy paid 19,998.
        var small = Enumerable.Range(0, 1_999).Select(_ => S(5, 30)).ToArray();
        var c = StackCoverSolver.Cover([W(1, S(2, 9_999)), W(2, small)], 50);
        Assert.False(c.Approximate);
        Assert.Equal("2x60=300[0,1]", Render(c));
    }

    [Fact]
    public void PastTheGuard_GreedyLosesToACheaperStackThatCoversAlone()
    {
        // Greedy (cheapest unit first) pays 2,000,000 for two 500,000 stacks at 2; one 500,001 stack at 3 covers alone for 1,500,003.
        var c = StackCoverSolver.Cover([W(1, S(2, 500_000)), W(2, S(2, 500_000)), W(3, S(3, 500_001)), W(4, S(5, 2))], 500_001);
        Assert.True(c.Approximate);
        Assert.Equal("3x500001=1500003[0]", Render(c));
    }

    [Fact]
    public void PicksTheSameStacksAsTheFullTable_IndexForIndex_On400RandomWorldSets()
    {
        // The full table of the old search, read through a WorldOffer holding every stack in the solver's order (world id, then
        // position). The solver must choose exactly the same stacks, ties included.
        var rng = new Random(8080);
        for (var n = 0; n < 400; n++)
        {
            var worlds = Enumerable.Range(1, rng.Next(1, 5))
                .Select(w => new WorldOffer((uint)(w * 3), "W", Enumerable.Range(0, rng.Next(1, 4))
                    .Select(_ => StackBruteForce.RandomStep(rng, T0, maxQty: n % 3 == 0 ? 4 : 10, maxPrice: n % 2 == 0 ? 3 : 25)).ToList()))
                .ToList();
            var merged = new WorldOffer(99, "All", worlds.OrderBy(w => w.WorldId).SelectMany(w => w.Steps).ToList());
            var owner = worlds.OrderBy(w => w.WorldId).SelectMany(w => w.Steps.Select((_, i) => (w.WorldId, i))).ToList();
            var supply = worlds.Sum(w => w.UnitsForSale);
            for (var need = 1; need <= supply + 1; need++)
            {
                var expected = WorldOfferStackTests.FullTableCover(merged, need).Select(k => owner[k]).ToList();
                var c = StackCoverSolver.Cover(worlds, need);
                var got = c.Worlds.SelectMany(b => b.StackIndices.Select(i => (b.WorldId, i))).ToList();
                Assert.False(c.Approximate);
                Assert.True(expected.SequenceEqual(got), $"set {n}, need {need}");
            }
        }
    }

    [Fact]
    public void CellBound_AtTheBoundIsExact()
    {
        // 25 stacks, totals up to 799,999: 25 * 800,000 = 20,000,000 cell updates, the bound: exact.
        var worlds = new List<WorldOffer> { W(1, S(1, 799_999)) };
        worlds.AddRange(Enumerable.Range(2, 24).Select(i => W((uint)i, S(5, 1))));
        var c = StackCoverSolver.Cover(worlds, 1);
        Assert.False(c.Approximate);
        Assert.Equal("2x1=5[0]", Render(c));
    }

    [Fact]
    public void HugeStackPastTheOldCellBound_IsBeatenByTheOneUnitStack()
    {
        // Superseded 2026-11-24 (was CellBound_OnePastIsGreedyAndFlagged, expecting Approximate true and "1x800000=800000[0]"). Every
        // stack covers a need of 1 alone: the first 5 gil stack, exactly.
        var worlds = new List<WorldOffer> { W(1, S(1, 800_000)) };
        worlds.AddRange(Enumerable.Range(2, 24).Select(i => W((uint)i, S(5, 1))));
        var c = StackCoverSolver.Cover(worlds, 1);
        Assert.False(c.Approximate);
        Assert.Equal("2x1=5[0]", Render(c));
    }
}
