using System.Text.Json;

namespace LunaShoppingList.Core.Tests;

/// <summary>
/// ArtisanListExport.Build exports the full sub-craft tree (user request 2026-11-22: the Artisan export must include
/// sub-crafts): every craft from RecipeExpander.ExpandToCrafts, intermediates before the items that use them, a shared
/// intermediate once with the combined demand.
/// </summary>
public class ArtisanFullTreeTests
{
    private sealed class FakeRecipes : IRecipeLookup, IRecipeData
    {
        private readonly Dictionary<uint, RecipeInfo> _byItem = new();
        private readonly Dictionary<uint, RecipeInfo> _byId = new();

        // Recipe id == result item id * 10, unless given.
        public FakeRecipes Add(uint item, int yield, params (uint item, int amount)[] ingredients)
            => AddWithId(item * 10, item, yield, ingredients);

        public FakeRecipes AddWithId(uint recipeId, uint item, int yield, params (uint item, int amount)[] ingredients)
        {
            var r = new RecipeInfo(recipeId, item, yield, ingredients.Select(i => new Ingredient(i.item, i.amount)).ToList());
            _byItem[item] = r;
            _byId[recipeId] = r;
            return this;
        }

        /// <summary>Makes the lookup answer <paramref name="item"/> with another item's recipe.</summary>
        public FakeRecipes Alias(uint item, uint sameRecipeAs)
        {
            _byItem[item] = _byItem[sameRecipeAs];
            return this;
        }

        public RecipeInfo? FindRecipeForItem(uint itemId) => _byItem.GetValueOrDefault(itemId);
        public RecipeInfo? GetRecipe(uint recipeId) => _byId.GetValueOrDefault(recipeId);
    }

    private const string Opts = "\"ListItemOptions\":{\"NQOnly\":false,\"Skipping\":false}";
    private const string Tail = "\"ExpandedList\":[],\"SkipLiteral\":false,\"OnlyRestockNonCrafted\":false}";

    private static string Json(params (uint id, int qty)[] entries)
        => "{\"Name\":\"L\",\"Recipes\":[" + string.Join(",", entries.Select(e => $"{{\"ID\":{e.id},\"Quantity\":{e.qty}," + Opts + "}")) + "]," + Tail;

    private static ArtisanExportResult Build(FakeRecipes r, params (uint item, int qty)[] rows)
        => ArtisanListExport.Build("L", rows.Select(x => new Requirement(x.item, x.qty)).ToList(), r);

    [Fact]
    public void ParentWithTwoLevelsOfSubCrafts_ChildrenFirst_CraftsRoundedPerLevel()
    {
        // 7 of item 1 (yield 3) = 3 crafts -> 6 of item 2; item 2 (yield 4) = 2 crafts -> 2 of item 3; item 3 = 2 crafts.
        var r = new FakeRecipes().Add(1, 3, (2, 2), (90, 1)).Add(2, 4, (3, 1)).Add(3, 1, (91, 5));
        var res = Build(r, (1, 7));
        Assert.Equal(Json((30, 2), (20, 2), (10, 3)), res.Json);
        Assert.Equal(1, res.CraftedItems);
        Assert.Empty(res.SkippedItemIds);
    }

    [Fact]
    public void SharedIntermediate_OneEntry_CombinedDemand()
    {
        // Items 1 and 4 each need one of item 2 (yield 2): one craft, not two.
        var r = new FakeRecipes().Add(1, 1, (2, 1)).Add(4, 1, (2, 1)).Add(2, 2, (90, 1));
        var res = Build(r, (1, 1), (4, 1));
        Assert.Equal(Json((20, 1), (10, 1), (40, 1)), res.Json);
        Assert.Equal(2, res.CraftedItems);
    }

    [Fact]
    public void RowThatIsAnIngredientOfAnotherRow_MergedIntoTheTree_NotDroppedNotDoubled()
    {
        // Row item 2 (1) is also needed 5 times by row item 1; item 2 makes 4 per craft: 6 needed -> 2 crafts.
        // Row 2 alone (the tree not expanded) would be 1 craft; rounding the two demands apart would be 1 + 2 = 3.
        var r = new FakeRecipes().Add(1, 1, (2, 5)).Add(2, 4, (90, 1));
        var res = Build(r, (2, 1), (1, 1));
        Assert.Equal(Json((20, 2), (10, 1)), res.Json);
        Assert.Equal(2, res.CraftedItems);
    }

    [Fact]
    public void UncraftableRow_Skipped_TreeOfTheOthersKept()
    {
        var r = new FakeRecipes().Add(1, 1, (2, 1)).Add(2, 1, (90, 1));
        var res = Build(r, (400, 3), (1, 2));
        Assert.Equal(Json((20, 2), (10, 2)), res.Json);
        Assert.Equal(1, res.CraftedItems);
        Assert.Equal(new uint[] { 400 }, res.SkippedItemIds.ToArray());
    }

    [Fact]
    public void IntermediateWithRecipeIdZero_NotWritten_Reported_ItsOwnSubCraftsKept()
    {
        // Item 5's recipe has id 0, which Artisan cannot use; item 6 (a sub-craft of 5) still is exported.
        var r = new FakeRecipes().Add(1, 1, (5, 1)).AddWithId(0, 5, 1, (6, 1)).Add(6, 1, (90, 1));
        var res = Build(r, (1, 1));
        Assert.Equal(Json((60, 1), (10, 1)), res.Json);
        Assert.Equal(1, res.CraftedItems);
        Assert.Equal(new uint[] { 5 }, res.SkippedItemIds.ToArray());
    }

    [Fact]
    public void RowWithRecipeIdZero_StillSkippedAsBefore()
    {
        var r = new FakeRecipes().AddWithId(0, 5, 1, (6, 1)).Add(6, 1, (90, 1)).Add(1, 1, (90, 1));
        var res = Build(r, (5, 1), (1, 1));
        Assert.Equal(Json((10, 1)), res.Json);
        Assert.Equal(1, res.CraftedItems);
        Assert.Equal(new uint[] { 5 }, res.SkippedItemIds.ToArray());
    }

    [Fact]
    public void TwoItemsWithTheSameRecipe_MergedIntoTheFirstEntry()
    {
        var r = new FakeRecipes().Add(8, 1, (90, 1)).Alias(9, 8).Add(7, 1, (91, 1));
        var res = Build(r, (8, 2), (7, 1), (9, 3));
        Assert.Equal(Json((80, 5), (70, 1)), res.Json);
        Assert.Equal(3, res.CraftedItems);
    }

    [Fact]
    public void TooLargeTree_NoJson_ErrorSet()
    {
        var r = new FakeRecipes();
        for (uint i = 0; i <= RecipeExpander.MaxCraftableItems; i++) r.Add(100_000 + i, 1, (100_001 + i, 1));
        var res = Build(r, (100_000, 1));
        Assert.Equal(string.Empty, res.Json);
        Assert.Equal(0, res.CraftedItems);
        Assert.False(string.IsNullOrWhiteSpace(res.Error));
    }

    [Fact]
    public void Error_IsNullOnSuccessAndOnNothingCraftable()
    {
        var r = new FakeRecipes().Add(1, 1, (90, 1));
        Assert.Null(Build(r, (1, 1)).Error);
        Assert.Null(Build(r, (400, 1)).Error);
    }

    [Fact]
    public void TreeExport_MemberNames_AreExactlyTheAllowedSetAtEveryLevel()
    {
        var r = new FakeRecipes().Add(1, 3, (2, 2), (90, 1)).Add(2, 4, (3, 1)).Add(3, 1, (91, 5)).Add(4, 1, (2, 1));
        using var doc = JsonDocument.Parse(Build(r, (1, 7), (4, 2)).Json);
        var root = doc.RootElement;
        Assert.Equal(new[] { "Name", "Recipes", "ExpandedList", "SkipLiteral", "OnlyRestockNonCrafted" }, Names(root));
        Assert.Equal(0, root.GetProperty("ExpandedList").GetArrayLength());
        Assert.Equal(4, root.GetProperty("Recipes").GetArrayLength());
        foreach (var e in root.GetProperty("Recipes").EnumerateArray())
        {
            Assert.Equal(new[] { "ID", "Quantity", "ListItemOptions" }, Names(e));
            Assert.Equal(new[] { "NQOnly", "Skipping" }, Names(e.GetProperty("ListItemOptions")));
        }
        Assert.DoesNotContain("$", doc.RootElement.GetRawText());
    }

    [Fact]
    public void RoundTrip_TreeExportReadBack_GivesTheTopItemOnly()
    {
        // Reading drops entries that are ingredients of other entries, as Artisan's own Teamcraft export does,
        // so the sub-crafts written by Build do not come back as extra items.
        var r = new FakeRecipes().Add(1, 1, (2, 2), (90, 1)).Add(2, 1, (91, 1));
        var parsed = ArtisanListExport.TryParse(Build(r, (1, 3)).Json, r);
        Assert.Null(parsed.Error);
        Assert.Equal(new[] { new Requirement(1, 3) }, parsed.Requirements!.ToArray());
    }

    private static string[] Names(JsonElement e) => e.EnumerateObject().Select(p => p.Name).ToArray();
}
