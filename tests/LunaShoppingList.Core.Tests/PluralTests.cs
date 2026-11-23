namespace LunaShoppingList.Core.Tests;

public class PluralTests
{
    [Theory]
    [InlineData(1, "1 world")]
    [InlineData(0, "0 worlds")]
    [InlineData(2, "2 worlds")]
    [InlineData(1200, "1,200 worlds")]
    public void Of_UsesSingularOnlyForExactlyOne(long count, string expected)
        => Assert.Equal(expected, Plural.Of(count, "world"));

    [Fact]
    public void Of_UsesAGivenPluralForm()
    {
        Assert.Equal("1 copy", Plural.Of(1, "copy", "copies"));
        Assert.Equal("3 copies", Plural.Of(3, "copy", "copies"));
    }
}
