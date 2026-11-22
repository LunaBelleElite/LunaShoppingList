namespace LunaShoppingList.Core.Tests;

public class ShoppingSessionTickAllTests
{
    private const uint Crystal1 = 2, Crystal2 = 5, Ore = 100, Ingot = 50;

    private sealed class FakeItems : IItemInfo
    {
        public string GetName(uint itemId) => "Item " + itemId;
        public bool IsCrystalLike(uint itemId) => itemId == Crystal1 || itemId == Crystal2;
        public uint? FindByName(string name) => null;
    }

    private sealed class FakeRecipes : IRecipeData
    {
        public RecipeInfo? GetRecipe(uint recipeId) => recipeId == 1
            ? new RecipeInfo(1, 999, 1, new[] { new Ingredient(Ore, 2), new Ingredient(Crystal2, 1), new Ingredient(Crystal1, 3), new Ingredient(Ingot, 1) })
            : null;
    }

    private static ShoppingSession New() => new(new MaterialsCalculator(new FakeRecipes()), new FakeItems());

    private static ShoppingSession Loaded()
    {
        var s = New();
        s.LoadFromList(new CraftingList(7, "l", new[] { new CraftItem(1, 1, false, false) }));
        return s;
    }

    [Fact]
    public void AreAllIncluded_TrueWhenNothingExcluded()
    {
        Assert.True(Loaded().AreAllIncluded);
    }

    [Fact]
    public void AreAllIncluded_FalseWhenOneIsExcluded()
    {
        var s = Loaded();
        s.SetIncluded(Ore, false);
        Assert.False(s.AreAllIncluded);
    }

    [Fact]
    public void AreAllIncluded_FalseWhenEmpty()
    {
        Assert.False(New().AreAllIncluded);
    }

    [Fact]
    public void SetAllIncluded_False_ExcludesEveryRowIncludingCrystals()
    {
        var s = Loaded();
        s.SetAllIncluded(false);
        Assert.All(s.Requirements, r => Assert.False(s.IsIncluded(r.ItemId)));
        Assert.True(s.AreAllCrystalsExcluded);
    }

    [Fact]
    public void SetAllIncluded_True_IncludesEveryRowFromMixed()
    {
        var s = Loaded();
        s.SetIncluded(Ore, false);
        s.SetCrystalsExcluded(true);
        s.SetAllIncluded(true);
        Assert.All(s.Requirements, r => Assert.True(s.IsIncluded(r.ItemId)));
        Assert.False(s.AreAllCrystalsExcluded);
        Assert.Empty(s.ExportExclusions());
    }

    [Fact]
    public void SetAllIncluded_LeavesExclusionsForItemsNotInTheListAlone()
    {
        var s = Loaded();
        s.ImportExclusions(new uint[] { 777 });
        s.SetAllIncluded(true);
        Assert.Equal(new uint[] { 777 }, s.ExportExclusions().ToArray());
        s.SetAllIncluded(false);
        Assert.Contains(777u, s.ExportExclusions());
    }

    [Fact]
    public void SetAllIncluded_EmptyList_DoesNothing()
    {
        var s = New();
        s.SetAllIncluded(false);
        Assert.Empty(s.ExportExclusions());
        s.SetAllIncluded(true);
        Assert.Empty(s.ExportExclusions());
    }

    [Fact]
    public void ToggleAllRule_AllIncluded_ThenUnticksAll_ThenTicksAll()
    {
        var s = Loaded();
        s.SetAllIncluded(!s.AreAllIncluded);
        Assert.All(s.Requirements, r => Assert.False(s.IsIncluded(r.ItemId)));
        s.SetAllIncluded(!s.AreAllIncluded);
        Assert.All(s.Requirements, r => Assert.True(s.IsIncluded(r.ItemId)));
    }

    [Fact]
    public void ToggleAllRule_Mixed_TicksAll()
    {
        var s = Loaded();
        s.SetIncluded(Ingot, false);
        s.SetAllIncluded(!s.AreAllIncluded);
        Assert.All(s.Requirements, r => Assert.True(s.IsIncluded(r.ItemId)));
    }
}
