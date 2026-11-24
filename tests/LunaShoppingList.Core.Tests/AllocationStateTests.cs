namespace LunaShoppingList.Core.Tests;

public class AllocationStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PriceStep Step(int price, int qty) => new(price, qty, false, T0);

    private static WorldOffer World(uint id, string name, params PriceStep[] steps) => new(id, name, steps);

    private static readonly WorldOffer WA = World(10, "Alpha", Step(10, 2), Step(25, 4));
    private static readonly WorldOffer WB = World(11, "Beta", Step(7, 3));

    private static OfferBook Book() => new(new[]
    {
        new ItemOffers(1, 5, new[] { WA, WB }),
        new ItemOffers(2, 3, new[] { WB }),
        new ItemOffers(3, 4, Array.Empty<WorldOffer>()),
    }, Array.Empty<SkippedItem>());

    [Fact]
    public void StartsEmpty()
    {
        var a = new AllocationState(Book());
        Assert.Empty(a.Lines);
        Assert.Equal(0, a.GetQuantity(1, 10));
        Assert.Equal(0, a.Allocated(1));
        Assert.Equal(5, a.Unmet(1));
        Assert.Equal(0, a.Cost(1, 10));
        Assert.Empty(a.Summarize().Worlds);
    }

    [Fact]
    public void BookIsTheOneGiven()
    {
        var book = Book();
        Assert.Same(book, new AllocationState(book).Book);
    }

    [Fact]
    public void NullBookThrows() => Assert.Throws<ArgumentNullException>(() => new AllocationState(null!));

    [Fact]
    public void SetQuantityStoresAndGetReturns()
    {
        var a = new AllocationState(Book());
        a.SetQuantity(1, 10, 3);
        // 2026-11-24: was 3 (divisible units). WA sells whole stacks of 2 and 4, so 3 is not a legal total and snaps up to 4.
        Assert.Equal(4, a.GetQuantity(1, 10));
        Assert.Equal(0, a.GetQuantity(1, 11));
    }

    [Fact]
    public void SetQuantityClampsToUnitsForSale()
    {
        var a = new AllocationState(Book());
        a.SetQuantity(1, 10, 100);
        Assert.Equal(6, a.GetQuantity(1, 10));
        a.SetQuantity(1, 11, int.MaxValue);
        Assert.Equal(3, a.GetQuantity(1, 11));
    }

    [Fact]
    public void SetQuantityNegativeClampsToZeroAndRemoves()
    {
        var a = new AllocationState(Book());
        a.SetQuantity(1, 10, 3);
        a.SetQuantity(1, 10, -5);
        Assert.Equal(0, a.GetQuantity(1, 10));
        Assert.Empty(a.Lines);
    }

    [Fact]
    public void SetQuantityZeroRemovesTheLine()
    {
        var a = new AllocationState(Book());
        a.SetQuantity(1, 10, 3);
        a.SetQuantity(2, 11, 1);
        a.SetQuantity(1, 10, 0);
        // 2026-11-24: was AllocationLine(2, 11, 1). WB sells one stack of 3, so a request for 1 snaps up to 3.
        Assert.Equal(new[] { new AllocationLine(2, 11, 3) }, a.Lines);
        Assert.DoesNotContain(a.Summarize().Worlds, w => w.WorldId == 10);
    }

    [Fact]
    public void SetQuantityIgnoresUnknownItemOrWorld()
    {
        var a = new AllocationState(Book());
        a.SetQuantity(99, 10, 1);
        a.SetQuantity(1, 99, 1);
        a.SetQuantity(2, 10, 1);   // item 2 is not offered on world 10
        a.SetQuantity(3, 10, 1);   // item 3 has no offers
        Assert.Empty(a.Lines);
        Assert.Equal(0, a.GetQuantity(99, 10));
    }

    [Fact]
    public void AllocatedSumsOverWorldsAndUnmetFollows()
    {
        var a = new AllocationState(Book());
        a.SetQuantity(1, 10, 2);
        a.SetQuantity(1, 11, 1);
        // 2026-11-24: was Allocated 3 / Unmet 2. Requests of 2 (a legal total) and 1 (snaps up to WB's stack of 3) buy 2 + 3 = 5 units.
        Assert.Equal(5, a.Allocated(1));
        Assert.Equal(0, a.Unmet(1));
    }

    [Fact]
    public void AllocatedAndUnmetAreIsolatedPerItem()
    {
        var a = new AllocationState(Book());
        a.SetQuantity(1, 11, 2);
        a.SetQuantity(2, 11, 3);
        // 2026-11-24: Allocated(1) was 2 and Unmet(1) was 3: a request of 2 on WB snaps up to its one stack of 3.
        Assert.Equal(3, a.Allocated(1));
        Assert.Equal(3, a.Allocated(2));
        Assert.Equal(0, a.Allocated(3));
        Assert.Equal(2, a.Unmet(1));
        Assert.Equal(0, a.Unmet(2));
        Assert.Equal(4, a.Unmet(3));
    }

    [Fact]
    public void OverAllocationReportsZeroUnmet()
    {
        var a = new AllocationState(Book());
        a.SetQuantity(1, 10, 6);
        a.SetQuantity(1, 11, 3);
        Assert.Equal(9, a.Allocated(1));
        Assert.Equal(0, a.Unmet(1));
    }

    [Fact]
    public void UnmetForUnknownItemIsZeroAndNoOfferItemIsFullNeed()
    {
        var a = new AllocationState(Book());
        Assert.Equal(0, a.Unmet(99));
        Assert.Equal(0, a.Allocated(99));
        Assert.Equal(4, a.Unmet(3));
    }

    [Fact]
    public void CostFollowsTheLadderAcrossSteps()
    {
        var a = new AllocationState(Book());
        a.SetQuantity(1, 10, 5);
        // 2026-11-24: was 2 * 10 + 3 * 25 = 95 (part of the 25 gil stack). 5 is not a legal total; it snaps up to 6 = both stacks whole.
        Assert.Equal(2 * 10 + 4 * 25, a.Cost(1, 10));
        a.SetQuantity(1, 10, 2);
        Assert.Equal(20, a.Cost(1, 10));
        Assert.Equal(0, a.Cost(1, 11));
        Assert.Equal(0, a.Cost(99, 10));
    }

    [Fact]
    public void ApplyReplacesPreviousLines()
    {
        var a = new AllocationState(Book());
        a.SetQuantity(1, 10, 3);
        a.SetQuantity(2, 11, 2);
        a.Apply(new[] { new AllocationLine(1, 11, 1) });
        // 2026-11-24: was AllocationLine(1, 11, 1); a request of 1 on WB snaps up to its stack of 3.
        Assert.Equal(new[] { new AllocationLine(1, 11, 3) }, a.Lines);
        Assert.Equal(0, a.GetQuantity(1, 10));
        Assert.Equal(0, a.GetQuantity(2, 11));
    }

    [Fact]
    public void ApplyEmptyClearsEverything()
    {
        var a = new AllocationState(Book());
        a.SetQuantity(1, 10, 3);
        a.Apply(Array.Empty<AllocationLine>());
        Assert.Empty(a.Lines);
    }

    [Fact]
    public void ApplySumsDuplicatesClampsAndIgnoresUnknownAndNonPositive()
    {
        var a = new AllocationState(Book());
        a.Apply(new[]
        {
            new AllocationLine(1, 11, 2), new AllocationLine(1, 11, 2),   // 4 -> clamp 3
            new AllocationLine(99, 10, 1), new AllocationLine(1, 99, 1),
            new AllocationLine(2, 11, 0), new AllocationLine(2, 11, -3),
        });
        Assert.Equal(new[] { new AllocationLine(1, 11, 3) }, a.Lines);
    }

    [Fact]
    public void ApplyWithHugeDuplicatesDoesNotOverflow()
    {
        var a = new AllocationState(Book());
        a.Apply(new[] { new AllocationLine(1, 11, int.MaxValue), new AllocationLine(1, 11, int.MaxValue) });
        Assert.Equal(3, a.GetQuantity(1, 11));
    }

    [Fact]
    public void ApplyIsNotAffectedByLaterChangesToTheSourceList()
    {
        var src = new List<AllocationLine> { new(1, 10, 2) };
        var a = new AllocationState(Book());
        a.Apply(src);
        src.Clear();
        Assert.Single(a.Lines);
    }

    [Fact]
    public void LinesAreOrderedByItemThenWorld()
    {
        var a = new AllocationState(Book());
        a.SetQuantity(2, 11, 1);
        a.SetQuantity(1, 11, 1);
        a.SetQuantity(1, 10, 1);
        // 2026-11-24: quantities were 1, 1, 1; each request of 1 snaps up to the world's smallest legal total (WA 2, WB 3, WB 3).
        Assert.Equal(new[]
        {
            new AllocationLine(1, 10, 2), new AllocationLine(1, 11, 3), new AllocationLine(2, 11, 3),
        }, a.Lines);
    }

    [Fact]
    public void LinesReflectEditsImmediatelyAndAreSnapshots()
    {
        var a = new AllocationState(Book());
        a.SetQuantity(1, 10, 1);
        var before = a.Lines;
        a.SetQuantity(1, 10, 3);
        // 2026-11-24: was 1 then 2 (the second edit, 2, is already a legal total so it would no longer change anything). Now 2 (1 snapped up), then 4 (3 snapped up).
        Assert.Equal(2, Assert.Single(before).Quantity);          // earlier snapshot unchanged
        Assert.Equal(4, Assert.Single(a.Lines).Quantity);
    }

    [Fact]
    public void MutatingTheReturnedListDoesNotChangeTheState()
    {
        var a = new AllocationState(Book());
        a.SetQuantity(1, 10, 1);
        if (a.Lines is IList<AllocationLine> list && !list.IsReadOnly)
            list.Clear();
        Assert.Single(a.Lines);
    }

    [Fact]
    public void SummarizeReflectsEditsImmediately()
    {
        var a = new AllocationState(Book());
        a.SetQuantity(1, 10, 5);
        var s1 = a.Summarize();
        // 2026-11-24: was 95 (5 divisible units); 5 snaps up to 6 whole units = 2 * 10 + 4 * 25.
        Assert.Equal(120, s1.GrandTotal);
        Assert.Equal(new[] { new ShortfallItem(2, 3), new ShortfallItem(3, 4) }, s1.Unmet);
        a.SetQuantity(2, 11, 3);
        var s2 = a.Summarize();
        Assert.Equal(120 + 21, s2.GrandTotal);   // 2026-11-24: was 95 + 21
        Assert.Equal(2, s2.WorldCount);
        Assert.Equal(new[] { new ShortfallItem(3, 4) }, s2.Unmet);
    }

    [Fact]
    public void SummarizeEqualsRouteSummarizerOverLines()
    {
        var a = new AllocationState(Book());
        a.SetQuantity(1, 10, 4);
        a.SetQuantity(1, 11, 1);
        var direct = RouteSummarizer.Summarize(a.Book, a.Lines);
        var s = a.Summarize();
        Assert.Equal(direct.GrandTotal, s.GrandTotal);
        Assert.Equal(direct.Unmet, s.Unmet);
        Assert.Equal(direct.Worlds.Select(w => (w.WorldId, w.Total)), s.Worlds.Select(w => (w.WorldId, w.Total)));
    }

    [Fact]
    public void EmptyBookIsUsable()
    {
        var a = new AllocationState(new OfferBook(Array.Empty<ItemOffers>(), Array.Empty<SkippedItem>()));
        a.SetQuantity(1, 1, 1);
        a.Apply(new[] { new AllocationLine(1, 1, 1) });
        Assert.Empty(a.Lines);
        Assert.Equal(0, a.Summarize().GrandTotal);
    }

    [Fact]
    public void ApplyNullThrows() => Assert.Throws<ArgumentNullException>(() => new AllocationState(Book()).Apply(null!));
}
