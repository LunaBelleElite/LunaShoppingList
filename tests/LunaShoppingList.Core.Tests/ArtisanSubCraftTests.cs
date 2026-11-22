using System.Text.Json;

namespace LunaShoppingList.Core.Tests;

/// <summary>
/// An Artisan list made with sub-crafts holds the intermediates as their own entries. Like Artisan's own Teamcraft export,
/// an entry whose result item is a direct ingredient of another (non-Skip) entry is dropped, in CraftedItems and TryParse.
/// </summary>
public class ArtisanSubCraftTests
{
    private sealed class FakeRecipes : IRecipeData
    {
        private readonly Dictionary<uint, RecipeInfo> _r = new();
        // recipe id == result item id * 10 for readability
        public FakeRecipes Add(uint item, params uint[] ingredients)
        {
            _r[item * 10] = new RecipeInfo(item * 10, item, 1, ingredients.Select(i => new Ingredient(i, 2)).ToList());
            return this;
        }
        public RecipeInfo? GetRecipe(uint recipeId) => _r.GetValueOrDefault(recipeId);
    }

    private static CraftItem C(uint item, int crafts = 1, bool skip = false) => new(item * 10, crafts, skip, false);
    private static Requirement[] Items(FakeRecipes r, params CraftItem[] items)
        => ArtisanListItems.CraftedItems(new CraftingList(1, "L", items), r).ToArray();

    [Fact]
    public void ParentAndIntermediate_OnlyParentRemains()
    {
        var r = new FakeRecipes().Add(1, 2, 90).Add(2, 91);
        Assert.Equal(new[] { new Requirement(1, 1) }, Items(r, C(1), C(2, 2)));
    }

    [Fact]
    public void Chain_OnlyTopRemains()
    {
        var r = new FakeRecipes().Add(1, 2).Add(2, 3).Add(3, 90);
        Assert.Equal(new[] { new Requirement(1, 1) }, Items(r, C(1), C(2), C(3)));
    }

    [Fact]
    public void SharedIntermediate_UsedByTwoParents_IsDroppedOnce()
    {
        var r = new FakeRecipes().Add(1, 3).Add(2, 3).Add(3, 90);
        Assert.Equal(new[] { new Requirement(1, 1), new Requirement(2, 1) }, Items(r, C(1), C(2), C(3)));
    }

    [Fact]
    public void EntryThatIsNobodysIngredient_Stays()
    {
        var r = new FakeRecipes().Add(1, 2).Add(2, 90).Add(5, 91);
        Assert.Equal(new[] { new Requirement(1, 1), new Requirement(5, 1) }, Items(r, C(1), C(2), C(5)));
    }

    [Fact]
    public void OnlyDirectIngredientsCount_NotAnythingElseInTheList()
    {
        // 2 is an ingredient of 1 but 3 is an ingredient of nothing in the list: 3 stays.
        var r = new FakeRecipes().Add(1, 2).Add(2, 90).Add(3, 2);
        Assert.Equal(new[] { new Requirement(1, 1), new Requirement(3, 1) }, Items(r, C(1), C(2), C(3)));
    }

    [Fact]
    public void SkippedParent_DoesNotDropItsIngredient()
    {
        var r = new FakeRecipes().Add(1, 2).Add(2, 90);
        Assert.Equal(new[] { new Requirement(2, 1) }, Items(r, C(1, skip: true), C(2)));
    }

    [Fact]
    public void SkippedIntermediate_IsNotCountedAndDoesNotHideAnything()
    {
        var r = new FakeRecipes().Add(1, 2).Add(2, 3).Add(3, 90);
        // 2 is skipped, so it neither drops 3 nor is counted; 1 drops 2 (skipped anyway); 3 stays.
        Assert.Equal(new[] { new Requirement(1, 1), new Requirement(3, 1) }, Items(r, C(1), C(2, skip: true), C(3)));
    }

    [Fact]
    public void RecipeThatNeedsItself_IsKept()
    {
        var r = new FakeRecipes().Add(1, 1, 90).Add(2, 91);
        Assert.Equal(new[] { new Requirement(1, 1), new Requirement(2, 1) }, Items(r, C(1), C(2)));
    }

    [Fact]
    public void MutualCycle_KeepsEverything()
    {
        var r = new FakeRecipes().Add(1, 2).Add(2, 1);
        Assert.Equal(new[] { new Requirement(1, 1), new Requirement(2, 1) }, Items(r, C(1), C(2)));
    }

    [Fact]
    public void SubCraftRecipeIds_ReportsTheDroppedRecipes()
    {
        var r = new FakeRecipes().Add(1, 2).Add(2, 3).Add(3, 90);
        var drop = ArtisanListItems.SubCraftRecipeIds(new uint[] { 10, 20, 30 }, r);
        Assert.Equal(new uint[] { 20, 30 }, drop.OrderBy(x => x).ToArray());
        Assert.Empty(ArtisanListItems.SubCraftRecipeIds(new uint[] { 10 }, r));
    }

    // ---------- TryParse ----------

    private static string Json(params uint[] items)
        => "{\"Name\":\"L\",\"Recipes\":[" + string.Join(",", items.Select(i => $"{{\"ID\":{i * 10},\"Quantity\":1}}")) + "]}";

    [Fact]
    public void TryParse_DropsIntermediates_SilentlyNotInUnmatched()
    {
        var r = new FakeRecipes().Add(1, 2, 90).Add(2, 91);
        var res = ArtisanListExport.TryParse(Json(1, 2), r);
        Assert.Null(res.Error);
        Assert.Equal(new[] { new Requirement(1, 1) }, res.Requirements!.ToArray());
        Assert.Empty(res.Unmatched);
    }

    [Fact]
    public void TryParse_Chain_OnlyTopRemains()
    {
        var r = new FakeRecipes().Add(1, 2).Add(2, 3).Add(3, 90);
        Assert.Equal(new[] { new Requirement(1, 1) }, ArtisanListExport.TryParse(Json(1, 2, 3), r).Requirements!.ToArray());
    }

    [Fact]
    public void TryParse_Cycle_KeepsEverything()
    {
        var r = new FakeRecipes().Add(1, 2).Add(2, 1);
        Assert.Equal(2, ArtisanListExport.TryParse(Json(1, 2), r).Requirements!.Count);
    }

    [Fact]
    public void TryParse_SkippedParent_DoesNotDrop()
    {
        var r = new FakeRecipes().Add(1, 2).Add(2, 90);
        var json = "{\"Recipes\":[{\"ID\":10,\"Quantity\":1,\"ListItemOptions\":{\"Skipping\":true}},{\"ID\":20,\"Quantity\":1}]}";
        Assert.Equal(new[] { new Requirement(2, 1) }, ArtisanListExport.TryParse(json, r).Requirements!.ToArray());
    }
}
