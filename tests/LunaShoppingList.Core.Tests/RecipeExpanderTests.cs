namespace LunaShoppingList.Core.Tests;

public class RecipeExpanderTests
{
    private sealed class FakeLookup : IRecipeLookup
    {
        private readonly Dictionary<uint, RecipeInfo> _map = new();
        public readonly Dictionary<uint, int> Calls = new();
        public FakeLookup Add(uint result, int yield, params (uint item, int amount)[] ing)
        {
            _map[result] = new RecipeInfo(result + 5000, result, yield, ing.Select(i => new Ingredient(i.item, i.amount)).ToList());
            return this;
        }
        public RecipeInfo? FindRecipeForItem(uint itemId)
        {
            Calls[itemId] = Calls.GetValueOrDefault(itemId) + 1;
            return _map.TryGetValue(itemId, out var r) ? r : null;
        }
    }

    private static (uint, int)[] Rows(MaterialsResult r) => r.Items!.Select(i => (i.ItemId, i.Quantity)).ToArray();

    [Fact]
    public void SingleLevel_Yield3Quantity7_Is3Crafts()
    {
        var recipes = new FakeLookup().Add(100, 3, (10, 2), (11, 1));
        var r = RecipeExpander.ExpandToMaterials(recipes, 100, 7);
        Assert.True(r.Ok);
        Assert.Equal(new[] { (10u, 6), (11u, 3) }, Rows(r));
    }

    [Fact]
    public void ExactMultipleOfYield_NoExtraCraft()
    {
        var recipes = new FakeLookup().Add(100, 3, (10, 2));
        Assert.Equal(new[] { (10u, 4) }, Rows(RecipeExpander.ExpandToMaterials(recipes, 100, 6)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void YieldBelowOne_TreatedAsOne(int yield)
    {
        var recipes = new FakeLookup().Add(100, yield, (10, 2));
        Assert.Equal(new[] { (10u, 10) }, Rows(RecipeExpander.ExpandToMaterials(recipes, 100, 5)));
    }

    [Fact]
    public void TwoLevels_YieldRoundsUpOncePerIntermediate()
    {
        // 7 of 100 (yield 3) = 3 crafts = 6 of 20; 20 (yield 2) = 3 crafts, each 5 of 1 = 15.
        var recipes = new FakeLookup().Add(100, 3, (20, 2)).Add(20, 2, (1, 5));
        Assert.Equal(new[] { (1u, 15) }, Rows(RecipeExpander.ExpandToMaterials(recipes, 100, 7)));
    }

    [Fact]
    public void SharedIntermediate_CombinedDemandRoundedOnce()
    {
        // 100 needs 20 x1 and 30 x1; 30 needs 20 x1; 20 (yield 10) needs 1 x1.
        // Combined demand of 20 is 2 -> 1 craft -> 1 of item 1. Per-parent rounding would give 2.
        var recipes = new FakeLookup()
            .Add(100, 1, (20, 1), (30, 1))
            .Add(30, 1, (20, 1))
            .Add(20, 10, (1, 1));
        Assert.Equal(new[] { (1u, 1) }, Rows(RecipeExpander.ExpandToMaterials(recipes, 100, 1)));
    }

    [Fact]
    public void SharedIntermediate_DiamondOrderIndependent()
    {
        // Same diamond with the shared item listed in the other order and the deeper parent first.
        var recipes = new FakeLookup()
            .Add(100, 1, (30, 1), (20, 1))
            .Add(30, 1, (20, 1))
            .Add(20, 10, (1, 1));
        Assert.Equal(new[] { (1u, 1) }, Rows(RecipeExpander.ExpandToMaterials(recipes, 100, 1)));
    }

    [Fact]
    public void SharedIntermediate_LookedUpOnce()
    {
        var recipes = new FakeLookup()
            .Add(100, 1, (20, 1), (30, 1))
            .Add(30, 1, (20, 1))
            .Add(20, 1, (1, 1));
        RecipeExpander.ExpandToMaterials(recipes, 100, 1);
        Assert.All(recipes.Calls, c => Assert.Equal(1, c.Value));
    }

    [Fact]
    public void ThreeLevelTree_TotalsLeavesOnly()
    {
        var recipes = new FakeLookup()
            .Add(100, 1, (20, 2), (21, 1), (5, 1))
            .Add(20, 1, (30, 3), (6, 1))
            .Add(21, 1, (30, 1), (7, 2))
            .Add(30, 1, (8, 2), (1, 1));
        // 20 x2, 21 x1 -> 30 demand 2*3 + 1 = 7 -> 8 x14, 1 x7; 6 x2; 7 x2; 5 x1.
        Assert.Equal(
            new[] { (1u, 7), (5u, 1), (6u, 2), (7u, 2), (8u, 14) },
            Rows(RecipeExpander.ExpandToMaterials(recipes, 100, 1)));
    }

    [Fact]
    public void Crystals_AreLeaves()
    {
        var recipes = new FakeLookup().Add(100, 1, (10, 1), (2, 2), (8, 2));
        Assert.Equal(new[] { (2u, 2), (8u, 2), (10u, 1) }, Rows(RecipeExpander.ExpandToMaterials(recipes, 100, 1)));
    }

    [Fact]
    public void ItemThatIsLeafForOneParentButCraftable_IsExpandedEverywhere()
    {
        // 20 is craftable, so it never appears as a material even where a parent lists it as a plain ingredient.
        var recipes = new FakeLookup().Add(100, 1, (20, 1), (30, 1)).Add(30, 1, (20, 1)).Add(20, 1, (1, 3));
        var r = RecipeExpander.ExpandToMaterials(recipes, 100, 1);
        Assert.Equal(new[] { (1u, 6) }, Rows(r));
    }

    [Fact]
    public void RootAndIntermediatesAreNotInResult()
    {
        var recipes = new FakeLookup().Add(100, 1, (20, 1), (5, 1)).Add(20, 1, (1, 1));
        var ids = Rows(RecipeExpander.ExpandToMaterials(recipes, 100, 1)).Select(x => x.Item1).ToArray();
        Assert.DoesNotContain(100u, ids);
        Assert.DoesNotContain(20u, ids);
    }

    [Fact]
    public void ResultOrderedByItemIdAscending()
    {
        var recipes = new FakeLookup().Add(100, 1, (50, 1), (3, 1), (20, 1));
        Assert.Equal(new[] { 3u, 20u, 50u }, Rows(RecipeExpander.ExpandToMaterials(recipes, 100, 1)).Select(x => x.Item1).ToArray());
    }

    [Fact]
    public void DuplicateIngredientLines_AreMerged()
    {
        var recipes = new FakeLookup().Add(100, 1, (10, 1), (10, 2));
        Assert.Equal(new[] { (10u, 6) }, Rows(RecipeExpander.ExpandToMaterials(recipes, 100, 2)));
    }

    [Fact]
    public void Cycle_Terminates_RepeatedItemIsRaw()
    {
        // 100 needs 20 and 1; 20 needs 100 (loop) and 2. 100 reached again on the path counts as a material.
        var recipes = new FakeLookup().Add(100, 1, (20, 1), (1, 1)).Add(20, 1, (100, 2), (2, 1));
        var r = RecipeExpander.ExpandToMaterials(recipes, 100, 3);
        Assert.True(r.Ok);
        Assert.Equal(new[] { (1u, 3), (2u, 3), (100u, 6) }, Rows(r));
    }

    [Fact]
    public void TwoItemCycleBetweenIntermediates_Terminates()
    {
        var recipes = new FakeLookup().Add(100, 1, (20, 1)).Add(20, 1, (30, 1)).Add(30, 1, (20, 1), (1, 1));
        var r = RecipeExpander.ExpandToMaterials(recipes, 100, 1);
        Assert.True(r.Ok);
        Assert.Equal(new[] { (1u, 1), (20u, 1) }, Rows(r));
    }

    [Fact]
    public void SelfIngredient_Terminates_ItemIsAlsoRaw()
    {
        var recipes = new FakeLookup().Add(100, 1, (100, 2), (10, 1));
        Assert.Equal(new[] { (10u, 3), (100u, 6) }, Rows(RecipeExpander.ExpandToMaterials(recipes, 100, 3)));
    }

    private static FakeLookup Chain(int craftable)
    {
        // Items 1..craftable are craftable; item i needs item i+1; the last needs raw item 900000.
        var f = new FakeLookup();
        for (uint i = 1; i <= craftable; i++)
            f.Add(i, 1, i == craftable ? (900_000u, 1) : (i + 1, 1));
        return f;
    }

    [Theory]
    [InlineData(4_999)]
    [InlineData(5_000)]
    public void ChainAtOrUnderCap_Succeeds(int craftable)
    {
        var r = RecipeExpander.ExpandToMaterials(Chain(craftable), 1, 1);
        Assert.True(r.Ok);
        Assert.Equal(new[] { (900_000u, 1) }, Rows(r));
    }

    [Fact]
    public void ChainOverCap_ReturnsError()
    {
        var r = RecipeExpander.ExpandToMaterials(Chain(5_001), 1, 1);
        Assert.False(r.Ok);
        Assert.Null(r.Items);
        Assert.Equal("This recipe is too large to expand", r.Error);
    }

    [Fact]
    public void WideTreeOverCap_ReturnsError()
    {
        var f = new FakeLookup();
        var ing = new List<(uint, int)>();
        for (uint i = 2; i <= 5_002; i++) { ing.Add((i, 1)); f.Add(i, 1, (900_000, 1)); }
        f.Add(1, 1, ing.ToArray());
        Assert.Equal("This recipe is too large to expand", RecipeExpander.ExpandToMaterials(f, 1, 1).Error);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(int.MaxValue, 9_999_999)]
    public void QuantityClamped(int quantity, int expectedCrafts)
    {
        var recipes = new FakeLookup().Add(100, 1, (10, 1));
        Assert.Equal(new[] { (10u, expectedCrafts) }, Rows(RecipeExpander.ExpandToMaterials(recipes, 100, quantity)));
    }

    [Fact]
    public void LeafTotals_SaturateAt9999999()
    {
        var recipes = new FakeLookup().Add(100, 1, (10, 2_000_000_000), (11, 5), (20, 1)).Add(20, 1, (11, 2_000_000_000));
        var r = RecipeExpander.ExpandToMaterials(recipes, 100, 9_999_999);
        Assert.Equal(new[] { (10u, 9_999_999), (11u, 9_999_999) }, Rows(r));
    }

    [Fact]
    public void DeepMultiplication_DoesNotOverflowIntoSmallNumber()
    {
        // Ten levels of 65,536 per craft is 2^160 of the raw material: it must clamp, not wrap around to a small number.
        var f = new FakeLookup();
        for (uint i = 1; i <= 10; i++) f.Add(i, 1, i == 10 ? (900_000u, 65_536) : (i + 1, 65_536));
        Assert.Equal(new[] { (900_000u, 9_999_999) }, Rows(RecipeExpander.ExpandToMaterials(f, 1, 1)));
    }

    [Fact]
    public void NonCraftableRoot_Error()
    {
        var r = RecipeExpander.ExpandToMaterials(new FakeLookup(), 100, 2);
        Assert.False(r.Ok);
        Assert.Null(r.Items);
        Assert.Equal("This item cannot be crafted", r.Error);
    }

    [Fact]
    public void ItemIdZero_Error()
    {
        var r = RecipeExpander.ExpandToMaterials(new FakeLookup().Add(0, 1, (10, 1)), 0, 1);
        Assert.False(r.Ok);
        Assert.Equal("Item is not valid", r.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RootWithNoUsableIngredient_Error(bool empty)
    {
        var recipes = empty
            ? new FakeLookup().Add(100, 1)
            : new FakeLookup().Add(100, 1, (0, 3), (10, 0), (11, -1));
        var r = RecipeExpander.ExpandToMaterials(recipes, 100, 1);
        Assert.False(r.Ok);
        Assert.Equal("This recipe has no ingredients", r.Error);
    }

    [Fact]
    public void UnusableIngredientsSkipped_UsableOnesKept()
    {
        var recipes = new FakeLookup().Add(100, 1, (0, 3), (10, 0), (11, 2));
        Assert.Equal(new[] { (11u, 2) }, Rows(RecipeExpander.ExpandToMaterials(recipes, 100, 1)));
    }

    [Fact]
    public void IntermediateWithOnlyUnusableIngredients_IsAMaterialWithItsDemand()
    {
        // 20 is craftable but its recipe has nothing usable, so it is gathered like a raw material.
        var recipes = new FakeLookup()
            .Add(100, 1, (20, 2), (1, 1))
            .Add(20, 1, (0, 3), (10, 0), (11, -1));
        Assert.Equal(new[] { (1u, 3), (20u, 6) }, Rows(RecipeExpander.ExpandToMaterials(recipes, 100, 3)));
    }

    [Fact]
    public void UnusableIntermediateShared_DemandIsCombined()
    {
        var recipes = new FakeLookup()
            .Add(100, 1, (20, 1), (30, 1))
            .Add(30, 1, (20, 4))
            .Add(20, 1);
        Assert.Equal(new[] { (20u, 5) }, Rows(RecipeExpander.ExpandToMaterials(recipes, 100, 1)));
    }
}
