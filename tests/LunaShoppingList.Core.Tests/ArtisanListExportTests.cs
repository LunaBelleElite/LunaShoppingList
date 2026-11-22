using System.Text.Json;

namespace LunaShoppingList.Core.Tests;

/// <summary>
/// ArtisanListExport: the JSON Artisan's "Import List From Clipboard (Artisan Export)" accepts, and reading
/// Artisan's "Export List" JSON back. Artisan deserialises with Json.NET MissingMemberHandling.Error, so one
/// unknown member (including a "$type" that is not first) makes the whole import fail; the member-set test below
/// keeps any extra member from slipping in.
/// </summary>
public class ArtisanListExportTests
{
    private sealed class FakeRecipes : IRecipeLookup, IRecipeData
    {
        private readonly Dictionary<uint, RecipeInfo> _byId = new();
        private readonly Dictionary<uint, RecipeInfo> _byItem = new();

        public FakeRecipes Add(uint recipeId, uint itemId, int yield, bool indexByItem = true)
        {
            var r = new RecipeInfo(recipeId, itemId, yield, Array.Empty<Ingredient>());
            _byId[recipeId] = r;
            if (indexByItem)
                _byItem[itemId] = r;
            return this;
        }

        public RecipeInfo? FindRecipeForItem(uint itemId) => _byItem.TryGetValue(itemId, out var r) ? r : null;
        public RecipeInfo? GetRecipe(uint recipeId) => _byId.TryGetValue(recipeId, out var r) ? r : null;
    }

    // Item 100 -> recipe 1000 (yield 1); 200 -> 2000 (yield 3); 300 -> 3000 (yield 0, treated as 1);
    // 500 -> 5000 (yield 2); 600 -> 6000 (yield -2, treated as 1); 700 -> recipe id 0 (unusable);
    // recipe 1001 also makes item 100 but is not the lookup's pick; recipe 8000 makes item 0.
    // Item 400 has no recipe.
    private static FakeRecipes Recipes() => new FakeRecipes()
        .Add(1000, 100, 1).Add(2000, 200, 3).Add(3000, 300, 0).Add(5000, 500, 2).Add(6000, 600, -2)
        .Add(0, 700, 1).Add(1001, 100, 1, indexByItem: false).Add(8000, 0, 1, indexByItem: false);

    private static ArtisanExportResult Build(string name, params (uint Item, int Qty)[] rows)
        => ArtisanListExport.Build(name, rows.Select(r => new Requirement(r.Item, r.Qty)).ToList(), Recipes());

    private static ArtisanImportResult Parse(string json) => ArtisanListExport.TryParse(json, Recipes());

    private const string Opts = "\"ListItemOptions\":{\"NQOnly\":false,\"Skipping\":false}";
    private const string Tail = "\"ExpandedList\":[],\"SkipLiteral\":false,\"OnlyRestockNonCrafted\":false}";

    // ---------- Build ----------

    [Fact]
    public void Build_Golden_ExactCompactJson()
    {
        var r = Build("Gear", (100, 2), (200, 7));

        Assert.Equal(
            "{\"Name\":\"Gear\",\"Recipes\":[{\"ID\":1000,\"Quantity\":2," + Opts + "},{\"ID\":2000,\"Quantity\":3," + Opts + "}]," + Tail,
            r.Json);
        Assert.Equal(2, r.CraftedItems);
        Assert.Empty(r.SkippedItemIds);
    }

    [Theory]
    [InlineData(200u, 1, 1)]
    [InlineData(200u, 3, 1)]
    [InlineData(200u, 4, 2)]
    [InlineData(200u, 6, 2)]
    [InlineData(200u, 7, 3)]
    [InlineData(100u, 5, 5)]
    [InlineData(300u, 5, 5)]   // yield 0 treated as 1
    [InlineData(600u, 5, 5)]   // negative yield treated as 1
    [InlineData(500u, 1, 1)]
    public void Build_QuantityIsCrafts_CeilOfItemsOverYield(uint item, int qty, int crafts)
    {
        var r = Build("L", (item, qty));
        using var doc = JsonDocument.Parse(r.Json);
        Assert.Equal(crafts, doc.RootElement.GetProperty("Recipes")[0].GetProperty("Quantity").GetInt32());
    }

    [Fact]
    public void Build_DuplicateRows_SummedThenRoundedOnce()
    {
        // Superseded 2026-11-22 (user request: the Artisan export includes the full sub-craft tree, every root
        // expanded together by RecipeExpander.ExpandToCrafts, so the same item's demand is combined, then rounded once).
        // Was Build_DuplicateRows_CeilPerRowThenSum_LikeArtisansTeamcraftImport: Quantity 2 (ceil(1/3) + ceil(1/3),
        // as Artisan's Teamcraft import does per line). Now Quantity 1 (ceil(2/3)). CraftedItems still counts rows: 2.
        var r = Build("L", (200, 1), (200, 1));

        Assert.Equal("{\"Name\":\"L\",\"Recipes\":[{\"ID\":2000,\"Quantity\":1," + Opts + "}]," + Tail, r.Json);
        Assert.Equal(2, r.CraftedItems);
    }

    [Fact]
    public void Build_RecipesInFirstSeenRowOrder_DuplicatesMergedIntoFirstPosition()
    {
        // Superseded 2026-11-22 (full sub-craft tree, roots combined): recipe 5000 was Quantity 3 (ceil(3/2) + ceil(1/2));
        // now Quantity 2 (ceil(4/2)). Order and CraftedItems unchanged.
        var r = Build("L", (500, 3), (100, 1), (500, 1), (200, 3));

        Assert.Equal(
            "{\"Name\":\"L\",\"Recipes\":[{\"ID\":5000,\"Quantity\":2," + Opts + "},{\"ID\":1000,\"Quantity\":1," + Opts
            + "},{\"ID\":2000,\"Quantity\":1," + Opts + "}]," + Tail,
            r.Json);
        Assert.Equal(4, r.CraftedItems);
    }

    [Fact]
    public void Build_UncraftableItems_Skipped_DistinctInFirstSeenOrder()
    {
        var r = Build("L", (400, 5), (100, 1), (700, 2), (400, 2));

        Assert.Equal("{\"Name\":\"L\",\"Recipes\":[{\"ID\":1000,\"Quantity\":1," + Opts + "}]," + Tail, r.Json);
        Assert.Equal(1, r.CraftedItems);
        Assert.Equal(new uint[] { 400, 700 }, r.SkippedItemIds.ToArray());
    }

    [Fact]
    public void Build_NothingCraftable_EmptyJson()
    {
        var r = Build("L", (400, 5));

        Assert.Equal(string.Empty, r.Json);
        Assert.Equal(0, r.CraftedItems);
        Assert.Equal(new uint[] { 400 }, r.SkippedItemIds.ToArray());
    }

    [Fact]
    public void Build_ItemZeroAndQuantityBelowOne_IgnoredNotReported()
    {
        var r = Build("L", (0, 5), (100, 0), (100, -3), (400, 0), (400, -1));

        Assert.Equal(string.Empty, r.Json);
        Assert.Equal(0, r.CraftedItems);
        Assert.Empty(r.SkippedItemIds);
    }

    [Fact]
    public void Build_NoRows_EmptyJson()
    {
        var r = ArtisanListExport.Build("L", Array.Empty<Requirement>(), Recipes());

        Assert.Equal(string.Empty, r.Json);
        Assert.Equal(0, r.CraftedItems);
        Assert.Empty(r.SkippedItemIds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    [InlineData(null)]
    public void Build_BlankName_DefaultsToShoppingList(string? name)
    {
        var r = Build(name!, (100, 1));
        Assert.StartsWith("{\"Name\":\"Shopping list\",\"Recipes\":", r.Json);
    }

    [Fact]
    public void Build_Name_EscapedMinimally_NonAsciiKept()
    {
        var r = Build("Luna's +1 \"Ä\" café\n\\<>& クラフター", (100, 1));

        Assert.StartsWith("{\"Name\":\"Luna's +1 \\\"Ä\\\" café\\n\\\\<>& クラフター\",\"Recipes\":", r.Json);
        using var doc = JsonDocument.Parse(r.Json);
        Assert.Equal("Luna's +1 \"Ä\" café\n\\<>& クラフター", doc.RootElement.GetProperty("Name").GetString());
    }

    [Fact]
    public void Build_IsOneLine()
    {
        var r = Build("a\r\nb", (100, 1), (200, 4), (500, 9));
        Assert.DoesNotContain('\n', r.Json);
        Assert.DoesNotContain('\r', r.Json);
        Assert.DoesNotContain(' ', r.Json);
    }

    [Fact]
    public void Build_CraftsClampedTo9999999_NoOverflow()
    {
        var one = Build("L", (100, int.MaxValue));
        var two = Build("L", (100, int.MaxValue), (100, int.MaxValue));

        Assert.Equal("{\"Name\":\"L\",\"Recipes\":[{\"ID\":1000,\"Quantity\":9999999," + Opts + "}]," + Tail, one.Json);
        Assert.Equal(one.Json, two.Json);
    }

    [Fact]
    public void Build_MemberNames_AreExactlyTheAllowedSetAtEveryLevel()
    {
        var r = Build("L", (100, 1), (200, 4), (500, 9), (300, 2));
        using var doc = JsonDocument.Parse(r.Json);
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(new[] { "Name", "Recipes", "ExpandedList", "SkipLiteral", "OnlyRestockNonCrafted" }, Names(root));
        Assert.Equal(JsonValueKind.String, root.GetProperty("Name").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("ExpandedList").ValueKind);
        Assert.Equal(0, root.GetProperty("ExpandedList").GetArrayLength());
        Assert.Equal(JsonValueKind.False, root.GetProperty("SkipLiteral").ValueKind);
        Assert.Equal(JsonValueKind.False, root.GetProperty("OnlyRestockNonCrafted").ValueKind);

        var recipes = root.GetProperty("Recipes");
        Assert.Equal(4, recipes.GetArrayLength());
        foreach (var e in recipes.EnumerateArray())
        {
            Assert.Equal(new[] { "ID", "Quantity", "ListItemOptions" }, Names(e));
            Assert.True(e.GetProperty("ID").TryGetUInt32(out var id) && id > 0);
            Assert.True(e.GetProperty("Quantity").TryGetInt32(out var q) && q >= 1);
            var o = e.GetProperty("ListItemOptions");
            Assert.Equal(new[] { "NQOnly", "Skipping" }, Names(o));
            Assert.Equal(JsonValueKind.False, o.GetProperty("NQOnly").ValueKind);
            Assert.Equal(JsonValueKind.False, o.GetProperty("Skipping").ValueKind);
        }
    }

    private static string[] Names(JsonElement e) => e.EnumerateObject().Select(p => p.Name).ToArray();

    // ---------- TryParse ----------

    [Fact]
    public void RoundTrip_BuildThenParse()
    {
        var built = Build("Gear 'n' café", (500, 3), (100, 2), (200, 7));
        var r = Parse(built.Json);

        Assert.Null(r.Error);
        Assert.Equal("Gear 'n' café", r.Name);
        // 5000: 2 crafts x yield 2 = 4; 1000: 2 x 1; 2000: 3 crafts x yield 3 = 9.
        Assert.Equal(new[] { new Requirement(500, 4), new Requirement(100, 2), new Requirement(200, 9) }, r.Requirements!.ToArray());
        Assert.Empty(r.Unmatched);
    }

    [Fact]
    public void Parse_ArtisansOwnExportShape_AllMembers()
    {
        // JsonConvert.SerializeObject(NewCraftingList) with default settings (ListEditor "Export List").
        const string json = "{\"ID\":4321,\"Name\":\"Mine\",\"Recipes\":[{\"ID\":2000,\"Quantity\":2,\"ListItemOptions\":{\"NQOnly\":true,\"Skipping\":false}},"
            + "{\"ID\":1000,\"Quantity\":5,\"ListItemOptions\":null}],\"ExpandedList\":[2000,2000,1000],\"SkipIfEnough\":false,\"SkipLiteral\":false,"
            + "\"Materia\":false,\"Repair\":true,\"RepairPercent\":50,\"AddAsQuickSynth\":false,\"TidyAfter\":true,\"OnlyRestockNonCrafted\":false,\"IsPremade\":false}";
        var r = Parse(json);

        Assert.Null(r.Error);
        Assert.Equal("Mine", r.Name);
        Assert.Equal(new[] { new Requirement(200, 6), new Requirement(100, 5) }, r.Requirements!.ToArray());
        Assert.Empty(r.Unmatched);
    }

    [Fact]
    public void Parse_TypeAndValuesMarkers_Tolerated()
    {
        const string json = "{\"$type\":\"Artisan.CraftingLists.NewCraftingList, Artisan\",\"Name\":\"T\",\"Recipes\":{\"$type\":\"x\",\"$values\":["
            + "{\"$type\":\"Artisan.CraftingLists.ListItem, Artisan\",\"ID\":1000,\"Quantity\":3,\"ListItemOptions\":{\"$type\":\"y\",\"NQOnly\":false,\"Skipping\":false}}]}}";
        var r = Parse(json);

        Assert.Null(r.Error);
        Assert.Equal("T", r.Name);
        Assert.Equal(new[] { new Requirement(100, 3) }, r.Requirements!.ToArray());
    }

    [Fact]
    public void Parse_ObjectWithRecipesOnly_NameNull()
    {
        var r = Parse("{\"Recipes\":[{\"ID\":1000,\"Quantity\":1}]}");

        Assert.Null(r.Error);
        Assert.Null(r.Name);
        Assert.Equal(new[] { new Requirement(100, 1) }, r.Requirements!.ToArray());
    }

    [Theory]
    [InlineData("{\"Name\":null,\"Recipes\":[{\"ID\":1000,\"Quantity\":1}]}")]
    [InlineData("{\"Name\":5,\"Recipes\":[{\"ID\":1000,\"Quantity\":1}]}")]
    [InlineData("{\"name\":\"lower\",\"Recipes\":[{\"ID\":1000,\"Quantity\":1}]}")]
    public void Parse_NameNotAStringOrWrongCase_Null(string json)
    {
        var r = Parse(json);
        Assert.Null(r.Error);
        Assert.Null(r.Name);
    }

    [Fact]
    public void Parse_SameItemFromTwoRecipesAndRepeatedRecipe_Merged()
    {
        var r = Parse("{\"Recipes\":[{\"ID\":1000,\"Quantity\":2},{\"ID\":2000,\"Quantity\":1},{\"ID\":1001,\"Quantity\":3},{\"ID\":1000,\"Quantity\":4}]}");

        Assert.Null(r.Error);
        Assert.Equal(new[] { new Requirement(100, 9), new Requirement(200, 3) }, r.Requirements!.ToArray());
    }

    [Fact]
    public void Parse_HugeQuantities_SaturateAt9999999()
    {
        var r = Parse("{\"Recipes\":[{\"ID\":2000,\"Quantity\":2147483647},{\"ID\":2000,\"Quantity\":2147483647},{\"ID\":1000,\"Quantity\":9999999},{\"ID\":1001,\"Quantity\":1}]}");

        Assert.Null(r.Error);
        Assert.Equal(new[] { new Requirement(200, 9_999_999), new Requirement(100, 9_999_999) }, r.Requirements!.ToArray());
    }

    [Fact]
    public void Parse_SingleHugeEntry_ClampedWithoutAMerge()
    {
        // One entry per item, so the merge-site clamp cannot hide a missing first-entry clamp.
        var r = Parse("{\"Recipes\":[{\"ID\":2000,\"Quantity\":2147483647}]}");
        Assert.Equal(new[] { new Requirement(200, 9_999_999) }, r.Requirements!.ToArray());
    }

    [Fact]
    public void Parse_YieldBelowOne_TreatedAsOne()
    {
        var r = Parse("{\"Recipes\":[{\"ID\":3000,\"Quantity\":4},{\"ID\":6000,\"Quantity\":2}]}");
        Assert.Equal(new[] { new Requirement(300, 4), new Requirement(600, 2) }, r.Requirements!.ToArray());
    }

    [Fact]
    public void Parse_BadEntries_ReportedReadably_GoodOnesKept()
    {
        const string json = "{\"Recipes\":[5,{\"Quantity\":1},{\"ID\":\"1000\",\"Quantity\":1},{\"ID\":0,\"Quantity\":1},{\"ID\":777,\"Quantity\":1},"
            + "{\"ID\":1000,\"Quantity\":0},{\"ID\":1000,\"Quantity\":-2},{\"ID\":1000},{\"ID\":1000,\"Quantity\":2.5},{\"ID\":1000,\"Quantity\":\"3\"},"
            + "{\"ID\":8000,\"Quantity\":1},{\"ID\":2000,\"Quantity\":1,\"ListItemOptions\":{\"Skipping\":true}},{\"ID\":-4,\"Quantity\":1},{\"ID\":500,\"Quantity\":1},{\"ID\":1000,\"Quantity\":1}]}";
        var r = Parse(json);

        Assert.Null(r.Error);
        Assert.Equal(new[] { new Requirement(100, 1) }, r.Requirements!.ToArray());
        Assert.Equal(new[]
        {
            "Entry 1: not a recipe",
            "Entry 2: no recipe ID",
            "Entry 3: no recipe ID",
            "Entry 4: no recipe ID",
            "Entry 5: recipe 777 is not a known recipe",
            "Entry 6: recipe 1000 has no quantity of 1 or more",
            "Entry 7: recipe 1000 has no quantity of 1 or more",
            "Entry 8: recipe 1000 has no quantity of 1 or more",
            "Entry 9: recipe 1000 has no quantity of 1 or more",
            "Entry 10: recipe 1000 has no quantity of 1 or more",
            "Entry 11: recipe 8000 is not a known recipe",
            "Entry 12: recipe 2000 is set to Skip in Artisan",
            "Entry 13: no recipe ID",
            "Entry 14: recipe 500 is not a known recipe",
        }, r.Unmatched.ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("[{\"Recipes\":[{\"ID\":1000,\"Quantity\":1}]}]")]
    [InlineData("123")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("{\"Name\":\"x\"}")]
    [InlineData("{\"Recipes\":5}")]
    [InlineData("{\"Recipes\":null}")]
    [InlineData("{\"Recipes\":{\"$values\":5}}")]
    [InlineData("{\"recipes\":[{\"ID\":1000,\"Quantity\":1}]}")]
    [InlineData("{\"Recipes\":[]}")]
    [InlineData("{\"Recipes\":[{\"ID\":777,\"Quantity\":1}]}")]
    [InlineData("{\"Recipes\":[{\"ID\":1000,\"Quantity\":0}]}")]
    [InlineData("{\"Recipes\":[{\"ID\":1000,\"Quantity\":1}]} trailing")]
    public void Parse_Unusable_ErrorAndNullRequirements_NeverThrows(string? json)
    {
        var r = Parse(json!);

        Assert.NotNull(r.Error);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
        Assert.Null(r.Requirements);
    }

    [Fact]
    public void Parse_NothingUsable_StillReportsTheUnmatchedEntries()
    {
        var r = Parse("{\"Name\":\"N\",\"Recipes\":[{\"ID\":777,\"Quantity\":1}]}");

        Assert.NotNull(r.Error);
        Assert.Null(r.Requirements);
        Assert.Equal(new[] { "Entry 1: recipe 777 is not a known recipe" }, r.Unmatched.ToArray());
    }

    [Fact]
    public void Parse_DeeplyNested_ErrorNotThrow()
    {
        var deep = "{\"Recipes\":" + new string('[', 5000) + new string(']', 5000) + "}";
        var r = Parse(deep);
        Assert.NotNull(r.Error);
        Assert.Null(r.Requirements);

        var deepName = "{\"Name\":" + new string('[', 5000) + new string(']', 5000) + ",\"Recipes\":[{\"ID\":1000,\"Quantity\":1}]}";
        Assert.NotNull(Parse(deepName).Error);
    }

    [Fact]
    public void Parse_HugeInput_NeverThrows()
    {
        var many = "{\"Name\":\"" + new string('x', 200_000) + "\",\"Recipes\":["
            + string.Join(",", Enumerable.Repeat("{\"ID\":1000,\"Quantity\":1}", 20_000)) + "]}";
        var r = Parse(many);

        Assert.Null(r.Error);
        Assert.Equal(new[] { new Requirement(100, 20_000) }, r.Requirements!.ToArray());
    }
}
