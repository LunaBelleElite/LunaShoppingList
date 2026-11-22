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

    [Theory]
    [InlineData("Crystals :")]
    [InlineData("Pre_crafts :")]
    [InlineData("Crystals :   ")]
    [InlineData("  Pre_crafts : ")]
    public void TeamcraftSectionHeaders_AreSkippedSilently(string header)
    {
        var r = Parse(header + "\n3x Iron Ore");
        Assert.Equal(new[] { new Requirement(5, 3) }, r.Requirements.ToArray());
        Assert.Empty(r.UnmatchedLines);
    }

    [Theory]
    [InlineData("Crystals:")]
    [InlineData("Required ingredients")]
    [InlineData("Note : something here")]
    public void NonHeaderLines_StillUnmatched(string line)
    {
        var r = Parse(line);
        Assert.Equal(new[] { line }, r.UnmatchedLines.ToArray());
    }

    [Fact]
    public void QuantityLineEndingInSpaceColon_IsNotAHeader()
    {
        // Matches the quantity pattern, so it goes through the item lookup (unknown name -> unmatched), not skipped.
        var r = Parse("3x Mystery :");
        Assert.Single(r.UnmatchedLines);
    }

    // ---- ReDoS guard and behaviour-equivalence for the line pattern (2026-11-22) ----

    [Theory]
    [InlineData(" ")]
    [InlineData(" \u00A0")]
    public void PathologicalTrailingWhitespace_FinishesQuicklyAndStillParses(string filler)
    {
        // The old pattern ^\s*([0-9]+)\s*[xX×]\s*(\S.*?)\s*$ backtracked quadratically on this shape (9-13 s at 50,000).
        var sb = new System.Text.StringBuilder("1x a");
        for (int i = 0; i < 50_000; i++) sb.Append(filler[i % filler.Length]);
        sb.Append('b');
        var text = sb.ToString();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = Parse(text);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");
        // "a   ...   b" is not a known item: reported verbatim, not dropped.
        Assert.Equal(new[] { text }, r.UnmatchedLines.ToArray());
        Assert.Empty(r.Requirements);
    }

    [Fact]
    public void PathologicalTrailingWhitespace_AfterAKnownName_StillMatches()
    {
        var text = "2x Iron Ore" + new string(' ', 50_000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = Parse(text);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(new[] { new Requirement(5, 2) }, r.Requirements.ToArray());
        Assert.Empty(r.UnmatchedLines);
    }

    /// <summary>Echoes every name asked about and answers a stable id derived from it.</summary>
    private sealed class EchoItems : IItemInfo
    {
        public readonly List<string> Names = new();
        public string GetName(uint itemId) => "Item #" + itemId;
        public bool IsCrystalLike(uint itemId) => false;
        public uint? FindByName(string name)
        {
            Names.Add(name);
            uint h = 17;
            foreach (var ch in name) h = h * 31 + ch;
            return h;
        }
    }

    // The pre-change implementation, kept verbatim as the oracle.
    private static readonly System.Text.RegularExpressions.Regex OldLine = new(@"^\s*([0-9]+)\s*[xX\u00D7]\s*(\S.*?)\s*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex OldHeader = new(@"^\s*\S.* :\s*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    [Fact]
    public void NewLinePattern_IsEquivalentToTheOldOneOnManyRandomLines()
    {
        string[] pieces = { "0", "1", "7", "12", "99999999999", "x", "X", "\u00D7", " ", " ", "\t", "\u00A0", "\u3000", "\u2003", "\u0085",
            "a", "Iron", "b", ":", " :", "-", "\u200B", "\u001F", "\u180E" };
        var rng = new Random(20261122);
        for (int n = 0; n < 30_000; n++)
        {
            var sb = new System.Text.StringBuilder();
            int len = rng.Next(0, 10);
            for (int i = 0; i < len; i++) sb.Append(pieces[rng.Next(pieces.Length)]);
            var line = sb.ToString();
            if (string.IsNullOrWhiteSpace(line)) continue;

            // oracle
            var m = OldLine.Match(line);
            var expectedReq = new List<Requirement>();
            var expectedUnmatched = new List<string>();
            string? expectedName = null;
            if (!m.Success && OldHeader.IsMatch(line)) { /* header: skipped silently */ }
            else if (!m.Success || !int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var q) || q <= 0)
                expectedUnmatched.Add(line);
            else
            {
                expectedName = m.Groups[2].Value;
                var probe = new EchoItems();
                expectedReq.Add(new Requirement(probe.FindByName(expectedName)!.Value, q));
            }

            var items = new EchoItems();
            var actual = PasteParser.Parse(line, items);

            // Parse splits on \r and \n only; no piece contains them, so one line in, one line out.
            Assert.True(expectedReq.SequenceEqual(actual.Requirements), $"requirements differ for [{Escape(line)}]");
            Assert.True(expectedUnmatched.SequenceEqual(actual.UnmatchedLines), $"unmatched differ for [{Escape(line)}]");
            if (expectedName is not null)
                Assert.Equal(expectedName, items.Names[0]);
        }
    }

    private static string Escape(string s) => string.Concat(s.Select(c => c < 32 || c > 126 ? "U+" + ((int)c).ToString("X4") : c.ToString()));
}
