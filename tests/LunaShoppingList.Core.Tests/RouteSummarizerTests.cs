namespace LunaShoppingList.Core.Tests;

public class RouteSummarizerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PriceStep Step(int price, int qty, int ageDays = 0) => new(price, qty, false, T0.AddDays(-ageDays));

    private static WorldOffer World(uint id, string name, params PriceStep[] steps) => new(id, name, steps);

    private static ItemOffers Item(uint id, int needed, params WorldOffer[] worlds) => new(id, needed, worlds);

    private static OfferBook Book(params ItemOffers[] items) => new(items, Array.Empty<SkippedItem>());

    private static AllocationLine L(uint item, uint world, int qty) => new(item, world, qty);

    [Fact]
    public void EmptyBookAndEmptyAllocation()
    {
        var s = RouteSummarizer.Summarize(Book(), Array.Empty<AllocationLine>());
        Assert.Empty(s.Worlds);
        Assert.Empty(s.Unmet);
        Assert.Equal(0, s.GrandTotal);
        Assert.Equal(0, s.WorldCount);
    }

    [Fact]
    public void EmptyAllocationReportsEverythingUnmet()
    {
        var book = Book(Item(1, 5, World(10, "A", Step(10, 9))), Item(2, 3, World(10, "A", Step(10, 9))));
        var s = RouteSummarizer.Summarize(book, Array.Empty<AllocationLine>());
        Assert.Equal(new[] { new ShortfallItem(1, 5), new ShortfallItem(2, 3) }, s.Unmet);
        Assert.Empty(s.Worlds);
    }

    [Fact]
    public void CostEqualsLadderAcrossTwoSteps()
    {
        var book = Book(Item(1, 5, World(10, "A", Step(10, 2, 3), Step(25, 4, 1))));
        var s = RouteSummarizer.Summarize(book, new[] { L(1, 10, 5) });
        var line = Assert.Single(Assert.Single(s.Worlds).Lines);
        // 2026-11-24: Cost was 2 * 10 + 3 * 25 = 95, Quantity 5, GrandTotal 95. Five is not a legal total of stacks 2 and 4;
        // it snaps up to 6, both stacks whole, so Cost is 2 * 10 + 4 * 25 = 120.
        Assert.Equal(2 * 10 + 4 * 25, line.Cost);
        Assert.Equal(10, line.MinUnitPrice);
        Assert.Equal(25, line.MaxUnitPrice);
        Assert.Equal(T0.AddDays(-3), line.OldestReview);
        Assert.Equal(6, line.Quantity);
        Assert.Equal(120, s.GrandTotal);
        Assert.Empty(s.Unmet);
    }

    [Fact]
    public void PartialQuantityUsesOnlyCheapStep()
    {
        var book = Book(Item(1, 5, World(10, "A", Step(10, 2, 3), Step(25, 4, 1))));
        var line = RouteSummarizer.Summarize(book, new[] { L(1, 10, 2) }).Worlds[0].Lines[0];
        Assert.Equal(20, line.Cost);
        Assert.Equal(10, line.MaxUnitPrice);
        Assert.Equal(T0.AddDays(-3), line.OldestReview);
    }

    [Fact]
    public void QuantityClampedToUnitsForSale()
    {
        var book = Book(Item(1, 10, World(10, "A", Step(10, 3))));
        var s = RouteSummarizer.Summarize(book, new[] { L(1, 10, 8) });
        var line = s.Worlds[0].Lines[0];
        Assert.Equal(3, line.Quantity);
        Assert.Equal(30, line.Cost);
        Assert.Equal(new[] { new ShortfallItem(1, 7) }, s.Unmet);
    }

    [Fact]
    public void DuplicatesAreSummedBeforeClamping()
    {
        var book = Book(Item(1, 10, World(10, "A", Step(10, 5))));
        var s = RouteSummarizer.Summarize(book, new[] { L(1, 10, 3), L(1, 10, 4) });
        Assert.Equal(5, s.Worlds[0].Lines[0].Quantity);
        Assert.Single(s.Worlds[0].Lines);
        Assert.Equal(50, s.GrandTotal);
        Assert.Equal(new[] { new ShortfallItem(1, 5) }, s.Unmet);
    }

    [Fact]
    public void UnknownItemsWorldsAndNonPositiveQuantitiesAreIgnored()
    {
        var book = Book(Item(1, 3, World(10, "A", Step(10, 5))));
        var s = RouteSummarizer.Summarize(book, new[]
        {
            L(99, 10, 2), L(1, 77, 2), L(1, 10, 0), L(1, 10, -4),
        });
        Assert.Empty(s.Worlds);
        Assert.Equal(new[] { new ShortfallItem(1, 3) }, s.Unmet);
    }

    [Fact]
    public void NegativeLineDoesNotCancelPositiveLine()
    {
        var book = Book(Item(1, 3, World(10, "A", Step(10, 5))));
        var s = RouteSummarizer.Summarize(book, new[] { L(1, 10, 3), L(1, 10, -2) });
        // 2026-11-24: was 3; the world sells one stack of 5, so a request of 3 buys the whole stack.
        Assert.Equal(5, s.Worlds[0].Lines[0].Quantity);
        Assert.Empty(s.Unmet);
    }

    [Fact]
    public void OverAllocationIsNotUnmet()
    {
        var book = Book(Item(1, 3, World(10, "A", Step(10, 5)), World(11, "B", Step(12, 5))));
        var s = RouteSummarizer.Summarize(book, new[] { L(1, 10, 4), L(1, 11, 4) });
        Assert.Empty(s.Unmet);
        // 2026-11-24: was 4 * 10 + 4 * 12 = 88; each world sells one stack of 5, so each request of 4 buys 5.
        Assert.Equal(5 * 10 + 5 * 12, s.GrandTotal);
    }

    [Fact]
    public void ExactlyNeededIsNotUnmet()
    {
        var book = Book(Item(1, 3, World(10, "A", Step(10, 5))));
        Assert.Empty(RouteSummarizer.Summarize(book, new[] { L(1, 10, 3) }).Unmet);
    }

    [Fact]
    public void ItemWithNoOffersIsAlwaysUnmet()
    {
        var book = Book(Item(1, 4), Item(2, 1, World(10, "A", Step(5, 1))));
        var s = RouteSummarizer.Summarize(book, new[] { L(1, 10, 3), L(2, 10, 1) });
        Assert.Equal(new[] { new ShortfallItem(1, 4) }, s.Unmet);
        Assert.Single(s.Worlds[0].Lines);
    }

    [Fact]
    public void UnmetSumsAcrossWorldsAndOrdersByItemId()
    {
        var w = new[] { World(10, "A", Step(1, 9)), World(11, "B", Step(1, 9)) };
        var book = Book(Item(5, 10, w), Item(3, 10, w), Item(4, 2, w));
        var s = RouteSummarizer.Summarize(book, new[] { L(5, 10, 2), L(5, 11, 3), L(4, 10, 2) });
        // 2026-11-24: was { Shortfall(3, 10), Shortfall(5, 5) }. Each world sells one stack of 9, so item 5's requests of 2 and 3
        // buy 9 + 9 = 18 (need 10) and item 5 is no longer short.
        Assert.Equal(new[] { new ShortfallItem(3, 10) }, s.Unmet);
    }

    [Fact]
    public void UnmetOrderedByItemIdEvenWhenBookIsNot()
    {
        var book = Book(Item(9, 1), Item(2, 1), Item(5, 1));
        var s = RouteSummarizer.Summarize(book, Array.Empty<AllocationLine>());
        Assert.Equal(new uint[] { 2, 5, 9 }, s.Unmet.Select(u => u.ItemId));
    }

    [Fact]
    public void WorldsOrderedByNameIgnoringCaseThenId_LinesByItemId_ShuffledInput()
    {
        var wB = World(20, "Beta", Step(1, 9));
        var wA = World(21, "alpha", Step(1, 9));
        var wA2 = World(5, "ALPHA", Step(1, 9));
        var book = Book(Item(8, 9, wB, wA, wA2), Item(3, 9, wB, wA, wA2));
        var s = RouteSummarizer.Summarize(book, new[]
        {
            L(8, 20, 1), L(3, 21, 1), L(8, 21, 1), L(3, 5, 1), L(3, 20, 1), L(8, 5, 1),
        });
        Assert.Equal(new uint[] { 5, 21, 20 }, s.Worlds.Select(w => w.WorldId));
        Assert.All(s.Worlds, w => Assert.Equal(new uint[] { 3, 8 }, w.Lines.Select(l => l.ItemId)));
        Assert.Equal(3, s.WorldCount);
    }

    [Fact]
    public void WorldTotalAndGrandTotalAreSumsOfLineCosts()
    {
        var wA = World(10, "A", Step(10, 5));
        var wB = World(11, "B", Step(7, 5));
        var book = Book(Item(1, 4, wA, wB), Item(2, 4, wA, wB));
        var s = RouteSummarizer.Summarize(book, new[] { L(1, 10, 2), L(2, 10, 3), L(1, 11, 2) });
        // 2026-11-24: totals were 50, 14, 64. Each world sells one stack of 5, so every line buys all 5 units:
        // world A 2 lines * 50 = 100, world B 5 * 7 = 35.
        Assert.Equal(100, s.Worlds[0].Total);
        Assert.Equal(35, s.Worlds[1].Total);
        Assert.Equal(135, s.GrandTotal);
        Assert.Equal("A", s.Worlds[0].WorldName);
    }

    [Fact]
    public void WorldCountOnlyCountsWorldsWithLines()
    {
        var book = Book(Item(1, 4, World(10, "A", Step(1, 5)), World(11, "B", Step(1, 5))));
        var s = RouteSummarizer.Summarize(book, new[] { L(1, 10, 4), L(1, 11, 0) });
        Assert.Equal(1, s.WorldCount);
    }

    [Fact]
    public void LargeNumbersDoNotOverflowInt()
    {
        var big = int.MaxValue;
        var book = Book(Item(1, big, World(10, "A", Step(big, big))), Item(2, big, World(10, "A", Step(big, big))));
        var s = RouteSummarizer.Summarize(book, new[] { L(1, 10, big), L(2, 10, big), L(2, 10, big) });
        var one = (long)big * big;
        Assert.Equal(one, s.Worlds[0].Lines[0].Cost);
        Assert.Equal(one, s.Worlds[0].Lines[1].Cost);
        Assert.Equal(2 * one, s.Worlds[0].Total);
        Assert.Equal(2 * one, s.GrandTotal);
        Assert.Equal(big, s.Worlds[0].Lines[1].Quantity);
        Assert.Empty(s.Unmet);
    }

    [Fact]
    public void ZeroStockWorldProducesNoLine()
    {
        var book = Book(Item(1, 2, World(10, "A")));
        var s = RouteSummarizer.Summarize(book, new[] { L(1, 10, 2) });
        Assert.Empty(s.Worlds);
        Assert.Equal(new[] { new ShortfallItem(1, 2) }, s.Unmet);
    }

    [Fact]
    public void CompareSummarizesBothAndDerivesDifferences()
    {
        var wA = World(10, "A", Step(10, 5));
        var wB = World(11, "B", Step(4, 2), Step(30, 5));
        var book = Book(Item(1, 4, wA, wB));
        var cmp = RouteSummarizer.Compare(book,
            new[] { L(1, 11, 2), L(1, 10, 2) },
            new[] { L(1, 10, 4) });
        Assert.Equal(2, cmp.Cheapest.WorldCount);
        // 2026-11-24: totals were 28 / 40 and ExtraGilForFewestWorlds 12. World A sells one stack of 5, so a request of 2 buys 5 for 50
        // (cheapest = 2 * 4 + 50 = 58) and the request of 4 also buys 5 for 50. The fixture is arithmetic only, not a real suggestion,
        // so the difference is now negative.
        Assert.Equal(58, cmp.Cheapest.GrandTotal);
        Assert.Equal(1, cmp.FewestWorlds.WorldCount);
        Assert.Equal(50, cmp.FewestWorlds.GrandTotal);
        Assert.Equal(-8, cmp.ExtraGilForFewestWorlds);
        Assert.Equal(1, cmp.WorldsSaved);
    }

    [Fact]
    public void NullArgumentsThrow()
    {
        var book = Book();
        var none = Array.Empty<AllocationLine>();
        Assert.Throws<ArgumentNullException>(() => RouteSummarizer.Summarize(null!, none));
        Assert.Throws<ArgumentNullException>(() => RouteSummarizer.Summarize(book, null!));
        Assert.Throws<ArgumentNullException>(() => RouteSummarizer.Compare(null!, none, none));
        Assert.Throws<ArgumentNullException>(() => RouteSummarizer.Compare(book, null!, none));
        Assert.Throws<ArgumentNullException>(() => RouteSummarizer.Compare(book, none, null!));
    }
}
