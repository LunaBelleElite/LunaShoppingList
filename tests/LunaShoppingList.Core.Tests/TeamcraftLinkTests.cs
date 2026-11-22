using System.Text;

namespace LunaShoppingList.Core.Tests;

public class TeamcraftLinkTests
{
    private const string Prefix = "https://ffxivteamcraft.com/import/";

    // Independent vector (python base64.b64encode) of "20545,null,3;17962,null,1;20247,null,1".
    private const string BuildVector = "MjA1NDUsbnVsbCwzOzE3OTYyLG51bGwsMTsyMDI0NyxudWxsLDE=";

    // Teamcraft wiki vector: "20545,null,3;17962,32308,1;20247,null,1" (carries a real recipe id).
    private const string WikiVector = "MjA1NDUsbnVsbCwzOzE3OTYyLDMyMzA4LDE7MjAyNDcsbnVsbCwx";

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
    private static string Enc(string b64) => b64.Replace("+", "%2B").Replace("/", "%2F").Replace("=", "%3D");

    private static Requirement R(uint id, int q) => new(id, q);

    // ---- Build ----

    [Fact]
    public void Build_Golden_MatchesIndependentVector()
    {
        var link = TeamcraftLink.Build(new[] { R(20545, 3), R(17962, 1), R(20247, 1) });
        Assert.Equal(Prefix + "MjA1NDUsbnVsbCwzOzE3OTYyLG51bGwsMTsyMDI0NyxudWxsLDE%3D", link);
        Assert.Equal(Prefix + Enc(BuildVector), link);
    }

    [Fact]
    public void Build_PaddingIsPercentEncoded()
    {
        var link = TeamcraftLink.Build(new[] { R(7, 1) })!;
        Assert.Equal(Prefix + "NyxudWxsLDE%3D", link);
    }

    // Note: Build's rows only contain digits, ',', ';' and "null", whose base64 can never contain '+' or '/'
    // (those need a byte >= 0x3E in the 4th position or >= 0x80 elsewhere), so only '=' encoding is reachable here.

    [Fact]
    public void Build_NoTrailingSemicolon()
    {
        var link = TeamcraftLink.Build(new[] { R(1, 1), R(2, 2) })!;
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(Uri.UnescapeDataString(link.Substring(Prefix.Length))));
        Assert.Equal("1,null,1;2,null,2", decoded);
    }

    [Fact]
    public void Build_DropsItemZeroAndNonPositiveQuantity()
    {
        var link = TeamcraftLink.Build(new[] { R(0, 5), R(9, 0), R(8, -3), R(4, 2) })!;
        Assert.Equal(Prefix + Enc(B64("4,null,2")), link);
    }

    [Fact]
    public void Build_NoUsableRow_IsNull()
    {
        Assert.Null(TeamcraftLink.Build(Array.Empty<Requirement>()));
        Assert.Null(TeamcraftLink.Build(new[] { R(0, 1), R(5, 0) }));
    }

    [Fact]
    public void Build_MergesDuplicatesKeepingFirstSeenOrder()
    {
        var link = TeamcraftLink.Build(new[] { R(9, 1), R(3, 2), R(9, 4) })!;
        Assert.Equal(Prefix + Enc(B64("9,null,5;3,null,2")), link);
    }

    [Fact]
    public void Build_DuplicateSumsSaturateAtMax()
    {
        var link = TeamcraftLink.Build(new[] { R(9, int.MaxValue), R(9, int.MaxValue), R(9, 5) })!;
        Assert.Equal(Prefix + Enc(B64("9,null,9999999")), link);
    }

    [Fact]
    public void Build_SingleHugeQuantityIsClamped()
    {
        var link = TeamcraftLink.Build(new[] { R(9, int.MaxValue) })!;
        Assert.Equal(Prefix + Enc(B64("9,null,9999999")), link);
    }

    [Fact]
    public void LongLinkWarningLength_Is8000() => Assert.Equal(8000, TeamcraftLink.LongLinkWarningLength);

    // ---- TryParse ----

    [Fact]
    public void TryParse_WikiVector_ReadsRealRecipeIdRow()
    {
        var r = TeamcraftLink.TryParse(Prefix + WikiVector);
        Assert.Null(r.Error);
        Assert.Empty(r.Unmatched);
        Assert.Equal(new[] { R(20545, 3), R(17962, 1), R(20247, 1) }, r.Requirements!.ToArray());
    }

    [Fact]
    public void TryParse_RoundTripsBuild()
    {
        var rows = new[] { R(20545, 3), R(17962, 1), R(20247, 1) };
        var r = TeamcraftLink.TryParse(TeamcraftLink.Build(rows)!);
        Assert.Equal(rows, r.Requirements!.ToArray());
    }

    [Theory]
    [InlineData("  {0}  ")]
    [InlineData("{0}?foo=bar")]
    [InlineData("{0}#frag")]
    [InlineData("{0}?a=b#frag")]
    [InlineData("\r\n{0}\r\n")]
    [InlineData("http://example.org/some/import/{0}")]
    public void TryParse_LinkDecorations(string template)
    {
        var text = template.Contains("/import/")
            ? string.Format(template, Enc(WikiVector))
            : string.Format(Prefix + template, Enc(WikiVector));
        var r = TeamcraftLink.TryParse(text);
        Assert.Null(r.Error);
        Assert.Equal(3, r.Requirements!.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryParse_BareBase64_RawOrPercentEncoded(bool encoded)
    {
        var b = B64("5,null,2;6,null,3");
        var r = TeamcraftLink.TryParse(encoded ? Enc(b) : b);
        Assert.Null(r.Error);
        Assert.Equal(new[] { R(5, 2), R(6, 3) }, r.Requirements!.ToArray());
    }

    [Fact]
    public void TryParse_DoublePercentEncoded_PercentTwentyFiveIsUndone()
    {
        // Teamcraft replaces %25 with % before decoding, so "%253D" reads as "%3D" -> "=".
        var b = B64("5,null,2;6,null,3");
        var r = TeamcraftLink.TryParse(Prefix + b.Replace("=", "%253D"));
        Assert.Null(r.Error);
        Assert.Equal(2, r.Requirements!.Count);
    }

    [Fact]
    public void TryParse_UrlSafeAlphabetAndMissingPadding()
    {
        // The recipe field is ignored, so it can carry bytes that make the base64 contain '+' and '/'.
        const string row = "5,?>?>?>>,2";
        var std = B64(row);
        Assert.True(std.Contains('+') || std.Contains('/'));
        Assert.EndsWith("=", std);
        var safe = std.Replace('+', '-').Replace('/', '_').TrimEnd('=');
        Assert.True(safe.Contains('-') || safe.Contains('_'));
        foreach (var form in new[] { safe, std, Enc(std), std.TrimEnd('=') })
        {
            var r = TeamcraftLink.TryParse(Prefix + form);
            Assert.Null(r.Error);
            Assert.Equal(new[] { R(5, 2) }, r.Requirements!.ToArray());
        }
    }

    [Fact]
    public void TryParse_ToleratesOneTrailingEmptyRow()
    {
        var r = TeamcraftLink.TryParse(Prefix + Enc(B64("5,null,2;")));
        Assert.Null(r.Error);
        Assert.Empty(r.Unmatched);
        Assert.Equal(new[] { R(5, 2) }, r.Requirements!.ToArray());
    }

    [Fact]
    public void TryParse_TwoTrailingEmptyRows_SecondIsReported()
    {
        var r = TeamcraftLink.TryParse(Prefix + Enc(B64("5,null,2;;")));
        Assert.Null(r.Error);
        Assert.Single(r.Unmatched);
        Assert.StartsWith("Row 2:", r.Unmatched[0]);
    }

    [Fact]
    public void TryParse_BadRowsGoToUnmatchedWithRowNumber()
    {
        var r = TeamcraftLink.TryParse(Prefix + Enc(B64("5,null,2;abc,null,1;7,null,0;8,null;0,null,4;9,null,x;6,null,1")));
        Assert.Null(r.Error);
        Assert.Equal(new[] { R(5, 2), R(6, 1) }, r.Requirements!.ToArray());
        Assert.Equal(5, r.Unmatched.Count);
        Assert.StartsWith("Row 2:", r.Unmatched[0]);
        Assert.StartsWith("Row 3:", r.Unmatched[1]);
        Assert.StartsWith("Row 4:", r.Unmatched[2]);
        Assert.StartsWith("Row 5:", r.Unmatched[3]);
        Assert.StartsWith("Row 6:", r.Unmatched[4]);
    }

    [Fact]
    public void TryParse_RecipeFieldIsIgnored_WhateverItHolds()
    {
        var r = TeamcraftLink.TryParse(Prefix + Enc(B64("5,zzz,2")));
        Assert.Null(r.Error);
        Assert.Equal(new[] { R(5, 2) }, r.Requirements!.ToArray());
    }

    [Fact]
    public void TryParse_MergesDuplicatesBySumming_InFirstSeenOrder()
    {
        var r = TeamcraftLink.TryParse(Prefix + Enc(B64("9,null,5;3,null,1;9,null,7")));
        Assert.Null(r.Error);
        Assert.Equal(new[] { R(9, 12), R(3, 1) }, r.Requirements!.ToArray());
    }

    [Fact]
    public void TryParse_MergesDuplicatesInFirstSeenOrder_AndClamps()
    {
        var r = TeamcraftLink.TryParse(Prefix + Enc(B64("9,null,5;3,null,1;9,null,2147483647;4,null,99999999999")));
        Assert.Null(r.Error);
        Assert.Equal(new[] { R(9, 9_999_999), R(3, 1), R(4, 9_999_999) }, r.Requirements!.ToArray());
    }

    [Fact]
    public void TryParse_ItemIdLargerThanUint_IsUnmatched()
    {
        var r = TeamcraftLink.TryParse(Prefix + Enc(B64("99999999999,null,1;5,null,1")));
        Assert.Equal(new[] { R(5, 1) }, r.Requirements!.ToArray());
        Assert.Single(r.Unmatched);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n ")]
    [InlineData("https://ffxivteamcraft.com/import/")]
    [InlineData("https://ffxivteamcraft.com/import/   ")]
    [InlineData("!!!not base64!!!")]
    [InlineData("garbage garbage garbage")]
    [InlineData("%%%%")]
    [InlineData("https://ffxivteamcraft.com/import/%ZZ")]
    [InlineData("https://x.test/import/???")]
    public void TryParse_Garbage_GivesErrorAndNoRequirements(string text)
    {
        var r = TeamcraftLink.TryParse(text);
        Assert.Null(r.Requirements);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    [Fact]
    public void TryParse_NullText_DoesNotThrow()
    {
        var r = TeamcraftLink.TryParse(null!);
        Assert.Null(r.Requirements);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void TryParse_NoCommas_ErrorBecauseNoValidRows()
    {
        var r = TeamcraftLink.TryParse(Prefix + Enc(B64("hello world")));
        Assert.Null(r.Requirements);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void TryParse_InvalidUtf8_DoesNotThrow_AndIsRejectedEvenWithValidRows()
    {
        // A valid first row followed by invalid UTF-8: a lenient decoder would keep row 1.
        var bytes = Encoding.ASCII.GetBytes("5,null,2;").Concat(new byte[] { 0xFF, 0xFE, 0x2C, 0xC3, 0x28 }).ToArray();
        var r = TeamcraftLink.TryParse(Prefix + Enc(Convert.ToBase64String(bytes)));
        Assert.Null(r.Requirements);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void TryParse_AllRowsBad_IsError()
    {
        var r = TeamcraftLink.TryParse(Prefix + Enc(B64("abc,null,1;0,null,1")));
        Assert.Null(r.Requirements);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void TryParse_Huge_DoesNotThrow()
    {
        var rows = string.Join(";", Enumerable.Range(1, 20000).Select(i => $"{i},null,1"));
        var r = TeamcraftLink.TryParse(Prefix + Enc(B64(rows)));
        Assert.Null(r.Error);
        Assert.Equal(20000, r.Requirements!.Count);
        var junk = TeamcraftLink.TryParse(new string('A', 200000) + "!");
        Assert.NotNull(junk.Error);
    }
}
