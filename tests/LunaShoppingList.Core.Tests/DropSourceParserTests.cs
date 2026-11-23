namespace LunaShoppingList.Core.Tests;

public class DropSourceParserTests
{
    [Fact]
    public void Golden_ReadsItemIdsWithMonsters()
    {
        var ids = DropSourceParser.Parse("{\"4\":[115],\"44072\":[13101],\"7\":[1,2,3]}");
        Assert.Equal(new uint[] { 4, 7, 44072 }, ids.OrderBy(i => i).ToArray());
    }

    [Fact]
    public void EmptyObject_IsEmptySet()
    {
        Assert.Empty(DropSourceParser.Parse("{}"));
    }

    [Fact]
    public void EmptyArray_IsSkipped()
    {
        var ids = DropSourceParser.Parse("{\"4\":[],\"5\":[1]}");
        Assert.Equal(new uint[] { 5 }, ids.ToArray());
    }

    [Fact]
    public void NonArrayValues_AreSkipped()
    {
        var ids = DropSourceParser.Parse("{\"1\":5,\"2\":\"x\",\"3\":{\"a\":[1]},\"4\":null,\"5\":true,\"6\":[9]}");
        Assert.Equal(new uint[] { 6 }, ids.ToArray());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData(" 5")]
    [InlineData("5.0")]
    [InlineData("4294967296")]
    public void NonNumericOrOutOfRangeKeys_AreSkipped(string key)
    {
        var ids = DropSourceParser.Parse("{\"" + key + "\":[1],\"9\":[1]}");
        Assert.Equal(new uint[] { 9 }, ids.ToArray());
    }

    [Fact]
    public void LargestUintKey_IsKept()
    {
        Assert.Equal(new uint[] { uint.MaxValue }, DropSourceParser.Parse("{\"4294967295\":[1]}").ToArray());
    }

    [Fact]
    public void UnknownNestedContent_IsTolerated()
    {
        var ids = DropSourceParser.Parse("{\"3\":[{\"x\":1},[2]],\"note\":[1]}");
        Assert.Equal(new uint[] { 3 }, ids.ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"4\":[115]")]
    [InlineData("{\"4\":[115],}")]
    [InlineData("[1,2]")]
    [InlineData("[{\"4\":[1]}]")]
    [InlineData("null")]
    [InlineData("5")]
    [InlineData("\"4\"")]
    public void InvalidJsonOrNonObject_ThrowsFormatException(string json)
    {
        Assert.Throws<FormatException>(() => DropSourceParser.Parse(json));
    }

    [Fact]
    public void NullText_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => DropSourceParser.Parse(null!));
    }
}
