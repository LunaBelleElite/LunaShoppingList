namespace LunaShoppingList.Core.Tests;

/// <summary>
/// RecipeExpander.ExpandToCrafts: the crafts (recipe and count) that make a set of items, all roots together, so a
/// shared intermediate is crafted once with the combined demand rounded up once. Entries are in crafting order.
/// </summary>
public class RecipeExpanderCraftsTests
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

    private static Requirement[] Roots(params (uint item, int qty)[] rows) => rows.Select(r => new Requirement(r.item, r.qty)).ToArray();

    private static (uint Item, uint Recipe, int Crafts)[] Crafts(FakeLookup recipes, params (uint item, int qty)[] roots)
    {
        var r = RecipeExpander.ExpandToCrafts(recipes, Roots(roots));
        Assert.True(r.Ok, r.Error);
        return r.Crafts!.Select(c => (c.ItemId, c.RecipeId, c.Crafts)).ToArray();
    }

    [Fact]
    public void SingleRoot_TwoLevels_YieldRoundedUpAtEachLevel_ChildFirst()
    {
        // 7 of 100 (yield 3) = 3 crafts, needing 2 x 3 = 6 of 20; 20 (yield 4) = 2 crafts. Raw 1 is not an entry.
        var recipes = new FakeLookup().Add(100, 3, (20, 2)).Add(20, 4, (1, 5));
        Assert.Equal(new[] { (20u, 5020u, 2), (100u, 5100u, 3) }, Crafts(recipes, (100, 7)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void YieldBelowOne_TreatedAsOne(int yield)
    {
        var recipes = new FakeLookup().Add(100, yield, (1, 2));
        Assert.Equal(new[] { (100u, 5100u, 5) }, Crafts(recipes, (100, 5)));
    }

    [Fact]
    public void SharedIntermediate_OfTwoRoots_CraftedOnce_CombinedDemandRoundedOnce()
    {
        // 100 and 200 each need one 20; 20 makes 10 per craft. Combined demand 2 -> 1 craft (per root would be 2).
        var recipes = new FakeLookup().Add(100, 1, (20, 1), (1, 1)).Add(200, 1, (20, 1)).Add(20, 10, (2, 1));
        Assert.Equal(new[] { (20u, 5020u, 1), (100u, 5100u, 1), (200u, 5200u, 1) }, Crafts(recipes, (100, 1), (200, 1)));
    }

    [Fact]
    public void RootThatIsAlsoIngredientOfAnotherRoot_ListedAfter_MergedWithCombinedDemand()
    {
        // 100 needs one 20; 20 is also asked for directly (1). 20 makes 2 per craft: 2 needed -> 1 craft, not 1 + 1.
        var recipes = new FakeLookup().Add(100, 1, (20, 1)).Add(20, 2, (1, 1));
        Assert.Equal(new[] { (20u, 5020u, 1), (100u, 5100u, 1) }, Crafts(recipes, (100, 1), (20, 1)));
    }

    [Fact]
    public void RootThatIsAlsoIngredientOfAnotherRoot_ListedBefore_StillChildFirst()
    {
        var recipes = new FakeLookup().Add(100, 1, (20, 1)).Add(20, 2, (1, 1));
        Assert.Equal(new[] { (20u, 5020u, 1), (100u, 5100u, 1) }, Crafts(recipes, (20, 1), (100, 1)));
    }

    [Fact]
    public void DuplicateRoots_CombinedBeforeRounding()
    {
        // Three rows of one at yield 2: 3 -> 2 crafts. Rounding each row would be 3; keeping only one row would be 1.
        var recipes = new FakeLookup().Add(20, 2, (1, 1));
        Assert.Equal(new[] { (20u, 5020u, 2) }, Crafts(recipes, (20, 1), (20, 1), (20, 1)));
    }

    [Fact]
    public void Order_IsCraftingOrder_EachItemAfterEverythingItUses()
    {
        // 100 uses 20 then 40; 20 uses 30. Children first, in ingredient order: 30, 20, 40, 100.
        var recipes = new FakeLookup().Add(100, 1, (20, 1), (40, 1)).Add(20, 1, (30, 1)).Add(30, 1, (1, 1)).Add(40, 1, (2, 1));
        Assert.Equal(new uint[] { 30, 20, 40, 100 }, Crafts(recipes, (100, 1)).Select(c => c.Item).ToArray());
    }

    [Fact]
    public void Order_IndependentRoots_KeepTheirGivenOrder()
    {
        var recipes = new FakeLookup().Add(300, 1, (1, 1)).Add(100, 1, (2, 1)).Add(200, 1, (3, 1));
        Assert.Equal(new uint[] { 300, 100, 200 }, Crafts(recipes, (300, 1), (100, 1), (200, 1)).Select(c => c.Item).ToArray());
    }

    [Fact]
    public void Cycle_Terminates_RepeatedItemIsNotRecursedInto()
    {
        // 100 needs 20; 20 needs 100 x2 (a loop) and raw 2. 100 is not expanded again.
        var recipes = new FakeLookup().Add(100, 1, (20, 1)).Add(20, 1, (100, 2), (2, 1));
        Assert.Equal(new[] { (20u, 5020u, 1), (100u, 5100u, 1) }, Crafts(recipes, (100, 1)));
    }

    [Fact]
    public void RootsWithoutRecipe_AndRawMaterials_AreNotEntries()
    {
        var recipes = new FakeLookup().Add(100, 1, (1, 4));
        Assert.Equal(new[] { (100u, 5100u, 2) }, Crafts(recipes, (7, 5), (100, 2), (1, 3)));
    }

    [Fact]
    public void InvalidRoots_Ignored()
    {
        var recipes = new FakeLookup().Add(100, 1, (1, 1)).Add(0, 1, (1, 1));
        Assert.Empty(Crafts(recipes, (0, 5), (100, 0), (100, -3)));
    }

    [Fact]
    public void EmptyRoots_OkAndEmpty()
    {
        var r = RecipeExpander.ExpandToCrafts(new FakeLookup(), Array.Empty<Requirement>());
        Assert.True(r.Ok);
        Assert.Empty(r.Crafts!);
    }

    [Fact]
    public void RootWithNoUsableIngredient_IsStillAnEntry_IntermediateLikeThatIsNot()
    {
        // 100's recipe lists nothing usable: asked for directly it is crafted. 20 has the same kind of recipe but is
        // only an ingredient, so (as in ExpandToMaterials) it is gathered like a raw material and not crafted.
        var recipes = new FakeLookup().Add(100, 1, (0, 3)).Add(200, 1, (20, 1)).Add(20, 1, (5, 0));
        Assert.Equal(new[] { (100u, 5100u, 2), (200u, 5200u, 1) }, Crafts(recipes, (100, 2), (200, 1)));
    }

    [Fact]
    public void Crafts_ClampedTo9999999_RootAndIntermediate()
    {
        // 20 is needed 2,000,000,000 per craft of 100: far above the clamp, never wraps.
        var recipes = new FakeLookup().Add(100, 1, (20, 2_000_000_000)).Add(20, 1, (1, 1));
        Assert.Equal(new[] { (20u, 5020u, 9_999_999), (100u, 5100u, 9_999_999) }, Crafts(recipes, (100, int.MaxValue), (100, int.MaxValue)));
    }

    [Fact]
    public void DeepMultiplication_DoesNotOverflowIntoSmallNumber()
    {
        // 30 levels each needing 1,000 of the next: the demand saturates instead of wrapping.
        var f = new FakeLookup();
        for (uint i = 0; i < 30; i++) f.Add(1000 + i, 1, (1001 + i, 1000));
        var crafts = Crafts(f, (1000, 1));
        Assert.Equal(30, crafts.Length);
        Assert.All(crafts.Take(27), c => Assert.Equal(9_999_999, c.Crafts));
        Assert.Equal(new[] { (1002u, 6002u, 1_000_000), (1001u, 6001u, 1_000), (1000u, 6000u, 1) }, crafts.Skip(27).ToArray());
    }

    private static FakeLookup Chains(params (uint start, int length)[] chains)
    {
        var f = new FakeLookup();
        foreach (var (start, length) in chains)
            for (var i = 0; i < length; i++)
                f.Add(start + (uint)i, 1, (start + (uint)i + 1, 1));
        return f;
    }

    [Fact]
    public void Cap_CountsAllRootsTogether_ErrorOverIt()
    {
        // Two separate chains of 2,600 craftable items: each alone is under the cap, together they are over it.
        var f = Chains((100_000, 2_600), (200_000, 2_600));
        Assert.True(RecipeExpander.ExpandToCrafts(f, Roots((100_000, 1))).Ok);
        var r = RecipeExpander.ExpandToCrafts(f, Roots((100_000, 1), (200_000, 1)));
        Assert.False(r.Ok);
        Assert.Null(r.Crafts);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    [Fact]
    public void Cap_ExactlyAtCap_Succeeds()
    {
        var f = Chains((100_000, 2_500), (200_000, 2_500));
        var r = RecipeExpander.ExpandToCrafts(f, Roots((100_000, 1), (200_000, 1)));
        Assert.True(r.Ok);
        Assert.Equal(RecipeExpander.MaxCraftableItems, r.Crafts!.Count);
    }
}
