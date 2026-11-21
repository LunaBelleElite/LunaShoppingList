namespace LunaShoppingList.Core.Tests;

public class PasteParserTests
{
    private sealed class FakeItems : IItemInfo
    {
        private readonly Dictionary<string, uint> _byName = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Iron Ore"] = 5, ["Copper Ore"] = 3, ["Fire Shard"] = 2, ["Maple Log"] = 40, ["Cobalt Ingot"] = 9,
        };
        public string GetName(uint itemId) => "Item #" + itemId;
        public bool IsCrystalLike(uint itemId) => itemId == 2;
        public uint? FindByName(string name) => _byName.TryGetValue(name, out var id) ? id : null;
    }

    private static PasteResult Parse(string text) => PasteParser.Parse(text, new FakeItems());

    [Fact]
    public void ValidLines_AreSummedAndOrderedByItemId()
    {
        var r = Parse("3x Iron Ore\n10x Fire Shard\n2x Copper Ore\n4x Iron Ore");

        Assert.Equal(new[] { new Requirement(2, 10), new Requirement(3, 2), new Requirement(5, 7) }, r.Requirements.ToArray());
        Assert.Empty(r.UnmatchedLines);
    }

    [Fact]
    public void FixtureFile_MixOfValidAndInvalid()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PasteParser", "export.txt"));
        var r = Parse(text);

        Assert.Equal(
            new[] { new Requirement(2, 10), new Requirement(3, 2), new Requirement(5, 3), new Requirement(40, 4) },
            r.Requirements.ToArray());
        Assert.Equal(new[] { "Required ingredients", "7x Mystery Item" }, r.UnmatchedLines.ToArray());
    }

    [Theory]
    [InlineData("3x Iron Ore")]
    [InlineData("3X Iron Ore")]
    [InlineData("3× Iron Ore")]
    [InlineData("3 x Iron Ore")]
    [InlineData("  3x   Iron Ore  ")]
    [InlineData("3xIron Ore")]
    [InlineData("3x iron ore")]
    [InlineData("03x Iron Ore")]
    [InlineData("3x Iron   Ore")]
    public void QuantityMarkerVariants_Match(string line)
    {
        var r = Parse(line);
        Assert.Equal(new[] { new Requirement(5, 3) }, r.Requirements.ToArray());
        Assert.Empty(r.UnmatchedLines);
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\r\nb")]
    [InlineData("a\rb")]
    [InlineData("a\n\n  \r\n\tb\n")]
    public void AnyNewlineStyle_AndBlankLinesAreIgnored(string template)
    {
        var text = template.Replace("a", "1x Iron Ore").Replace("b", "2x Copper Ore");
        var r = Parse(text);
        Assert.Equal(new[] { new Requirement(3, 2), new Requirement(5, 1) }, r.Requirements.ToArray());
        Assert.Empty(r.UnmatchedLines);
    }

    [Fact]
    public void InvalidLines_GoToUnmatchedVerbatim_AndValidOnesStillParse()
    {
        var text = "Gathering list:\n3x Iron Ore\n5x Unknown Thing\n0x Copper Ore\n-2x Copper Ore\nx Iron Ore\n3x\n1,000x Iron Ore\n"
                 + "99999999999x Iron Ore\nhttps://ffxivteamcraft.com/import/WzFd\n2x Fire Shard";
        var r = Parse(text);

        Assert.Equal(new[] { new Requirement(2, 2), new Requirement(5, 3) }, r.Requirements.ToArray());
        Assert.Equal(new[]
        {
            "Gathering list:", "5x Unknown Thing", "0x Copper Ore", "-2x Copper Ore", "x Iron Ore", "3x",
            "1,000x Iron Ore", "99999999999x Iron Ore", "https://ffxivteamcraft.com/import/WzFd",
        }, r.UnmatchedLines.ToArray());
    }

    [Fact]
    public void UnmatchedLine_KeepsInnerAndLeadingSpacing_ButNotNewlines()
    {
        var r = Parse("  7x  Nope Item \r\n");
        Assert.Equal(new[] { "  7x  Nope Item " }, r.UnmatchedLines.ToArray());
    }

    [Fact]
    public void NoFuzzyMatching()
    {
        var r = Parse("1x Iron Or\n1x Iron Ore Extra");
        Assert.Empty(r.Requirements);
        Assert.Equal(2, r.UnmatchedLines.Count);
    }

    [Fact]
    public void SummedOverflow_IsNotWrapped()
    {
        var r = Parse("2000000000x Iron Ore\n2000000000x Iron Ore\n1x Copper Ore");
        Assert.Equal(new[] { new Requirement(3, 1) }, r.Requirements.ToArray());
        Assert.Single(r.UnmatchedLines);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n  \n")]
    public void EmptyInput_GivesEmptyResult(string text)
    {
        var r = Parse(text);
        Assert.Empty(r.Requirements);
        Assert.Empty(r.UnmatchedLines);
    }

    [Fact]
    public void NullText_IsTreatedAsEmpty()
    {
        var r = PasteParser.Parse(null!, new FakeItems());
        Assert.Empty(r.Requirements);
        Assert.Empty(r.UnmatchedLines);
    }
}
