namespace LunaShoppingList.Core.Tests;

/// <summary>Whole-stack truth for <see cref="RouteSummarizer"/> (release 0.16.0.0): lines are priced, summed and described by their stacks.</summary>
public class RouteSummarizerWholeStackTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PriceStep Step(int price, int qty, int ageDays = 0) => new(price, qty, false, T0.AddDays(-ageDays));

    private static AllocationLine L(uint item, uint world, int qty) => new(item, world, qty);

    // Alpha (world 10): [0] 20 @ 10 (3 days old), [1] 99 @ 12 (1 day), [2] 5 @ 30 (2 days). Legal totals 5, 20, 25, 99, 104, 119, 124.
    private static readonly WorldOffer Alpha = new(10, "Alpha", new[] { Step(10, 20, 3), Step(12, 99, 1), Step(30, 5, 2) });
    private static readonly WorldOffer Beta = new(11, "Beta", new[] { Step(5, 10) });

    private static OfferBook Book() => new(new[]
    {
        new ItemOffers(1, 100, new[] { Alpha, Beta }),
        new ItemOffers(2, 500, new[] { Alpha }),
        new ItemOffers(3, 7, new[] { Beta }),
        new ItemOffers(4, 2, Array.Empty<WorldOffer>()),
    }, Array.Empty<SkippedItem>());

    private static RouteLine OneLine(int qty, uint item = 1) => RouteSummarizer.Summarize(Book(), new[] { L(item, 10, qty) }).Worlds[0].Lines[0];

    [Theory]
    [InlineData(5, 150, 30, 30, -2)]     // the 5 stack alone: the cheapest price on the world (10) is NOT in it
    [InlineData(20, 200, 10, 10, -3)]
    [InlineData(25, 350, 10, 30, -3)]
    [InlineData(99, 1188, 12, 12, -1)]
    [InlineData(104, 1338, 12, 30, -2)]
    [InlineData(119, 1388, 10, 12, -3)]
    [InlineData(124, 1538, 10, 30, -3)]
    public void LegalTotalIsPricedAndDescribedByItsChosenStacksOnly(int total, long cost, int min, int max, int oldestAgeDays)
    {
        var line = OneLine(total);
        Assert.Equal(total, line.Quantity);
        Assert.Equal(cost, line.Cost);
        Assert.Equal(min, line.MinUnitPrice);
        Assert.Equal(max, line.MaxUnitPrice);
        Assert.Equal(T0.AddDays(oldestAgeDays), line.OldestReview);
        Assert.False(line.Approximate);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(21, 25)]
    [InlineData(100, 104)]
    [InlineData(120, 124)]
    [InlineData(100000, 124)]
    public void AnIllegalQuantityOnALineSnapsUpAndIsPricedAsWhatIsBought(int asked, int bought)
    {
        var line = OneLine(asked);
        Assert.Equal(bought, line.Quantity);
        Assert.Equal(line.Quantity, line.StackSizes.Sum());
    }

    [Fact]
    public void ALineSnapsUpToTheSmallestLegalTotalNotTheCheapestCover()
    {
        // Stacks 6 @ 1 and 4 @ 100: legal totals 4, 6, 10. A request of 3 buys the 4 stack (400 gil), not 6 units for 6 gil.
        var book = new OfferBook(new[] { new ItemOffers(5, 3, new[] { new WorldOffer(30, "Gamma", new[] { Step(1, 6), Step(100, 4) }) }) }, Array.Empty<SkippedItem>());
        var line = RouteSummarizer.Summarize(book, new[] { L(5, 30, 3) }).Worlds[0].Lines[0];
        Assert.Equal(4, line.Quantity);
        Assert.Equal(400, line.Cost);
        Assert.Equal(new[] { 4 }, line.StackSizes);
        Assert.False(line.Approximate);
    }

    [Theory]
    [InlineData(25, new[] { 20, 5 })]
    [InlineData(119, new[] { 20, 99 })]
    [InlineData(124, new[] { 20, 99, 5 })]
    [InlineData(5, new[] { 5 })]
    public void StackSizesAreInAscendingListingOrderAndCountMatches(int total, int[] sizes)
    {
        var line = OneLine(total);
        Assert.Equal(sizes, line.StackSizes);
        Assert.Equal(sizes.Length, line.StackCount);
    }

    [Fact]
    public void StackOvershootIsSurplusOnTheLineAndTheItem()
    {
        var s = RouteSummarizer.Summarize(Book(), new[] { L(1, 10, 104) });   // item 1 needs 100
        var line = s.Worlds[0].Lines[0];
        Assert.Equal(100, line.ItemNeeded);
        Assert.Equal(104, line.ItemBought);
        Assert.Equal(4, line.ItemSurplus);
        Assert.Equal(0, line.ItemUnmet);
        var item = s.Items.Single(i => i.ItemId == 1);
        Assert.Equal(new ItemCoverage(1, 100, 104), item);
        Assert.Equal(4, item.Surplus);
        Assert.Equal(0, item.Unmet);
        Assert.DoesNotContain(s.Unmet, u => u.ItemId == 1);
    }

    [Fact]
    public void ShortSupplyBuysEverythingAndReportsTheRestAsUnmet()
    {
        var s = RouteSummarizer.Summarize(Book(), new[] { L(2, 10, 100000) });   // item 2 needs 500, the world sells 124
        var line = s.Worlds[0].Lines[0];
        Assert.Equal(124, line.Quantity);
        Assert.Equal(3, line.StackCount);
        Assert.Equal(376, line.ItemUnmet);
        Assert.Equal(0, line.ItemSurplus);
        Assert.Contains(new ShortfallItem(2, 376), s.Unmet);
        Assert.Equal(376, s.Items.Single(i => i.ItemId == 2).Unmet);
    }

    [Fact]
    public void ItemBoughtSumsAcrossWorldsOnEveryLineOfTheItem()
    {
        var s = RouteSummarizer.Summarize(Book(), new[] { L(1, 10, 99), L(1, 11, 3) });   // 99 + one stack of 10
        Assert.All(s.Worlds.SelectMany(w => w.Lines), l =>
        {
            Assert.Equal(109, l.ItemBought);
            Assert.Equal(9, l.ItemSurplus);
        });
        Assert.Equal(109, s.Items.Single(i => i.ItemId == 1).Bought);
    }

    [Fact]
    public void ItemsListsEveryBookItemByItemIdIncludingUntouchedOnes()
    {
        var s = RouteSummarizer.Summarize(Book(), new[] { L(3, 11, 7) });
        Assert.Equal(new uint[] { 1, 2, 3, 4 }, s.Items.Select(i => i.ItemId));
        Assert.Equal(new[] { 0, 0, 10, 0 }, s.Items.Select(i => i.Bought));
        Assert.Equal(new[] { 100, 500, 0, 2 }, s.Items.Select(i => i.Unmet));
        Assert.Equal(new[] { 0, 0, 3, 0 }, s.Items.Select(i => i.Surplus));
    }

    [Fact]
    public void EmptyRouteStillListsItemsAsFullyUnmet()
    {
        var s = RouteSummarizer.Summarize(Book(), Array.Empty<AllocationLine>());
        Assert.Equal(4, s.Items.Count);
        Assert.All(s.Items, i => Assert.Equal(i.Needed, i.Unmet));
    }

    // ---- The fallback beyond the exact limit (LinePricer) ----

    private static readonly WorldOffer Huge = new(20, "Huge", new[] { Step(5, 700_000, 1), Step(6, 500_000, 2), Step(7, 300_000, 3) });

    private static OfferBook HugeBook() => new(new[] { new ItemOffers(7, 1_100_000, new[] { Huge }) }, Array.Empty<SkippedItem>());

    [Fact]
    public void BeyondTheLimitTheFullSupplyIsExactAndNotFlagged()
    {
        var line = RouteSummarizer.Summarize(HugeBook(), new[] { L(7, 20, int.MaxValue) }).Worlds[0].Lines[0];
        Assert.Equal(1_500_000, line.Quantity);
        Assert.Equal(700_000L * 5 + 500_000L * 6 + 300_000L * 7, line.Cost);
        Assert.False(line.Approximate);
        Assert.Equal(new[] { 700_000, 500_000, 300_000 }, line.StackSizes);
    }

    [Fact]
    public void BeyondTheLimitAPartialQuantityIsCoveredByWholeStacksAndFlaggedApproximate()
    {
        var s = RouteSummarizer.Summarize(HugeBook(), new[] { L(7, 20, 1_100_000) });
        var line = s.Worlds[0].Lines[0];
        Assert.True(line.Approximate);
        Assert.InRange(line.Quantity, 1_100_000, 1_500_000);
        Assert.Equal(line.Quantity, line.StackSizes.Sum());
        // The cost is the sum of the chosen stacks, each at its own price.
        long expected = 0;
        foreach (var size in line.StackSizes)
            expected += size == 700_000 ? size * 5L : size == 500_000 ? size * 6L : size * 7L;
        Assert.Equal(expected, line.Cost);
        Assert.Equal(expected, s.GrandTotal);
    }

    // ---- Randomized comparison with an independent brute force ----

    [Fact]
    public void RandomRoutesMatchABruteForceRecomputationFromTheChosenStacks()
    {
        var rng = new Random(20261124);
        for (var round = 0; round < 300; round++)
        {
            var itemCount = rng.Next(1, 4);
            var worldCount = rng.Next(1, 4);
            var worldsById = new Dictionary<uint, WorldOffer>();
            for (uint w = 1; w <= worldCount; w++)
            {
                var steps = Enumerable.Range(0, rng.Next(1, 7)).Select(_ => StackBruteForce.RandomStep(rng, T0))
                    .OrderBy(st => st.UnitPrice).ThenByDescending(st => st.LastReview).ToArray();
                worldsById[w] = new WorldOffer(w, "W" + w, steps);
            }

            var items = new List<ItemOffers>();
            for (uint i = 1; i <= itemCount; i++)
            {
                var offered = worldsById.Values.Where(_ => rng.Next(4) != 0).ToArray();
                items.Add(new ItemOffers(i, rng.Next(1, 60), offered));
            }
            var book = new OfferBook(items, Array.Empty<SkippedItem>());

            var lines = new List<AllocationLine>();
            for (var n = rng.Next(0, 9); n > 0; n--)
                lines.Add(new AllocationLine((uint)rng.Next(1, itemCount + 2), (uint)rng.Next(1, worldCount + 2), rng.Next(-2, 160)));

            var summary = RouteSummarizer.Summarize(book, lines);

            // Brute force: sum duplicates, cap at supply, exact subset or the smallest legal total above.
            var wanted = new Dictionary<(uint, uint), long>();
            foreach (var line in lines)
            {
                var item = items.FirstOrDefault(it => it.ItemId == line.ItemId);
                if (line.Quantity <= 0 || item is null || item.Worlds.All(w => w.WorldId != line.WorldId))
                    continue;
                wanted[(line.ItemId, line.WorldId)] = wanted.GetValueOrDefault((line.ItemId, line.WorldId)) + line.Quantity;
            }

            long grand = 0;
            var boughtPerItem = new Dictionary<uint, long>();
            var worldTotals = new Dictionary<uint, long>();
            foreach (var ((itemId, worldId), want) in wanted)
            {
                var steps = items.First(it => it.ItemId == itemId).Worlds.First(w => w.WorldId == worldId).Steps;
                var all = StackBruteForce.All(steps);
                var supply = steps.Sum(s => (long)s.Quantity);
                var q = Math.Min(want, supply);
                var best = all.Where(s => s.Units >= q && StackBruteForce.Exact(all, s.Units) is not null)
                    .OrderBy(s => s.Units).First().Units;
                var subset = StackBruteForce.Exact(all, best)!.Value;

                var actual = summary.Worlds.Single(w => w.WorldId == worldId).Lines.Single(l => l.ItemId == itemId);
                Assert.Equal(best, actual.Quantity);
                Assert.Equal(subset.Cost, actual.Cost);
                Assert.Equal(best, actual.StackSizes.Sum());
                Assert.False(actual.Approximate);
                Assert.All(actual.StackSizes, size => Assert.Contains(steps, st => st.Quantity == size));
                Assert.Equal(subset.Oldest, actual.OldestReview);

                grand += subset.Cost;
                boughtPerItem[itemId] = boughtPerItem.GetValueOrDefault(itemId) + best;
                worldTotals[worldId] = worldTotals.GetValueOrDefault(worldId) + subset.Cost;
            }

            Assert.Equal(grand, summary.GrandTotal);
            Assert.Equal(worldTotals.Count, summary.WorldCount);
            foreach (var w in summary.Worlds)
                Assert.Equal(worldTotals[w.WorldId], w.Total);
            foreach (var item in items)
            {
                var bought = boughtPerItem.GetValueOrDefault(item.ItemId);
                var coverage = summary.Items.Single(i => i.ItemId == item.ItemId);
                Assert.Equal(bought, coverage.Bought);
                Assert.Equal(Math.Max(0, item.Needed - bought), coverage.Unmet);
                Assert.Equal(Math.Max(0, bought - item.Needed), coverage.Surplus);
                Assert.Equal(coverage.Unmet > 0, summary.Unmet.Any(u => u.ItemId == item.ItemId));
            }
        }
    }
}
