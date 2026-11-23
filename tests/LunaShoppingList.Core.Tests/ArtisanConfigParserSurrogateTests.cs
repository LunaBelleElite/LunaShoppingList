namespace LunaShoppingList.Core.Tests;

public class ArtisanConfigParserSurrogateTests
{
    private static string One(string nameJson, string extra = "")
        => "{\"NewCraftingLists\":[{\"ID\":1,\"Name\":\"" + nameJson + "\"," + extra + "\"Recipes\":[{\"ID\":1000,\"Quantity\":3}]}]}";

    [Fact]
    public void EscapedLoneSurrogateInName_DoesNotThrow_NameIsEmpty_RecipesKept()
    {
        var lists = ArtisanConfigParser.ParseLists("{\"NewCraftingLists\":[{\"ID\":1,\"Name\":\"\\ud800\",\"Recipes\":[]}]}");
        var list = Assert.Single(lists);
        Assert.Equal(1, list.Id);
        Assert.Equal(string.Empty, list.Name);

        var withRecipes = Assert.Single(ArtisanConfigParser.ParseLists(One("\\ud800abc")));
        Assert.Equal(string.Empty, withRecipes.Name);
        Assert.Equal(new CraftItem(1000, 3, false, false), Assert.Single(withRecipes.Items));
    }

    [Fact]
    public void EscapedLowSurrogateAlone_AlsoGivesAnEmptyName()
        => Assert.Equal(string.Empty, Assert.Single(ArtisanConfigParser.ParseLists(One("\\udc00x"))).Name);

    [Fact]
    public void RawLoneSurrogateInName_DoesNotThrow_AndIsReplaced()
    {
        var list = Assert.Single(ArtisanConfigParser.ParseLists(One("\ud800abc")));
        Assert.Equal("\ufffdabc", list.Name);
        Assert.Single(list.Items);
    }

    [Fact]
    public void RawLoneLowSurrogateInName_IsReplaced()
        => Assert.Equal("a\ufffd", Assert.Single(ArtisanConfigParser.ParseLists(One("a\udc00"))).Name);

    [Fact]
    public void ValidSurrogatePair_IsKeptAsIs_RawAndEscaped()
    {
        Assert.Equal("a\U0001F600", Assert.Single(ArtisanConfigParser.ParseLists(One("a\U0001F600"))).Name);
        Assert.Equal("a\U0001F600", Assert.Single(ArtisanConfigParser.ParseLists(One("a\\ud83d\\ude00"))).Name);
    }

    [Fact]
    public void RawValidPairAndRawLoneSurrogateInTheSameText_OnlyTheLoneOneIsReplaced()
    {
        var list = Assert.Single(ArtisanConfigParser.ParseLists(One("a\U0001F600b\ud800c")));
        Assert.Equal("a\U0001F600b�c", list.Name);
    }

    [Fact]
    public void LoneSurrogateInAnIgnoredMember_IsHarmless()
    {
        foreach (var bad in new[] { "\\ud800", "\ud800", "\\udc00x" })
        {
            var list = Assert.Single(ArtisanConfigParser.ParseLists(One("ok", "\"Other\":\"" + bad + "\",")));
            Assert.Equal("ok", list.Name);
            Assert.Single(list.Items);
        }
    }

    [Fact]
    public void BadJsonWithALoneSurrogate_IsInvalidDataException_NotArgumentException()
    {
        Assert.Throws<InvalidDataException>(() => ArtisanConfigParser.ParseLists("{\"NewCraftingLists\":[ \ud800"));
        Assert.Throws<InvalidDataException>(() => ArtisanConfigParser.ParseLists("\ud800"));
    }
}
