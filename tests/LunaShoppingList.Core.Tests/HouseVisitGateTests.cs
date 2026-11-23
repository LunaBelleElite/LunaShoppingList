namespace LunaShoppingList.Core.Tests;

public class HouseVisitGateTests
{
    private static Dictionary<uint, int> C(params (uint Item, int Count)[] pairs)
        => pairs.ToDictionary(p => p.Item, p => p.Count);

    private static readonly Dictionary<uint, int> A = C((1, 5));
    private static readonly Dictionary<uint, int> B = C((2, 7));
    private static readonly Dictionary<uint, int> Empty = C();

    private static IReadOnlyDictionary<HousePlace, IReadOnlyDictionary<uint, int>> Pl(Dictionary<uint, int> placed, Dictionary<uint, int> store)
        => new Dictionary<HousePlace, IReadOnlyDictionary<uint, int>> { [HousePlace.Placed] = placed, [HousePlace.Storeroom] = store };

    private static HouseVisitGate Opened(long openedMs, ulong id = 1)
    {
        var g = new HouseVisitGate();
        Assert.False(g.ShouldRead(id, true, openedMs));
        return g;
    }

    private static HouseVisitGate Confirmed()
    {
        var g = Opened(0);
        Assert.True(g.ShouldRead(1, true, 2_000));
        Assert.False(g.Offer(Pl(A, Empty), 2_000));
        Assert.True(g.ShouldRead(1, true, 3_000));
        Assert.True(g.Offer(Pl(A, Empty), 3_000));
        return g;
    }

    [Fact]
    public void Constants_AreTwoOneAndThreeSeconds()
    {
        Assert.Equal(2_000L, HouseVisitGate.SettleMs);
        Assert.Equal(1_000L, HouseVisitGate.StableIntervalMs);
        Assert.Equal(3_000L, HouseVisitGate.RecheckMs);
    }

    [Fact]
    public void NoHouseNeverReads()
    {
        var g = new HouseVisitGate();
        Assert.False(g.ShouldRead(0, true, 0));
        Assert.False(g.ShouldRead(0, true, 100_000));
        Assert.Equal(0UL, g.HouseId);
        Assert.False(g.Offer(Pl(A, Empty), 100_000));
    }

    [Fact]
    public void NoHouseNeverConfirmsEvenOnTwoIdenticalOffers()
    {
        var g = new HouseVisitGate();
        Assert.False(g.Offer(Pl(A, Empty), 1_000));
        Assert.False(g.Offer(Pl(A, Empty), 2_000));
        Assert.False(g.Confirmed);
    }

    [Fact]
    public void WaitsTwoSecondsAfterAHouseOpens()
    {
        var g = Opened(10_000);
        Assert.Equal(1UL, g.HouseId);
        Assert.False(g.ShouldRead(1, true, 11_999));
        Assert.True(g.ShouldRead(1, true, 12_000));
    }

    [Fact]
    public void FirstCaptureNeedsTwoIdenticalReadsASecondApart()
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
    public void ChangingContentsRestartTheStabilityCheck()
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
    public void StoreroomIsPartOfTheComparison()
    {
        var g = Opened(0);
        Assert.True(g.ShouldRead(1, true, 2_000));
        Assert.False(g.Offer(Pl(A, Empty), 2_000));
        Assert.True(g.ShouldRead(1, true, 3_000));
        Assert.False(g.Offer(Pl(A, B), 3_000));
        Assert.False(g.Confirmed);
    }

    [Fact]
    public void MovingAnItemBetweenPlacedAndStoreroomIsAChange()
    {
        var g = Opened(0);
        Assert.True(g.ShouldRead(1, true, 2_000));
        Assert.False(g.Offer(Pl(A, Empty), 2_000));
        Assert.True(g.ShouldRead(1, true, 3_000));
        Assert.False(g.Offer(Pl(Empty, A), 3_000));
        Assert.True(g.ShouldRead(1, true, 4_000));
        Assert.True(g.Offer(Pl(Empty, A), 4_000));
    }

    [Fact]
    public void ConfirmedVisitRechecksEveryThreeSecondsAndAlwaysFiles()
    {
        var g = Confirmed();
        Assert.False(g.ShouldRead(1, true, 5_999));
        Assert.True(g.ShouldRead(1, true, 6_000));
        Assert.True(g.Offer(Pl(B, Empty), 6_000));
        Assert.False(g.ShouldRead(1, true, 8_999));
        Assert.True(g.ShouldRead(1, true, 9_000));
    }

    [Fact]
    public void NotReadyBreaksTheStabilityCheck()
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
    public void NotReadyAtTheEndOfTheSettleTimeDoesNotRead()
    {
        var g = Opened(0);
        Assert.False(g.ShouldRead(1, false, 2_000));
        Assert.True(g.ShouldRead(1, true, 2_001));
    }

    [Fact]
    public void SwitchingThroughNoHouseRestartsTheSettleTime()
    {
        var g = Confirmed();
        Assert.False(g.ShouldRead(0, true, 5_000));
        Assert.False(g.Confirmed);
        Assert.False(g.ShouldRead(2, true, 6_000));
        Assert.Equal(2UL, g.HouseId);
        Assert.False(g.ShouldRead(2, true, 7_999));
        Assert.True(g.ShouldRead(2, true, 8_000));
    }

    [Fact]
    public void LeavingAndReenteringTheSameHouseNeedsANewConfirmation()
    {
        var g = Confirmed();
        Assert.False(g.ShouldRead(0, true, 4_000));
        Assert.False(g.ShouldRead(1, true, 4_500));
        Assert.False(g.Confirmed);
        Assert.True(g.ShouldRead(1, true, 6_500));
        Assert.False(g.Offer(Pl(A, Empty), 6_500));
    }

    [Fact]
    public void AConfirmedVisitDoesNotConfirmTheNextHouse()
    {
        var g = Confirmed();
        Assert.False(g.ShouldRead(2, true, 4_000));
        Assert.True(g.ShouldRead(2, true, 6_000));
        Assert.False(g.Offer(Pl(A, Empty), 6_000));
        Assert.False(g.Confirmed);
    }

    [Fact]
    public void AnUnconfirmedReadIsNeverCarriedToTheNextHouse()
    {
        var g = Opened(0);
        Assert.True(g.ShouldRead(1, true, 2_000));
        Assert.False(g.Offer(Pl(A, Empty), 2_000));
        Assert.False(g.ShouldRead(2, true, 2_500));
        Assert.True(g.ShouldRead(2, true, 4_500));
        Assert.False(g.Offer(Pl(A, Empty), 4_500));
        Assert.True(g.ShouldRead(2, true, 5_500));
        Assert.True(g.Offer(Pl(A, Empty), 5_500));
    }

    [Fact]
    public void AnOfferAfterTheHouseClosedIsRefused()
    {
        var g = Confirmed();
        Assert.False(g.ShouldRead(0, true, 9_000));
        Assert.False(g.Offer(Pl(A, Empty), 9_000));
    }

    [Fact]
    public void OfferRejectsNull()
    {
        var g = Opened(0);
        Assert.Throws<ArgumentNullException>(() => g.Offer(null!, 2_000));
    }
}
