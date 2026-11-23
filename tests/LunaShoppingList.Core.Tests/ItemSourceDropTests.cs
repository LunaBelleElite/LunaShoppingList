namespace LunaShoppingList.Core.Tests;

public class ItemSourceDropTests
{
    [Fact]
    public void DropText_IsTheLiteralLabel()
    {
        Assert.Equal("Monster drop", ItemSourceSummary.DropText);
    }

    [Fact]
    public void NotADrop_IsUnchanged()
    {
        Assert.Equal("-", ItemSourceSummary.WithDrop("-", false));
        Assert.Equal("...", ItemSourceSummary.WithDrop("...", false));
        Assert.Equal("1,200 gil", ItemSourceSummary.WithDrop("1,200 gil", false));
        Assert.Equal("Craftable", ItemSourceSummary.WithDrop("Craftable", false));
    }

    [Fact]
    public void DropWithNoVendor_ReadsMonsterDrop()
    {
        Assert.Equal("Monster drop", ItemSourceSummary.WithDrop("-", true));
    }

    [Fact]
    public void DropWhileVendorsAreStillLoading_IsUnchanged()
    {
        Assert.Equal("...", ItemSourceSummary.WithDrop("...", true));
    }

    [Fact]
    public void DropWithAVendor_AppendsMonsterDrop()
    {
        Assert.Equal("1,200 gil, monster drop", ItemSourceSummary.WithDrop("1,200 gil", true));
        Assert.Equal("1,200 gil +2, monster drop", ItemSourceSummary.WithDrop("1,200 gil +2", true));
    }

    [Fact]
    public void DropWithCraftableText_Composes()
    {
        Assert.Equal("Craftable, monster drop", ItemSourceSummary.WithDrop(ItemSourceSummary.WithCraftable("-", true), true));
        Assert.Equal("1,200 gil (craftable), monster drop", ItemSourceSummary.WithDrop(ItemSourceSummary.WithCraftable("1,200 gil", true), true));
    }
}
