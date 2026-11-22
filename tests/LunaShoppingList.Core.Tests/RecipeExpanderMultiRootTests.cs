namespace LunaShoppingList.Core.Tests;

/// <summary>
/// RecipeExpander.ExpandToMaterials over several roots at once: the raw materials of all of them expanded together, so a
/// shared intermediate is crafted once with the combined demand. Roots the single-item overload would reject are skipped.
/// </summary>
public class RecipeExpanderMultiRootTests
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

    private static (uint, int)[] Expand(FakeLookup recipes, params (uint item, int qty)[] roots)
    {
        var r = RecipeExpander.ExpandToMaterials(recipes, roots.Select(x => new Requirement(x.item, x.qty)).ToList());
        Assert.True(r.Ok, r.Error);
        return r.Items!.Select(i => (i.ItemId, i.Quantity)).ToArray();
    }

    [Fact]
    public void SharedIntermediate_CombinedDemandRoundedOnce_ByItemIdAscending()
    {
        var recipes = new FakeLookup().Add(100, 1, (20, 1), (7, 2)).Add(200, 1, (20, 1), (3, 1)).Add(20, 10, (5, 1));
        Assert.Equal(new[] { (3u, 1), (5u, 1), (7u, 2) }, Expand(recipes, (100, 1), (200, 1)));
    }

    [Fact]
    public void SingleRoot_SameAsSingleItemOverload()
    {
        var recipes = new FakeLookup().Add(100, 3, (20, 2), (4, 1)).Add(20, 4, (1, 5));
        var single = RecipeExpander.ExpandToMaterials(recipes, 100, 7);
        Assert.Equal(single.Items!.Select(i => (i.ItemId, i.Quantity)).ToArray(), Expand(recipes, (100, 7)));
    }

    [Fact]
    public void RejectedRoots_Skipped_NoRecipeNoIngredientsItemZeroQuantityBelowOne()
    {
        var recipes = new FakeLookup().Add(100, 1, (1, 1)).Add(400, 1, (0, 1)).Add(0, 1, (2, 1));
        Assert.Equal(new[] { (1u, 1) }, Expand(recipes, (300, 1), (400, 1), (0, 1), (100, 0), (100, 1)));
    }

    [Fact]
    public void RootWithNoIngredients_StillGatheredWhenAnotherRootNeedsIt()
    {
        var recipes = new FakeLookup().Add(400, 1, (0, 1)).Add(500, 1, (400, 2));
        Assert.Equal(new[] { (400u, 2) }, Expand(recipes, (400, 5), (500, 1)));
    }

    [Fact]
    public void NoRoots_OkAndEmpty()
    {
        Assert.Empty(Expand(new FakeLookup()));
    }

    [Fact]
    public void Cap_CountsAllRootsTogether()
    {
        var f = new FakeLookup();
        foreach (var start in new uint[] { 100_000, 200_000 })
            for (uint i = 0; i < 2_600; i++) f.Add(start + i, 1, (start + i + 1, 1));
        var r = RecipeExpander.ExpandToMaterials(f, new[] { new Requirement(100_000, 1), new Requirement(200_000, 1) });
        Assert.False(r.Ok);
        Assert.Null(r.Items);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }
}
