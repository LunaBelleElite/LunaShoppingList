using System.Text;

namespace LunaShoppingList.Core.Tests;

/// <summary>ImportParser: detection order (Artisan JSON, Teamcraft link, item list), blank and garbage input, passthrough.</summary>
public class ImportParserTests
{
    private sealed class FakeItems : IItemInfo
    {
        public List<string> Lookups { get; } = new();
        public string GetName(uint itemId) => "Item #" + itemId;
        public bool IsCrystalLike(uint itemId) => false;
        public uint? FindByName(string name)
        {
            Lookups.Add(name);
            return name.Equals("Iron Ore", StringComparison.OrdinalIgnoreCase) ? 5111u
                 : name.Equals("Iron", StringComparison.OrdinalIgnoreCase) ? 5222u : null;
        }
    }

    private sealed class FakeRecipes : IRecipeData
    {
        public RecipeInfo? GetRecipe(uint recipeId) => recipeId == 1000 ? new RecipeInfo(1000, 100, 1, Array.Empty<Ingredient>()) : null;
    }

    private static ImportResult Parse(string? text, FakeItems? items = null)
        => ImportParser.Parse(text!, items ?? new FakeItems(), new FakeRecipes());

    private const string ArtisanJson = "{\"Name\":\"My crafts\",\"Recipes\":[{\"ID\":1000,\"Quantity\":4,\"ListItemOptions\":{\"NQOnly\":false,\"Skipping\":false}}]}";

    private static string Link(params (uint Item, int Qty)[] rows)
        => TeamcraftLink.Build(rows.Select(r => new Requirement(r.Item, r.Qty)).ToList())!;

    // ---------- blank ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n\t ")]
    public void Blank_GivesPasteSomethingFirst(string? text)
    {
        var r = Parse(text);
        Assert.Equal("Paste something first", r.Error);
        Assert.Equal(ImportKind.ItemList, r.Kind);
        Assert.NotNull(r.Requirements);
        Assert.Empty(r.Requirements);
        Assert.Empty(r.Unmatched);
    }

    // ---------- Artisan ----------

    [Fact]
    public void ArtisanJson_IsArtisanList_WithItsNameAndRows()
    {
        var r = Parse("  \n" + ArtisanJson + "\n ");
        Assert.Equal(ImportKind.ArtisanList, r.Kind);
        Assert.Null(r.Error);
        Assert.Equal("My crafts", r.SuggestedName);
        Assert.Equal(new[] { new Requirement(100, 4) }, r.Requirements.ToArray());
    }

    [Fact]
    public void ArtisanJson_WithoutName_UsesDefaultName()
    {
        var r = Parse("{\"Recipes\":[{\"ID\":1000,\"Quantity\":1}]}");
        Assert.Equal(ImportKind.ArtisanList, r.Kind);
        Assert.Equal("Imported Artisan list", r.SuggestedName);
    }

    [Fact]
    public void ArtisanJson_BlankName_UsesDefaultName()
    {
        var r = Parse("{\"Name\":\"  \",\"Recipes\":[{\"ID\":1000,\"Quantity\":1}]}");
        Assert.Equal("Imported Artisan list", r.SuggestedName);
    }

    [Fact]
    public void ArtisanJson_UnknownRecipe_PassesUnmatchedThrough()
    {
        var r = Parse("{\"Name\":\"A\",\"Recipes\":[{\"ID\":1000,\"Quantity\":1},{\"ID\":9999,\"Quantity\":1}]}");
        Assert.Equal(ImportKind.ArtisanList, r.Kind);
        Assert.Null(r.Error);
        Assert.Single(r.Requirements);
        Assert.Single(r.Unmatched);
        Assert.Contains("9999", r.Unmatched[0]);
    }

    [Fact]
    public void ArtisanJson_Broken_IsErrorWithEmptyRows()
    {
        var r = Parse("{ not json");
        Assert.Equal(ImportKind.ArtisanList, r.Kind);
        Assert.False(string.IsNullOrEmpty(r.Error));
        Assert.NotNull(r.Requirements);
        Assert.Empty(r.Requirements);
    }

    // ---------- Teamcraft ----------

    [Fact]
    public void FullLink_IsTeamcraftLink()
    {
        var items = new FakeItems();
        var r = Parse(Link((5111, 3), (7, 2)), items);
        Assert.Equal(ImportKind.TeamcraftLink, r.Kind);
        Assert.Null(r.Error);
        Assert.Equal("Teamcraft import", r.SuggestedName);
        Assert.Equal(new[] { new Requirement(5111, 3), new Requirement(7, 2) }, r.Requirements.ToArray());
        Assert.Empty(items.Lookups); // a link never reaches PasteParser
    }

    [Fact]
    public void BareBase64_IsTeamcraftLink()
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("5111,null,3"));
        var r = Parse(b64);
        Assert.Equal(ImportKind.TeamcraftLink, r.Kind);
        Assert.Equal(new[] { new Requirement(5111, 3) }, r.Requirements.ToArray());
    }

    [Fact]
    public void LinkMarker_IsCaseInsensitive()
    {
        var link = Link((9, 1)).Replace("/import/", "/IMPORT/");
        Assert.Equal(ImportKind.TeamcraftLink, Parse(link).Kind);
    }

    [Theory]
    [InlineData("/import/")]
    [InlineData("/IMPORT/")]
    public void LinkInsideTextWithSpaces_IsTeamcraftLink_AndNeverReachesPasteParser(string marker)
    {
        // Only the marker (not the single-token rule) can recognise this one, in any letter case.
        var items = new FakeItems();
        var link = Link((5111, 3)).Replace("/import/", marker);
        var r = Parse("Here is my list: " + link, items);
        Assert.Equal(ImportKind.TeamcraftLink, r.Kind);
        Assert.Equal(new[] { new Requirement(5111, 3) }, r.Requirements.ToArray());
        Assert.Empty(items.Lookups);
    }

    [Fact]
    public void LinkWithBadRows_PassesUnmatchedThrough()
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("5111,null,3;oops"));
        var r = Parse("https://ffxivteamcraft.com/import/" + b64);
        Assert.Equal(ImportKind.TeamcraftLink, r.Kind);
        Assert.Single(r.Requirements);
        Assert.Single(r.Unmatched);
    }

    [Fact]
    public void LinkMarker_WithUndecodableData_IsTeamcraftErrorNotItemList()
    {
        var items = new FakeItems();
        var r = Parse("https://ffxivteamcraft.com/import/!!!!", items);
        Assert.Equal(ImportKind.TeamcraftLink, r.Kind);
        Assert.False(string.IsNullOrEmpty(r.Error));
        Assert.Empty(r.Requirements);
        Assert.Empty(items.Lookups);
    }

    [Fact]
    public void LinkMarker_WithNoData_IsTeamcraftError()
    {
        var r = Parse("https://ffxivteamcraft.com/import/");
        Assert.Equal(ImportKind.TeamcraftLink, r.Kind);
        Assert.False(string.IsNullOrEmpty(r.Error));
    }

    // ---------- Item list, and precedence ----------

    [Fact]
    public void BareItemName_WithSpace_StaysItemList()
    {
        var r = Parse("Iron Ore");
        Assert.Equal(ImportKind.ItemList, r.Kind);
        Assert.Equal("Imported list", r.SuggestedName);
        Assert.Empty(r.Requirements);
        Assert.Equal(new[] { "Iron Ore" }, r.Unmatched.ToArray());
    }

    [Fact]
    public void SingleWord_ThatDecodesButHasNoRows_StaysItemList()
    {
        // "Iron" is valid base64 text, but decodes to no usable rows, so it must not become a Teamcraft error.
        var r = Parse("Iron");
        Assert.Equal(ImportKind.ItemList, r.Kind);
        Assert.Null(r.Error);
        Assert.Equal(new[] { "Iron" }, r.Unmatched.ToArray());
    }

    [Fact]
    public void ItemLines_AreItemList()
    {
        var r = Parse("3x Iron Ore\n2x Iron\n1x Nope");
        Assert.Equal(ImportKind.ItemList, r.Kind);
        Assert.Null(r.Error);
        Assert.Equal(new[] { new Requirement(5111, 3), new Requirement(5222, 2) }, r.Requirements.ToArray());
        Assert.Equal(new[] { "1x Nope" }, r.Unmatched.ToArray());
    }

    [Fact]
    public void ItemLines_AllUnmatched_IsNotAnError()
    {
        var r = Parse("hello world");
        Assert.Equal(ImportKind.ItemList, r.Kind);
        Assert.Null(r.Error);
        Assert.Empty(r.Requirements);
        Assert.Equal(new[] { "hello world" }, r.Unmatched.ToArray());
    }

    [Fact]
    public void MultiLinePaste_WithoutMarker_IsItemListEvenIfALineLooksLikeBase64()
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("5111,null,3"));
        var r = Parse("3x Iron Ore\n" + b64);
        Assert.Equal(ImportKind.ItemList, r.Kind);
        Assert.Equal(new[] { new Requirement(5111, 3) }, r.Requirements.ToArray());
        Assert.Equal(new[] { b64 }, r.Unmatched.ToArray());
    }

    [Fact]
    public void Garbage_NeverThrows()
    {
        foreach (var s in new[] { "\0\0\0", "}{", "%%%", "/import/", "{", "[1,2]", new string('x', 100000) })
        {
            var r = Parse(s);
            Assert.NotNull(r.Requirements);
            Assert.NotNull(r.Unmatched);
        }
    }

    [Fact]
    public void Brace_StartsArtisanEvenWhenTheTextAlsoContainsImportMarker()
    {
        var r = Parse("{\"Name\":\"x/import/y\",\"Recipes\":[{\"ID\":1000,\"Quantity\":1}]}");
        Assert.Equal(ImportKind.ArtisanList, r.Kind);
        Assert.Equal("x/import/y", r.SuggestedName);
    }
}
