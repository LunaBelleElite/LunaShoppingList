using LunaShoppingList.Core;

namespace LunaShoppingList.Core.Tests;

/// <summary>Whole-stack members of <see cref="WorldOffer"/>: a listing is bought as a whole stack or not at all.</summary>
public class WorldOfferStackTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static PriceStep S(int price, int qty, int ageDays = 0) => new(price, qty, false, T0.AddDays(-ageDays));

    private static WorldOffer W(params PriceStep[] steps) => new(1, "Alpha", steps);

    // 3 @ 10 (30), 2 @ 25 (50), 4 @ 40 (160): legal totals 2,3,4,5,6,7,9
    private static WorldOffer Ladder() => W(S(10, 3, 1), S(25, 2, 5), S(40, 4, 2));

    private static long SumUnits(WorldOffer w, IEnumerable<int> idx) => idx.Sum(i => (long)w.Steps[i].Quantity);

    private static long SumCost(WorldOffer w, IEnumerable<int> idx) => idx.Sum(i => (long)w.Steps[i].Quantity * w.Steps[i].UnitPrice);

    // ---- literal examples ----

    [Fact]
    public void Ladder_LegalTotalsAreTheSubsetSums()
        => Assert.Equal(new[] { 2, 3, 4, 5, 6, 7, 9 }, Ladder().LegalTotals(100));

    [Fact]
    public void LegalTotals_StopAtTheCap()
        => Assert.Equal(new[] { 2, 3, 4, 5 }, Ladder().LegalTotals(5));

    [Theory]
    [InlineData(0, 0L)]
    [InlineData(2, 50L)]
    [InlineData(3, 30L)]
    [InlineData(4, 160L)]
    [InlineData(5, 80L)]
    [InlineData(6, 210L)]
    [InlineData(7, 190L)]
    [InlineData(9, 240L)]
    public void ExactCost_LegalTotals(int total, long cost) => Assert.Equal(cost, Ladder().ExactCost(total));

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(-1)]
    public void ExactCost_IllegalTotalIsNull(int total) => Assert.Null(Ladder().ExactCost(total));

    [Theory]
    [InlineData(1, 3, 30L, new[] { 0 })]
    [InlineData(3, 3, 30L, new[] { 0 })]
    [InlineData(4, 5, 80L, new[] { 0, 1 })]
    [InlineData(6, 7, 190L, new[] { 0, 2 })]
    [InlineData(8, 9, 240L, new[] { 0, 1, 2 })]
    public void Cover_Ladder(int need, int units, long cost, int[] stacks)
    {
        var c = Ladder().Cover(need);
        Assert.Equal(units, c.Units);
        Assert.Equal(cost, c.Cost);
        Assert.Equal(stacks, c.StackIndices);
        Assert.False(c.Approximate);
    }

    [Fact]
    public void Cover_SurplusWholeStackCanBeCheapest()
    {
        // 5 @ 1 (5 gil) against 3 @ 10 (30 gil): for a need of 3 the five-stack is cheaper, two units of surplus.
        var w = W(S(1, 5), S(10, 3));
        var c = w.Cover(3);
        Assert.Equal(5, c.Units);
        Assert.Equal(5L, c.Cost);
        Assert.Equal(new[] { 0 }, c.StackIndices);
        Assert.Equal(1, c.MinUnitPrice);
        Assert.Equal(1, c.MaxUnitPrice);
    }

    [Fact]
    public void Cover_ReportsPriceRangeAndOldestReviewOfChosenStacksOnly()
    {
        // need 6 on the ladder: 3 @ 10 (1 day) + 4 @ 40 (2 days) = 190; the 2 @ 25 (5 days old) is not used.
        var c = Ladder().Cover(6);
        Assert.Equal(10, c.MinUnitPrice);
        Assert.Equal(40, c.MaxUnitPrice);
        Assert.Equal(T0.AddDays(-2), c.OldestReview);
    }

    [Fact]
    public void Cover_ShortWorldBuysEverything()
    {
        var c = Ladder().Cover(100);
        Assert.Equal(9, c.Units);
        Assert.Equal(240L, c.Cost);
        Assert.Equal(new[] { 0, 1, 2 }, c.StackIndices);
        Assert.Equal(T0.AddDays(-5), c.OldestReview);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(int.MinValue)]
    public void Cover_NothingNeededBuysNothing(int need)
    {
        var c = Ladder().Cover(need);
        Assert.Equal(0, c.Units);
        Assert.Equal(0L, c.Cost);
        Assert.Empty(c.StackIndices);
        Assert.Equal(0, c.MinUnitPrice);
        Assert.Equal(0, c.MaxUnitPrice);
        Assert.Equal(DateTimeOffset.MaxValue, c.OldestReview);
    }

    [Fact]
    public void Cover_TieOnCost_FewerUnitsWins()
    {
        // 3 @ 2 (6 gil) and 2 @ 3 (6 gil): a need of 2 costs 6 either way; the two-stack leaves no surplus.
        var c = W(S(2, 3), S(3, 2)).Cover(2);
        Assert.Equal(2, c.Units);
        Assert.Equal(new[] { 1 }, c.StackIndices);
    }

    [Fact]
    public void Cover_TieOnCostAndUnits_NewerReviewWins_EvenWhenListedLater()
    {
        var c = W(S(5, 2, 5), S(5, 2, 1)).Cover(2);
        Assert.Equal(new[] { 1 }, c.StackIndices);
        Assert.Equal(T0.AddDays(-1), c.OldestReview);
    }

    [Fact]
    public void Stacks_TieOnCost_NewerOldestReviewWins()
    {
        // total 4: {2 @ 5 old, 2 @ 5 new} is the only way; total 2: old or new stack, same cost: the newer one.
        var w = W(S(5, 2, 5), S(5, 2, 1), S(9, 4, 0));
        Assert.Equal(new[] { 1 }, w.Stacks(2));
        Assert.Equal(new[] { 0, 1 }, w.Stacks(4));
        Assert.Equal(20L, w.ExactCost(4));
    }

    [Fact]
    public void Stacks_FullTie_EarlierStackWins()
        => Assert.Equal(new[] { 0 }, W(S(5, 2, 1), S(5, 2, 1)).Stacks(2));

    [Fact]
    public void Stacks_IllegalIsNull_ZeroIsEmpty()
    {
        Assert.Null(Ladder().Stacks(8));
        Assert.Empty(Ladder().Stacks(0)!);
    }

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(8, 9)]
    [InlineData(9, 9)]
    [InlineData(50, 9)]
    public void SnapUp_SmallestLegalTotalAtOrAbove_ElseMax(int n, int expected) => Assert.Equal(expected, Ladder().SnapUp(n));

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 2)]
    [InlineData(8, 7)]
    [InlineData(9, 9)]
    [InlineData(50, 9)]
    public void SnapDown_LargestLegalTotalAtOrBelow(int n, int expected) => Assert.Equal(expected, Ladder().SnapDown(n));

    [Fact]
    public void EmptyLadder_EverythingIsZeroOrNothing()
    {
        var w = W();
        Assert.Equal(0, w.ExactTotalsLimit);
        Assert.Empty(w.LegalTotals(10));
        Assert.Equal(0L, w.ExactCost(0));
        Assert.Null(w.ExactCost(1));
        Assert.Empty(w.Stacks(0)!);
        Assert.Equal(0, w.Cover(3).Units);
        Assert.Equal(0, w.SnapUp(3));
        Assert.Equal(0, w.SnapDown(3));
    }

    // ---- oracle: brute force over every subset ----

    [Fact]
    public void ExactCostAndStacks_MatchBruteForce_On300RandomLadders()
    {
        var rng = new Random(20261124);
        for (var n = 0; n < 300; n++)
        {
            var steps = Enumerable.Range(0, rng.Next(0, 9)).Select(_ => StackBruteForce.RandomStep(rng, T0)).ToList();
            var w = new WorldOffer(7, "X", steps);
            var all = StackBruteForce.All(steps);
            var supply = steps.Sum(s => s.Quantity);
            var legal = all.Where(s => s.Units > 0).Select(s => (int)s.Units).Distinct().OrderBy(t => t).ToList();
            Assert.Equal(legal, w.LegalTotals(supply + 5));
            for (var t = 0; t <= supply + 3; t++)
            {
                var best = StackBruteForce.Exact(all, t);
                Assert.Equal(best?.Cost, w.ExactCost(t));
                var stacks = w.Stacks(t);
                if (best is null)
                {
                    Assert.Null(stacks);
                    continue;
                }
                Assert.NotNull(stacks);
                Assert.Equal(stacks!.Distinct().OrderBy(i => i), stacks);
                Assert.Equal(t, SumUnits(w, stacks));
                Assert.Equal(best.Value.Cost, SumCost(w, stacks));
                var oldest = stacks.Count == 0 ? DateTimeOffset.MaxValue : stacks.Min(i => steps[i].LastReview);
                Assert.Equal(best.Value.Oldest, oldest);
            }
        }
    }

    [Fact]
    public void Cover_MatchesBruteForce_On300RandomLadders()
    {
        var rng = new Random(77001);
        for (var n = 0; n < 300; n++)
        {
            // Every other ladder uses only prices 1 to 3, so covers often tie on cost and units and the review tie-break is exercised.
            var steps = Enumerable.Range(0, rng.Next(0, 9))
                .Select(_ => StackBruteForce.RandomStep(rng, T0, maxPrice: n % 2 == 0 ? 3 : 40)).ToList();
            var w = new WorldOffer(7, "X", steps);
            var all = StackBruteForce.All(steps);
            var supply = steps.Sum(s => s.Quantity);
            for (var need = 1; need <= supply + 2; need++)
            {
                var best = StackBruteForce.Cover(all, need);
                var c = w.Cover(need);
                Assert.False(c.Approximate);
                Assert.Equal(best.Cost, c.Cost);
                Assert.Equal(best.Units, c.Units);
                Assert.Equal(best.Oldest, c.OldestReview);
                Assert.Equal(c.Units, SumUnits(w, c.StackIndices));
                Assert.Equal(c.Cost, SumCost(w, c.StackIndices));
                Assert.Equal(c.StackIndices.Count == 0 ? 0 : c.StackIndices.Min(i => steps[i].UnitPrice), c.MinUnitPrice);
                Assert.Equal(c.StackIndices.Count == 0 ? 0 : c.StackIndices.Max(i => steps[i].UnitPrice), c.MaxUnitPrice);
                if (need <= supply)
                    Assert.True(c.Units >= need);
                else
                    Assert.Equal(supply, c.Units);
            }
        }
    }

    /// <summary>
    /// The cover as the full table answered it before 2026-11-24's dominance change: every total from the need up to
    /// <c>need + largest stack - 1</c>, cheapest first, lower total on a tie, with the stacks the full table names for that total
    /// (<see cref="WorldOffer.Stacks"/>, which still reads the full table). Only valid while that range is inside the exact limit.
    /// </summary>
    internal static IReadOnlyList<int> FullTableCover(WorldOffer w, int need)
    {
        var supply = w.Steps.Sum(s => (long)s.Quantity);
        if (need <= 0)
            return Array.Empty<int>();
        if (supply <= need)
            return Enumerable.Range(0, w.Steps.Count).ToArray();
        var cap = Math.Min(supply, (long)need + w.Steps.Max(s => s.Quantity) - 1);
        Assert.True(cap <= w.ExactTotalsLimit, "reference only valid inside the exact limit");
        long best = -1, bestCost = -1;
        for (var t = need; t <= cap; t++)
            if (w.ExactCost(t) is { } c && (best < 0 || c < bestCost))
            {
                best = t;
                bestCost = c;
            }
        return w.Stacks((int)best)!;
    }

    [Fact]
    public void Cover_PicksTheSameStacksAsTheFullTable_IndexForIndex_On600RandomLadders()
    {
        // 2026-11-24: the cover now solves "one stack that covers the need alone" apart from the table, which only reaches
        // need + (largest stack below the need) - 1. The chosen stacks must be exactly those the full table chose, ties included:
        // prices 1 to 3 and reviews 0 to 3 days old make cost, units and review ties common.
        var rng = new Random(5150);
        for (var n = 0; n < 600; n++)
        {
            var steps = Enumerable.Range(0, rng.Next(1, 10))
                .Select(_ => StackBruteForce.RandomStep(rng, T0, maxQty: n % 3 == 0 ? 4 : 12, maxPrice: n % 2 == 0 ? 3 : 40)).ToList();
            var supply = steps.Sum(s => s.Quantity);
            for (var need = 1; need <= supply + 1; need++)
            {
                var reference = FullTableCover(new WorldOffer(7, "X", steps), need);
                var c = new WorldOffer(7, "X", steps).Cover(need);
                Assert.False(c.Approximate);
                Assert.True(reference.SequenceEqual(c.StackIndices),
                    $"ladder {n}, need {need}: table [{string.Join(",", reference)}] vs cover [{string.Join(",", c.StackIndices)}]");
            }
        }
    }

    // ---- one stack that covers the need alone (2026-11-24) ----

    [Fact]
    public void Cover_OneHugeCheapUnitStack_LosesToTheCheapestStackThatCoversAlone()
    {
        // 2,000 listings: one 9,999 crystal stack at 2 and 1,999 stacks of 50 at 5. Need 50. Covering it exactly used to need totals up
        // to 50 + 9,999 - 1 over 2,000 stacks (20,098,000 cell updates, past the guard), and the greedy fallback paid 19,998 for the
        // big stack. Any one 50 stack covers alone for 250.
        var steps = new List<PriceStep> { S(2, 9_999) };
        steps.AddRange(Enumerable.Range(0, 1_999).Select(_ => S(5, 50)));
        var c = new WorldOffer(1, "Alpha", steps).Cover(50);
        Assert.False(c.Approximate);
        Assert.Equal(50, c.Units);
        Assert.Equal(250L, c.Cost);
        Assert.Equal(new[] { 1 }, c.StackIndices);
    }

    [Fact]
    public void Cover_OneHugeCheapUnitStack_LosesToSmallStacksTogether()
    {
        // As above with 1,999 stacks of 30 at 5: the table only has to reach 50 + 30 - 1 = 79, and two 30 stacks (300) beat the big
        // stack (19,998).
        var steps = new List<PriceStep> { S(2, 9_999) };
        steps.AddRange(Enumerable.Range(0, 1_999).Select(_ => S(5, 30)));
        var c = new WorldOffer(1, "Alpha", steps).Cover(50);
        Assert.False(c.Approximate);
        Assert.Equal(60, c.Units);
        Assert.Equal(300L, c.Cost);
        Assert.Equal(new[] { 1, 2 }, c.StackIndices);
    }

    [Fact]
    public void Snaps_MatchBruteForce_On200RandomLadders()
    {
        var rng = new Random(4242);
        for (var n = 0; n < 200; n++)
        {
            var steps = Enumerable.Range(0, rng.Next(0, 8)).Select(_ => StackBruteForce.RandomStep(rng, T0)).ToList();
            var w = new WorldOffer(7, "X", steps);
            var sums = StackBruteForce.All(steps).Select(s => (int)s.Units).Distinct().OrderBy(t => t).ToList();
            var supply = steps.Sum(s => s.Quantity);
            for (var q = -1; q <= supply + 2; q++)
            {
                var up = sums.Where(t => t >= q).DefaultIfEmpty(supply).Min();
                var down = sums.Where(t => t <= q).DefaultIfEmpty(0).Max();
                Assert.Equal(up, w.SnapUp(q));
                Assert.Equal(down, w.SnapDown(q));
            }
        }
    }

    // ---- size guard ----

    [Fact]
    public void ExactTotalsLimit_IsSupplyWhenSmall_ElseTheDocumentedBounds()
    {
        Assert.Equal(9, Ladder().ExactTotalsLimit);
        // one stack: the dense-table bound (1,000,000 totals, so 999,999)
        Assert.Equal(999_999, W(S(1, 2_000_000)).ExactTotalsLimit);
        // forty stacks: the cell bound, 20,000,000 / 40 = 500,000 totals, so 499,999
        Assert.Equal(499_999, new WorldOffer(1, "A", Enumerable.Range(0, 40).Select(_ => S(1, 50_000)).ToList()).ExactTotalsLimit);
        Assert.Equal(20_000_000L, StackCoverSolver.MaxCellUpdates);
        Assert.Equal(1_000_000, StackCoverSolver.MaxDenseTotals);
    }

    [Fact]
    public void Cover_AtTheBoundIsStillExact()
    {
        // supply 1,000,000; need 1 needs totals up to 999,999 = the limit: exact, so the 1-unit stack at 5 gil wins.
        var c = W(S(1, 999_999), S(5, 1)).Cover(1);
        Assert.False(c.Approximate);
        Assert.Equal(1, c.Units);
        Assert.Equal(5L, c.Cost);
    }

    [Fact]
    public void Cover_HugeStackPastTheOldBound_IsBeatenByTheOneUnitStack()
    {
        // Superseded 2026-11-24 (was Cover_PastTheBoundIsGreedyAndFlagged, expecting Approximate true, 1,000,000 units for
        // 1,000,000 gil, stacks [0]: a pathological answer). Both stacks cover a need of 1 alone, so no table is needed and the
        // 1-unit stack at 5 gil wins exactly.
        var c = W(S(1, 1_000_000), S(5, 1)).Cover(1);
        Assert.False(c.Approximate);
        Assert.Equal(1, c.Units);
        Assert.Equal(5L, c.Cost);
        Assert.Equal(new[] { 1 }, c.StackIndices);
    }

    [Fact]
    public void Cover_OneStackCoveringALargeNeedAlone_IsExact()
    {
        // Superseded 2026-11-24 (was Cover_GreedyTrimsStacksThatAreNoLongerNeeded, expecting Approximate true): the 1,000,000 stack
        // covers alone and the 100 stack cannot reach the need, so the answer is exact. Same stacks and cost as before.
        var c = W(S(1, 100), S(2, 1_000_000)).Cover(1_000_000);
        Assert.False(c.Approximate);
        Assert.Equal(1_000_000, c.Units);
        Assert.Equal(2_000_000L, c.Cost);
        Assert.Equal(new[] { 1 }, c.StackIndices);
    }

    // Small stacks only (each below the need), so the table has to reach need + largest - 1.
    // 500,000 + 500,000 - 1 = 999,999: 1,000,000 totals, the dense bound.

    [Fact]
    public void Cover_DenseBound_AtTheBoundIsExact()
    {
        // need 500,001 from 499,999 @ 1 twice and 2 @ 5: totals up to 500,001 + 499,999 - 1 = 999,999. Exact: 499,999 + 2 = 500,009.
        var c = W(S(1, 499_999), S(1, 499_999), S(5, 2)).Cover(500_001);
        Assert.False(c.Approximate);
        Assert.Equal(500_001, c.Units);
        Assert.Equal(500_009L, c.Cost);
        Assert.Equal(new[] { 0, 2 }, c.StackIndices);
    }

    [Fact]
    public void Cover_DenseBound_OnePastIsGreedyAndFlagged()
    {
        // need 500,001 from 500,000 @ 1 twice and 2 @ 5: totals up to 1,000,000, one past. Greedy takes both big stacks (1,000,000
        // gil); the exact answer would be 500,010.
        var c = W(S(1, 500_000), S(1, 500_000), S(5, 2)).Cover(500_001);
        Assert.True(c.Approximate);
        Assert.Equal(1_000_000, c.Units);
        Assert.Equal(1_000_000L, c.Cost);
        Assert.Equal(new[] { 0, 1 }, c.StackIndices);
    }

    [Fact]
    public void Cover_PastTheGuard_GreedyLosesToACheaperStackThatCoversAlone()
    {
        // As above plus one 500,001 stack at 3 (1,500,003): greedy (cheapest unit first) still pays 2,000,000 for the two 500,000
        // stacks at 2, so the single covering stack is taken. Still flagged: the fallback ran.
        var c = W(S(2, 500_000), S(2, 500_000), S(3, 500_001), S(5, 2)).Cover(500_001);
        Assert.True(c.Approximate);
        Assert.Equal(500_001, c.Units);
        Assert.Equal(1_500_003L, c.Cost);
        Assert.Equal(new[] { 2 }, c.StackIndices);
    }

    [Fact]
    public void Cover_GreedyTrimsStacksThatAreNoLongerNeeded()
    {
        // Past the guard with stacks below the need: greedy takes 100 @ 1, then both 600,000 @ 2; the 100 stack is then redundant for
        // a need of 1,000,000 and is dropped.
        var c = W(S(1, 100), S(2, 600_000), S(2, 600_000)).Cover(1_000_000);
        Assert.True(c.Approximate);
        Assert.Equal(1_200_000, c.Units);
        Assert.Equal(2_400_000L, c.Cost);
        Assert.Equal(new[] { 1, 2 }, c.StackIndices);
    }

    [Fact]
    public void PastTheBound_FullSupplyIsStillKnownAndSnapUpUsesTheCover()
    {
        var w = W(S(1, 1_000_000), S(5, 1));
        Assert.Equal(1_000_005L, w.ExactCost(1_000_001));
        Assert.Equal(new[] { 0, 1 }, w.Stacks(1_000_001));
        Assert.Equal(1, w.SnapUp(1));                  // inside the limit: exact
        Assert.Equal(1_000_001, w.SnapUp(1_000_001));  // the full supply
        Assert.Equal(1_000_001, w.SnapDown(2_000_000));
    }

    // ---- cache ----

    [Fact]
    public void Equality_IgnoresTheStackTable()
    {
        IReadOnlyList<PriceStep> steps = [S(10, 3), S(25, 2)];
        var read = new WorldOffer(1, "Alpha", steps);
        var fresh = new WorldOffer(1, "Alpha", steps);
        _ = read.Cover(4);
        _ = read.ExactCost(5);
        Assert.Equal(fresh, read);
        Assert.Equal(fresh.GetHashCode(), read.GetHashCode());
    }

    [Fact]
    public void WithReplacedSteps_RecomputesTheStackTable()
    {
        var original = Ladder();
        Assert.Equal(80L, original.Cover(4).Cost);
        var copy = original with { Steps = [S(5, 2), S(6, 2)] };
        Assert.Equal(22L, copy.Cover(4).Cost);
        Assert.Equal(new[] { 2, 4 }, copy.LegalTotals(10));
        Assert.Equal(80L, original.Cover(4).Cost);
    }

    [Fact]
    public void Answers_DoNotDependOnWhatWasAskedFirst()
    {
        var steps = Enumerable.Range(1, 12).Select(i => S(i * 7 % 13 + 1, i * 5 % 9 + 1, i % 3)).ToList();
        var a = new WorldOffer(1, "A", steps);
        var b = new WorldOffer(1, "A", steps);
        _ = b.ExactCost(3);           // b's table first built small, then grown
        _ = b.LegalTotals(1000);
        for (var t = 0; t < 70; t++)
        {
            Assert.Equal(a.ExactCost(t), b.ExactCost(t));
            Assert.Equal(a.Cover(t), b.Cover(t), new CoverComparer());
        }
    }

    [Fact]
    public void ConcurrentReaders_AllSeeTheSameAnswers()
    {
        var steps = Enumerable.Range(1, 10).Select(i => S(i % 4 + 1, i % 5 + 1, i % 2)).ToList();
        var expected = Enumerable.Range(0, 40).Select(t => new WorldOffer(1, "A", steps).ExactCost(t)).ToArray();
        for (var round = 0; round < 20; round++)
        {
            var shared = new WorldOffer(1, "A", steps);
            var got = new long?[40];
            Parallel.For(0, 40, t => got[t] = shared.ExactCost(t));
            Assert.Equal(expected, got);
        }
    }

    private sealed class CoverComparer : IEqualityComparer<StackCover>
    {
        public bool Equals(StackCover? x, StackCover? y)
            => x is not null && y is not null && x.Units == y.Units && x.Cost == y.Cost && x.StackIndices.SequenceEqual(y.StackIndices)
               && x.OldestReview == y.OldestReview && x.Approximate == y.Approximate;

        public int GetHashCode(StackCover obj) => obj.Units;
    }
}
