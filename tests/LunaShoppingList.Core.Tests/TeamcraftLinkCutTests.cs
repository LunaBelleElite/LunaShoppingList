namespace LunaShoppingList.Core.Tests;

/// <summary>A pasted Teamcraft link may be followed by other text; the data ends at whitespace, '&amp;', '?' or '#'.</summary>
public class TeamcraftLinkCutTests
{
    private static readonly string Link = TeamcraftLink.Build(new[] { new Requirement(5111, 3), new Requirement(7, 2) })!;
    private static readonly Requirement[] Expected = { new(5111, 3), new(7, 2) };

    [Theory]
    [InlineData("\nthanks!")]
    [InlineData(" text")]
    [InlineData("\t more")]
    [InlineData("&url=https://example.com/a")]
    [InlineData("?x=1")]
    [InlineData("#frag")]
    public void TextAfterTheLink_IsIgnored(string tail)
    {
        var r = TeamcraftLink.TryParse(Link + tail);
        Assert.Null(r.Error);
        Assert.Equal(Expected, r.Requirements!.ToArray());
    }

    [Fact]
    public void TextBeforeTheLink_StillWorks()
    {
        var r = TeamcraftLink.TryParse("look: " + Link + " ok");
        Assert.Null(r.Error);
        Assert.Equal(Expected, r.Requirements!.ToArray());
    }
}
