namespace LunaShoppingList.Core.Tests;

public class TeamcraftSafeLinkTests
{
    [Theory]
    [InlineData("https://ffxivteamcraft.com/import/MjA1NDUsbnVsbCwz")]
    [InlineData("http://example.com/x")]
    [InlineData("HTTPS://EXAMPLE.COM/")]
    public void IsSafeWebLink_HttpAndHttps_True(string url) => Assert.True(TeamcraftLink.IsSafeWebLink(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ffxivteamcraft.com/import/abc")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("C:\\Windows\\System32\\calc.exe")]
    [InlineData("ftp://example.com/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("steam://run/1")]
    [InlineData("cmd.exe")]
    public void IsSafeWebLink_AnythingElse_False(string? url) => Assert.False(TeamcraftLink.IsSafeWebLink(url));

    [Fact]
    public void IsSafeWebLink_AcceptsEveryLinkBuildProduces()
    {
        var link = TeamcraftLink.Build(new[] { new Requirement(20545, 3), new Requirement(17962, 1) });
        Assert.True(TeamcraftLink.IsSafeWebLink(link));
    }
}
