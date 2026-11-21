namespace LunaShoppingList.Core.Tests;

public class MaterialsCalculatorTests
{
    private sealed class FakeRecipes : IRecipeData
    {
        private readonly Dictionary<uint, RecipeInfo> _map = new();
        public FakeRecipes Add(uint id, int yield, params (uint item, int amount)[] ing)
        {
            _map[id] = new RecipeInfo(id, id + 1000, yield,
                ing.Select(i => new Ingredient(i.item, i.amount)).ToList());
            return this;
        }
        public RecipeInfo? GetRecipe(uint recipeId) => _map.TryGetValue(recipeId, out var r) ? r : null;
    }

    private static CraftingList List(params CraftItem[] items) => new(1, "t", items);
    private static CraftItem Item(uint recipe, int count, bool skip = false) => new(recipe, count, skip, false);
    private static (uint, int)[] Flat(IReadOnlyList<Requirement> r) => r.Select(x => (x.ItemId, x.Quantity)).ToArray();

    [Fact]
    public void EmptyList_ReturnsEmpty()
        => Assert.Empty(new MaterialsCalculator(new FakeRecipes()).Compute(List()));

    [Fact]
    public void AllSkipped_ReturnsEmpty()
    {
        var calc = new MaterialsCalculator(new FakeRecipes().Add(1, 1, (10, 2)));
        Assert.Empty(calc.Compute(List(Item(1, 5, true), Item(1, 3, true))));
    }

    [Fact]
    public void ZeroAndNegativeCraftCount_Ignored()
    {
        var calc = new MaterialsCalculator(new FakeRecipes().Add(1, 1, (10, 2)));
        Assert.Empty(calc.Compute(List(Item(1, 0), Item(1, -4))));
    }

    [Fact]
    public void NegativeCraftCount_DoesNotReduceOtherEntries()
    {
        var calc = new MaterialsCalculator(new FakeRecipes().Add(1, 1, (10, 2)));
        Assert.Equal(new[] { (10u, 6) }, Flat(calc.Compute(List(Item(1, 3), Item(1, -2)))));
    }

    [Fact]
    public void AmountTimesCraftCount()
    {
        var calc = new MaterialsCalculator(new FakeRecipes().Add(1, 1, (10, 3), (11, 1)));
        Assert.Equal(new[] { (10u, 12), (11u, 4) }, Flat(calc.Compute(List(Item(1, 4)))));
    }

    [Fact]
    public void SameIngredientAcrossRecipes_Summed()
    {
        var calc = new MaterialsCalculator(new FakeRecipes().Add(1, 1, (10, 2)).Add(2, 1, (10, 5), (12, 1)));
        Assert.Equal(new[] { (10u, 2 * 3 + 5 * 2), (12u, 2) }, Flat(calc.Compute(List(Item(1, 3), Item(2, 2)))));
    }

    [Fact]
    public void DuplicateRecipeEntries_Summed()
    {
        var calc = new MaterialsCalculator(new FakeRecipes().Add(1, 1, (10, 2)));
        Assert.Equal(new[] { (10u, 10) }, Flat(calc.Compute(List(Item(1, 3), Item(1, 2)))));
    }

    [Fact]
    public void OrderedByItemIdAscending()
    {
        var calc = new MaterialsCalculator(new FakeRecipes().Add(1, 1, (30, 1), (10, 1)).Add(2, 1, (20, 1), (5, 1)));
        Assert.Equal(new[] { (5u, 1), (10u, 1), (20u, 1), (30u, 1) }, Flat(calc.Compute(List(Item(1, 1), Item(2, 1)))));
    }

    [Fact]
    public void ZeroAmountIngredient_NeverAppears()
    {
        var calc = new MaterialsCalculator(new FakeRecipes().Add(1, 1, (10, 0), (11, 2)));
        Assert.Equal(new[] { (11u, 2) }, Flat(calc.Compute(List(Item(1, 1)))));
    }

    [Fact]
    public void Yield_Ignored()
    {
        var calc = new MaterialsCalculator(new FakeRecipes().Add(1, 3, (10, 2)));
        Assert.Equal(new[] { (10u, 8) }, Flat(calc.Compute(List(Item(1, 4)))));
    }

    [Fact]
    public void UnknownRecipe_ThrowsWithId()
    {
        var calc = new MaterialsCalculator(new FakeRecipes());
        var ex = Assert.Throws<InvalidOperationException>(() => calc.Compute(List(Item(31337, 1))));
        Assert.Contains("31337", ex.Message);
    }

    [Fact]
    public void UnknownRecipe_ThrowsEvenAfterKnownOnes()
    {
        var calc = new MaterialsCalculator(new FakeRecipes().Add(1, 1, (10, 1)));
        Assert.Throws<InvalidOperationException>(() => calc.Compute(List(Item(1, 1), Item(99, 1))));
    }

    [Fact]
    public void SkippedUnknownRecipe_DoesNotThrow()
    {
        var calc = new MaterialsCalculator(new FakeRecipes().Add(1, 1, (10, 1)));
        Assert.Equal(new[] { (10u, 1) }, Flat(calc.Compute(List(Item(99, 1, true), Item(1, 1)))));
    }

    [Fact]
    public void ZeroCountUnknownRecipe_DoesNotThrow()
    {
        var calc = new MaterialsCalculator(new FakeRecipes());
        Assert.Empty(calc.Compute(List(Item(99, 0), Item(98, -1))));
    }

    [Fact]
    public void SubCraftIntermediate_AppearsAsPlainIngredient_NotExpanded()
    {
        // recipe 2's result (item 1002) is an ingredient of recipe 1; it must not be expanded via recipe 2.
        var calc = new MaterialsCalculator(new FakeRecipes().Add(1, 1, (1002, 2)).Add(2, 1, (50, 9)));
        Assert.Equal(new[] { (1002u, 2) }, Flat(calc.Compute(List(Item(1, 1)))));
    }

    [Fact]
    public void Overflow_Throws()
    {
        var calc = new MaterialsCalculator(new FakeRecipes().Add(1, 1, (10, int.MaxValue)));
        Assert.Throws<OverflowException>(() => calc.Compute(List(Item(1, 2))));
    }

    [Fact]
    public void OverflowAcrossSummation_Throws()
    {
        var calc = new MaterialsCalculator(new FakeRecipes().Add(1, 1, (10, int.MaxValue)));
        Assert.Throws<OverflowException>(() => calc.Compute(List(Item(1, 1), Item(1, 1))));
    }

    [Fact]
    public void Constructor_NullRecipes_Throws()
        => Assert.Throws<ArgumentNullException>(() => new MaterialsCalculator(null!));

    [Fact]
    public void Compute_NullList_Throws()
        => Assert.Throws<ArgumentNullException>(() => new MaterialsCalculator(new FakeRecipes()).Compute(null!));

    [Fact]
    public void IngredientWhoseItemIdEqualsAnotherRecipesId_IsNotExpandedAsThatRecipe()
    {
        // Recipe 7 exists and produces item 1007. Recipe 1 uses item 7 (a recipe id collision) and item 1007
        // (recipe 7's result). Neither may be treated as a reference to recipe 7: a lookup by ingredient item id
        // (a plausible bug) would pull in item 50 here.
        var calc = new MaterialsCalculator(new FakeRecipes().Add(1, 1, (7, 2), (1007, 3)).Add(7, 1, (50, 9)));
        Assert.Equal(new[] { (7u, 2), (1007u, 3) }, Flat(calc.Compute(List(Item(1, 1)))));
    }

    [Fact]
    public void RecipeLookupIsCalledOnlyForListedRecipeIds()
    {
        var asked = new List<uint>();
        var calc = new MaterialsCalculator(new SpyRecipes(asked, new FakeRecipes().Add(1, 1, (7, 2)).Add(7, 1, (50, 9))));
        calc.Compute(List(Item(1, 1)));
        Assert.Equal(new uint[] { 1 }, asked);
    }

    private sealed class SpyRecipes(List<uint> asked, IRecipeData inner) : IRecipeData
    {
        public RecipeInfo? GetRecipe(uint recipeId) { asked.Add(recipeId); return inner.GetRecipe(recipeId); }
    }
}
