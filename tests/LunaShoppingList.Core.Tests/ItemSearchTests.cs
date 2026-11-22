namespace LunaShoppingList.Core.Tests;

public class ItemSearchTests
{
    private static ItemSearchEntry E(uint id, string name) => new(id, name);
    private static uint[] Ids(IReadOnlyList<ItemSearchEntry> r) => r.Select(x => x.ItemId).ToArray();

    [Fact]
    public void Order_Exact_Prefix_WordBoundary_MidWord()
    {
        var entries = new[]
        {
            E(1, "Dark Cotton Boll"),   // word-boundary contains
            E(2, "Scottonia"),          // mid-word contains
            E(3, "Cotton Yarn"),        // prefix
            E(4, "cotton"),             // exact
            E(5, "Cottonade Cloth"),    // prefix, longer
        };
        Assert.Equal(new uint[] { 4, 3, 5, 1, 2 }, Ids(ItemSearch.Rank("Cotton", entries, 10)));
    }

    [Fact]
    public void WordBoundaryOccurrenceAfterMidWordOccurrence_StillCountsAsBoundary()
    {
        var entries = new[] { E(1, "Xab"), E(2, "Cabin Cab ab") };
        // "Cabin Cab ab": first "ab" is mid-word, a later one starts a word; boundary tier beats the shorter mid-word name.
        Assert.Equal(new uint[] { 2, 1 }, Ids(ItemSearch.Rank("ab", entries, 10)));
    }

    [Fact]
    public void Prefix_ShorterFirst_ThenAlphabetical_ThenId()
    {
        var entries = new[]
        {
            E(9, "Iron Ore"),
            E(8, "iron bar"),
            E(7, "Iron Ore"),
            E(6, "Iron Rod"),
            E(5, "Iron Ingot"),
        };
        // lengths: bar 8, Ore 8, Ore 8, Rod 8, Ingot 10. Alphabetical among 8s: bar, Ore(7), Ore(9), Rod.
        Assert.Equal(new uint[] { 8, 7, 9, 6, 5 }, Ids(ItemSearch.Rank("iron", entries, 10)));
    }

    [Fact]
    public void Exact_TiesById_AndBeatsPrefix()
    {
        var entries = new[] { E(5, "Ore"), E(3, "ore"), E(4, "Ore Bag") };
        Assert.Equal(new uint[] { 3, 5, 4 }, Ids(ItemSearch.Rank("ORE", entries, 10)));
    }

    [Fact]
    public void WordBoundary_ShorterThenAlphabetical()
    {
        var entries = new[] { E(1, "Big Silk Bolt"), E(2, "Big Silk"), E(3, "Big Silk Bag") };
        Assert.Equal(new uint[] { 2, 3, 1 }, Ids(ItemSearch.Rank("silk", entries, 10)));
    }

    [Fact]
    public void MidWord_ShorterThenAlphabetical()
    {
        var entries = new[] { E(1, "xxsilkzz"), E(2, "xsilk"), E(3, "xxsilkaa") };
        Assert.Equal(new uint[] { 2, 3, 1 }, Ids(ItemSearch.Rank("silk", entries, 10)));
    }

    [Fact]
    public void Max_CapsResults_AndKeepsBest()
    {
        var entries = Enumerable.Range(1, 50).Select(i => E((uint)i, "Thing " + (100 - i))).Append(E(999, "Thing")).ToList();
        var r = ItemSearch.Rank("thing", entries, 3);
        Assert.Equal(3, r.Count);
        Assert.Equal(new[] { "Thing", "Thing 50", "Thing 51" }, r.Select(x => x.Name).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MaxNotPositive_ReturnsNothing(int max)
        => Assert.Empty(ItemSearch.Rank("a", new[] { E(1, "a") }, max));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyQuery_ReturnsNothing(string? q)
        => Assert.Empty(ItemSearch.Rank(q, new[] { E(1, "a") }, 10));

    [Fact]
    public void QueryIsTrimmed()
        => Assert.Equal(new uint[] { 1 }, Ids(ItemSearch.Rank("  cotton  ", new[] { E(1, "Cotton"), E(2, "Wool") }, 10)));

    [Fact]
    public void NoMatch_ReturnsEmpty()
        => Assert.Empty(ItemSearch.Rank("zzz", new[] { E(1, "Cotton") }, 10));

    [Fact]
    public void Unicode_CaseInsensitive()
    {
        var entries = new[] { E(1, "Ärmel Stoff"), E(2, "Stoff Ärmel"), E(3, "日本語の剣") };
        Assert.Equal(new uint[] { 1, 2 }, Ids(ItemSearch.Rank("ÄRMEL", entries, 10)));
        Assert.Equal(new uint[] { 3 }, Ids(ItemSearch.Rank("日本語", entries, 10)));
    }

    [Fact]
    public void Deterministic_RegardlessOfInputOrder()
    {
        var entries = new[] { E(1, "Ab"), E(2, "Ab"), E(3, "Abc"), E(4, "xAb"), E(5, "x Ab") };
        var a = Ids(ItemSearch.Rank("ab", entries, 10));
        var b = Ids(ItemSearch.Rank("ab", entries.Reverse().ToList(), 10));
        Assert.Equal(new uint[] { 1, 2, 3, 5, 4 }, a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void LargeInput_RespectsCap()
    {
        var entries = Enumerable.Range(1, 20000).Reverse().Select(i => E((uint)i, "Item number " + i)).ToList();
        var r = ItemSearch.Rank("item number 1", entries, 8);
        Assert.Equal(8, r.Count);
        Assert.Equal("Item number 1", r[0].Name);
    }
}
