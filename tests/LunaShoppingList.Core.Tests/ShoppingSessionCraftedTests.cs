namespace LunaShoppingList.Core.Tests;

public class ShoppingSessionCraftedTests
{
    // 2 and 5 are crystals; 100 and 101 raw; 50 and 51 have recipes. The list needs 100x2, 5x1, 2x3, 50x1, 51x1.
    private const uint Crystal1 = 2, Crystal2 = 5, Ore = 100, Raw2 = 101, Ingot = 50, Plate = 51;

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

    private sealed class FakeLookup : IRecipeLookup
    {
        private readonly HashSet<uint> _crafted;
        public List<uint> Asked { get; } = new();
        public FakeLookup(params uint[] crafted) => _crafted = crafted.ToHashSet();
        public RecipeInfo? FindRecipeForItem(uint itemId)
        {
            Asked.Add(itemId);
            return _crafted.Contains(itemId) ? new RecipeInfo(itemId + 5000, itemId, 1, new[] { new Ingredient(Ore, 1) }) : null;
        }
    }

    private static ShoppingSession Loaded()
    {
        var s = new ShoppingSession(new MaterialsCalculator(new FakeRecipes()), new FakeItems());
        s.LoadFromList(new CraftingList(7, "l", new[] { new CraftItem(1, 1, false, false) }));
        return s;
    }

    [Fact]
    public void ExcludeCrafted_ButtonPress_OverridesAHandTick()
    {
        var s = Loaded();
        s.SetIncluded(Ingot, true);
        s.ExcludeCrafted(new FakeLookup(Ingot, Plate), overrideHandTicks: true);
        Assert.False(s.IsIncluded(Ingot));
        Assert.DoesNotContain(Ingot, s.ExportIncludes());
    }

    [Fact]
    public void ExcludeCrafted_ButtonPress_StillSkipsCrystals()
    {
        var s = Loaded();
        s.ExcludeCrafted(new FakeLookup(Crystal1, Ingot), overrideHandTicks: true);
        Assert.True(s.IsIncluded(Crystal1));
    }

    [Fact]
    public void ExcludeCrafted_Automatic_StillKeepsAHandTick()
    {
        var s = Loaded();
        s.SetIncluded(Ingot, true);
        s.ExcludeCrafted(new FakeLookup(Ingot));
        Assert.True(s.IsIncluded(Ingot));
    }

    [Fact]
    public void ExcludeCrafted_UntickCraftedIngredients()
    {
        var s = Loaded();
        s.ExcludeCrafted(new FakeLookup(Ingot, Plate));
        Assert.False(s.IsIncluded(Ingot));
        Assert.False(s.IsIncluded(Plate));
    }

    [Fact]
    public void ExcludeCrafted_KeepsRawMaterialsTicked()
    {
        var s = Loaded();
        s.ExcludeCrafted(new FakeLookup(Ingot, Plate));
        Assert.True(s.IsIncluded(Ore));
    }

    [Fact]
    public void ExcludeCrafted_KeepsCrystalsTickedEvenIfALookupReportsARecipe()
    {
        var s = Loaded();
        s.ExcludeCrafted(new FakeLookup(Crystal1, Crystal2, Ingot));
        Assert.True(s.IsIncluded(Crystal1));
        Assert.True(s.IsIncluded(Crystal2));
        Assert.False(s.IsIncluded(Ingot));
    }

    [Fact]
    public void ExcludeCrafted_KeepsSavedExclusions()
    {
        var s = Loaded();
        s.ImportExclusions(new uint[] { Ore, 777 });
        s.ExcludeCrafted(new FakeLookup(Ingot));
        Assert.Equal(new uint[] { Ingot, Ore, 777 }, s.ExportExclusions().OrderBy(x => x).ToArray());
    }

    [Fact]
    public void ExcludeCrafted_NeverReincludesAnItemSavedAsExcluded()
    {
        var s = Loaded();
        s.ImportExclusions(new uint[] { Ore });
        s.ExcludeCrafted(new FakeLookup());
        Assert.False(s.IsIncluded(Ore));
    }

    [Fact]
    public void ExcludeCrafted_NothingCrafted_ChangesNothing()
    {
        var s = Loaded();
        s.ExcludeCrafted(new FakeLookup());
        Assert.Empty(s.ExportExclusions());
    }

    [Fact]
    public void ExcludeCrafted_EmptyList_ChangesNothingAndDoesNotAsk()
    {
        var s = new ShoppingSession(new MaterialsCalculator(new FakeRecipes()), new FakeItems());
        var lookup = new FakeLookup(Ingot);
        s.ExcludeCrafted(lookup);
        Assert.Empty(s.ExportExclusions());
        Assert.Empty(lookup.Asked);
    }

    [Fact]
    public void ExcludeCrafted_OnlyAsksAboutTheListsOwnItems()
    {
        var s = Loaded();
        var lookup = new FakeLookup(Ingot, 888);
        s.ExcludeCrafted(lookup);
        Assert.DoesNotContain(888u, s.ExportExclusions());
        Assert.DoesNotContain(Raw2, lookup.Asked);
    }

    [Fact]
    public void ExcludeCrafted_NullLookup_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Loaded().ExcludeCrafted(null!));
    }
}
