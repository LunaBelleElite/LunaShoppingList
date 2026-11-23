namespace LunaShoppingList.Core.Tests;

public class ShoppingSessionUndoTests
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

    /// <summary>Only the ingot is crafted.</summary>
    private sealed class IngotCrafted : IRecipeLookup
    {
        public RecipeInfo? FindRecipeForItem(uint itemId) => itemId == Ingot ? new RecipeInfo(2, Ingot, 1, Array.Empty<Ingredient>()) : null;
    }

    private static CraftingList List() => new(7, "l", new[] { new CraftItem(1, 2, false, false) });

    private static ShoppingSession Loaded()
    {
        var s = new ShoppingSession(new MaterialsCalculator(new FakeRecipes()), new FakeItems());
        s.LoadFromList(List());
        return s;
    }

    private static uint[] Sorted(IReadOnlySet<uint> set) => set.OrderBy(x => x).ToArray();

    [Fact]
    public void Undo_RestoresExclusionsAndHandTicks_AfterExcludeCraftedOverridingHandTicks()
    {
        var s = Loaded();
        s.SetIncluded(Ingot, true);
        s.SetIncluded(Ore, false);

        Assert.True(s.ApplyBulk("Untick crafted", x => x.ExcludeCrafted(new IngotCrafted(), overrideHandTicks: true)));
        Assert.Equal(new uint[] { Ingot, Ore }, Sorted(s.ExportExclusions()));
        Assert.Empty(s.ExportIncludes());

        Assert.Equal("Untick crafted", s.Undo());
        Assert.Equal(new uint[] { Ore }, Sorted(s.ExportExclusions()));
        Assert.Equal(new uint[] { Ingot }, Sorted(s.ExportIncludes()));
    }

    [Fact]
    public void Undo_RestoresExclusionsAndHandTicks_AfterSetAllIncluded()
    {
        var s = Loaded();
        s.SetIncluded(Ore, false);
        s.SetIncluded(Crystal1, true);

        Assert.True(s.ApplyBulk("Tick all", x => x.SetAllIncluded(true)));
        Assert.Empty(s.ExportExclusions());
        Assert.Equal(new uint[] { 2, 5, 50, 100 }, Sorted(s.ExportIncludes()));

        s.Undo();
        Assert.Equal(new uint[] { Ore }, Sorted(s.ExportExclusions()));
        Assert.Equal(new uint[] { Crystal1 }, Sorted(s.ExportIncludes()));
    }

    [Fact]
    public void Undo_AfterTickAll_RestoresAMixedState()
    {
        var s = Loaded();
        s.SetIncluded(Ore, false);
        s.SetIncluded(Ingot, false);
        s.SetIncluded(Crystal2, true);
        s.ApplyBulk("Untick all", x => x.SetAllIncluded(false));
        s.Undo();
        Assert.Equal(new uint[] { Ingot, Ore }, Sorted(s.ExportExclusions()));
        Assert.Equal(new uint[] { Crystal2 }, Sorted(s.ExportIncludes()));
        Assert.False(s.IsIncluded(Ore));
        Assert.True(s.IsIncluded(Crystal2));
    }

    [Fact]
    public void ApplyBulk_ThatChangesNothing_ReturnsFalse_AndLeavesCanUndoFalse()
    {
        var s = Loaded();
        Assert.False(s.ApplyBulk("Untick owned", x => x.ExcludeOwned(_ => null)));
        Assert.False(s.CanUndo);
        Assert.Null(s.UndoLabel);
        Assert.Null(s.Undo());
    }

    [Fact]
    public void ApplyBulk_NoOpAfterARealChange_KeepsTheOlderSnapshotAndLabel()
    {
        var s = Loaded();
        Assert.True(s.ApplyBulk("first", x => x.ExcludeOwned(id => id == Ore ? 100 : null)));
        Assert.False(s.ApplyBulk("second", x => x.ExcludeOwned(id => id == Ore ? 100 : null)));
        Assert.True(s.CanUndo);
        Assert.Equal("first", s.UndoLabel);
        Assert.Equal("first", s.Undo());
        Assert.Empty(s.ExportExclusions());
    }

    [Fact]
    public void ApplyBulk_ASecondChange_ReplacesTheFirst_OneLevelOnly()
    {
        var s = Loaded();
        s.ApplyBulk("first", x => x.ExcludeOwned(id => id == Ore ? 100 : null));
        s.ApplyBulk("second", x => x.ExcludeOwned(id => id == Ingot ? 100 : null));
        Assert.Equal("second", s.UndoLabel);
        Assert.Equal("second", s.Undo());
        Assert.Equal(new uint[] { Ore }, Sorted(s.ExportExclusions()));
        Assert.Null(s.Undo());
        Assert.Equal(new uint[] { Ore }, Sorted(s.ExportExclusions()));
    }

    [Fact]
    public void Undo_TwiceReturnsNullTheSecondTime()
    {
        var s = Loaded();
        s.ApplyBulk("Tick all", x => x.SetAllIncluded(true));
        Assert.Equal("Tick all", s.Undo());
        Assert.False(s.CanUndo);
        Assert.Null(s.Undo());
    }

    [Fact]
    public void ApplyBulk_ThatOnlyChangesHandTicks_StillArmsUndo()
    {
        var s = Loaded();
        Assert.True(s.ApplyBulk("Tick all", x => x.SetAllIncluded(true)));
        Assert.Empty(s.ExportExclusions());
        Assert.True(s.CanUndo);
        s.Undo();
        Assert.Empty(s.ExportIncludes());
    }

    public static TheoryData<string> Enders() => new()
    {
        "LoadFromList", "LoadFromListWithExclusions", "LoadFromPaste", "ImportExclusions", "ImportIncludes", "SetIncluded", "ClearUndo",
    };

    [Theory]
    [MemberData(nameof(Enders))]
    public void EachClearingMethod_EndsUndo(string which)
    {
        var s = Loaded();
        Assert.True(s.ApplyBulk("Untick owned", x => x.ExcludeOwned(id => id == Ore ? 100 : null)));
        Assert.True(s.CanUndo);

        switch (which)
        {
            case "LoadFromList": s.LoadFromList(List()); break;
            case "LoadFromListWithExclusions": s.LoadFromList(List(), new uint[] { Ingot }); break;
            case "LoadFromPaste": s.LoadFromPaste(new PasteResult(new[] { new Requirement(Ore, 1) }, Array.Empty<string>())); break;
            case "ImportExclusions": s.ImportExclusions(new uint[] { Ingot }); break;
            case "ImportIncludes": s.ImportIncludes(new uint[] { Ingot }); break;
            case "SetIncluded": s.SetIncluded(Crystal1, false); break;
            case "ClearUndo": s.ClearUndo(); break;
            default: throw new ArgumentException(which);
        }

        Assert.False(s.CanUndo);
        Assert.Null(s.UndoLabel);
        Assert.Null(s.Undo());
    }

    [Fact]
    public void ManualTickAfterABulkChange_EndsUndo_AndLeavesTheTickInPlace()
    {
        var s = Loaded();
        s.ApplyBulk("Untick owned", x => x.ExcludeOwned(id => id == Ore ? 100 : null));
        s.SetIncluded(Ingot, false);
        Assert.Null(s.Undo());
        Assert.Equal(new uint[] { Ingot, Ore }, Sorted(s.ExportExclusions()));
    }

    [Fact]
    public void FailedLoad_LeavesTheUndoLevelAlone()
    {
        var s = Loaded();
        s.ApplyBulk("Tick all", x => x.SetAllIncluded(true));
        var bad = new CraftingList(8, "bad", new[] { new CraftItem(404, 1, false, false) });
        Assert.ThrowsAny<Exception>(() => s.LoadFromList(bad, new uint[] { Ore }));
        Assert.Equal("Tick all", s.UndoLabel);
    }

    [Fact]
    public void SnapshotSurvivesLaterChangesToTheSession()
    {
        var s = Loaded();
        s.SetIncluded(Ore, false);
        s.ApplyBulk("Tick all", x => x.SetAllIncluded(true));
        s.Exclusions.ExcludedItemIds.Add(Crystal1); // direct edit must not leak into the snapshot
        s.Undo();
        Assert.Equal(new uint[] { Ore }, Sorted(s.ExportExclusions()));
    }
}
