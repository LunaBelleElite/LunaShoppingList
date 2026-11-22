namespace LunaShoppingList.Core.Tests;

public class CustomListBreakdownTests
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

    [Fact]
    public void CraftableRow_IsReplacedByMaterials()
    {
        var recipes = new FakeLookup().Add(100, 1, (10, 2), (11, 1));
        var r = CustomListOps.Breakdown(recipes, new[] { R(100, 3) });
        Assert.Equal(new[] { R(10, 6), R(11, 3) }, r.ToArray());
    }

    [Fact]
    public void UncraftableRow_PassesThrough()
    {
        var recipes = new FakeLookup().Add(100, 1, (10, 2));
        var r = CustomListOps.Breakdown(recipes, new[] { R(55, 4), R(100, 1) });
        Assert.Equal(new[] { R(55, 4), R(10, 2) }, r.ToArray());
    }

    [Fact]
    public void SharedMaterialsOfTwoRows_Merge_AtFirstSeenPosition()
    {
        var recipes = new FakeLookup().Add(100, 1, (10, 2), (11, 1)).Add(200, 1, (12, 1), (10, 3));
        var r = CustomListOps.Breakdown(recipes, new[] { R(100, 1), R(200, 1) });
        Assert.Equal(new[] { R(10, 5), R(11, 1), R(12, 1) }, r.ToArray());
    }

    [Fact]
    public void MaterialAlsoListedAsPlainRow_Merges()
    {
        var recipes = new FakeLookup().Add(100, 1, (10, 2));
        var r = CustomListOps.Breakdown(recipes, new[] { R(10, 1), R(100, 1) });
        Assert.Equal(new[] { R(10, 3) }, r.ToArray());
    }

    [Fact]
    public void ErrorRow_NoIngredients_PassesThroughAsGiven()
    {
        var recipes = new FakeLookup().Add(100, 1, (0, 2));
        var r = CustomListOps.Breakdown(recipes, new[] { R(100, 3) });
        Assert.Equal(new[] { R(100, 3) }, r.ToArray());
    }

    [Fact]
    public void ErrorRow_TooLarge_PassesThroughAsGiven()
    {
        var recipes = new FakeLookup();
        var ing = new List<(uint, int)>();
        for (uint i = 1; i <= RecipeExpander.MaxCraftableItems + 1; i++)
        {
            ing.Add((1000 + i, 1));
            recipes.Add(1000 + i, 1, (1, 1));
        }
        recipes.Add(100, 1, ing.ToArray());
        var r = CustomListOps.Breakdown(recipes, new[] { R(100, 2), R(7, 1) });
        Assert.Equal(new[] { R(100, 2), R(7, 1) }, r.ToArray());
    }

    [Fact]
    public void SumsSaturateAtMaxQuantity()
    {
        var recipes = new FakeLookup().Add(100, 1, (10, 1)).Add(200, 1, (10, 1));
        var r = CustomListOps.Breakdown(recipes, new[] { R(100, 9_999_999), R(200, 9_999_999), R(10, int.MaxValue) });
        Assert.Equal(new[] { R(10, CustomListOps.MaxQuantity) }, r.ToArray());
    }

    [Fact]
    public void PassThroughDuplicates_Merge()
    {
        var r = CustomListOps.Breakdown(new FakeLookup(), new[] { R(5, 1), R(6, 1), R(5, 2) });
        Assert.Equal(new[] { R(5, 3), R(6, 1) }, r.ToArray());
    }

    [Fact]
    public void InvalidRows_AreDropped_AndEmptyGivesEmpty()
    {
        var r = CustomListOps.Breakdown(new FakeLookup(), new[] { R(0, 3), R(5, 0), R(6, -1) });
        Assert.Empty(r);
        Assert.Empty(CustomListOps.Breakdown(new FakeLookup(), Array.Empty<Requirement>()));
    }

    [Fact]
    public void DoesNotMutateInput()
    {
        var recipes = new FakeLookup().Add(100, 1, (10, 1));
        var rows = new[] { R(100, 1) };
        CustomListOps.Breakdown(recipes, rows);
        Assert.Equal(new[] { R(100, 1) }, rows);
    }
}
