namespace LunaShoppingList.Core.Tests;

/// <summary>ExportFilter.CraftableOnly: which rows an export (Teamcraft, Artisan) may send. Craftable = the recipe lookup finds a recipe whose id is not 0.</summary>
public class ExportFilterTests
{
    private sealed class FakeRecipes : IRecipeLookup
    {
        private readonly Dictionary<uint, RecipeInfo> _byItem = new();
        public int Calls;

        public FakeRecipes Add(uint recipeId, uint itemId)
        {
            _byItem[itemId] = new RecipeInfo(recipeId, itemId, 1, Array.Empty<Ingredient>());
            return this;
        }

        public RecipeInfo? FindRecipeForItem(uint itemId)
        {
            Calls++;
            return _byItem.TryGetValue(itemId, out var r) ? r : null;
        }
    }

    // 100, 200 craftable; 700 has a recipe with id 0 (unusable); 400, 500, 600 have no recipe.
    private static FakeRecipes Recipes() => new FakeRecipes().Add(1000, 100).Add(2000, 200).Add(0, 700);

    private static Requirement R(uint id, int q) => new(id, q);

    [Fact]
    public void AllCraftable_KeepsEveryRowInOrder_NothingSkipped()
    {
        var r = ExportFilter.CraftableOnly(new[] { R(200, 3), R(100, 1) }, Recipes());

        Assert.Equal(new[] { R(200, 3), R(100, 1) }, r.Rows);
        Assert.Empty(r.SkippedItemIds);
    }

    [Fact]
    public void MixWithRawMaterials_DropsRaw_KeepsOrder_ReportsSkippedOnceInFirstSeenOrder()
    {
        var r = ExportFilter.CraftableOnly(
            new[] { R(500, 2), R(200, 3), R(400, 1), R(100, 1), R(500, 9), R(600, 4) }, Recipes());

        Assert.Equal(new[] { R(200, 3), R(100, 1) }, r.Rows);
        Assert.Equal(new uint[] { 500, 400, 600 }, r.SkippedItemIds);
    }

    [Fact]
    public void NoneCraftable_NoRows_AllSkipped()
    {
        var r = ExportFilter.CraftableOnly(new[] { R(400, 1), R(500, 2) }, Recipes());

        Assert.Empty(r.Rows);
        Assert.Equal(new uint[] { 400, 500 }, r.SkippedItemIds);
    }

    [Fact]
    public void EmptyRows_NothingAtAll()
    {
        var r = ExportFilter.CraftableOnly(Array.Empty<Requirement>(), Recipes());

        Assert.Empty(r.Rows);
        Assert.Empty(r.SkippedItemIds);
    }

    [Fact]
    public void DuplicateCraftableRows_AreKeptAsGiven()
    {
        var r = ExportFilter.CraftableOnly(new[] { R(100, 1), R(200, 1), R(100, 2) }, Recipes());

        Assert.Equal(new[] { R(100, 1), R(200, 1), R(100, 2) }, r.Rows);
        Assert.Empty(r.SkippedItemIds);
    }

    [Fact]
    public void RecipeIdZero_CountsAsNotCraftable()
    {
        var r = ExportFilter.CraftableOnly(new[] { R(700, 1), R(100, 1) }, Recipes());

        Assert.Equal(new[] { R(100, 1) }, r.Rows);
        Assert.Equal(new uint[] { 700 }, r.SkippedItemIds);
    }

    [Fact]
    public void ItemIdZero_AndQuantityBelowOne_AreIgnored_NeitherKeptNorReported()
    {
        var recipes = Recipes().Add(3000, 0);
        var r = ExportFilter.CraftableOnly(new[] { R(0, 1), R(100, 0), R(400, -1), R(200, 1) }, recipes);

        Assert.Equal(new[] { R(200, 1) }, r.Rows);
        Assert.Empty(r.SkippedItemIds);
    }

    [Fact]
    public void AgreesWithArtisanExport_AboutWhichRowsAreSkipped()
    {
        var rows = new[] { R(500, 2), R(200, 3), R(700, 1), R(100, 1), R(500, 9) };
        var recipes = Recipes();

        var filtered = ExportFilter.CraftableOnly(rows, recipes);
        var artisan = ArtisanListExport.Build("x", rows, recipes);

        Assert.Equal(artisan.SkippedItemIds, filtered.SkippedItemIds);
    }
}
