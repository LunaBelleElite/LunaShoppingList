namespace LunaShoppingList.Core.Tests;

public class RecipeExpanderTests
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
        var l = new CustomList { Id = "1", Name = "t" };
        foreach (var r in rows) l.Items.Add(new CustomListItem { ItemId = r.item, Quantity = r.qty });
        return l;
    }

    private static (uint, int)[] Rows(ExpandResult r) => r.Items!.Select(i => (i.ItemId, i.Quantity)).ToArray();

    [Fact]
    public void Yield3Quantity7_Is3Crafts()
    {
        var recipes = new FakeLookup().Add(100, 3, (10, 2), (11, 1));
        var r = RecipeExpander.ExpandOnce(recipes, L((100, 7)), 100);
        Assert.True(r.Ok);
        Assert.Equal(new[] { (10u, 6), (11u, 3) }, Rows(r));
    }

    [Fact]
    public void ExactMultipleOfYield_NoExtraCraft()
    {
        var recipes = new FakeLookup().Add(100, 3, (10, 2));
        Assert.Equal(new[] { (10u, 4) }, Rows(RecipeExpander.ExpandOnce(recipes, L((100, 6)), 100)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void YieldBelowOne_TreatedAsOne(int yield)
    {
        var recipes = new FakeLookup().Add(100, yield, (10, 2));
        Assert.Equal(new[] { (10u, 10) }, Rows(RecipeExpander.ExpandOnce(recipes, L((100, 5)), 100)));
    }

    [Fact]
    public void RemovesRow_MergesIntoExistingRow_AppendsNewInRecipeOrder()
    {
        var recipes = new FakeLookup().Add(100, 1, (30, 1), (10, 2), (20, 3));
        var list = L((10, 5), (100, 2), (40, 1));
        var r = RecipeExpander.ExpandOnce(recipes, list, 100);
        Assert.Equal(new[] { (10u, 9), (40u, 1), (30u, 2), (20u, 6) }, Rows(r));
    }

    [Fact]
    public void DoesNotMutateInputList()
    {
        var recipes = new FakeLookup().Add(100, 1, (10, 2));
        var list = L((10, 5), (100, 2));
        RecipeExpander.ExpandOnce(recipes, list, 100);
        Assert.Equal(new[] { (10u, 5), (100u, 2) }, list.Items.Select(i => (i.ItemId, i.Quantity)).ToArray());
    }

    [Fact]
    public void ResultRowsAreNewObjects()
    {
        var recipes = new FakeLookup().Add(100, 1, (10, 2));
        var list = L((10, 5), (100, 2));
        var r = RecipeExpander.ExpandOnce(recipes, list, 100);
        Assert.DoesNotContain(r.Items!, i => list.Items.Contains(i));
    }

    [Fact]
    public void NonCraftable_Error()
    {
        var r = RecipeExpander.ExpandOnce(new FakeLookup(), L((100, 2)), 100);
        Assert.False(r.Ok);
        Assert.Equal("This item cannot be crafted", r.Error);
    }

    [Fact]
    public void ItemNotOnList_Error()
    {
        var r = RecipeExpander.ExpandOnce(new FakeLookup().Add(100, 1, (10, 1)), L((11, 2)), 100);
        Assert.False(r.Ok);
        Assert.Equal("Item is not on the list", r.Error);
    }

    [Fact]
    public void LargeTotals_ClampedTo9999999()
    {
        var recipes = new FakeLookup().Add(100, 1, (10, 2_000_000_000), (11, 5));
        var r = RecipeExpander.ExpandOnce(recipes, L((100, 9_999_999)), 100);
        Assert.Equal(new[] { (10u, 9_999_999), (11u, 9_999_999) }, Rows(r));
    }

    [Fact]
    public void MergeSaturates()
    {
        var recipes = new FakeLookup().Add(100, 1, (10, 5_000_000));
        var r = RecipeExpander.ExpandOnce(recipes, L((10, 9_000_000), (100, 1)), 100);
        Assert.Equal(new[] { (10u, 9_999_999) }, Rows(r));
    }

    [Fact]
    public void OneLevelOnly_SubCraftsNotExpanded()
    {
        var recipes = new FakeLookup().Add(100, 1, (10, 1)).Add(10, 1, (1, 1));
        Assert.Equal(new[] { (10u, 1) }, Rows(RecipeExpander.ExpandOnce(recipes, L((100, 1)), 100)));
    }

    [Fact]
    public void CrystalIngredients_Included()
    {
        var recipes = new FakeLookup().Add(100, 1, (10, 1), (2, 2), (8, 2));
        Assert.Equal(new[] { (10u, 1), (2u, 2), (8u, 2) }, Rows(RecipeExpander.ExpandOnce(recipes, L((100, 1)), 100)));
    }

    [Fact]
    public void SelfIngredient_Terminates_RowReplacedByAppendedIngredient()
    {
        var recipes = new FakeLookup().Add(100, 1, (100, 2), (10, 1));
        var r = RecipeExpander.ExpandOnce(recipes, L((100, 3), (50, 1)), 100);
        Assert.Equal(new[] { (50u, 1), (100u, 6), (10u, 3) }, Rows(r));
    }

    [Fact]
    public void DuplicateIngredientLines_AreMerged()
    {
        var recipes = new FakeLookup().Add(100, 1, (10, 1), (10, 2));
        Assert.Equal(new[] { (10u, 6) }, Rows(RecipeExpander.ExpandOnce(recipes, L((100, 2)), 100)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoUsableIngredient_Error_ListUnchanged(bool empty)
    {
        var recipes = empty
            ? new FakeLookup().Add(100, 1)
            : new FakeLookup().Add(100, 1, (0, 3), (10, 0), (11, -1));
        var list = L((10, 5), (100, 2));
        var r = RecipeExpander.ExpandOnce(recipes, list, 100);
        Assert.False(r.Ok);
        Assert.Equal("This recipe has no ingredients", r.Error);
        Assert.Equal(new[] { (10u, 5), (100u, 2) }, list.Items.Select(i => (i.ItemId, i.Quantity)).ToArray());
    }

    [Fact]
    public void OneUsableIngredient_IsEnough()
    {
        var recipes = new FakeLookup().Add(100, 1, (0, 3), (11, 2));
        Assert.Equal(new[] { (11u, 2) }, Rows(RecipeExpander.ExpandOnce(recipes, L((100, 1)), 100)));
    }
}
