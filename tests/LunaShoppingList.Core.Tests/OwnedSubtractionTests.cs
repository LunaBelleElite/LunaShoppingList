using LunaShoppingList.Core;

namespace LunaShoppingList.Core.Tests;

public class OwnedSubtractionTests
{
    private static Func<uint, long?> Owned(params (uint Id, long? Count)[] counts)
        => id => counts.Where(c => c.Id == id).Select(c => c.Count).FirstOrDefault();

    [Fact]
    public void PartialCover_ReducesTheQuantity_AndReportsWhatIsHeld()
    {
        var r = OwnedSubtraction.Apply([new(1, 50)], Owned((1, 40)));
        Assert.Equal([new Requirement(1, 10)], r.Remaining);
        Assert.Empty(r.FullyCovered);
        Assert.Equal(40, r.Held[1]);
    }

    [Fact]
    public void ExactCover_DropsTheItem_AndListsItAsCovered()
    {
        var r = OwnedSubtraction.Apply([new(1, 50), new(2, 3)], Owned((1, 50)));
        Assert.Equal([new Requirement(2, 3)], r.Remaining);
        Assert.Equal([new Requirement(1, 50)], r.FullyCovered);
        Assert.Equal(50, r.Held[1]);
    }

    [Fact]
    public void OverCover_DropsTheItem_HeldIsCappedAtTheNeed()
    {
        var r = OwnedSubtraction.Apply([new(1, 5)], Owned((1, 900)));
        Assert.Empty(r.Remaining);
        Assert.Equal([new Requirement(1, 5)], r.FullyCovered);
        Assert.Equal(5, r.Held[1]);
    }

    [Fact]
    public void UnknownCount_LeavesTheQuantityUnchanged()
    {
        var r = OwnedSubtraction.Apply([new(1, 7)], Owned((1, null)));
        Assert.Equal([new Requirement(1, 7)], r.Remaining);
        Assert.Empty(r.FullyCovered);
        Assert.Empty(r.Held);
    }

    [Fact]
    public void ZeroOrNegativeOwned_LeavesTheQuantityUnchanged()
    {
        var r = OwnedSubtraction.Apply([new(1, 7), new(2, 8)], Owned((1, 0), (2, -5)));
        Assert.Equal([new Requirement(1, 7), new Requirement(2, 8)], r.Remaining);
        Assert.Empty(r.Held);
    }

    [Fact]
    public void ZeroOrNegativeNeed_IsIgnored_NeverCoveredNorRemaining()
    {
        var r = OwnedSubtraction.Apply([new(1, 0), new(2, -3)], Owned((1, 10), (2, 10)));
        Assert.Empty(r.Remaining);
        Assert.Empty(r.FullyCovered);
        Assert.Empty(r.Held);
    }

    [Fact]
    public void DuplicateRequirements_AreMergedBeforeSubtracting_AndOrderedById()
    {
        var r = OwnedSubtraction.Apply([new(9, 4), new(3, 2), new(9, 6)], Owned((9, 7), (3, 2)));
        Assert.Equal([new Requirement(9, 3)], r.Remaining);
        Assert.Equal([new Requirement(3, 2)], r.FullyCovered);
        Assert.Equal(7, r.Held[9]);
    }

    [Fact]
    public void MergedNeedBeyondIntMax_ClampsAndNeverGoesNegative()
    {
        var r = OwnedSubtraction.Apply([new(1, int.MaxValue), new(1, 10)], Owned((1, 5)));
        Assert.Equal([new Requirement(1, int.MaxValue - 5)], r.Remaining);
    }

    [Fact]
    public void NeverIncreasesAQuantity()
    {
        Requirement[] input = [new(1, 10), new(2, 10), new(3, 10)];
        var r = OwnedSubtraction.Apply(input, Owned((1, 1), (2, null), (3, 10)));
        Assert.Equal([new Requirement(1, 9), new Requirement(2, 10)], r.Remaining);
        Assert.Equal([new Requirement(3, 10)], r.FullyCovered);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => OwnedSubtraction.Apply(null!, _ => null));
        Assert.Throws<ArgumentNullException>(() => OwnedSubtraction.Apply([], null!));
    }
}
