namespace LunaShoppingList.Core.Tests;

public class ShoppingSessionTests
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
    private static CraftingList List() => new(7, "l", new[] { new CraftItem(1, 2, false, false) });
    private static ShoppingSession Loaded() { var s = New(); s.LoadFromList(List()); return s; }

    [Fact]
    public void DefaultsAllIncluded()
    {
        var s = Loaded();
        Assert.All(s.Requirements, r => Assert.True(s.IsIncluded(r.ItemId)));
        Assert.Empty(s.ExportExclusions());
    }

    [Fact]
    public void RequirementsOrderedByItemId_AndQuantitiesFromCalculator()
    {
        var s = Loaded();
        Assert.Equal(new[] { (2u, 6), (5u, 2), (50u, 2), (100u, 4) }, s.Requirements.Select(r => (r.ItemId, r.Quantity)).ToArray());
    }

    [Fact]
    public void SetIncluded_ExcludesOnlyThatItem_AndCanReinclude()
    {
        var s = Loaded();
        s.SetIncluded(Ore, false);
        Assert.False(s.IsIncluded(Ore));
        Assert.True(s.IsIncluded(Ingot));
        Assert.True(s.IsIncluded(Crystal1));
        s.SetIncluded(Ore, true);
        Assert.True(s.IsIncluded(Ore));
    }

    [Fact]
    public void CrystalToggle_AffectsOnlyCrystalLikeItems()
    {
        var s = Loaded();
        s.SetCrystalsExcluded(true);
        Assert.False(s.IsIncluded(Crystal1));
        Assert.False(s.IsIncluded(Crystal2));
        Assert.True(s.IsIncluded(Ore));
        Assert.True(s.IsIncluded(Ingot));
        s.SetCrystalsExcluded(false);
        Assert.True(s.IsIncluded(Crystal1));
        Assert.True(s.IsIncluded(Crystal2));
    }

    [Fact]
    public void CrystalToggle_LeavesPreExcludedNonCrystalsAlone()
    {
        var s = Loaded();
        s.SetIncluded(Ore, false);
        s.SetCrystalsExcluded(true);
        s.SetCrystalsExcluded(false);
        Assert.False(s.IsIncluded(Ore));
    }

    [Fact]
    public void CrystalLikeItemIds_ListsOnlyCrystalsInRequirements()
        => Assert.Equal(new uint[] { Crystal1, Crystal2 }, Loaded().CrystalLikeItemIds);

    [Fact]
    public void AreAllCrystalsExcluded_Cases()
    {
        var s = Loaded();
        Assert.False(s.AreAllCrystalsExcluded);
        s.SetIncluded(Crystal1, false);
        Assert.False(s.AreAllCrystalsExcluded);
        s.SetIncluded(Crystal2, false);
        Assert.True(s.AreAllCrystalsExcluded);
        s.SetIncluded(Crystal1, true);
        Assert.False(s.AreAllCrystalsExcluded);
    }

    [Fact]
    public void AreAllCrystalsExcluded_NoCrystals_IsFalse()
    {
        var s = New();
        s.LoadFromPaste(new PasteResult(new[] { new Requirement(Ore, 1) }, Array.Empty<string>()));
        Assert.False(s.AreAllCrystalsExcluded);
        Assert.False(New().AreAllCrystalsExcluded);
    }

    [Fact]
    public void AreAllCrystalsExcluded_IgnoresExcludedIdsNotInRequirements()
    {
        var s = Loaded();
        s.ImportExclusions(new uint[] { Crystal1, 777 });
        Assert.False(s.AreAllCrystalsExcluded);
    }

    [Fact]
    public void ExportImport_RoundTrip()
    {
        var a = Loaded();
        a.SetIncluded(Ore, false);
        a.SetIncluded(Crystal1, false);
        var b = Loaded();
        b.ImportExclusions(a.ExportExclusions());
        Assert.False(b.IsIncluded(Ore));
        Assert.False(b.IsIncluded(Crystal1));
        Assert.True(b.IsIncluded(Ingot));
        Assert.Equal(new uint[] { Crystal1, Ore }, b.ExportExclusions().OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Import_KeepsIdsNotInRequirements()
    {
        var s = Loaded();
        s.ImportExclusions(new uint[] { 12345 });
        Assert.Contains(12345u, s.ExportExclusions());
        Assert.False(s.IsIncluded(12345));
    }

    [Fact]
    public void Import_ReplacesPreviousExclusions()
    {
        var s = Loaded();
        s.SetIncluded(Ore, false);
        s.ImportExclusions(new uint[] { Ingot });
        Assert.True(s.IsIncluded(Ore));
        Assert.False(s.IsIncluded(Ingot));
    }

    [Fact]
    public void Export_ReturnsACopy()
    {
        var s = Loaded();
        s.SetIncluded(Ore, false);
        var snapshot = s.ExportExclusions();
        s.SetIncluded(Ore, true);
        Assert.Contains(Ore, snapshot);
    }

    [Fact]
    public void Import_OwnExclusionSet_IsNotWipedByClear()
    {
        var s = Loaded();
        s.SetIncluded(Ore, false);
        s.ImportExclusions(s.Exclusions.ExcludedItemIds);
        Assert.False(s.IsIncluded(Ore));
    }

    [Fact]
    public void LoadFromList_AppliesPreviouslyImportedExclusions()
    {
        var s = New();
        s.ImportExclusions(new uint[] { Ore });
        s.LoadFromList(List());
        Assert.False(s.IsIncluded(Ore));
        Assert.True(s.IsIncluded(Ingot));
        Assert.Equal(4, s.Requirements.Count);
    }

    [Fact]
    public void LoadFromPaste_UsesPasteRequirementsOrderedById_AndClearsExclusions()
    {
        var s = Loaded();
        s.SetIncluded(Ore, false);
        s.LoadFromPaste(new PasteResult(new[] { new Requirement(9, 3), new Requirement(4, 1) }, Array.Empty<string>()));
        Assert.Equal(new[] { (4u, 1), (9u, 3) }, s.Requirements.Select(r => (r.ItemId, r.Quantity)).ToArray());
        Assert.True(s.IsIncluded(Ore));
        Assert.Empty(s.ExportExclusions());
    }

    [Fact]
    public void LoadFromList_ReplacesPreviousRequirements()
    {
        var s = New();
        s.LoadFromPaste(new PasteResult(new[] { new Requirement(9, 3) }, Array.Empty<string>()));
        s.LoadFromList(List());
        Assert.DoesNotContain(s.Requirements, r => r.ItemId == 9);
    }

    private static readonly (uint, int)[] LoadedRequirements = { (2u, 6), (5u, 2), (50u, 2), (100u, 4) };

    private static void AssertUnchangedAfterFailedLoad(ShoppingSession s)
    {
        Assert.Equal(LoadedRequirements, s.Requirements.Select(r => (r.ItemId, r.Quantity)).ToArray());
        Assert.Equal(new uint[] { Ore }, s.ExportExclusions().OrderBy(x => x).ToArray());
    }

    [Fact]
    public void LoadFromListWithExclusions_UnknownRecipe_LeavesRequirementsAndExclusionsUnchanged()
    {
        var s = Loaded();
        s.SetIncluded(Ore, false);
        var bad = new CraftingList(8, "bad", new[] { new CraftItem(1, 1, false, false), new CraftItem(404, 1, false, false) });
        Assert.Throws<InvalidOperationException>(() => s.LoadFromList(bad, new uint[] { Ingot, Crystal1 }));
        AssertUnchangedAfterFailedLoad(s);
    }

    [Fact]
    public void LoadFromListWithExclusions_Overflow_LeavesRequirementsAndExclusionsUnchanged()
    {
        var s = Loaded();
        s.SetIncluded(Ore, false);
        var huge = new CraftingList(9, "huge", new[] { new CraftItem(1, int.MaxValue, false, false) });
        Assert.Throws<OverflowException>(() => s.LoadFromList(huge, new uint[] { Ingot, Crystal1 }));
        AssertUnchangedAfterFailedLoad(s);
    }

    [Fact]
    public void LoadFromListWithExclusions_Success_ReplacesRequirementsAndExclusions()
    {
        var s = New();
        s.LoadFromPaste(new PasteResult(new[] { new Requirement(9, 3) }, Array.Empty<string>()));
        s.SetIncluded(Ore, false);
        s.LoadFromList(List(), new uint[] { Ingot, 777 });
        Assert.Equal(LoadedRequirements, s.Requirements.Select(r => (r.ItemId, r.Quantity)).ToArray());
        Assert.Equal(new uint[] { Ingot, 777 }, s.ExportExclusions().OrderBy(x => x).ToArray());
        Assert.True(s.IsIncluded(Ore));
    }

    [Fact]
    public void LoadFromListWithExclusions_NullArguments_Throw_AndLeaveStateUnchanged()
    {
        var s = Loaded();
        s.SetIncluded(Ore, false);
        Assert.Throws<ArgumentNullException>(() => s.LoadFromList(null!, new uint[] { Ingot }));
        Assert.Throws<ArgumentNullException>(() => s.LoadFromList(List(), null!));
        AssertUnchangedAfterFailedLoad(s);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        var s = New();
        Assert.Throws<ArgumentNullException>(() => new ShoppingSession(null!, new FakeItems()));
        Assert.Throws<ArgumentNullException>(() => new ShoppingSession(new MaterialsCalculator(new FakeRecipes()), null!));
        Assert.Throws<ArgumentNullException>(() => s.LoadFromList(null!));
        Assert.Throws<ArgumentNullException>(() => s.LoadFromPaste(null!));
        Assert.Throws<ArgumentNullException>(() => s.ImportExclusions(null!));
    }
}
