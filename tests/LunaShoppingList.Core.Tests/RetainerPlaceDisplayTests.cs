namespace LunaShoppingList.Core.Tests;

public class RetainerPlaceDisplayTests
{
    private static string Show(params (RetainerPlace Place, int Count)[] entries)
        => string.Join(", ", RetainerPlaceDisplay.Merge(entries.ToDictionary(e => e.Place, e => e.Count)).Select(p => $"{p.Label} x{p.Count}"));

    [Fact]
    public void AllInventoryPagesMergeIntoOneInventoryEntry()
        => Assert.Equal("Inventory x12", Show((RetainerPlace.Bag1, 4), (RetainerPlace.Bag2, 3), (RetainerPlace.Bag6, 5)));

    [Fact]
    public void OrderIsInventoryThenCrystalsThenMarketListings()
        => Assert.Equal("Inventory x12, Crystals x5, Market listings x3",
            Show((RetainerPlace.Market, 3), (RetainerPlace.Crystals, 5), (RetainerPlace.Bag7, 12)));

    [Fact]
    public void PlacesWithNoItemsAreOmitted()
        => Assert.Equal("Crystals x5", Show((RetainerPlace.Bag1, 0), (RetainerPlace.Crystals, 5)));

    [Fact]
    public void NothingGivesAnEmptyList()
        => Assert.Empty(RetainerPlaceDisplay.Merge(new Dictionary<RetainerPlace, int>()));

    [Fact]
    public void InventorySumsDoNotOverflow()
    {
        var merged = Assert.Single(RetainerPlaceDisplay.Merge(new Dictionary<RetainerPlace, int>
        {
            [RetainerPlace.Bag1] = int.MaxValue,
            [RetainerPlace.Bag2] = int.MaxValue,
        }));
        Assert.Equal(2L * int.MaxValue, merged.Count);
    }
}
