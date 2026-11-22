namespace LunaShoppingList.Core.Tests;

/// <summary>
/// Regression tests for the 2026-11-22 five-reviewer bug sweep (SC-1..SC-4, SE-1, SM-2, and the explicit-ticks rule).
/// Each test names the defect it pins.
/// </summary>
public class BugSweepTests
{
    private sealed class FakeLookup : IRecipeLookup
    {
        private readonly Dictionary<uint, RecipeInfo> _map = new();
        public FakeLookup Add(uint result, int yield, params (uint item, int amount)[] ing)
        {
            _map[result] = new RecipeInfo(result + 5000, result, yield, ing.Select(i => new Ingredient(i.item, i.amount)).ToList());
            return this;
        }
        public RecipeInfo? FindRecipeForItem(uint itemId) => _map.TryGetValue(itemId, out var r) ? r : null;
    }

    private static Requirement R(uint id, int q) => new(id, q);

    private static (uint, int)[] Rows(IEnumerable<Requirement> r) => r.Select(x => (x.ItemId, x.Quantity)).ToArray();

    private static CustomList ListWith(params (uint id, int q)[] goals)
    {
        var l = new CustomList { Id = "x", Name = "x" };
        foreach (var (id, q) in goals) l.Goals.Add(new CustomListItem { ItemId = id, Quantity = q });
        return l;
    }

    /// <summary>Goal 80000 needs 990700 x2 and 990701 x1 (both raw); items 1..5000 form a chain of craftables, 1 on top.</summary>
    private static FakeLookup CapRecipes()
    {
        var f = new FakeLookup().Add(80000, 1, (990700, 2), (990701, 1));
        for (uint i = 1; i <= RecipeExpander.MaxCraftableItems; i++)
            f.Add(i, 1, i == RecipeExpander.MaxCraftableItems ? (6_000_000u, 1) : (i + 1, 1));
        return f;
    }

    // ---- SC-1 ----

    [Fact]
    public void Sc1_PruneExclusions_KeepsEveryExclusion_WhenGoalsPassTheCap()
    {
        var recipes = CapRecipes();
        var l = ListWith((80000, 1), (1, 1));
        l.ExcludedItemIds.Add(990700);
        Assert.Equal(0, CustomListOps.PruneExclusions(l, recipes));
        Assert.Contains(990700u, l.ExcludedItemIds);
    }

    [Fact]
    public void Sc1_PruneExclusions_WithAnyGoalProblem_RemovesNothing()
    {
        // Rule: while any goal could not be expanded, no exclusion is pruned (the rows are not known for certain).
        var recipes = new FakeLookup().Add(80000, 1, (990700, 2));
        var l = ListWith((80000, 1), (555, 1)); // 555 has no recipe: a problem
        l.ExcludedItemIds.Add(990700);
        l.ExcludedItemIds.Add(424242);
        Assert.Single(CustomListOps.EffectiveRequirements(l, recipes).Problems);
        Assert.Equal(0, CustomListOps.PruneExclusions(l, recipes));
        Assert.Equal(new uint[] { 424242, 990700 }, l.ExcludedItemIds.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Sc1_PruneExclusions_WithoutProblems_StillRemovesStaleIds()
    {
        var recipes = new FakeLookup().Add(80000, 1, (990700, 2));
        var l = ListWith((80000, 1));
        l.ExcludedItemIds.Add(990700);
        l.ExcludedItemIds.Add(424242);
        Assert.Equal(1, CustomListOps.PruneExclusions(l, recipes));
        Assert.Equal(new uint[] { 990700 }, l.ExcludedItemIds.ToArray());
    }

    [Fact]
    public void Sc1_CheckAddGoal_RefusesAGoalThatPushesTheListPastTheCap_AndChangesNothing()
    {
        var recipes = CapRecipes();
        var l = ListWith((80000, 1));
        l.ExcludedItemIds.Add(990700);
        var error = CustomListOps.CheckAddGoal(l, 1, 1, recipes);
        Assert.Equal("This list is too large to expand", error);
        Assert.Equal(new[] { (80000u, 1) }, l.Goals.Select(g => (g.ItemId, g.Quantity)).ToArray());
        Assert.Equal(new uint[] { 990700 }, l.ExcludedItemIds.ToArray());
        Assert.Empty(l.Items);
    }

    [Fact]
    public void Sc1_CheckAddGoal_AcceptsAnOrdinaryGoal_AndMergesWithAnExistingOne()
    {
        var recipes = new FakeLookup().Add(10, 1, (1, 1));
        var l = ListWith((10, 2));
        Assert.Null(CustomListOps.CheckAddGoal(l, 10, 3, recipes));
        Assert.Null(CustomListOps.CheckAddGoal(new CustomList(), 10, 1, recipes));
        Assert.Equal(2, l.Goals[0].Quantity);
    }

    [Fact]
    public void Sc1_CheckAddGoal_ReusesTheExistingWording_ForTheItemItself()
    {
        var recipes = new FakeLookup().Add(10, 1, (0, 1));
        var l = new CustomList();
        Assert.Equal("Item is not valid", CustomListOps.CheckAddGoal(l, 0, 1, recipes));
        Assert.Equal("This item cannot be crafted", CustomListOps.CheckAddGoal(l, 99, 1, recipes));
        Assert.Equal("This recipe has no ingredients", CustomListOps.CheckAddGoal(l, 10, 1, recipes));
    }

    // ---- SC-2 ----

    [Fact]
    public void Sc2_Breakdown_CombinesSharedIntermediates_RoundedOnce()
    {
        var recipes = new FakeLookup().Add(10, 1, (20, 1)).Add(11, 1, (20, 1)).Add(20, 3, (1000, 1));
        Assert.Equal(new[] { (1000u, 1) }, Rows(CustomListOps.Breakdown(recipes, new[] { R(10, 1), R(11, 1) })));
        Assert.Equal(new[] { (1000u, 1) }, Rows(CustomListOps.Breakdown(recipes, new[] { R(10, 1), R(10, 1) })));
    }

    [Fact]
    public void Sc2_Breakdown_OrderIsPassThroughFirstThenMaterialsByIdAscending_MergedOntoPassThrough()
    {
        var recipes = new FakeLookup().Add(100, 1, (30, 1), (10, 1)).Add(101, 1, (20, 2));
        var r = CustomListOps.Breakdown(recipes, new[] { R(100, 1), R(900, 4), R(20, 1), R(101, 1), R(800, 2), R(900, 1) });
        Assert.Equal(new[] { (900u, 5), (20u, 3), (800u, 2), (10u, 1), (30u, 1) }, Rows(r));
    }

    [Fact]
    public void Sc2_Breakdown_EqualsEffectiveRequirementsOfTheSameRowsAsGoalsAndPlainRows()
    {
        var recipes = new FakeLookup().Add(10, 1, (20, 1), (7, 1)).Add(11, 1, (20, 1)).Add(20, 3, (1000, 1)).Add(12, 2, (7, 3));
        var rows = new[] { R(10, 4), R(55, 2), R(7, 1), R(11, 5), R(12, 3), R(10, 1) };
        var l = new CustomList();
        l.Items.Add(new CustomListItem { ItemId = 55, Quantity = 2 });
        l.Items.Add(new CustomListItem { ItemId = 7, Quantity = 1 });
        foreach (var (id, q) in new[] { (10u, 4), (11u, 5), (12u, 3), (10u, 1) }) CustomListOps.AddGoal(l, id, q);
        var expected = CustomListOps.EffectiveRequirements(l, recipes);
        Assert.Empty(expected.Problems);
        Assert.Equal(Rows(expected.Requirements), Rows(CustomListOps.Breakdown(recipes, rows)));
    }

    // ---- SC-3 ----

    [Theory]
    [InlineData(9_000_000, 9_000_000)]
    [InlineData(-7, int.MaxValue)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void Sc3_DuplicateGoals_ShoppingListAndArtisanExportAgree(int a, int b)
    {
        // Yield 3: the merged demand 9,999,999 is exactly 3,333,333 crafts; 10,000,000 would be 3,333,334.
        var recipes = new FakeLookup().Add(10, 3, (1000, 1));
        var l = new CustomList();
        l.Goals.Add(new CustomListItem { ItemId = 10, Quantity = a });
        l.Goals.Add(new CustomListItem { ItemId = 10, Quantity = b });

        var effective = CustomListOps.EffectiveRequirements(l, recipes);
        var crafts = RecipeExpander.ExpandToCrafts(recipes, CustomListOps.ExportRows(l));

        var craftedTen = crafts.Crafts!.Single(c => c.ItemId == 10).Crafts;
        Assert.Equal(3_333_333, craftedTen);
        Assert.Equal(new[] { (1000u, 3_333_333) }, Rows(effective.Requirements));
    }

    [Fact]
    public void Sc3_RepeatedRoots_AreClampedBeforeTheyAreRounded()
    {
        var recipes = new FakeLookup().Add(10, 3, (1000, 1));
        var mats = RecipeExpander.ExpandToMaterials(recipes, new[] { R(10, 9_000_000), R(10, 9_000_000) });
        Assert.Equal(new[] { (1000u, 3_333_333) }, Rows(mats.Items!));
        var crafts = RecipeExpander.ExpandToCrafts(recipes, new[] { R(10, 9_000_000), R(10, 9_000_000) });
        Assert.Equal(3_333_333, crafts.Crafts!.Single().Crafts);
    }

    // ---- SC-4 ----

    [Fact]
    public void Sc4_NullRowInItems_DoesNotThrow()
    {
        var recipes = new FakeLookup().Add(10, 1, (1, 1));
        var l = ListWith((10, 1));
        l.Items.Insert(0, null!);
        l.Items.Add(new CustomListItem { ItemId = 5, Quantity = 1 });
        l.ExcludedItemIds.Add(5);
        var eff = CustomListOps.EffectiveRequirements(l, recipes);
        Assert.Equal(new[] { (5u, 1), (1u, 1) }, Rows(eff.Requirements));
        Assert.Equal(new[] { (5u, 1) }, Rows(CustomListOps.ToRequirements(l)));
        Assert.Equal(0, CustomListOps.PruneExclusions(l, recipes));
        var noGoals = new CustomList();
        noGoals.Items.Add(null!);
        noGoals.Items.Add(new CustomListItem { ItemId = 5, Quantity = 1 });
        noGoals.ExcludedItemIds.Add(5);
        noGoals.ExcludedItemIds.Add(6);
        Assert.Equal(1, CustomListOps.PruneExclusions(noGoals));
    }

    // ---- SE-1 ----

    [Fact]
    public void Se1_EscapedLoneSurrogateInName_IsNoName_AndRecipesImport()
    {
        var r = ArtisanListExport.TryParse("{\"Name\":\"\\ud800abc\",\"Recipes\":[{\"ID\":1000,\"Quantity\":1}]}", Rec);
        Assert.Null(r.Error);
        Assert.Null(r.Name);
        Assert.Equal(new[] { (100u, 1) }, Rows(r.Requirements!));
    }

    [Fact]
    public void Se1_RawLoneSurrogateInName_StillImportsTheRecipes()
    {
        var r = ArtisanListExport.TryParse("{\"Name\":\"\ud800abc\",\"Recipes\":[{\"ID\":1000,\"Quantity\":1}]}", Rec);
        Assert.Null(r.Error);
        Assert.Equal(new[] { (100u, 1) }, Rows(r.Requirements!));
    }

    [Fact]
    public void Se1_LoneSurrogateInIgnoredMember_IsHarmless()
    {
        foreach (var bad in new[] { "\\ud800", "\ud800", "\\udc00x" })
        {
            var r = ArtisanListExport.TryParse("{\"Name\":\"ok\",\"Other\":\"" + bad + "\",\"Recipes\":[{\"ID\":1000,\"Quantity\":1,\"X\":\"" + bad + "\"}]}", Rec);
            Assert.Null(r.Error);
            Assert.Equal("ok", r.Name);
            Assert.Equal(new[] { (100u, 1) }, Rows(r.Requirements!));
        }
    }

    [Fact]
    public void Se1_ImportParser_KeepsTheListWhenTheNameHasALoneSurrogate()
    {
        var r = ImportParser.Parse("{\"Name\":\"\\ud800abc\",\"Recipes\":[{\"ID\":1000,\"Quantity\":1}]}", new Items(), Rec);
        Assert.Null(r.Error);
        Assert.NotEmpty(r.Requirements);
    }

    private sealed class Items : IItemInfo
    {
        public string GetName(uint itemId) => "Item #" + itemId;
        public bool IsCrystalLike(uint itemId) => false;
        public uint? FindByName(string name) => null;
    }

    private sealed class Recipes : IRecipeLookup, IRecipeData
    {
        public RecipeInfo? FindRecipeForItem(uint itemId) => itemId == 100 ? new RecipeInfo(1000, 100, 1, Array.Empty<Ingredient>()) : null;
        public RecipeInfo? GetRecipe(uint recipeId) => recipeId == 1000 ? new RecipeInfo(1000, 100, 1, Array.Empty<Ingredient>()) : null;
    }

    private static readonly Recipes Rec = new();

    // ---- SM-6 (behaviour kept while the joined query is built once) ----

    [Fact]
    public void Sm6_ApostropheNames_MatchTheSameWayWithMultiWordAndApostropheQueries()
    {
        var entries = new[]
        {
            new ItemSearchEntry(1, "Warrior's Helm"),
            new ItemSearchEntry(2, "Helm of Warriors"),
            new ItemSearchEntry(3, "Queen\u2019s Guard Shield"),
            new ItemSearchEntry(4, "Plain Shield"),
            new ItemSearchEntry(5, "Queen's Hall Guard"),
        };
        Assert.Equal(new uint[] { 1 }, ItemSearch.Rank("warriors helm", entries, 10).Select(e => e.ItemId).Take(1).ToArray());
        // 3 holds the whole phrase "queens guard" (tier 1); 5 only both words (tier 4), though its name is shorter.
        Assert.Equal(new uint[] { 3, 5 }, ItemSearch.Rank("queens guard", entries, 10).Select(e => e.ItemId).ToArray());
        Assert.Equal(new uint[] { 3 }, ItemSearch.Rank("shield queens", entries, 10).Select(e => e.ItemId).Take(1).ToArray());
        Assert.Equal(new uint[] { 1 }, ItemSearch.Rank("warrior's helm", entries, 10).Select(e => e.ItemId).Take(1).ToArray());
    }
}
