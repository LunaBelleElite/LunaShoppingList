namespace LunaShoppingList.Core.Tests;

/// <summary>ArtisanListItems.CraftedItems: an Artisan list's crafts as the items they make.</summary>
public class ArtisanListItemsTests
{
    private sealed class FakeRecipes : IRecipeData
    {
        private readonly Dictionary<uint, RecipeInfo> _r = new()
        {
            [1] = new RecipeInfo(1, 100, 1, Array.Empty<Ingredient>()),
            [2] = new RecipeInfo(2, 200, 3, Array.Empty<Ingredient>()),
            [3] = new RecipeInfo(3, 100, 2, Array.Empty<Ingredient>()),   // same item as recipe 1
            [4] = new RecipeInfo(4, 300, 0, Array.Empty<Ingredient>()),   // yield 0 counts as 1
            [5] = new RecipeInfo(5, 0, 1, Array.Empty<Ingredient>()),     // makes item 0
            [6] = new RecipeInfo(6, 400, 5_000_000, Array.Empty<Ingredient>()),
        };
        public RecipeInfo? GetRecipe(uint recipeId) => _r.GetValueOrDefault(recipeId);
    }

    private static CraftItem C(uint recipe, int crafts, bool skip = false) => new(recipe, crafts, skip, false);
    private static CraftingList L(params CraftItem[] items) => new(1, "L", items);
    private static Requirement[] Run(params CraftItem[] items) => ArtisanListItems.CraftedItems(L(items), new FakeRecipes()).ToArray();

    [Fact]
    public void CraftsTimesYield()
        => Assert.Equal(new[] { new Requirement(200, 12) }, Run(C(2, 4)));

    [Fact]
    public void YieldBelowOne_CountsAsOne()
        => Assert.Equal(new[] { new Requirement(300, 7) }, Run(C(4, 7)));

    [Fact]
    public void SkippingEntries_AreLeftOut()
        => Assert.Equal(new[] { new Requirement(200, 3) }, Run(C(1, 5, skip: true), C(2, 1)));

    [Fact]
    public void UnknownRecipe_ItemZero_AndNonPositiveCrafts_AreLeftOut()
        => Assert.Empty(Run(C(99, 1), C(5, 1), C(1, 0), C(1, -2)));

    [Fact]
    public void SameItemFromTwoRecipes_MergesAtFirstPosition()
        => Assert.Equal(new[] { new Requirement(100, 1 + 2 * 3), new Requirement(200, 3) }, Run(C(1, 1), C(2, 1), C(3, 3)));

    [Fact]
    public void Total_SaturatesAtMaxQuantity()
        => Assert.Equal(new[] { new Requirement(400, CustomListOps.MaxQuantity) }, Run(C(6, 3), C(6, 2_000_000)));

    [Fact]
    public void SingleLargeEntry_SaturatesAtMaxQuantity()
        => Assert.Equal(new[] { new Requirement(400, CustomListOps.MaxQuantity) }, Run(C(6, 9_999_999)));

    [Fact]
    public void EmptyList_GivesEmpty()
        => Assert.Empty(Run());
}
