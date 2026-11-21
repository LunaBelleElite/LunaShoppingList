using LunaShoppingList.Core;

namespace LunaShoppingList.Core.Tests;

public class WorldOfferTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static PriceStep S(int price, int qty, int ageDays = 0) => new(price, qty, false, T0.AddDays(-ageDays));

    private static WorldOffer W(params PriceStep[] steps) => new(1, "Alpha", steps);

    // 3 @ 10, 2 @ 25, 4 @ 40 => 9 units
    private static WorldOffer Ladder() => W(S(10, 3, 1), S(25, 2, 5), S(40, 4, 2));

    [Theory]
    [InlineData(1, 10)]
    [InlineData(3, 30)]
    [InlineData(4, 55)]
    [InlineData(5, 80)]
    [InlineData(6, 120)]
    [InlineData(9, 240)]
    public void CostFor_BuysCheapestStepsFirst(int qty, long cost) => Assert.Equal(cost, Ladder().CostFor(qty));

    [Theory]
    [InlineData(10)]
    [InlineData(1000)]
    [InlineData(int.MaxValue)]
    public void CostFor_ClampsAboveUnitsForSale(int qty) => Assert.Equal(240, Ladder().CostFor(qty));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void CostFor_ZeroOrNegativeCostsNothing(int qty) => Assert.Equal(0, Ladder().CostFor(qty));

    [Fact]
    public void CostFor_EmptyLadderCostsNothing() => Assert.Equal(0, W().CostFor(5));

    [Fact]
    public void CostFor_UsesLongArithmetic()
        => Assert.Equal(2_000_000_000L * 3, W(S(2_000_000_000, 3)).CostFor(3));

    [Fact]
    public void UnitsForSale_SumsSteps() => Assert.Equal(9, Ladder().UnitsForSale);

    [Fact]
    public void UnitsForSale_EmptyIsZero() => Assert.Equal(0, W().UnitsForSale);

    [Fact]
    public void UnitsForSale_ClampsToIntMaxValue()
        => Assert.Equal(int.MaxValue, W(S(1, int.MaxValue), S(2, 5)).UnitsForSale);

    [Fact]
    public void CostFor_WithHugeStepsDoesNotOverflow()
        => Assert.Equal((long)int.MaxValue * 7, W(S(7, int.MaxValue), S(8, 5)).CostFor(int.MaxValue));

    [Fact]
    public void MinUnitPrice_IsFirstStep() => Assert.Equal(10, Ladder().MinUnitPrice);

    [Fact]
    public void MinUnitPrice_EmptyIsZero() => Assert.Equal(0, W().MinUnitPrice);

    [Theory]
    [InlineData(1, 10)]
    [InlineData(3, 10)]
    [InlineData(4, 25)]
    [InlineData(5, 25)]
    [InlineData(6, 40)]
    [InlineData(9, 40)]
    [InlineData(100, 40)]
    [InlineData(0, 0)]
    [InlineData(-4, 0)]
    public void MaxUnitPriceFor_IsPriceOfLastStepTouched(int qty, int price) => Assert.Equal(price, Ladder().MaxUnitPriceFor(qty));

    [Fact]
    public void MaxUnitPriceFor_EmptyIsZero() => Assert.Equal(0, W().MaxUnitPriceFor(3));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 5)]
    [InlineData(6, 5)]
    [InlineData(9, 5)]
    [InlineData(100, 5)]
    public void OldestReviewFor_IsOldestAmongStepsTouched(int qty, int oldestAgeDays)
        => Assert.Equal(T0.AddDays(-oldestAgeDays), Ladder().OldestReviewFor(qty));

    [Fact]
    public void OldestReviewFor_PicksOldestEvenWhenNotLastTouched()
        => Assert.Equal(T0.AddDays(-9), W(S(1, 1, 9), S(2, 1, 1), S(3, 1, 2)).OldestReviewFor(3));

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void OldestReviewFor_NothingBoughtIsMaxValue(int qty)
        => Assert.Equal(DateTimeOffset.MaxValue, Ladder().OldestReviewFor(qty));

    [Fact]
    public void OldestReviewFor_EmptyIsMaxValue() => Assert.Equal(DateTimeOffset.MaxValue, W().OldestReviewFor(2));

    [Fact]
    public void Equality_IgnoresWhetherUnitsForSaleWasAlreadyRead()
    {
        IReadOnlyList<PriceStep> steps = [S(10, 3), S(25, 2)];
        var read = new WorldOffer(1, "Alpha", steps);
        var fresh = new WorldOffer(1, "Alpha", steps);
        _ = read.UnitsForSale;
        Assert.Equal(fresh, read);
        Assert.True(fresh == read);
        Assert.Equal(fresh.GetHashCode(), read.GetHashCode());
        Assert.NotEqual(read, new WorldOffer(2, "Alpha", steps));
        Assert.NotEqual(read, new WorldOffer(1, "Beta", steps));
        Assert.NotEqual(read, new WorldOffer(1, "Alpha", [S(10, 3), S(25, 2)]));
    }

    [Fact]
    public void WithReplacedSteps_RecomputesTotalsInsteadOfCarryingTheCachedOne()
    {
        var original = Ladder();
        Assert.Equal(9, original.UnitsForSale);
        var copy = original with { Steps = [S(5, 2)] };
        Assert.Equal(2, copy.UnitsForSale);
        Assert.Equal(10, copy.CostFor(100));
        Assert.Equal(9, original.UnitsForSale);
        Assert.Equal(240, original.CostFor(100));
    }

    [Fact]
    public void WithOtherMemberChanged_KeepsTotalsAndRepeatedReadsAgree()
    {
        var original = Ladder();
        var renamed = original with { WorldName = "Beta" };
        Assert.Equal(9, renamed.UnitsForSale);
        Assert.Equal(9, renamed.UnitsForSale);
        Assert.Equal(original.CostFor(5), renamed.CostFor(5));
    }
}
