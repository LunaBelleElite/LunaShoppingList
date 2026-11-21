using LunaShoppingList.Core;

namespace LunaShoppingList.Core.Tests;

public class OfferBookBuilderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static Listing L(uint item, uint world, string name, int price, int qty = 1, bool hq = false, int ageDays = 0)
        => new(item, world, name, price, qty, hq, T0.AddDays(-ageDays));

    private static IReadOnlySet<uint> Allow(params uint[] ids) => ids.ToHashSet();

    private static OfferBook Build(Requirement[] r, ItemListings[] l, IReadOnlySet<uint>? allowed = null, ExclusionState? ex = null)
        => OfferBookBuilder.Build(r, l, allowed ?? Allow(1, 2, 3, 4), ex ?? new ExclusionState());

    private static uint[] WorldIds(ItemOffers o) => o.Worlds.Select(w => w.WorldId).ToArray();

    [Fact]
    public void RequirementsAreMergedPerItem()
    {
        var book = Build([new(7, 2), new(7, 3), new(5, 1)], []);
        Assert.Equal([5u, 7u], book.Items.Select(i => i.ItemId).ToArray());
        Assert.Equal(5, book.Items[1].Needed);
        Assert.Equal(1, book.Items[0].Needed);
    }

    [Fact]
    public void MergedQuantityClampsToIntMax()
    {
        var book = Build([new(7, int.MaxValue), new(7, 10)], []);
        Assert.Equal(int.MaxValue, book.Items.Single().Needed);
    }

    [Fact]
    public void NonPositiveRequirementsAreIgnored()
    {
        var book = Build([new(1, 0), new(2, -4), new(3, 2), new(3, -1)], []);
        Assert.Equal([3u], book.Items.Select(i => i.ItemId).ToArray());
        Assert.Equal(2, book.Items[0].Needed);
        Assert.Empty(book.Skipped);
    }

    [Fact]
    public void ExcludedItemsGoToSkippedWithMergedQuantityAndNoOffers()
    {
        var ex = new ExclusionState();
        ex.SetExcluded(9, true);
        ex.SetExcluded(4, true);
        var book = Build([new(9, 2), new(4, 1), new(9, 5), new(6, 1)], [new(9, [L(9, 1, "A", 10)])], ex: ex);
        Assert.Equal([new SkippedItem(4, 1), new SkippedItem(9, 7)], book.Skipped);
        Assert.Equal([6u], book.Items.Select(i => i.ItemId).ToArray());
    }

    [Fact]
    public void ExcludedItemWithNonPositiveQuantityIsNotSkipped()
    {
        var ex = new ExclusionState();
        ex.SetExcluded(9, true);
        Assert.Empty(Build([new(9, 0)], [], ex: ex).Skipped);
    }

    [Fact]
    public void ItemWithNoListingStillAppearsWithEmptyWorlds()
    {
        var book = Build([new(1, 3)], []);
        var it = Assert.Single(book.Items);
        Assert.Equal(3, it.Needed);
        Assert.Empty(it.Worlds);
    }

    [Fact]
    public void ListingsFilteredByAllowedWorldPriceAndQuantity()
    {
        var book = Build([new(1, 1)],
            [new(1, [L(1, 1, "A", 10), L(1, 9, "Far", 1), L(1, 2, "B", 0), L(1, 2, "B", -5), L(1, 3, "C", 5, 0), L(1, 3, "C", 5, -2)])]);
        var w = Assert.Single(Assert.Single(book.Items).Worlds);
        Assert.Equal(1u, w.WorldId);
    }

    [Fact]
    public void OnlyListingsWhoseItemIdMatchAreUsed()
    {
        var book = Build([new(1, 1)], [new(1, [L(1, 1, "A", 10), L(2, 1, "A", 1)])]);
        var w = Assert.Single(Assert.Single(book.Items).Worlds);
        Assert.Equal([new PriceStep(10, 1, false, T0)], w.Steps);
    }

    [Fact]
    public void ListingsAreFlattenedAcrossItemListingsEntries()
    {
        var book = Build([new(1, 2)], [new(1, [L(1, 1, "A", 10)]), new(1, [L(1, 1, "A", 20)])]);
        var w = Assert.Single(Assert.Single(book.Items).Worlds);
        Assert.Equal([10, 20], w.Steps.Select(s => s.UnitPrice).ToArray());
    }

    [Fact]
    public void ListingsAreNotMergedEvenWhenIdentical()
    {
        var book = Build([new(1, 2)], [new(1, [L(1, 1, "A", 10, 2), L(1, 1, "A", 10, 2)])]);
        Assert.Equal(2, book.Items[0].Worlds[0].Steps.Count);
    }

    [Fact]
    public void StepsAreOrderedByPriceThenNewerReviewThenNqThenLargerQuantity()
    {
        var book = Build([new(1, 1)],
        [
            new(1,
            [
                L(1, 1, "A", 30, 1),
                L(1, 1, "A", 10, 1, hq: true, ageDays: 3),
                L(1, 1, "A", 10, 1, hq: false, ageDays: 5),
                L(1, 1, "A", 10, 1, hq: false, ageDays: 1),
                L(1, 1, "A", 20, 1, hq: true, ageDays: 0),
                L(1, 1, "A", 20, 1, hq: false, ageDays: 0),
                L(1, 1, "A", 20, 4, hq: false, ageDays: 0),
            ]),
        ]);
        var steps = book.Items[0].Worlds[0].Steps;
        Assert.Equal(
        [
            new PriceStep(10, 1, false, T0.AddDays(-1)),
            new PriceStep(10, 1, true, T0.AddDays(-3)),
            new PriceStep(10, 1, false, T0.AddDays(-5)),
            new PriceStep(20, 4, false, T0),
            new PriceStep(20, 1, false, T0),
            new PriceStep(20, 1, true, T0),
            new PriceStep(30, 1, false, T0),
        ], steps);
    }

    [Fact]
    public void WorldNameIsOrdinallySmallestWhenOneIdCarriesSeveral()
    {
        var book = Build([new(1, 1)], [new(1, [L(1, 1, "beta", 10), L(1, 1, "Zed", 11), L(1, 1, "Alpha", 12)])]);
        Assert.Equal("Alpha", book.Items[0].Worlds[0].WorldName);
    }

    [Fact]
    public void WorldsOrderedByCostForNeededAscending()
    {
        // need 3: world 1 costs 150, world 2 costs 60, world 3 costs 30.
        var book = Build([new(1, 3)],
        [
            new(1,
            [
                L(1, 1, "A", 50, 9),
                L(1, 2, "B", 10), L(1, 2, "B", 20), L(1, 2, "B", 30),
                L(1, 3, "C", 10, 5),
            ]),
        ]);
        Assert.Equal([3u, 2u, 1u], WorldIds(book.Items[0]));
    }

    // Superseded 2026-11-21: was CostTieBrokenByMoreUnitsForSaleFirst, expecting [2,1] (world 2 sells more units). Worlds that
    // can supply everything needed are now tied on cost and then ordered by name and id only; units for sale no longer breaks the tie.
    [Fact]
    public void CostTieAmongFullSupplyWorldsBrokenByNameNotUnitsForSale()
    {
        var book = Build([new(1, 1)], [new(1, [L(1, 1, "A", 10, 1), L(1, 2, "B", 10, 6)])]);
        Assert.Equal([1u, 2u], WorldIds(book.Items[0]));
    }

    [Fact]
    public void UnitsTieBrokenByNameIgnoringCaseThenId()
    {
        var book = Build([new(1, 1)], [new(1, [L(1, 1, "beta", 10), L(1, 3, "ALPHA", 10), L(1, 2, "Alpha", 10)])]);
        Assert.Equal([2u, 3u, 1u], WorldIds(book.Items[0]));
    }

    [Fact]
    public void WorldIdIsFinalTiebreak()
    {
        var book = Build([new(1, 1)], [new(1, [L(1, 4, "Same", 10), L(1, 2, "Same", 10), L(1, 3, "Same", 10)])]);
        Assert.Equal([2u, 3u, 4u], WorldIds(book.Items[0]));
    }

    // Superseded 2026-11-21: was ShortSupplyWorldIsCostedByClamp, expecting [1,2] (the clamped 2-unit world ranked first on
    // cost 2). A world that cannot supply the full quantity now ranks after every world that can.
    [Fact]
    public void ShortSupplyWorldRanksAfterFullSupplyWorld()
    {
        // need 5: world 1 can supply only 2 units (clamped cost 2), world 2 supplies all 5 at cost 50.
        var book = Build([new(1, 5)], [new(1, [L(1, 1, "A", 1, 2), L(1, 2, "B", 10, 5)])]);
        Assert.Equal([2u, 1u], WorldIds(book.Items[0]));
    }

    [Fact]
    public void OneDearUnitDoesNotOutrankAWorldThatSuppliesEverything()
    {
        // need 50: world 1 sells 1 unit at 100, world 2 sells 50 at 10 (cost 500).
        var book = Build([new(1, 50)], [new(1, [L(1, 1, "A", 100, 1), L(1, 2, "B", 10, 50)])]);
        Assert.Equal([2u, 1u], WorldIds(book.Items[0]));
    }

    [Fact]
    public void FullSupplyWorldsAreOrderedByCostForNeeded()
    {
        // need 3: both supply it; world 1 costs 150, world 2 costs 30.
        var book = Build([new(1, 3)], [new(1, [L(1, 1, "A", 50, 3), L(1, 2, "B", 10, 3)])]);
        Assert.Equal([2u, 1u], WorldIds(book.Items[0]));
    }

    [Fact]
    public void ShortWorldsAreOrderedByUnitsForSaleDescending()
    {
        // need 10: nobody can supply it; world 1 sells 3 at 1, world 2 sells 5 at 50, world 3 sells 4 at 5.
        var book = Build([new(1, 10)], [new(1, [L(1, 1, "A", 1, 3), L(1, 2, "B", 50, 5), L(1, 3, "C", 5, 4)])]);
        Assert.Equal([2u, 3u, 1u], WorldIds(book.Items[0]));
    }

    [Fact]
    public void ShortWorldsWithEqualUnitsAreOrderedByAverageUnitPrice()
    {
        // need 10: world 1 sells 4 units averaging 25 (2 at 10, 2 at 40), world 2 sells 4 at 20, world 3 sells 4 averaging 22.5.
        var book = Build([new(1, 10)],
            [new(1, [L(1, 1, "A", 10, 2), L(1, 1, "A", 40, 2), L(1, 2, "B", 20, 4), L(1, 3, "C", 5, 2), L(1, 3, "C", 40, 2)])]);
        Assert.Equal([2u, 3u, 1u], WorldIds(book.Items[0]));
    }

    [Fact]
    public void AveragePriceComparisonDoesNotOverflowOrRound()
    {
        // Both sell 3 units, need 10. World 1 ("Z") totals 3 gil, world 2 ("A") totals 4 gil: integer division would tie them
        // at 1 each and fall to the name, putting "A" first.
        var book = Build([new(1, 10)], [new(1, [L(1, 2, "A", 1, 2), L(1, 2, "A", 2, 1), L(1, 1, "Z", 1, 3)])]);
        Assert.Equal([1u, 2u], WorldIds(book.Items[0]));
        // Huge prices: 3 units at int.MaxValue-ish vs 3 units one gil cheaper must compare in long.
        var big = Build([new(1, 10)], [new(1, [L(1, 1, "A", int.MaxValue, 3), L(1, 2, "B", int.MaxValue - 1, 3)])]);
        Assert.Equal([2u, 1u], WorldIds(big.Items[0]));
    }

    [Fact]
    public void ShortWorldsWithEqualUnitsAndAverageAreOrderedByNameThenId()
    {
        var book = Build([new(1, 10)], [new(1, [L(1, 1, "beta", 10, 2), L(1, 3, "ALPHA", 10, 2), L(1, 2, "Alpha", 10, 2), L(1, 4, "Alpha", 10, 2)])]);
        Assert.Equal([2u, 3u, 4u, 1u], WorldIds(book.Items[0]));
    }

    [Fact]
    public void WorldsAreRankedByCostOfTheNeededQuantity_NotOfEverythingForSale()
    {
        // need 5: world 1 costs 50 for the five cheap units (its other 100 units are dear), world 2 costs 100.
        var book = Build([new(1, 5)], [new(1, [L(1, 1, "A", 10, 5), L(1, 1, "A", 1000, 100), L(1, 2, "B", 20, 5)])]);
        Assert.Equal([1u, 2u], WorldIds(book.Items[0]));
    }

    [Fact]
    public void ResultIsIndependentOfInputOrder()
    {
        var all = new List<Listing>();
        var rng = new Random(12345);
        for (var i = 0; i < 40; i++)
            all.Add(L((uint)(1 + i % 3), (uint)(1 + rng.Next(4)), "W" + rng.Next(4), 1 + rng.Next(5), 1 + rng.Next(3), rng.Next(2) == 0, rng.Next(4)));
        var reqs = new Requirement[] { new(3, 2), new(1, 4), new(2, 1), new(2, 6) };

        var expected = Build(reqs, [new(1, all)]);
        for (var seed = 0; seed < 25; seed++)
        {
            var r = new Random(seed);
            var shuffled = all.OrderBy(_ => r.Next()).ToList();
            var half = shuffled.Count / 2;
            var book = Build(reqs.OrderBy(_ => r.Next()).ToArray(), [new(2, shuffled.Take(half).ToList()), new(1, shuffled.Skip(half).ToList())]);
            Assert.Equal(expected.Skipped, book.Skipped);
            Assert.Equal(expected.Items.Count, book.Items.Count);
            for (var i = 0; i < expected.Items.Count; i++)
            {
                Assert.Equal(expected.Items[i].ItemId, book.Items[i].ItemId);
                Assert.Equal(expected.Items[i].Needed, book.Items[i].Needed);
                Assert.Equal(expected.Items[i].Worlds.Count, book.Items[i].Worlds.Count);
                for (var j = 0; j < expected.Items[i].Worlds.Count; j++)
                {
                    Assert.Equal(expected.Items[i].Worlds[j].WorldId, book.Items[i].Worlds[j].WorldId);
                    Assert.Equal(expected.Items[i].Worlds[j].WorldName, book.Items[i].Worlds[j].WorldName);
                    Assert.Equal(expected.Items[i].Worlds[j].Steps, book.Items[i].Worlds[j].Steps);
                }
            }
        }
    }

    [Fact]
    public void NullArgumentsThrow()
    {
        var ex = new ExclusionState();
        Assert.Throws<ArgumentNullException>(() => OfferBookBuilder.Build(null!, [], Allow(), ex));
        Assert.Throws<ArgumentNullException>(() => OfferBookBuilder.Build([], null!, Allow(), ex));
        Assert.Throws<ArgumentNullException>(() => OfferBookBuilder.Build([], [], null!, ex));
        Assert.Throws<ArgumentNullException>(() => OfferBookBuilder.Build([], [], Allow(), null!));
    }
}
