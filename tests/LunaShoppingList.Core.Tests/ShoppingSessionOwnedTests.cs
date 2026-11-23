namespace LunaShoppingList.Core.Tests;

public class ShoppingSessionOwnedTests
{
    // 2 and 5 are crystals; 100 and 101 raw; 50 and 51 have recipes. The list needs 100x2, 5x1, 2x3, 50x1, 51x1.
    private const uint Crystal1 = 2, Crystal2 = 5, Ore = 100, Ingot = 50, Plate = 51;

    private sealed class FakeItems : IItemInfo
    {
        public string GetName(uint itemId) => "Item " + itemId;
        public bool IsCrystalLike(uint itemId) => itemId == Crystal1 || itemId == Crystal2;
        public uint? FindByName(string name) => null;
    }

    private sealed class FakeRecipes : IRecipeData
    {
        public RecipeInfo? GetRecipe(uint recipeId) => recipeId == 1
            ? new RecipeInfo(1, 999, 1, new[]
            {
                new Ingredient(Ore, 2), new Ingredient(Crystal2, 1), new Ingredient(Crystal1, 3),
                new Ingredient(Ingot, 1), new Ingredient(Plate, 1),
            })
            : null;
    }

    private static ShoppingSession Loaded()
    {
        var s = new ShoppingSession(new MaterialsCalculator(new FakeRecipes()), new FakeItems());
        s.LoadFromList(new CraftingList(7, "l", new[] { new CraftItem(1, 1, false, false) }));
        return s;
    }

    private static Func<uint, long?> Owns(params (uint Id, long? Count)[] counts)
    {
        var map = counts.ToDictionary(c => c.Id, c => c.Count);
        return id => map.TryGetValue(id, out var n) ? n : null;
    }

    [Fact]
    public void ExactlyEnough_IsExcluded()
    {
        var s = Loaded();
        s.ExcludeOwned(Owns((Ore, 2)));
        Assert.False(s.IsIncluded(Ore));
    }

    [Fact]
    public void MoreThanEnough_IsExcluded()
    {
        var s = Loaded();
        s.ExcludeOwned(Owns((Ore, 50)));
        Assert.False(s.IsIncluded(Ore));
    }

    [Fact]
    public void OneShort_StaysTicked()
    {
        var s = Loaded();
        s.ExcludeOwned(Owns((Ore, 1)));
        Assert.True(s.IsIncluded(Ore));
    }

    [Fact]
    public void UnknownCount_StaysTicked()
    {
        var s = Loaded();
        s.ExcludeOwned(Owns());
        Assert.True(s.IsIncluded(Ore));
        Assert.True(s.IsIncluded(Ingot));
    }

    [Fact]
    public void ZeroOwned_StaysTicked()
    {
        var s = Loaded();
        s.ExcludeOwned(Owns((Ore, 0)));
        Assert.True(s.IsIncluded(Ore));
    }

    [Fact]
    public void CrystalsAreJudgedByTheSameRule()
    {
        var s = Loaded();
        s.ExcludeOwned(Owns((Crystal1, 3), (Crystal2, 0)));
        Assert.False(s.IsIncluded(Crystal1));
        Assert.True(s.IsIncluded(Crystal2));
    }

    [Fact]
    public void OnlyTheCoveredItemsAreExcluded()
    {
        var s = Loaded();
        s.ExcludeOwned(Owns((Ore, 2), (Ingot, 0)));
        Assert.False(s.IsIncluded(Ore));
        Assert.True(s.IsIncluded(Ingot));
        Assert.True(s.IsIncluded(Plate));
    }

    [Fact]
    public void NeverReincludesAnItemSavedAsExcluded()
    {
        var s = Loaded();
        s.ImportExclusions(new uint[] { Ingot });
        s.ExcludeOwned(Owns((Ingot, 0)));
        Assert.False(s.IsIncluded(Ingot));
    }

    [Fact]
    public void AnExplicitTick_IsOverriddenByTheButton()
    {
        var s = Loaded();
        s.SetIncluded(Ore, true);
        s.ExcludeOwned(Owns((Ore, 2)));
        Assert.False(s.IsIncluded(Ore));
        Assert.DoesNotContain(Ore, s.ExportIncludes());
    }

    [Fact]
    public void AnExplicitTick_OnAnItemNotCovered_IsKept()
    {
        var s = Loaded();
        s.SetIncluded(Ore, true);
        s.ExcludeOwned(Owns((Ore, 1)));
        Assert.Contains(Ore, s.ExportIncludes());
    }
}
