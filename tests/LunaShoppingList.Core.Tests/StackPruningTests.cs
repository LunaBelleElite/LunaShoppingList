using LunaShoppingList.Core;
using Xunit;

namespace LunaShoppingList.Core.Tests;

/// <summary>
/// The same-quantity prune of <see cref="StackKnapsack.Prune"/> (2026-11-24): of the stacks of one quantity q only the best
/// floor(cap / q) can be in a cover within the cap. The reference path is the unpruned one: <see cref="StackKnapsack.Cover"/> given
/// a table built over every stack, which is how the old code (and <see cref="WorldOffer"/> within its limit) answered.
/// </summary>
public class StackPruningTests
{
    private static readonly long T0 = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero).UtcTicks;

    /// <summary>Cover with no prune at all: a table over every stack, as the shared table of a world does.</summary>
    private static (int[] Indices, bool Approximate) Unpruned(IReadOnlyList<StackKnapsack.Stack> stacks, long need)
    {
        if (need <= 0 || stacks.Sum(s => (long)s.Quantity) <= need)
            return StackKnapsack.Cover(stacks, need);
        var size = StackKnapsack.ExactSize(stacks, need);
        if (!size.Table)
            return StackKnapsack.Cover(stacks, need);
        return StackKnapsack.Cover(stacks, need, StackKnapsack.Table.Build(stacks, (int)size.Cap));
    }

    private static List<StackKnapsack.Stack> RandomBook(Random rng, int maxStacks, int[] quantities, int prices, int reviews)
    {
        var n = rng.Next(2, maxStacks + 1);
        var list = new List<StackKnapsack.Stack>();
        for (var i = 0; i < n; i++)
            list.Add(new StackKnapsack.Stack(quantities[rng.Next(quantities.Length)], rng.Next(1, prices + 1), T0 - rng.Next(reviews) * TimeSpan.TicksPerHour));
        return list;
    }

    [Fact]
    public void PrunedCoverEqualsTheUnprunedCoverIndexForIndexOnManySeededBooksWithTies()
    {
        var rng = new Random(20261124);
        var different = 0;
        for (var round = 0; round < 6000; round++)
        {
            var quantities = round % 2 == 0 ? new[] { 1, 2, 3, 5 } : new[] { 3, 10, 10, 10, 99 };
            var book = RandomBook(rng, 40, quantities, prices: round % 3 == 0 ? 2 : 3, reviews: round % 4 == 0 ? 6 : 2);
            var supply = book.Sum(s => (long)s.Quantity);
            var need = rng.Next(1, (int)Math.Min(supply + 2, 400));
            var expected = Unpruned(book, need);
            var actual = StackKnapsack.Cover(book, need);
            Assert.False(expected.Approximate);
            Assert.False(actual.Approximate);
            Assert.True(expected.Indices.SequenceEqual(actual.Indices),
                $"round {round}, need {need}, book {string.Join(" ", book.Select((b, k) => $"{k}:{b.Quantity}x{b.UnitPrice}r{(T0 - b.ReviewTicks) / TimeSpan.TicksPerHour}"))}: expected [{string.Join(",", expected.Indices)}] got [{string.Join(",", actual.Indices)}]");
            if (StackKnapsack.ExactSize(book, need) is { Table: true } size && size.Count < book.Count(s => s.Quantity <= size.Cap))
                different++;
        }
        // The books must actually drop stacks, or the equality above proves nothing.
        Assert.True(different > 500, $"only {different} books dropped a stack");
    }

    [Fact]
    public void PrunedCoverIsTheCheapestAndTheFewestUnitsAndTheNewestOldestReviewOnSmallBooks()
    {
        var rng = new Random(77);
        var t0 = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
        for (var round = 0; round < 1500; round++)
        {
            var book = RandomBook(rng, 12, new[] { 1, 2, 2, 3, 4 }, prices: 4, reviews: 3);
            var steps = book.Select(s => new PriceStep(s.UnitPrice, s.Quantity, false, new DateTimeOffset(s.ReviewTicks, TimeSpan.Zero))).ToList();
            var supply = steps.Sum(s => (long)s.Quantity);
            var need = rng.Next(1, (int)supply + 1);
            var best = StackBruteForce.Cover(StackBruteForce.All(steps), need);
            var (indices, approximate) = StackKnapsack.Cover(book, need);
            Assert.False(approximate);
            Assert.Equal(best.Cost, indices.Sum(i => book[i].Cost));
            Assert.Equal(best.Units, indices.Sum(i => (long)book[i].Quantity));
            Assert.Equal(best.Oldest, indices.Length == 0 ? DateTimeOffset.MaxValue : new DateTimeOffset(indices.Min(i => book[i].ReviewTicks), TimeSpan.Zero));
        }
    }

    [Fact]
    public void ABookThatOverranTheSizeGuardIsNowSolvedExactly()
    {
        // 3,000 stacks of 99, dearest first so the cheapest 101 are the last 101. need 9,999 = 101 whole stacks: cap 10,097.
        var book = new List<StackKnapsack.Stack>();
        for (var i = 0; i < 3000; i++)
            book.Add(new StackKnapsack.Stack(99, 5000 - i, T0));
        var size = StackKnapsack.ExactSize(book, 9999);
        Assert.Equal(10097, size.Cap);
        Assert.False(StackKnapsack.Fits(3000, size.Cap)); // the raw count (30.3M cells) is what used to trip the guard
        Assert.Equal(101, size.Count);
        Assert.True(StackKnapsack.Fits(size.Count, size.Cap));

        var (indices, approximate) = StackKnapsack.Cover(book, 9999);
        Assert.False(approximate);
        Assert.Equal(Enumerable.Range(2899, 101), indices);
    }

    [Fact]
    public void ABookWithDistinctQuantitiesIsUnchanged()
    {
        var book = new List<StackKnapsack.Stack>();
        for (var i = 1; i <= 30; i++)
            book.Add(new StackKnapsack.Stack(i, 10 + i % 4, T0 - i));
        var (keep, count) = StackKnapsack.Prune(book, 60);
        Assert.Equal(30, count);
        Assert.All(keep, Assert.True);
        foreach (var need in new[] { 1, 17, 100, 250, 400 })
            Assert.True(Unpruned(book, need).Indices.SequenceEqual(StackKnapsack.Cover(book, need).Indices), $"need {need}");
    }

    [Fact]
    public void PruneDropsAStackOnlyWhenFlooredCapOverQuantityOthersBeatIt()
    {
        // quantity 10, cap 25: floor(25 / 10) = 2, so a stack needs two beaters to go. A beater is cheaper, or the same price with a
        // review at least as new and an earlier place.
        var book = new List<StackKnapsack.Stack>
        {
            new(10, 5, T0),      // 0: one cheaper one (2): kept
            new(10, 5, T0 - 5),  // 1: beaten by 2 (cheaper) and 0 (same price, newer, earlier): dropped
            new(10, 4, T0 - 9),  // 2: the cheapest, nothing beats it: kept
            new(10, 5, T0),      // 3: full tie with 0, later: beaten by 2 and 0: dropped
            new(10, 9, T0),      // 4: dearer: beaten by 2, 0, 1, 3: dropped
            new(30, 1, T0),      // 5: above the cap: dropped
            new(7, 3, T0),       // 6: alone in its size: kept
            new(10, 5, T0 + 5),  // 7: later but newer than 0: only one cheaper one and no newer earlier one: kept
        };
        var (keep, count) = StackKnapsack.Prune(book, 25);
        Assert.Equal(new[] { true, false, true, false, false, false, true, true }, keep);
        Assert.Equal(4, count);
    }

    [Fact]
    public void AnEarlierOlderStackStaysBeatenOnlyByTheEarlierNewerOnes()
    {
        // The case the first draft of the prune got wrong (seed 20261124, round 67): equal price, the cover's oldest review is fixed
        // by other stacks, so the earliest stacks win whatever their own review. Quantity 99, cap 358: three allowed.
        var book = new List<StackKnapsack.Stack>
        {
            new(99, 1, T0 - 1), // 0: nothing beats it
            new(99, 1, T0),     // 1: kept
            new(99, 1, T0),     // 2: kept
            new(99, 1, T0),     // 3: three beaters needed, only 1 and 2 are newer-or-equal and earlier: kept
            new(99, 1, T0),     // 4: beaten by 1, 2, 3: dropped
        };
        var (keep, count) = StackKnapsack.Prune(book, 358);
        Assert.Equal(new[] { true, true, true, true, false }, keep);
        Assert.Equal(4, count);
    }
}
