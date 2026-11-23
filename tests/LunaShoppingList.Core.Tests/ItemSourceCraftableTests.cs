namespace LunaShoppingList.Core.Tests;

public class ItemSourceCraftableTests
{
    [Fact]
    public void NotCraftable_IsUnchanged()
    {
        Assert.Equal("-", ItemSourceSummary.WithCraftable("-", false));
        Assert.Equal("...", ItemSourceSummary.WithCraftable("...", false));
        Assert.Equal("1,200 gil", ItemSourceSummary.WithCraftable("1,200 gil", false));
    }

    [Fact]
    public void CraftableWithNoVendor_ReadsCraftable()
    {
        Assert.Equal("Craftable", ItemSourceSummary.WithCraftable("-", true));
    }

    [Fact]
    public void CraftableWhileVendorsAreStillLoading_ReadsCraftable()
    {
        Assert.Equal("Craftable", ItemSourceSummary.WithCraftable("...", true));
    }

    [Fact]
    public void CraftableWithAVendor_KeepsTheVendorAndSaysCraftable()
    {
        Assert.Equal("1,200 gil (craftable)", ItemSourceSummary.WithCraftable("1,200 gil", true));
    }
}
