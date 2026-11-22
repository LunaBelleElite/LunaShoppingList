namespace LunaShoppingList.Core.Tests;

/// <summary>The user's explicit ticks are remembered separately, so "Exclude crafted items" never overrides them (2026-11-22).</summary>
public class ShoppingSessionIncludesTests
{
    // 2 is a crystal; 100 raw; 50 and 51 have recipes. The list needs 100x2, 2x3, 50x1, 51x1.
    private const uint Crystal = 2, Ore = 100, Ingot = 50, Plate = 51;

    private sealed class FakeItems : IItemInfo
    {
        public string GetName(uint itemId) => "Item " + itemId;
        public bool IsCrystalLike(uint itemId) => itemId == Crystal;
        public uint? FindByName(string name) => null;
    }

    private sealed class FakeRecipes : IRecipeData
    {
        public RecipeInfo? GetRecipe(uint recipeId) => recipeId == 1
            ? new RecipeInfo(1, 999, 1, new[]
            {
                new Ingredient(Ore, 2), new Ingredient(Crystal, 3), new Ingredient(Ingot, 1), new Ingredient(Plate, 1),
            })
            : null;
    }

    private sealed class FakeLookup(params uint[] crafted) : IRecipeLookup
    {
        public RecipeInfo? FindRecipeForItem(uint itemId)
            => crafted.Contains(itemId) ? new RecipeInfo(itemId + 5000, itemId, 1, new[] { new Ingredient(Ore, 1) }) : null;
    }

    private static readonly CraftingList TheList = new(7, "l", new[] { new CraftItem(1, 1, false, false) });

    private static ShoppingSession NewSession() => new(new MaterialsCalculator(new FakeRecipes()), new FakeItems());

    private static ShoppingSession Loaded()
    {
        var s = NewSession();
        s.LoadFromList(TheList);
        return s;
    }

    [Fact]
    public void ExcludeCrafted_LeavesAnExplicitlyTickedCraftedItemIncluded()
    {
        var s = Loaded();
        s.ImportExclusions(new uint[] { Ingot, Plate }); // saved: both unticked
        s.SetIncluded(Ingot, true);                     // user re-ticks the ingot
        var inc = s.ExportIncludes();
        var exc = s.ExportExclusions();

        var s2 = NewSession();
        s2.LoadFromList(TheList, exc);
        s2.ImportIncludes(inc);
        s2.ExcludeCrafted(new FakeLookup(Ingot, Plate));
        Assert.True(s2.IsIncluded(Ingot));
        Assert.False(s2.IsIncluded(Plate));
    }

    [Fact]
    public void ExcludeCrafted_StillExcludesAnItemNeverTicked()
    {
        var s = Loaded();
        s.SetIncluded(Ingot, true);
        s.ExcludeCrafted(new FakeLookup(Ingot, Plate));
        Assert.True(s.IsIncluded(Ingot));
        Assert.False(s.IsIncluded(Plate));
    }

    [Fact]
    public void ExcludeCrafted_ExcludesAgainAfterTheUserUnticksIt()
    {
        var s = Loaded();
        s.SetIncluded(Ingot, true);
        s.SetIncluded(Ingot, false);
        Assert.DoesNotContain(Ingot, s.ExportIncludes());
        s.SetIncluded(Ingot, true);
        s.SetIncluded(Ingot, false);
        s.SetIncluded(Ingot, true);
        s.SetIncluded(Ingot, false);
        s.ExcludeCrafted(new FakeLookup(Ingot));
        Assert.False(s.IsIncluded(Ingot));
    }

    [Fact]
    public void CheckAll_RemembersEveryRow_AndSurvivesAReload()
    {
        var s = Loaded();
        s.ExcludeCrafted(new FakeLookup(Ingot, Plate));
        s.SetAllIncluded(true);
        Assert.Equal(new uint[] { Crystal, Ingot, Plate, Ore }, s.ExportIncludes().OrderBy(x => x).ToArray());

        var s2 = NewSession();
        s2.LoadFromList(TheList, s.ExportExclusions());
        s2.ImportIncludes(s.ExportIncludes());
        s2.ExcludeCrafted(new FakeLookup(Ingot, Plate));
        Assert.True(s2.AreAllIncluded);
    }

    [Fact]
    public void UncheckAll_ClearsTheExplicitTicks()
    {
        var s = Loaded();
        s.SetAllIncluded(true);
        s.ImportIncludes(s.ExportIncludes().Append(777u));
        s.SetAllIncluded(false);
        Assert.Empty(s.ExportIncludes());
        Assert.False(s.IsIncluded(Ingot));
    }

    [Fact]
    public void ExportIncludes_IsACopy()
    {
        var s = Loaded();
        s.SetIncluded(Ingot, true);
        var copy = s.ExportIncludes();
        s.SetIncluded(Ingot, false);
        Assert.Contains(Ingot, copy);
        Assert.Empty(s.ExportIncludes());
    }

    [Fact]
    public void ImportIncludes_ReplacesTheSet_AndToleratesDuplicatesAndNull()
    {
        var s = Loaded();
        s.SetIncluded(Ore, true);
        s.ImportIncludes(new uint[] { Ingot, Ingot, Plate });
        Assert.Equal(new uint[] { Ingot, Plate }, s.ExportIncludes().OrderBy(x => x).ToArray());
        s.ImportIncludes(null!);
        Assert.Empty(s.ExportIncludes());
    }

    [Fact]
    public void Excluding_AllCrystals_DropsThemFromTheExplicitTicks()
    {
        var s = Loaded();
        s.SetIncluded(Crystal, true);
        s.SetCrystalsExcluded(true);
        Assert.DoesNotContain(Crystal, s.ExportIncludes());
    }

    [Fact]
    public void LoadFromPaste_ForgetsExplicitTicks()
    {
        var s = Loaded();
        s.SetIncluded(Ingot, true);
        s.LoadFromPaste(new PasteResult(new[] { new Requirement(Ingot, 1) }, Array.Empty<string>()));
        Assert.Empty(s.ExportIncludes());
    }
}
