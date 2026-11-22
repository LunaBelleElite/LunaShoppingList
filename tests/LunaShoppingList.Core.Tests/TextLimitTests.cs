namespace LunaShoppingList.Core.Tests;

public class TextLimitTests
{
    [Fact]
    public void Bytes_ShortText_IsUnchanged()
    {
        Assert.Equal("abc", TextLimit.CutToBytes("abc", 3, out var cut));
        Assert.False(cut);
    }

    [Fact]
    public void Bytes_Ascii_CutsAtLimit()
    {
        Assert.Equal("ab", TextLimit.CutToBytes("abcd", 2, out var cut));
        Assert.True(cut);
    }

    [Fact]
    public void Bytes_MultiByteCharacter_IsNotSplit()
    {
        // each char is 3 bytes: 7 bytes allows only two
        Assert.Equal("あい", TextLimit.CutToBytes("あいう", 7, out var cut));
        Assert.True(cut);
    }

    [Fact]
    public void Bytes_SurrogatePair_IsNotSplit()
    {
        var pair = "\U0001F600"; // 4 bytes, 2 chars
        Assert.Equal("a", TextLimit.CutToBytes("a" + pair, 4, out var cut));
        Assert.True(cut);
        Assert.Equal("a" + pair, TextLimit.CutToBytes("a" + pair, 5, out cut));
        Assert.False(cut);
    }

    [Fact]
    public void Bytes_ZeroAndNull()
    {
        Assert.Equal("", TextLimit.CutToBytes("abc", 0, out var cut));
        Assert.True(cut);
        Assert.Equal("", TextLimit.CutToBytes(null!, 5, out cut));
        Assert.False(cut);
    }

    [Fact]
    public void Chars_CutsAndKeepsPairsWhole()
    {
        Assert.Equal("abc", TextLimit.CutToChars("abcdef", 3));
        Assert.Equal("ab", TextLimit.CutToChars("ab", 3));
        Assert.Equal("a", TextLimit.CutToChars("a\U0001F600", 2));
        Assert.Equal("a\U0001F600", TextLimit.CutToChars("a\U0001F600b", 3));
    }
}
