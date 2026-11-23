namespace LunaShoppingList.Core.Tests;

public class OwnedCountingTests
{
    private static readonly DateTimeOffset T1 = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 3, 2, 10, 0, 0, TimeSpan.Zero);

    private static Dictionary<uint, int> C(params (uint Item, int Count)[] pairs)
        => pairs.ToDictionary(p => p.Item, p => p.Count);

    // ---- Add ----

    [Fact]
    public void Add_SumsStacksOfTheSameItem()
    {
        var d = new Dictionary<uint, int>();
        OwnedCounting.Add(d, 5, 3);
        OwnedCounting.Add(d, 5, 4);
        OwnedCounting.Add(d, 6, 1);
        Assert.Equal(2, d.Count);
        Assert.Equal(7, d[5]);
        Assert.Equal(1, d[6]);
    }

    [Theory]
    [InlineData(0u, 5)]
    [InlineData(7u, 0)]
    [InlineData(7u, -3)]
    public void Add_IgnoresEmptySlotsAndNonPositiveQuantities(uint itemId, int quantity)
    {
        var d = new Dictionary<uint, int>();
        OwnedCounting.Add(d, itemId, quantity);
        Assert.Empty(d);
    }

    [Fact]
    public void Add_NonPositiveQuantityLeavesAnExistingCountAlone()
    {
        var d = new Dictionary<uint, int> { [7] = 4 };
        OwnedCounting.Add(d, 7, -3);
        Assert.Equal(4, d[7]);
    }

    [Fact]
    public void Add_SaturatesInsteadOfOverflowing()
    {
        var d = new Dictionary<uint, int>();
        OwnedCounting.Add(d, 9, int.MaxValue - 1);
        OwnedCounting.Add(d, 9, 10);
        Assert.Equal(int.MaxValue, d[9]);
    }

    // ---- SameCounts ----

    [Fact]
    public void SameCounts_EqualContentsInAnyOrder()
    {
        Assert.True(OwnedCounting.SameCounts(C((1, 2), (3, 4)), C((3, 4), (1, 2))));
        Assert.True(OwnedCounting.SameCounts(C(), C()));
    }

    [Fact]
    public void SameCounts_DifferentCountForOneItem()
        => Assert.False(OwnedCounting.SameCounts(C((1, 2), (3, 4)), C((1, 2), (3, 5))));

    [Fact]
    public void SameCounts_DifferentItemSameSize()
        => Assert.False(OwnedCounting.SameCounts(C((1, 2)), C((2, 2))));

    [Fact]
    public void SameCounts_OneSideHasAnExtraItem()
    {
        Assert.False(OwnedCounting.SameCounts(C((1, 2)), C((1, 2), (3, 4))));
        Assert.False(OwnedCounting.SameCounts(C((1, 2), (3, 4)), C((1, 2))));
    }

    // ---- Copy ----

    [Fact]
    public void Copy_IsIndependentOfTheSource()
    {
        var source = new OwnedCache();
        source.ReplaceSaddlebag(T1, C((10, 3)));
        source.ReplaceRetainer(100, "Alpha", T1, C((10, 2)), C((11, 5)));

        var copy = OwnedCounting.Copy(source);
        source.ReplaceRetainer(100, "Alpha", T2, C(), C());
        source.ReplaceRetainer(200, "Beta", T2, C((10, 9)), C());
        source.ReplaceSaddlebag(T2, C());

        Assert.Equal(1, copy.RetainerCount);
        Assert.Equal(2, copy.RetainerTotal(10));
        Assert.Equal(5, copy.RetainerTotal(11));
        Assert.Equal(3, copy.SaddlebagCount(10));
        Assert.Equal(T1, copy.Saddlebag!.CapturedUtc);
        Assert.Equal(T1, copy.OldestRetainerCapture);
        Assert.Equal("Alpha", copy.Retainers[0].Name);
    }

    [Fact]
    public void Copy_SerializesIdenticallyToTheSource()
    {
        var source = new OwnedCache();
        source.ReplaceSaddlebag(T2, C((10, 3), (12, 1)));
        source.ReplaceRetainer(200, "Beta", T2, C((10, 9)), C());
        source.ReplaceRetainer(100, "Alpha", T1, C((10, 2)), C((11, 5)));
        Assert.Equal(source.ToJson(), OwnedCounting.Copy(source).ToJson());
    }

    [Fact]
    public void Copy_KeepsPerPlaceCountsAndTheUnplacedFlag()
    {
        var source = new OwnedCache();
        source.ReplaceRetainer(100, "Alpha", T1, new Dictionary<RetainerPlace, IReadOnlyDictionary<uint, int>>
        {
            [RetainerPlace.Bag2] = C((10, 2)),
            [RetainerPlace.Market] = C((10, 1)),
        });
        source.ReplaceRetainer(200, "Beta", T1, C((10, 4)), C());

        var copy = OwnedCounting.Copy(source);
        var alpha = copy.Retainers.Single(r => r.Id == 100);
        Assert.True(alpha.LocationRecorded);
        Assert.Equal(2, alpha.Places![RetainerPlace.Bag2][10]);
        Assert.Equal(1, alpha.Places[RetainerPlace.Market][10]);
        Assert.Equal(2, alpha.Stored[10]);
        Assert.False(copy.Retainers.Single(r => r.Id == 200).LocationRecorded);
        Assert.Equal(source.ToJson(), copy.ToJson());
    }

    [Fact]
    public void CopyRetainer_RestampKeepsPlacesAndOnlyMovesTheTime()
    {
        var source = new OwnedCache();
        source.ReplaceRetainer(100, "Alpha", T1, new Dictionary<RetainerPlace, IReadOnlyDictionary<uint, int>> { [RetainerPlace.Bag3] = C((10, 2)) });
        var target = OwnedCounting.Copy(source);
        OwnedCounting.CopyRetainer(target, source.Retainers[0], T2);

        var r = Assert.Single(target.Retainers);
        Assert.Equal(T2, r.CapturedUtc);
        Assert.True(r.LocationRecorded);
        Assert.Equal(2, r.Places![RetainerPlace.Bag3][10]);
    }

    [Fact]
    public void SamePlaces_ComparesEachContainerAndTreatsNullAsDifferent()
    {
        IReadOnlyDictionary<RetainerPlace, IReadOnlyDictionary<uint, int>> Bag(RetainerPlace p, int n)
            => new Dictionary<RetainerPlace, IReadOnlyDictionary<uint, int>> { [p] = C((1, n)) };
        Assert.True(OwnedCounting.SamePlaces(Bag(RetainerPlace.Bag1, 2), Bag(RetainerPlace.Bag1, 2)));
        Assert.False(OwnedCounting.SamePlaces(Bag(RetainerPlace.Bag1, 2), Bag(RetainerPlace.Bag2, 2)));
        Assert.False(OwnedCounting.SamePlaces(Bag(RetainerPlace.Bag1, 2), Bag(RetainerPlace.Bag1, 3)));
        Assert.False(OwnedCounting.SamePlaces(null, Bag(RetainerPlace.Bag1, 2)));
        var both = new Dictionary<RetainerPlace, IReadOnlyDictionary<uint, int>> { [RetainerPlace.Bag1] = C((1, 2)), [RetainerPlace.Market] = C((1, 1)) };
        Assert.False(OwnedCounting.SamePlaces(Bag(RetainerPlace.Bag1, 2), both));
        Assert.False(OwnedCounting.SamePlaces(both, Bag(RetainerPlace.Bag1, 2)));
    }

    [Fact]
    public void Copy_OfAnEmptyCacheHasNoSaddlebag()
    {
        var copy = OwnedCounting.Copy(new OwnedCache());
        Assert.Null(copy.Saddlebag);
        Assert.Equal(0, copy.RetainerCount);
    }

    // ---- RetainerVisitGate ----

    private static readonly Dictionary<uint, int> A = C((1, 5));
    private static readonly Dictionary<uint, int> B = C((2, 7));
    private static readonly Dictionary<uint, int> Empty = C();

    /// <summary>A retainer read with <paramref name="bag1"/> in bag 1 and <paramref name="market"/> on the market.</summary>
    private static IReadOnlyDictionary<RetainerPlace, IReadOnlyDictionary<uint, int>> Pl(Dictionary<uint, int> bag1, Dictionary<uint, int> market)
        => new Dictionary<RetainerPlace, IReadOnlyDictionary<uint, int>> { [RetainerPlace.Bag1] = bag1, [RetainerPlace.Market] = market };

    /// <summary>A gate whose retainer 1 opened at <paramref name="openedMs"/>.</summary>
    private static RetainerVisitGate Opened(long openedMs, ulong id = 1)
    {
        var g = new RetainerVisitGate();
        Assert.False(g.ShouldRead(id, true, openedMs));
        return g;
    }

    /// <summary>A gate on retainer 1 that confirmed <see cref="A"/> at 3000 ms.</summary>
    private static RetainerVisitGate Confirmed()
    {
        var g = Opened(0);
        Assert.True(g.ShouldRead(1, true, 2_000));
        Assert.False(g.Offer(Pl(A, Empty), 2_000));
        Assert.True(g.ShouldRead(1, true, 3_000));
        Assert.True(g.Offer(Pl(A, Empty), 3_000));
        return g;
    }

    [Fact]
    public void Gate_NoRetainerOpenNeverReads()
    {
        var g = new RetainerVisitGate();
        Assert.False(g.ShouldRead(0, true, 0));
        Assert.False(g.ShouldRead(0, true, 100_000));
        Assert.Equal(0UL, g.RetainerId);
        Assert.False(g.Offer(Pl(A, Empty), 100_000));
    }

    [Fact]
    public void Gate_WaitsTwoSecondsAfterARetainerOpens()
    {
        var g = Opened(10_000);
        Assert.Equal(1UL, g.RetainerId);
        Assert.False(g.ShouldRead(1, true, 11_999));
        Assert.True(g.ShouldRead(1, true, 12_000));
    }

    [Fact]
    public void Gate_FirstCaptureNeedsTwoIdenticalReadsASecondApart()
    {
        var g = Opened(0);
        Assert.True(g.ShouldRead(1, true, 2_000));
        Assert.False(g.Offer(Pl(A, Empty), 2_000));
        Assert.False(g.Confirmed);
        Assert.False(g.ShouldRead(1, true, 2_999));
        Assert.True(g.ShouldRead(1, true, 3_000));
        Assert.True(g.Offer(Pl(A, Empty), 3_000));
        Assert.True(g.Confirmed);
    }

    [Fact]
    public void Gate_ChangingContentsRestartTheStabilityCheck()
    {
        var g = Opened(0);
        Assert.True(g.ShouldRead(1, true, 2_000));
        Assert.False(g.Offer(Pl(A, Empty), 2_000));
        Assert.True(g.ShouldRead(1, true, 3_000));
        Assert.False(g.Offer(Pl(B, Empty), 3_000));
        Assert.True(g.ShouldRead(1, true, 4_000));
        Assert.True(g.Offer(Pl(B, Empty), 4_000));
    }

    [Fact]
    public void Gate_MarketListingsArePartOfTheComparison()
    {
        var g = Opened(0);
        Assert.True(g.ShouldRead(1, true, 2_000));
        Assert.False(g.Offer(Pl(A, Empty), 2_000));
        Assert.True(g.ShouldRead(1, true, 3_000));
        Assert.False(g.Offer(Pl(A, B), 3_000));
    }

    [Fact]
    public void Gate_MovingAnItemBetweenBagsIsAChange()
    {
        var g = Opened(0);
        var inBag1 = new Dictionary<RetainerPlace, IReadOnlyDictionary<uint, int>> { [RetainerPlace.Bag1] = A };
        var inBag2 = new Dictionary<RetainerPlace, IReadOnlyDictionary<uint, int>> { [RetainerPlace.Bag2] = A };
        Assert.True(g.ShouldRead(1, true, 2_000));
        Assert.False(g.Offer(inBag1, 2_000));
        Assert.True(g.ShouldRead(1, true, 3_000));
        Assert.False(g.Offer(inBag2, 3_000));
        Assert.False(g.Confirmed);
        Assert.True(g.ShouldRead(1, true, 4_000));
        Assert.True(g.Offer(inBag2, 4_000));
    }

    [Fact]
    public void Gate_AnEmptyPlaceEqualsAMissingPlace()
    {
        var g = Opened(0);
        var withEmpty = new Dictionary<RetainerPlace, IReadOnlyDictionary<uint, int>> { [RetainerPlace.Bag1] = A, [RetainerPlace.Bag2] = Empty };
        var without = new Dictionary<RetainerPlace, IReadOnlyDictionary<uint, int>> { [RetainerPlace.Bag1] = A };
        Assert.True(g.ShouldRead(1, true, 2_000));
        Assert.False(g.Offer(withEmpty, 2_000));
        Assert.True(g.ShouldRead(1, true, 3_000));
        Assert.True(g.Offer(without, 3_000));
    }

    [Fact]
    public void Gate_NotReadyBreaksTheStabilityCheck()
    {
        var g = Opened(0);
        Assert.True(g.ShouldRead(1, true, 2_000));
        Assert.False(g.Offer(Pl(A, Empty), 2_000));
        Assert.False(g.ShouldRead(1, false, 2_500));
        Assert.True(g.ShouldRead(1, true, 3_000));
        Assert.False(g.Offer(Pl(A, Empty), 3_000));
        Assert.True(g.ShouldRead(1, true, 4_000));
        Assert.True(g.Offer(Pl(A, Empty), 4_000));
    }

    [Fact]
    public void Gate_NotReadyAfterConfirmationNeedsTwoMatchingReadsAgain()
    {
        var g = Confirmed();
        Assert.False(g.ShouldRead(1, false, 4_000));
        Assert.False(g.Confirmed);
        Assert.True(g.ShouldRead(1, true, 6_000));
        Assert.False(g.Offer(Pl(A, Empty), 6_000));
        Assert.True(g.ShouldRead(1, true, 7_000));
        Assert.True(g.Offer(Pl(A, Empty), 7_000));
        Assert.True(g.Confirmed);
    }

    [Fact]
    public void Gate_NotReadyAtTheEndOfTheSettleTimeDoesNotRead()
    {
        var g = Opened(0);
        Assert.False(g.ShouldRead(1, false, 2_000));
        Assert.True(g.ShouldRead(1, true, 2_001));
    }

    [Fact]
    public void Gate_SwitchingThroughNoRetainerRestartsTheSettleTime()
    {
        var g = Confirmed();
        Assert.False(g.ShouldRead(0, true, 5_000));
        Assert.False(g.Confirmed);
        Assert.False(g.ShouldRead(2, true, 6_000));
        Assert.Equal(2UL, g.RetainerId);
        Assert.False(g.ShouldRead(2, true, 7_999));
        Assert.True(g.ShouldRead(2, true, 8_000));
    }

    [Fact]
    public void Gate_AConfirmedVisitDoesNotConfirmTheNextRetainer()
    {
        // Retainer 1 confirmed A; retainer 2 opens straight away and still shows A once: that single read must not confirm.
        var g = Confirmed();
        Assert.False(g.ShouldRead(2, true, 4_000));
        Assert.True(g.ShouldRead(2, true, 6_000));
        Assert.False(g.Offer(Pl(A, Empty), 6_000));
        Assert.False(g.Confirmed);
    }

    [Fact]
    public void Gate_AnUnconfirmedReadIsNeverCarriedToTheNextRetainer()
    {
        // Retainer 1 was read once (A) and closed before confirming; retainer 2's first read of A must not pair with it.
        var g = Opened(0);
        Assert.True(g.ShouldRead(1, true, 2_000));
        Assert.False(g.Offer(Pl(A, Empty), 2_000));
        Assert.False(g.ShouldRead(2, true, 2_500));
        Assert.True(g.ShouldRead(2, true, 4_500));
        Assert.False(g.Offer(Pl(A, Empty), 4_500));
        Assert.False(g.Confirmed);
    }

    [Fact]
    public void Gate_ReopeningTheSameRetainerAfterClosingStartsOver()
    {
        var g = Confirmed();
        Assert.False(g.ShouldRead(0, true, 5_000));
        Assert.False(g.ShouldRead(1, true, 6_000));
        Assert.False(g.ShouldRead(1, true, 7_999));
        Assert.True(g.ShouldRead(1, true, 8_000));
        Assert.False(g.Offer(Pl(A, Empty), 8_000));
    }

    [Fact]
    public void Gate_AfterConfirmationRechecksEveryThreeSecondsAndFilesEachRead()
    {
        var g = Confirmed();
        Assert.False(g.ShouldRead(1, true, 5_999));
        Assert.True(g.ShouldRead(1, true, 6_000));
        Assert.True(g.Offer(Pl(B, Empty), 6_000));
        Assert.False(g.ShouldRead(1, true, 8_999));
        Assert.True(g.ShouldRead(1, true, 9_000));
    }
}
