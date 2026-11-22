using System.Globalization;
using System.Text;

namespace LunaShoppingList.Core.Tests;

/// <summary>
/// QA pass ie-qa (2026-11-22): adversarial tests over the Import / Export tab Core code that the authors' own
/// tests do not reach: truncation and byte-flip fuzzing, boundary quantities, Unicode names, the PasteParser
/// header rule, and a non-English culture. Every test here states the invariant it pins.
/// </summary>
public class ImportExportAttackTests
{
    private sealed class Items : IItemInfo
    {
        public string GetName(uint itemId) => "Item #" + itemId;
        public bool IsCrystalLike(uint itemId) => false;
        public uint? FindByName(string name) => name.Equals("Iron Ore", StringComparison.OrdinalIgnoreCase) ? 5111u : null;
    }

    private sealed class Recipes : IRecipeLookup, IRecipeData
    {
        public RecipeInfo? FindRecipeForItem(uint itemId) => itemId switch
        {
            100 => new RecipeInfo(1000, 100, 1, Array.Empty<Ingredient>()),
            200 => new RecipeInfo(2000, 200, 3, Array.Empty<Ingredient>()),
            _ => null,
        };

        public RecipeInfo? GetRecipe(uint recipeId) => recipeId switch
        {
            1000 => new RecipeInfo(1000, 100, 1, Array.Empty<Ingredient>()),
            2000 => new RecipeInfo(2000, 200, 3, Array.Empty<Ingredient>()),
            _ => null,
        };
    }

    private static readonly Recipes Rec = new();

    private static void AssertTeamcraftInvariants(TeamcraftParseResult r)
    {
        Assert.NotNull(r.Unmatched);
        if (r.Error is null)
        {
            Assert.NotNull(r.Requirements);
            Assert.NotEmpty(r.Requirements!);
            foreach (var q in r.Requirements!)
            {
                Assert.NotEqual(0u, q.ItemId);
                Assert.InRange(q.Quantity, 1, CustomListOps.MaxQuantity);
            }
            Assert.Equal(r.Requirements.Select(x => x.ItemId).Distinct().Count(), r.Requirements.Count);
        }
        else Assert.Null(r.Requirements);
    }

    private static void AssertArtisanInvariants(ArtisanImportResult r)
    {
        Assert.NotNull(r.Unmatched);
        if (r.Error is null)
        {
            Assert.NotNull(r.Requirements);
            Assert.NotEmpty(r.Requirements!);
            foreach (var q in r.Requirements!)
            {
                Assert.NotEqual(0u, q.ItemId);
                Assert.InRange(q.Quantity, 1, CustomListOps.MaxQuantity);
            }
        }
        else Assert.Null(r.Requirements);
    }

    // ---------- fuzz ----------

    [Fact]
    public void TeamcraftTryParse_EveryTruncationAndEveryByteFlipOfARealLink_NeverThrowsAndHoldsInvariants()
    {
        // Invariant: whatever the clipboard holds, TryParse returns Error XOR a non-empty, in-range, distinct row set.
        var link = TeamcraftLink.Build(new[] { new Requirement(20545, 3), new Requirement(17962, 1), new Requirement(20247, 9_999_999) })!;
        var alphabet = "AZaz09+/=-_%;,. \t\u00e9\u4e2d{}";
        var checkedCount = 0;
        for (var cut = 0; cut <= link.Length; cut++)
        {
            AssertTeamcraftInvariants(TeamcraftLink.TryParse(link.Substring(0, cut)));
            checkedCount++;
        }
        for (var i = 0; i < link.Length; i++)
            foreach (var c in alphabet)
            {
                AssertTeamcraftInvariants(TeamcraftLink.TryParse(link.Remove(i, 1).Insert(i, c.ToString())));
                checkedCount++;
            }
        Assert.True(checkedCount > 1000);
    }

    [Fact]
    public void TeamcraftTryParse_RandomText_NeverThrowsAndHoldsInvariants()
    {
        var rng = new Random(20261122);
        var pool = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=-_%;,:.?# \r\n\t\u00e9\u4e2d\ud83d\ude00/import/{}\"";
        for (var n = 0; n < 3000; n++)
        {
            var sb = new StringBuilder();
            var len = rng.Next(0, 80);
            for (var i = 0; i < len; i++) sb.Append(pool[rng.Next(pool.Length)]);
            AssertTeamcraftInvariants(TeamcraftLink.TryParse(sb.ToString()));
        }
    }

    [Fact]
    public void TeamcraftTryParse_LoneSurrogateAndNulChars_DoNotThrow()
    {
        foreach (var s in new[] { "\ud800", "https://ffxivteamcraft.com/import/\udc00", "\0", "/import/\0\0", "%", "%%%", "%zz", "%E4%B8", "%00" })
            AssertTeamcraftInvariants(TeamcraftLink.TryParse(s));
    }

    [Fact]
    public void ArtisanTryParse_EveryTruncationOfARealExport_NeverThrowsAndHoldsInvariants()
    {
        // Invariant: a half-pasted JSON is an error with null requirements (or a clean parse), never an exception.
        var json = ArtisanListExport.Build("Test list", new[] { new Requirement(100, 4), new Requirement(200, 7) }, Rec).Json;
        Assert.NotEmpty(json);
        for (var cut = 0; cut < json.Length; cut++)
            AssertArtisanInvariants(ArtisanListExport.TryParse(json.Substring(0, cut), Rec));
        Assert.Null(ArtisanListExport.TryParse(json, Rec).Error);
    }

    [Theory]
    [InlineData("{\"Recipes\":5}")]
    [InlineData("{\"Recipes\":\"abc\"}")]
    [InlineData("{\"Recipes\":null}")]
    [InlineData("{\"Recipes\":{}}")]
    [InlineData("{\"Recipes\":{\"$values\":5}}")]
    [InlineData("{\"Recipes\":[null,5,\"x\",[],true]}")]
    [InlineData("{\"Recipes\":[{\"ID\":-1,\"Quantity\":1}]}")]
    [InlineData("{\"Recipes\":[{\"ID\":1000.5,\"Quantity\":1}]}")]
    [InlineData("{\"Recipes\":[{\"ID\":\"1000\",\"Quantity\":1}]}")]
    [InlineData("{\"Recipes\":[{\"ID\":4294967296,\"Quantity\":1}]}")]
    [InlineData("{\"Recipes\":[{\"ID\":1000,\"Quantity\":1.5}]}")]
    [InlineData("{\"Recipes\":[{\"ID\":1000,\"Quantity\":2147483648}]}")]
    [InlineData("{\"Recipes\":[{\"ID\":1000,\"Quantity\":-3}]}")]
    [InlineData("{\"Recipes\":[{\"ID\":1000,\"Quantity\":\"4\"}]}")]
    [InlineData("{\"Recipes\":[{\"ID\":1000,\"Quantity\":1e400}]}")]
    [InlineData("{\"Recipes\":[{\"ID\":1000,\"Quantity\":1,\"ListItemOptions\":5}]}")]
    [InlineData("{\"Recipes\":[{\"ID\":1000,\"Quantity\":1,\"ListItemOptions\":{\"Skipping\":\"true\"}}]}")]
    [InlineData("[{\"ID\":1000,\"Quantity\":1}]")]
    [InlineData("{}")]
    [InlineData("{\"recipes\":[{\"id\":1000,\"quantity\":1}]}")]
    public void ArtisanTryParse_WrongShapesAndTypes_NeverThrowAndHoldInvariants(string json)
    {
        AssertArtisanInvariants(ArtisanListExport.TryParse(json, Rec));
    }

    [Fact]
    public void ArtisanTryParse_SkippingAsStringIsNotSkipping_CharacterizesCurrentBehaviour()
    {
        // CHARACTERIZATION: only JSON true skips; the string "true" does not. Artisan's bool field never writes a string.
        var r = ArtisanListExport.TryParse("{\"Recipes\":[{\"ID\":1000,\"Quantity\":2,\"ListItemOptions\":{\"Skipping\":\"true\"}}]}", Rec);
        Assert.Equal(new[] { new Requirement(100, 2) }, r.Requirements!.ToArray());
    }

    [Fact]
    public void ArtisanTryParse_DuplicateRootKeys_DoNotThrow()
    {
        var r = ArtisanListExport.TryParse("{\"Name\":\"a\",\"Name\":\"b\",\"Recipes\":[{\"ID\":1000,\"Quantity\":1}],\"Recipes\":[]}", Rec);
        AssertArtisanInvariants(r);
    }

    [Fact]
    public void ImportParser_RandomText_NeverThrowsAndRequirementsNeverNull()
    {
        var rng = new Random(77);
        var pool = "{}[]\":,0123456789xX\u00d7 \r\n\t:/import/AaZz+=%;Iron Ore\u4e2d\ud83d\ude00#?";
        for (var n = 0; n < 3000; n++)
        {
            var sb = new StringBuilder();
            var len = rng.Next(0, 90);
            for (var i = 0; i < len; i++) sb.Append(pool[rng.Next(pool.Length)]);
            var r = ImportParser.Parse(sb.ToString(), new Items(), Rec);
            Assert.NotNull(r.Requirements);
            Assert.NotNull(r.Unmatched);
            Assert.False(string.IsNullOrWhiteSpace(r.SuggestedName));
            if (r.Error is not null) Assert.Empty(r.Requirements);
            foreach (var q in r.Requirements) Assert.InRange(q.Quantity, 1, CustomListOps.MaxQuantity);
        }
    }

    [Fact]
    public void ImportParser_ByteOrderMarkBeforeArtisanJson_CharacterizesCurrentBehaviour()
    {
        // CHARACTERIZATION: char.IsWhiteSpace is false for U+FEFF, so Trim keeps it, the text no longer starts with '{',
        // and Artisan JSON is read as an item list (every line unmatched, no error). Artisan's clipboard has no BOM; a file
        // pasted from an editor might. Known, not yet addressed.
        var json = "{\"Name\":\"x\",\"Recipes\":[{\"ID\":1000,\"Quantity\":1}]}";
        var r = ImportParser.Parse("\uFEFF" + json, new Items(), Rec);
        Assert.Equal(ImportKind.ItemList, r.Kind);
        Assert.Null(r.Error);
        Assert.Empty(r.Requirements);
    }

    // ---------- boundary quantities ----------

    [Theory]
    [InlineData("9999998", 9_999_998)]
    [InlineData("9999999", 9_999_999)]
    [InlineData("10000000", 9_999_999)]
    [InlineData("2147483647", 9_999_999)]
    [InlineData("2147483648", 9_999_999)]
    [InlineData("4294967296", 9_999_999)]
    [InlineData("99999999999999999999999999", 9_999_999)]
    public void TeamcraftTryParse_QuantityBoundaries_ClampToMaxQuantity(string qty, int expected)
    {
        var link = "https://ffxivteamcraft.com/import/" + Convert.ToBase64String(Encoding.UTF8.GetBytes("7," + "null," + qty));
        var r = TeamcraftLink.TryParse(link);
        Assert.Null(r.Error);
        Assert.Equal(new[] { new Requirement(7, expected) }, r.Requirements!.ToArray());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+3")]
    [InlineData("1.5")]
    [InlineData("1e3")]
    [InlineData("")]
    [InlineData("0x10")]
    public void TeamcraftTryParse_NonPositiveOrNonIntegerQuantity_IsUnmatchedNotARow(string qty)
    {
        var link = "https://ffxivteamcraft.com/import/" + Convert.ToBase64String(Encoding.UTF8.GetBytes("7,null," + qty + ";8,null,2"));
        var r = TeamcraftLink.TryParse(link);
        Assert.Null(r.Error);
        Assert.Equal(new[] { new Requirement(8, 2) }, r.Requirements!.ToArray());
        Assert.Single(r.Unmatched);
        Assert.StartsWith("Row 1:", r.Unmatched[0]);
    }

    [Fact]
    public void Teamcraft_BuildThenParse_ItemIdUintMaxAndQuantityIntMax_RoundTripsClamped()
    {
        var link = TeamcraftLink.Build(new[] { new Requirement(uint.MaxValue, int.MaxValue), new Requirement(1, 1) })!;
        var r = TeamcraftLink.TryParse(link);
        Assert.Equal(new[] { new Requirement(uint.MaxValue, 9_999_999), new Requirement(1, 1) }, r.Requirements!.ToArray());
    }

    [Fact]
    public void ArtisanExport_QuantityIntMaxWithYieldOne_CraftsClampAndJsonStaysValid()
    {
        var r = ArtisanListExport.Build("n", new[] { new Requirement(100, int.MaxValue) }, Rec);
        Assert.Contains("\"Quantity\":9999999", r.Json);
        Assert.DoesNotContain("2147483647", r.Json);
        Assert.Equal(1, r.CraftedItems);
    }

    [Fact]
    public void ArtisanExport_ManyRowsForOneRecipe_SumClampsWithoutOverflow()
    {
        var rows = Enumerable.Range(0, 5).Select(_ => new Requirement(100, int.MaxValue)).ToList();
        var r = ArtisanListExport.Build("n", rows, Rec);
        Assert.Contains("\"Quantity\":9999999", r.Json);
        Assert.Equal(5, r.CraftedItems);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(6, 2)]
    [InlineData(7, 3)]
    public void ArtisanExport_YieldThree_CraftsAreCeilOfQuantityOverYield(int qty, int crafts)
    {
        var r = ArtisanListExport.Build("n", new[] { new Requirement(200, qty) }, Rec);
        Assert.Contains("\"ID\":2000,\"Quantity\":" + crafts + ",", r.Json);
    }

    // ---------- Unicode ----------

    [Theory]
    [InlineData("\u30a2\u30a4\u30a2\u30f3 Ingot")]
    [InlineData("Caf\u00e9 \u00fcber")]
    [InlineData("\ud83d\ude00 list \ud83d\udd28")]
    [InlineData("tab\there \"quoted\" back\\slash \r\n new")]
    [InlineData("<script>&amp;")]
    public void ArtisanExport_UnicodeAndSpecialCharacterNames_RoundTripThroughTryParse(string name)
    {
        var json = ArtisanListExport.Build(name, new[] { new Requirement(100, 2) }, Rec).Json;
        var r = ArtisanListExport.TryParse(json, Rec);
        Assert.Null(r.Error);
        Assert.Equal(name, r.Name);
        var viaImport = ImportParser.Parse(json, new Items(), Rec);
        Assert.Equal(ImportKind.ArtisanList, viaImport.Kind);
        Assert.Equal(name.Trim(), viaImport.SuggestedName);
    }

    [Fact]
    public void ArtisanExport_LoneSurrogateInName_DoesNotThrowAndStillParses()
    {
        // Measured: Utf8JsonWriter replaces an invalid UTF-16 lone surrogate with U+FFFD instead of throwing, so Build
        // (which has no try/catch) is safe for any string the player can type or an old settings file can hold.
        var json = ArtisanListExport.Build("bad \ud800 name", new[] { new Requirement(100, 1) }, Rec).Json;
        var r = ArtisanListExport.TryParse(json, Rec);
        Assert.Null(r.Error);
        Assert.Equal("bad \ufffd name", r.Name);
    }

    [Fact]
    public void Teamcraft_ParseOfNonAsciiPayloadRows_AreUnmatchedNotThrown()
    {
        var link = "https://ffxivteamcraft.com/import/" + Convert.ToBase64String(Encoding.UTF8.GetBytes("\u4e2d,null,3;9,null,1"));
        var r = TeamcraftLink.TryParse(link);
        Assert.Equal(new[] { new Requirement(9, 1) }, r.Requirements!.ToArray());
        Assert.Single(r.Unmatched);
    }

    [Fact]
    public void PasteParser_UnicodeItemName_IsLookedUpAsGiven()
    {
        var seen = new List<string>();
        var items = new RecordingItems(seen);
        var r = PasteParser.Parse("3x \u9244\u9271\u77f3\n2\u00d7 Caf\u00e9 \u00fcber", items);
        Assert.Equal(new[] { "\u9244\u9271\u77f3", "Caf\u00e9 \u00fcber" }, seen.Distinct().ToArray());
        Assert.Equal(2, r.UnmatchedLines.Count);
    }

    private sealed class RecordingItems(List<string> seen) : IItemInfo
    {
        public string GetName(uint itemId) => "Item #" + itemId;
        public bool IsCrystalLike(uint itemId) => false;
        public uint? FindByName(string name) { seen.Add(name); return null; }
    }

    // ---------- PasteParser ' :' header rule ----------

    [Theory]
    [InlineData("Crystals :")]
    [InlineData("Pre_crafts :")]
    [InlineData("  Gathered :  ")]
    [InlineData("Final items :")]
    [InlineData("Haupt Handwerk :")]
    [InlineData("\u30af\u30ea\u30b9\u30bf\u30eb :")]
    public void PasteParser_SpaceColonLineIsASilentHeader(string line)
    {
        var r = PasteParser.Parse(line + "\n3x Iron Ore", new Items());
        Assert.Equal(new[] { new Requirement(5111, 3) }, r.Requirements.ToArray());
        Assert.Empty(r.UnmatchedLines);
    }

    [Theory]
    [InlineData("Crystals:")]
    [InlineData("Crystals : extra")]
    [InlineData(":")]
    [InlineData(" :")]
    [InlineData("Crystals\uff1a")]
    [InlineData("Crystals  \u00a0:x")]
    public void PasteParser_LinesThatAreNotSpaceColonHeaders_StayUnmatched(string line)
    {
        // " :" alone has no non-space text before it; the others do not END with ' :'.
        var r = PasteParser.Parse(line, new Items());
        Assert.Empty(r.Requirements);
        Assert.Single(r.UnmatchedLines);
    }

    [Fact]
    public void PasteParser_QuantityLineWhoseItemNameEndsInSpaceColon_IsUnmatchedNotSkipped()
    {
        var r = PasteParser.Parse("3x Iron Ore :", new Items());
        Assert.Empty(r.Requirements);
        Assert.Equal(new[] { "3x Iron Ore :" }, r.UnmatchedLines.ToArray());
    }

    [Fact]
    public void PasteParser_ItemNameOnlyLineEndingInSpaceColon_IsSilentlyDropped_Characterization()
    {
        // CHARACTERIZATION: "Iron Ore :" (no quantity) is indistinguishable from a header, so it vanishes silently
        // instead of appearing in the unmatched list. Harmless for Teamcraft text; known, not yet addressed.
        var r = PasteParser.Parse("Iron Ore :", new Items());
        Assert.Empty(r.Requirements);
        Assert.Empty(r.UnmatchedLines);
    }

    // ---------- culture ----------

    private static void UnderCulture(string name, Action body)
    {
        var old = CultureInfo.CurrentCulture;
        var oldUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(name);
            CultureInfo.CurrentUICulture = new CultureInfo(name);
            body();
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
            CultureInfo.CurrentUICulture = oldUi;
        }
    }

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    [InlineData("fa-IR")]
    [InlineData("ja-JP")]
    [InlineData("th-TH")]
    public void Culture_LinkBuildParseAndImportDetection_AreCultureIndependent(string culture)
    {
        UnderCulture(culture, () =>
        {
            var rows = new[] { new Requirement(20545, 3), new Requirement(17962, 1234567), new Requirement(5, 9_999_999) };
            var link = TeamcraftLink.Build(rows)!;
            Assert.StartsWith("https://ffxivteamcraft.com/import/", link);
            Assert.Equal(rows, TeamcraftLink.TryParse(link).Requirements!.ToArray());

            // Upper-case marker: a culture-sensitive IgnoreCase compare fails for Turkish dotless i.
            var upper = link.Replace("/import/", "/IMPORT/");
            var viaImport = ImportParser.Parse(upper, new Items(), Rec);
            Assert.Equal(ImportKind.TeamcraftLink, viaImport.Kind);
            Assert.Equal(rows, viaImport.Requirements.ToArray());

            // Marker with spaces around it and no usable data: only the marker test can call it a link (Teamcraft error).
            var withText = ImportParser.Parse("see " + upper + " thanks", new Items(), Rec);
            Assert.Equal(ImportKind.TeamcraftLink, withText.Kind);

            var json = ArtisanListExport.Build("n", new[] { new Requirement(200, 1234567) }, Rec).Json;
            Assert.Contains("\"Quantity\":411523,", json);
            Assert.Equal(new[] { new Requirement(200, 1234569) }, ArtisanListExport.TryParse(json, Rec).Requirements!.ToArray());

            var paste = PasteParser.Parse("1234567x Iron Ore\nCrystals :", new Items());
            Assert.Equal(new[] { new Requirement(5111, 1234567) }, paste.Requirements.ToArray());
        });
    }
}
