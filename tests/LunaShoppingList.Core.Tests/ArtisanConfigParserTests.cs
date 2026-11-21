using System.Text;

namespace LunaShoppingList.Core.Tests;

public class ArtisanConfigParserTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ArtisanConfigParser", name));

    [Fact]
    public void Basic_ParsesListsItemsAndOptions_InOrder()
    {
        var lists = ArtisanConfigParser.ParseLists(Fixture("basic.json"));

        Assert.Equal(2, lists.Count);
        Assert.Equal(7, lists[0].Id);
        Assert.Equal("Alpha", lists[0].Name);
        Assert.Equal(
            new[] { new CraftItem(100, 3, false, true), new CraftItem(200, 5, true, false), new CraftItem(300, 1, false, false) },
            lists[0].Items.ToArray());
        Assert.Equal(2, lists[1].Id);
        Assert.Equal("Beta", lists[1].Name);
        Assert.Empty(lists[1].Items);
    }

    [Fact]
    public void CaseCollidingKeys_DoNotFailAndOnlyExactCaseIsRead()
    {
        var lists = ArtisanConfigParser.ParseLists(Fixture("case-collision.json"));

        var list = Assert.Single(lists);
        Assert.Equal(1, list.Id);
        Assert.Equal("Cased", list.Name);
        var item = Assert.Single(list.Items);
        Assert.Equal(new CraftItem(10, 2, false, true), item);
    }

    [Fact]
    public void ValuesWrapping_IsSupportedForListsAndRecipes()
    {
        var lists = ArtisanConfigParser.ParseLists(Fixture("values-wrapped.json"));

        var list = Assert.Single(lists);
        Assert.Equal(4, list.Id);
        Assert.Equal(new[] { 40u, 41u }, list.Items.Select(i => i.RecipeId).ToArray());
        Assert.Equal(new[] { 6, 7 }, list.Items.Select(i => i.CraftCount).ToArray());
    }

    [Fact]
    public void NullOrMissingName_BecomesEmptyString_AndMissingOrNullRecipesIsEmpty()
    {
        var lists = ArtisanConfigParser.ParseLists(Fixture("null-name.json"));

        Assert.Equal(3, lists.Count);
        Assert.All(lists, l => Assert.Equal(string.Empty, l.Name));
        Assert.Single(lists[0].Items);
        Assert.Empty(lists[1].Items);
        Assert.Empty(lists[2].Items);
    }

    [Fact]
    public void ZeroLists_ReturnsEmpty()
    {
        Assert.Empty(ArtisanConfigParser.ParseLists(Fixture("empty-lists.json")));
    }

    [Fact]
    public void BadEntries_AreSkippedWithoutFailingTheFile()
    {
        var lists = ArtisanConfigParser.ParseLists(Fixture("bad-entries.json"));

        Assert.Equal(new[] { 5, 6 }, lists.Select(l => l.Id).ToArray());
        Assert.Equal(
            new[] { new CraftItem(1, 2, false, false), new CraftItem(8, 4, false, false), new CraftItem(9, 4, false, false) },
            lists[0].Items.ToArray());
        Assert.Empty(lists[1].Items);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"NewCraftingLists\": 5}")]
    [InlineData("{\"NewCraftingLists\": null}")]
    [InlineData("{\"NewCraftingLists\": {\"$type\": \"x\"}}")]
    [InlineData("{\"NewCraftingLists\": {\"$values\": 5}}")]
    [InlineData("{\"NewCraftingLists\": {\"$values\": null}}")]
    [InlineData("{\"NewCraftingLists\": {\"$VALUES\": []}}")]
    [InlineData("{\"NewCraftingLists\": {\"values\": []}}")]
    public void InvalidOrMissingLists_ThrowInvalidDataException(string json)
    {
        Assert.Throws<InvalidDataException>(() => ArtisanConfigParser.ParseLists(json));
    }

    [Fact]
    public void NullText_ThrowsInvalidDataException()
    {
        Assert.Throws<InvalidDataException>(() => ArtisanConfigParser.ParseLists(null!));
    }

    [Fact]
    public void KeyNameMatchingIsCaseSensitive_LegacyAndWrongCaseTopLevelKeysAreNotUsed()
    {
        // Fixture has "CraftingLists" (legacy) and "newcraftinglists" but no "NewCraftingLists".
        Assert.Throws<InvalidDataException>(() => ArtisanConfigParser.ParseLists(Fixture("no-lists-key.json")));
    }

    // ---- qa-tester additions (2026-11-21) ----

    [Fact]
    public void WrappedAndBareMayBeMixedAtDifferentLevels()
    {
        // Lists wrapped but recipes bare, and lists bare but recipes wrapped: each level is unwrapped on its own.
        const string a = "{\"NewCraftingLists\":{\"$type\":\"t\",\"$values\":[{\"ID\":1,\"Recipes\":[{\"ID\":5,\"Quantity\":2}]}]}}";
        const string b = "{\"NewCraftingLists\":[{\"ID\":1,\"Recipes\":{\"$type\":\"t\",\"$values\":[{\"ID\":5,\"Quantity\":2}]}}]}";
        foreach (var json in new[] { a, b })
        {
            var list = Assert.Single(ArtisanConfigParser.ParseLists(json));
            Assert.Equal(new CraftItem(5, 2, false, false), Assert.Single(list.Items));
        }
    }

    [Fact]
    public void WrappedRecipesWithEmptyValues_GiveAListWithNoItems()
    {
        var list = Assert.Single(ArtisanConfigParser.ParseLists("{\"NewCraftingLists\":[{\"ID\":1,\"Name\":\"x\",\"Recipes\":{\"$type\":\"t\",\"$values\":[]}}]}"));
        Assert.Empty(list.Items);
    }

    [Theory]
    [InlineData("\"Name\":null", "")]
    [InlineData("\"Name\":12", "")]
    [InlineData("\"Name\":true", "")]
    [InlineData("\"Name\":{}", "")]
    [InlineData("\"Name\":[\"a\"]", "")]
    [InlineData("\"name\":\"wrong case\"", "")]
    [InlineData("\"Name\":\"\"", "")]
    [InlineData("\"Name\":\"Real\"", "Real")]
    public void ListName_OnlyAStringUnderTheExactKeyIsUsed(string nameMember, string expected)
    {
        var list = Assert.Single(ArtisanConfigParser.ParseLists("{\"NewCraftingLists\":[{\"ID\":1," + nameMember + "}]}"));
        Assert.Equal(expected, list.Name);
    }

    private static double MinMs(Action action, int runs = 3)
    {
        var best = double.MaxValue;
        for (var i = 0; i < runs; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            action();
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    [Fact]
    public void LargeSyntheticDocument_ParsesInTimeProportionalToReadingIt()
    {
        var sb = new StringBuilder(4_000_000);
        sb.Append("{\"Version\":1,\"NewCraftingLists\":[");
        for (var l = 0; l < 400; l++)
        {
            if (l > 0) sb.Append(',');
            sb.Append("{\"$type\":\"T\",\"ID\":").Append(l + 1).Append(",\"Name\":\"List ").Append(l)
              .Append("\",\"ExpandedList\":[").Append(string.Join(',', Enumerable.Range(0, 200))).Append("],\"Recipes\":[");
            for (var r = 0; r < 150; r++)
            {
                if (r > 0) sb.Append(',');
                sb.Append("{\"$type\":\"I\",\"ID\":").Append(r + 1).Append(",\"Quantity\":").Append(r % 9 + 1)
                  .Append(",\"ListItemOptions\":{\"NQOnly\":false,\"Skipping\":false,\"requiredFood\":1,\"RequiredFood\":2}}");
            }
            sb.Append("]}");
        }
        sb.Append("]}");
        var json = sb.ToString();
        Assert.True(json.Length > 3_000_000, "fixture should be ~3 MB, was " + json.Length);

        var lists = ArtisanConfigParser.ParseLists(json); // also the JIT warm-up
        Assert.Equal(400, lists.Count);
        Assert.Equal(1, lists[0].Id);
        Assert.Equal(400, lists[^1].Id);
        Assert.All(lists, l => Assert.Equal(150, l.Items.Count));

        // Not an absolute millisecond limit (flaky on a loaded machine): compare with the cost of merely reading the
        // same text into a JsonDocument, taking the best of 3 runs of each. A parser that re-reads the document per
        // list or per item (quadratic) is hundreds of times slower and fails; a slow machine slows both sides.
        var baseline = MinMs(() => System.Text.Json.JsonDocument.Parse(json).Dispose());
        var parse = MinMs(() => ArtisanConfigParser.ParseLists(json));
        Assert.True(parse < baseline * 30 + 300, $"ParseLists {parse:F1} ms vs plain JsonDocument.Parse {baseline:F1} ms");
        Assert.True(parse < 20_000, $"ParseLists took {parse:F0} ms");
    }
}
