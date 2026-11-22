namespace LunaShoppingList.Core.Tests;

/// <summary>
/// Goals on a player's list: finished items whose materials are worked out from the recipes every time, never stored.
/// Plain rows (Items) behave exactly as before; a list with no goals is an old list.
/// </summary>
public class CustomListGoalsTests
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

    private static CustomList L(params (uint item, int qty)[] rows)
    {
        var l = new CustomList { Id = "1", Name = "A" };
        foreach (var r in rows) l.Items.Add(new CustomListItem { ItemId = r.item, Quantity = r.qty });
        return l;
    }

    private static CustomList WithGoals(CustomList l, params (uint item, int qty)[] goals)
    {
        foreach (var g in goals) l.Goals.Add(new CustomListItem { ItemId = g.item, Quantity = g.qty });
        return l;
    }

    private static (uint, int)[] Goals(CustomList l) => l.Goals.Select(i => (i.ItemId, i.Quantity)).ToArray();
    private static (uint, int)[] Rows(CustomList l) => l.Items.Select(i => (i.ItemId, i.Quantity)).ToArray();
    private static (uint, int)[] Rows(IEnumerable<Requirement> r) => r.Select(i => (i.ItemId, i.Quantity)).ToArray();

    // ---------- model ----------

    [Fact]
    public void Goals_SetterCoalescesNull_AndStartsEmpty()
    {
        Assert.Empty(new CustomList().Goals);
        var l = new CustomList { Goals = null! };
        Assert.NotNull(l.Goals);
        Assert.Empty(l.Goals);
    }

    [Fact]
    public void JsonNet_OldSettingsWithoutGoals_ReadsAsEmptyGoals_ItemsKept()
    {
        const string old = "{\"Id\":\"abc\",\"Name\":\"Old\",\"Items\":[{\"ItemId\":5,\"Quantity\":3}],\"ExcludedItemIds\":[5]}";
        var l = Newtonsoft.Json.JsonConvert.DeserializeObject<CustomList>(old)!;
        Assert.NotNull(l.Goals);
        Assert.Empty(l.Goals);
        Assert.Equal(new[] { (5u, 3) }, Rows(l));
        Assert.Equal(new uint[] { 5 }, l.ExcludedItemIds.ToArray());
    }

    [Fact]
    public void JsonNet_GoalsNull_ReadsAsEmpty()
    {
        var l = Newtonsoft.Json.JsonConvert.DeserializeObject<CustomList>("{\"Id\":\"a\",\"Name\":\"N\",\"Goals\":null}")!;
        Assert.NotNull(l.Goals);
        Assert.Empty(l.Goals);
    }

    [Fact]
    public void JsonNet_RoundTrip_KeepsGoals_WithTypeNames()
    {
        // Dalamud stores plugin settings with Json.NET and type names on objects.
        var settings = new Newtonsoft.Json.JsonSerializerSettings { TypeNameHandling = Newtonsoft.Json.TypeNameHandling.Objects };
        var l = WithGoals(L((5, 3)), (100, 2), (200, 1));
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(l, settings);
        var back = Newtonsoft.Json.JsonConvert.DeserializeObject<CustomList>(json, settings)!;
        Assert.Equal(new[] { (100u, 2), (200u, 1) }, Goals(back));
        Assert.Equal(new[] { (5u, 3) }, Rows(back));
    }

    // ---------- goal operations ----------

    [Fact]
    public void AddGoal_NewThenMerge_ItemsUntouched()
    {
        var l = L((5, 1));
        Assert.True(CustomListOps.AddGoal(l, 100, 2));
        Assert.True(CustomListOps.AddGoal(l, 200, 1));
        Assert.True(CustomListOps.AddGoal(l, 100, 3));
        Assert.Equal(new[] { (100u, 5), (200u, 1) }, Goals(l));
        Assert.Equal(new[] { (5u, 1) }, Rows(l));
    }

    [Fact]
    public void AddGoal_SaturatesAndClamps()
    {
        var l = L();
        CustomListOps.AddGoal(l, 100, int.MaxValue);
        Assert.Equal(new[] { (100u, 9_999_999) }, Goals(l));
        CustomListOps.AddGoal(l, 100, int.MaxValue);
        Assert.Equal(new[] { (100u, 9_999_999) }, Goals(l));
        CustomListOps.AddGoal(l, 200, -4);
        Assert.Equal((200u, 1), Goals(l)[1]);
    }

    [Fact]
    public void AddGoal_ItemZero_Rejected()
    {
        var l = L();
        Assert.False(CustomListOps.AddGoal(l, 0, 3));
        Assert.Empty(l.Goals);
    }

    [Fact]
    public void AddGoal_SameItemAsPlainRow_KeptSeparately()
    {
        var l = L((100, 4));
        CustomListOps.AddGoal(l, 100, 1);
        Assert.Equal(new[] { (100u, 4) }, Rows(l));
        Assert.Equal(new[] { (100u, 1) }, Goals(l));
    }

    [Fact]
    public void SetGoalQuantity_ClampsAndUnknownIsNoOp()
    {
        var l = WithGoals(L((100, 7)), (100, 2));
        Assert.True(CustomListOps.SetGoalQuantity(l, 100, 0));
        Assert.Equal(new[] { (100u, 1) }, Goals(l));
        Assert.True(CustomListOps.SetGoalQuantity(l, 100, int.MaxValue));
        Assert.Equal(new[] { (100u, 9_999_999) }, Goals(l));
        Assert.False(CustomListOps.SetGoalQuantity(l, 300, 5));
        Assert.Equal(new[] { (100u, 7) }, Rows(l));
    }

    [Fact]
    public void RemoveGoal_RemovesGoalOnly()
    {
        var l = WithGoals(L((100, 7)), (100, 2), (200, 1));
        Assert.True(CustomListOps.RemoveGoal(l, 100));
        Assert.False(CustomListOps.RemoveGoal(l, 100));
        Assert.Equal(new[] { (200u, 1) }, Goals(l));
        Assert.Equal(new[] { (100u, 7) }, Rows(l));
    }

    // ---------- EffectiveRequirements ----------

    // 100 (yield 1) = 2 x item 10 + 1 x item 11; 200 (yield 1) = 3 x item 10 + 1 x item 12.
    private static FakeLookup Recipes() => new FakeLookup().Add(100, 1, (10, 2), (11, 1)).Add(200, 1, (10, 3), (12, 1));

    [Fact]
    public void Effective_PlainOnly_IsTheRowsInOrder()
    {
        var l = L((7, 2), (3, 1), (100, 1));
        var r = CustomListOps.EffectiveRequirements(l, Recipes());
        Assert.Equal(new[] { (7u, 2), (3u, 1), (100u, 1) }, Rows(r.Requirements));
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void Effective_GoalsOnly_MaterialsByItemIdAscending_SharedMerged()
    {
        var l = WithGoals(L(), (200, 1), (100, 2));
        var r = CustomListOps.EffectiveRequirements(l, Recipes());
        Assert.Equal(new[] { (10u, 7), (11u, 2), (12u, 1) }, Rows(r.Requirements));
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void Effective_Mixed_PlainFirstInOrder_ThenDerivedAscending_MaterialOnAPlainRowMergesThere()
    {
        var l = WithGoals(L((50, 1), (11, 4), (2, 1)), (100, 1));
        var r = CustomListOps.EffectiveRequirements(l, Recipes());
        Assert.Equal(new[] { (50u, 1), (11u, 5), (2u, 1), (10u, 2) }, Rows(r.Requirements));
    }

    [Fact]
    public void Effective_GoalQuantityChange_MaterialsFollow_NothingStored()
    {
        var l = WithGoals(L(), (100, 1));
        var recipes = Recipes();
        Assert.Equal(new[] { (10u, 2), (11u, 1) }, Rows(CustomListOps.EffectiveRequirements(l, recipes).Requirements));
        CustomListOps.SetGoalQuantity(l, 100, 3);
        Assert.Equal(new[] { (10u, 6), (11u, 3) }, Rows(CustomListOps.EffectiveRequirements(l, recipes).Requirements));
        CustomListOps.RemoveGoal(l, 100);
        Assert.Empty(CustomListOps.EffectiveRequirements(l, recipes).Requirements);
        Assert.Empty(l.Items);
    }

    [Fact]
    public void Effective_UnexpandableGoals_ContributeNothing_ReportedInGoalOrder_OthersStillCount()
    {
        // 300 has no recipe, 400's recipe has no usable ingredient; neither throws.
        var recipes = Recipes().Add(400, 1, (0, 1));
        var l = WithGoals(L((9, 1)), (300, 2), (100, 1), (400, 1));
        var r = CustomListOps.EffectiveRequirements(l, recipes);
        Assert.Equal(new[] { (9u, 1), (10u, 2), (11u, 1) }, Rows(r.Requirements));
        Assert.Equal(new uint[] { 300, 400 }, r.Problems.Select(p => p.ItemId).ToArray());
        Assert.All(r.Problems, p => Assert.False(string.IsNullOrWhiteSpace(p.Error)));
    }

    [Fact]
    public void Effective_TooLargeGoal_ReportedNotThrown()
    {
        var f = new FakeLookup();
        for (uint i = 0; i <= RecipeExpander.MaxCraftableItems; i++) f.Add(100_000 + i, 1, (100_001 + i, 1));
        var l = WithGoals(L(), (100_000, 1));
        var r = CustomListOps.EffectiveRequirements(l, f);
        Assert.Empty(r.Requirements);
        Assert.Equal(100_000u, Assert.Single(r.Problems).ItemId);
    }

    [Fact]
    public void Effective_Saturates_AcrossGoalsAndPlainRows()
    {
        var recipes = new FakeLookup().Add(100, 1, (10, 9_000_000)).Add(200, 1, (10, 9_000_000));
        var l = WithGoals(L((10, 5_000_000)), (100, 1), (200, 1));
        Assert.Equal(new[] { (10u, 9_999_999) }, Rows(CustomListOps.EffectiveRequirements(l, recipes).Requirements));
        var goalsOnly = WithGoals(L(), (100, 1), (200, 1));
        Assert.Equal(new[] { (10u, 9_999_999) }, Rows(CustomListOps.EffectiveRequirements(goalsOnly, recipes).Requirements));
    }

    [Fact]
    public void Effective_GoalsExpandedTogether_SharedIntermediateRoundedOnce()
    {
        // Superseded 2026-11-22 (user decision: the shopping list must agree with the Artisan export, which crafts a
        // shared intermediate once). Was Effective_EachGoalExpandedOnItsOwn_SharedIntermediateRoundedPerGoal, expecting
        // (1, 2): each goal expanded on its own, one craft of 20 each. Now all goals are expanded together: 100 and 200
        // each need one 20 (yield 10, = 1 x item 1), combined demand 2 -> one craft -> 1 of item 1.
        var recipes = new FakeLookup().Add(100, 1, (20, 1)).Add(200, 1, (20, 1)).Add(20, 10, (1, 1));
        var l = WithGoals(L(), (100, 1), (200, 1));
        Assert.Equal(new[] { (1u, 1) }, Rows(CustomListOps.EffectiveRequirements(l, recipes).Requirements));
    }

    [Fact]
    public void Effective_GoalThatIsAnIngredientOfAnotherGoal_DemandCombined()
    {
        // Goal 20 (1) is also needed once by goal 100; 20 makes 2 per craft: 2 needed -> 1 craft -> 3 of item 1.
        // Expanded apart it would be 1 + 1 crafts = 6 of item 1.
        var recipes = new FakeLookup().Add(100, 1, (20, 1), (2, 1)).Add(20, 2, (1, 3));
        var l = WithGoals(L(), (20, 1), (100, 1));
        Assert.Equal(new[] { (1u, 3), (2u, 1) }, Rows(CustomListOps.EffectiveRequirements(l, recipes).Requirements));
    }

    [Fact]
    public void Effective_BadGoalsAmongGoodOnes_ReportedInGoalOrder_GoodOnesStillCombined()
    {
        // 0 is not a valid item, 300 has no recipe; 100 and 200 still share one craft of 20.
        var recipes = new FakeLookup().Add(100, 1, (20, 1)).Add(200, 1, (20, 1)).Add(20, 10, (1, 1));
        var l = WithGoals(L((9, 4)), (100, 1), (0, 2), (300, 1), (200, 1));
        var r = CustomListOps.EffectiveRequirements(l, recipes);
        Assert.Equal(new[] { (9u, 4), (1u, 1) }, Rows(r.Requirements));
        Assert.Equal(new uint[] { 0, 300 }, r.Problems.Select(p => p.ItemId).ToArray());
    }

    [Fact]
    public void Effective_GoalWithNoIngredients_Reported_ButStillAMaterialOfAnotherGoal()
    {
        // 400's recipe has nothing usable: as a goal it is a problem and adds nothing; goal 500 needs two of it,
        // and there it is gathered like a raw material (as ExpandToMaterials treats such an intermediate).
        var recipes = new FakeLookup().Add(400, 1, (0, 1)).Add(500, 1, (400, 2), (3, 1));
        var l = WithGoals(L(), (400, 5), (500, 1));
        var r = CustomListOps.EffectiveRequirements(l, recipes);
        Assert.Equal(new[] { (3u, 1), (400u, 2) }, Rows(r.Requirements));
        Assert.Equal(400u, Assert.Single(r.Problems).ItemId);
    }

    [Fact]
    public void Effective_CapCountsAllGoalsTogether_EveryExpandedGoalReported_PlainRowsKept()
    {
        // Two chains of 2,600 craftable items: each goal alone is under the cap, together they are over it. The
        // combined expansion fails, so each goal that took part is reported (in goal order) and adds nothing; the
        // goal with no recipe keeps its own message.
        var f = new FakeLookup();
        foreach (var start in new uint[] { 100_000, 200_000 })
            for (uint i = 0; i < 2_600; i++) f.Add(start + i, 1, (start + i + 1, 1));
        Assert.Empty(CustomListOps.EffectiveRequirements(WithGoals(L(), (100_000, 1)), f).Problems);

        var l = WithGoals(L((9, 1)), (100_000, 1), (7, 1), (200_000, 1));
        var r = CustomListOps.EffectiveRequirements(l, f);
        Assert.Equal(new[] { (9u, 1) }, Rows(r.Requirements));
        Assert.Equal(new uint[] { 100_000, 7, 200_000 }, r.Problems.Select(p => p.ItemId).ToArray());
        Assert.Equal(r.Problems[0].Error, r.Problems[2].Error);
        Assert.NotEqual(r.Problems[0].Error, r.Problems[1].Error);
    }

    [Fact]
    public void Effective_CombinedDemand_SaturatesInsteadOfWrapping()
    {
        // Each goal needs 2,000,000,000 of 20 per craft at 9,999,999 crafts; 20 needs 2,000,000,000 of item 1.
        var recipes = new FakeLookup().Add(100, 1, (20, 2_000_000_000)).Add(200, 1, (20, 2_000_000_000)).Add(20, 1, (1, 2_000_000_000));
        var l = WithGoals(L(), (100, 9_999_999), (200, 9_999_999));
        Assert.Equal(new[] { (1u, 9_999_999) }, Rows(CustomListOps.EffectiveRequirements(l, recipes).Requirements));
    }

    [Fact]
    public void Effective_DoesNotChangeTheList()
    {
        var l = WithGoals(L((10, 1)), (100, 1));
        l.ExcludedItemIds.Add(11);
        CustomListOps.EffectiveRequirements(l, Recipes());
        Assert.Equal(new[] { (10u, 1) }, Rows(l));
        Assert.Equal(new[] { (100u, 1) }, Goals(l));
        Assert.Equal(new uint[] { 11 }, l.ExcludedItemIds.ToArray());
    }

    // ---------- ExportRows ----------

    [Fact]
    public void ExportRows_PlainThenGoals_MergedByItem_InOrder()
    {
        var l = WithGoals(L((5, 1), (100, 2)), (200, 3), (100, 4));
        Assert.Equal(new[] { (5u, 1), (100u, 6), (200u, 3) }, Rows(CustomListOps.ExportRows(l)));
    }

    [Fact]
    public void ExportRows_NoGoals_IsThePlainRows()
    {
        var l = L((5, 1), (3, 2));
        Assert.Equal(new[] { (5u, 1), (3u, 2) }, Rows(CustomListOps.ExportRows(l)));
    }

    [Fact]
    public void ExportRows_GoalsOnly()
    {
        var l = WithGoals(L(), (200, 3), (100, 1));
        Assert.Equal(new[] { (200u, 3), (100u, 1) }, Rows(CustomListOps.ExportRows(l)));
    }

    [Fact]
    public void ExportRows_SaturatesAndClamps_SkipsNullAndItemZero()
    {
        var l = WithGoals(L((100, 9_000_000), (0, 4)), (100, 9_000_000), (0, 2), (300, 0));
        l.Goals.Add(null!);
        l.Items.Add(null!);
        Assert.Equal(new[] { (100u, 9_999_999), (300u, 1) }, Rows(CustomListOps.ExportRows(l)));
    }

    // ---------- exclusions and repairs ----------

    [Fact]
    public void PruneExclusions_ListWithGoals_KeepsEverything()
    {
        // Without recipes the derived rows are unknown, so nothing may be dropped.
        var l = WithGoals(L((5, 1)), (100, 1));
        l.ExcludedItemIds.UnionWith(new uint[] { 5, 10, 999 });
        Assert.Equal(0, CustomListOps.PruneExclusions(l));
        Assert.Equal(new uint[] { 5, 10, 999 }, l.ExcludedItemIds.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void PruneExclusions_WithRecipes_KeepsShownRows_DropsTheRest()
    {
        var l = WithGoals(L((5, 1)), (100, 1));
        l.ExcludedItemIds.UnionWith(new uint[] { 5, 10, 11, 12, 100, 999 });
        Assert.Equal(3, CustomListOps.PruneExclusions(l, Recipes()));
        Assert.Equal(new uint[] { 5, 10, 11 }, l.ExcludedItemIds.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void PruneExclusions_WithRecipes_RemovedGoal_DropsItsMaterialsExclusions()
    {
        var l = WithGoals(L(), (100, 1), (200, 1));
        l.ExcludedItemIds.UnionWith(new uint[] { 10, 11, 12 });
        CustomListOps.RemoveGoal(l, 200);
        Assert.Equal(1, CustomListOps.PruneExclusions(l, Recipes()));
        Assert.Equal(new uint[] { 10, 11 }, l.ExcludedItemIds.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void PruneExclusions_WithRecipes_NoGoals_SameAsPlainPrune()
    {
        var l = L((5, 1), (6, 1));
        l.ExcludedItemIds.UnionWith(new uint[] { 5, 7 });
        Assert.Equal(1, CustomListOps.PruneExclusions(l, Recipes()));
        Assert.Equal(new uint[] { 5 }, l.ExcludedItemIds.ToArray());
    }

    [Fact]
    public void Normalize_DropsNullGoals()
    {
        var l = WithGoals(L((10, 1)), (100, 1));
        l.Goals.Insert(0, null!);
        l.Goals.Add(null!);
        Assert.Equal(2, CustomListOps.Normalize(new List<CustomList> { l }, () => "x"));
        Assert.Equal(new[] { (100u, 1) }, Goals(l));
    }
}
