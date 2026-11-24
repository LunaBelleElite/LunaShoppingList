namespace LunaShoppingList.Core.Tests;

/// <summary>Whole-stack truth for <see cref="AllocationState"/> (release 0.16.0.0): every quantity is a legal total, priced by its stacks.</summary>
public class AllocationStateWholeStackTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PriceStep Step(int price, int qty, int ageDays = 0) => new(price, qty, false, T0.AddDays(-ageDays));

    // Alpha (world 10) sells three stacks: [0] 20 @ 10, [1] 99 @ 12, [2] 5 @ 30.
    // Legal totals: 5, 20, 25, 99, 104, 119, 124 (the subset sums). Supply 124.
    private static readonly WorldOffer Alpha = new(10, "Alpha", new[] { Step(10, 20, 3), Step(12, 99, 1), Step(30, 5, 2) });
    private static readonly WorldOffer Beta = new(11, "Beta", new[] { Step(5, 10) });

    private static OfferBook Book() => new(new[]
    {
        new ItemOffers(1, 100, new[] { Alpha, Beta }),
        new ItemOffers(2, 500, new[] { Alpha }),
    }, Array.Empty<SkippedItem>());

    private static AllocationState State() => new(Book());

    private static readonly int[] Legal = { 5, 20, 25, 99, 104, 119, 124 };

    [Theory]
    [InlineData(1, 5)]
    [InlineData(5, 5)]
    [InlineData(6, 20)]
    [InlineData(20, 20)]
    [InlineData(21, 25)]
    [InlineData(26, 99)]
    [InlineData(100, 104)]
    [InlineData(105, 119)]
    [InlineData(120, 124)]
    [InlineData(124, 124)]
    [InlineData(9999, 124)]
    [InlineData(int.MaxValue, 124)]
    public void SetQuantitySnapsUpToTheNextLegalTotalCappedAtSupply(int typed, int expected)
    {
        var a = State();
        a.SetQuantity(1, 10, typed);
        Assert.Equal(expected, a.GetQuantity(1, 10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ZeroOrNegativeRemovesTheLine(int typed)
    {
        var a = State();
        a.SetQuantity(1, 10, 25);
        a.SetQuantity(1, 10, typed);
        Assert.Empty(a.Lines);
        Assert.Equal(0, a.GetQuantity(1, 10));
    }

    [Theory]
    [InlineData(5, 150)]
    [InlineData(20, 200)]
    [InlineData(25, 350)]
    [InlineData(99, 1188)]
    [InlineData(104, 1338)]
    [InlineData(119, 1388)]
    [InlineData(124, 1538)]
    public void CostIsTheExactPriceOfTheLegalTotal(int total, long gil)
    {
        var a = State();
        a.SetQuantity(1, 10, total);
        Assert.Equal(total, a.GetQuantity(1, 10));
        Assert.Equal(gil, a.Cost(1, 10));
    }

    // Cheap big stack, dear small stack: legal totals 4, 6, 10. A request of 3 snaps up to the SMALLEST legal total (4, the dear stack),
    // not to the cheapest cover (6 units for 6 gil). That is the ruling "snap up to the next legal total"; the suggesters, not the typing box, pick cheap covers.
    private static AllocationState DearSmallState()
        => new(new OfferBook(new[] { new ItemOffers(5, 3, new[] { new WorldOffer(30, "Gamma", new[] { Step(1, 6), Step(100, 4) }) }) }, Array.Empty<SkippedItem>()));

    [Fact]
    public void SnapUpMeansTheSmallestLegalTotalNotTheCheapestCover()
    {
        var a = DearSmallState();
        a.SetQuantity(5, 30, 3);
        Assert.Equal(4, a.GetQuantity(5, 30));
        Assert.Equal(400, a.Cost(5, 30));
        a.Apply(new[] { new AllocationLine(5, 30, 3) });
        Assert.Equal(4, a.GetQuantity(5, 30));
    }

    [Fact]
    public void CostOfASnappedRequestIsTheCostOfWhatWasActuallyBought()
    {
        var a = State();
        a.SetQuantity(1, 10, 21);   // snaps up to 25 = the 20 stack and the 5 stack
        Assert.Equal(20 * 10 + 5 * 30, a.Cost(1, 10));
    }

    [Theory]
    [InlineData(25, new[] { 20, 5 })]
    [InlineData(99, new[] { 99 })]
    [InlineData(119, new[] { 20, 99 })]
    [InlineData(124, new[] { 20, 99, 5 })]
    [InlineData(5, new[] { 5 })]
    public void StackSizesAreInAscendingListingOrderNotSizeOrder(int total, int[] sizes)
    {
        var a = State();
        a.SetQuantity(1, 10, total);
        Assert.Equal(sizes, a.StackSizes(1, 10));
        Assert.Equal(sizes.Length, a.StackCount(1, 10));
        Assert.Equal(total, a.StackSizes(1, 10).Sum());
    }

    [Fact]
    public void NoLineMeansNoStacks()
    {
        var a = State();
        Assert.Empty(a.StackSizes(1, 10));
        Assert.Equal(0, a.StackCount(1, 10));
        Assert.Empty(a.StackSizes(99, 10));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 20)]
    [InlineData(20, 25)]
    [InlineData(25, 99)]
    [InlineData(99, 104)]
    [InlineData(104, 119)]
    [InlineData(119, 124)]
    [InlineData(124, 124)]
    public void NextLegalUpIsTheAdjacentLegalTotalAndChangesNothing(int current, int expected)
    {
        var a = State();
        if (current > 0)
            a.SetQuantity(1, 10, current);
        Assert.Equal(expected, a.NextLegalUp(1, 10));
        Assert.Equal(current, a.GetQuantity(1, 10));
    }

    [Theory]
    [InlineData(124, 119)]
    [InlineData(119, 104)]
    [InlineData(104, 99)]
    [InlineData(99, 25)]
    [InlineData(25, 20)]
    [InlineData(20, 5)]
    [InlineData(5, 0)]
    [InlineData(0, 0)]
    public void NextLegalDownIsTheAdjacentLegalTotalAndChangesNothing(int current, int expected)
    {
        var a = State();
        if (current > 0)
            a.SetQuantity(1, 10, current);
        Assert.Equal(expected, a.NextLegalDown(1, 10));
        Assert.Equal(current, a.GetQuantity(1, 10));
    }

    [Fact]
    public void StepQuantityWalksUpThroughEveryLegalTotalAndStopsAtTheTop()
    {
        var a = State();
        foreach (var expected in Legal)
            Assert.Equal(expected, a.StepQuantity(1, 10, +1));
        Assert.Equal(124, a.StepQuantity(1, 10, +1));
        Assert.Equal(124, a.GetQuantity(1, 10));
    }

    [Fact]
    public void StepQuantityWalksDownAndZeroRemovesTheLine()
    {
        var a = State();
        a.SetQuantity(1, 10, 124);
        foreach (var expected in Legal.Reverse().Skip(1))
            Assert.Equal(expected, a.StepQuantity(1, 10, -1));
        Assert.Equal(0, a.StepQuantity(1, 10, -1));
        Assert.Empty(a.Lines);
        Assert.Equal(0, a.StepQuantity(1, 10, -1));
    }

    [Fact]
    public void StepQuantityFromANonLegalNeighbourStillLandsOnLegalTotals()
    {
        var a = State();
        a.SetQuantity(1, 10, 25);
        Assert.Equal(99, a.StepQuantity(1, 10, +1));
        Assert.Equal(25, a.StepQuantity(1, 10, -1));
    }

    [Fact]
    public void StepQuantityOnUnknownItemOrWorldDoesNothing()
    {
        var a = State();
        Assert.Equal(0, a.StepQuantity(99, 10, +1));
        Assert.Equal(0, a.StepQuantity(2, 11, +1));   // item 2 is not offered on Beta
        Assert.Equal(0, a.NextLegalUp(99, 10));
        Assert.Equal(0, a.NextLegalDown(2, 11));
        Assert.Empty(a.Lines);
    }

    [Fact]
    public void MaxQuantityIsTheWorldsLargestLegalTotal()
    {
        var a = State();
        Assert.Equal(124, a.MaxQuantity(1, 10));
        Assert.Equal(10, a.MaxQuantity(1, 11));
        Assert.Equal(0, a.MaxQuantity(99, 10));
        Assert.Equal(0, a.MaxQuantity(2, 11));
    }

    [Fact]
    public void ApplySumsDuplicatesFirstThenSnapsUp()
    {
        var a = State();
        a.Apply(new[] { new AllocationLine(1, 10, 10), new AllocationLine(1, 10, 11) });   // 21 -> 25
        Assert.Equal(new[] { new AllocationLine(1, 10, 25) }, a.Lines);
    }

    [Fact]
    public void ApplyKeepsALegalTotalAndSnapsAnIllegalOneUp()
    {
        var a = State();
        a.Apply(new[] { new AllocationLine(1, 10, 99), new AllocationLine(2, 10, 100) });
        Assert.Equal(99, a.GetQuantity(1, 10));
        Assert.Equal(104, a.GetQuantity(2, 10));
    }

    [Fact]
    public void BoughtSurplusAndUnmetAgreeWhenTheStackOvershootsTheNeed()
    {
        var a = State();
        a.SetQuantity(1, 10, 104);   // need 100
        Assert.Equal(104, a.Bought(1));
        Assert.Equal(104, a.Allocated(1));
        Assert.Equal(4, a.Surplus(1));
        Assert.Equal(0, a.Unmet(1));
    }

    [Fact]
    public void ShortOfTheNeedIsUnmetWithNoSurplus()
    {
        var a = State();
        a.SetQuantity(1, 10, 99);   // need 100
        Assert.Equal(0, a.Surplus(1));
        Assert.Equal(1, a.Unmet(1));
    }

    [Fact]
    public void WhenTheWholeSupplyIsStillShortTheRestIsUnmet()
    {
        var a = State();
        a.SetQuantity(2, 10, 1_000_000);   // item 2 needs 500, the world sells 124
        Assert.Equal(124, a.Bought(2));
        Assert.Equal(376, a.Unmet(2));
        Assert.Equal(0, a.Surplus(2));
    }

    [Fact]
    public void SurplusAndBoughtSumOverWorlds()
    {
        var a = State();
        a.SetQuantity(1, 10, 99);
        a.SetQuantity(1, 11, 3);   // Beta sells one stack of 10
        Assert.Equal(109, a.Bought(1));
        Assert.Equal(9, a.Surplus(1));
        Assert.Equal(0, a.Unmet(1));
        Assert.Equal(0, a.Surplus(99));
    }

    [Fact]
    public void SummaryAgreesWithTheStateLineByLine()
    {
        var a = State();
        a.SetQuantity(1, 10, 119);
        a.SetQuantity(1, 11, 1);
        var s = a.Summarize();
        foreach (var w in s.Worlds)
            foreach (var l in w.Lines)
            {
                Assert.Equal(a.GetQuantity(l.ItemId, w.WorldId), l.Quantity);
                Assert.Equal(a.Cost(l.ItemId, w.WorldId), l.Cost);
                Assert.Equal(a.StackSizes(l.ItemId, w.WorldId), l.StackSizes);
            }
    }

    // ---- A world beyond its exact limit: the documented fallback (LinePricer) ----

    private static readonly WorldOffer Huge = new(20, "Huge", new[] { Step(5, 700_000, 1), Step(6, 500_000, 2), Step(7, 300_000, 3) });

    private static AllocationState HugeState() => new(new OfferBook(new[] { new ItemOffers(7, 1_100_000, new[] { Huge }) }, Array.Empty<SkippedItem>()));

    [Fact]
    public void HugeWorldFixtureReallyIsBeyondTheExactLimit()
    {
        Assert.True(Huge.ExactTotalsLimit < 1_500_000);
        Assert.Equal(1_500_000, Huge.SnapDown(int.MaxValue));
    }

    [Fact]
    public void BeyondTheLimitTheFullSupplyIsStillExact()
    {
        var a = HugeState();
        a.SetQuantity(7, 20, int.MaxValue);
        Assert.Equal(1_500_000, a.GetQuantity(7, 20));
        Assert.Equal(700_000L * 5 + 500_000L * 6 + 300_000L * 7, a.Cost(7, 20));
        Assert.Equal(new[] { 700_000, 500_000, 300_000 }, a.StackSizes(7, 20));
    }

    [Fact]
    public void BeyondTheLimitAPartialTotalFallsBackToWholeStacksThatCoverTheRequest()
    {
        var a = HugeState();
        a.SetQuantity(7, 20, 1_100_000);
        var q = a.GetQuantity(7, 20);
        Assert.InRange(q, 1_100_000, 1_500_000);
        var sizes = a.StackSizes(7, 20);
        Assert.Equal(q, sizes.Sum());
        Assert.All(sizes, s => Assert.Contains(s, new[] { 700_000, 500_000, 300_000 }));
        Assert.Equal(sizes.Count, sizes.Distinct().Count());
    }

    [Fact]
    public void BeyondTheLimitStepUpAlwaysMovesUpToALegalSumOfStacks()
    {
        var a = HugeState();
        a.SetQuantity(7, 20, 1_100_000);
        var before = a.GetQuantity(7, 20);
        var up = a.NextLegalUp(7, 20);
        Assert.True(up >= before);
        a.SetQuantity(7, 20, up);
        Assert.Equal(a.GetQuantity(7, 20), a.StackSizes(7, 20).Sum());
    }
}
