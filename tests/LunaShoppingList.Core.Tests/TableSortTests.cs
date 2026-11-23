using LunaShoppingList.Core;
using Xunit;

namespace LunaShoppingList.Core.Tests;

public class TableSortTests
{
    private static readonly (string Name, int Qty)[] Rows = [("b", 2), ("a", 2), ("c", 1), ("d", 3)];

    [Fact]
    public void Ascending_orders_by_the_comparison()
        => Assert.Equal(new[] { "c", "b", "a", "d" }, TableSort.Order(Rows, (x, y) => x.Qty.CompareTo(y.Qty), true).Select(r => r.Name));

    [Fact]
    public void Descending_reverses_the_comparison_but_ties_keep_their_original_order()
        => Assert.Equal(new[] { "d", "b", "a", "c" }, TableSort.Order(Rows, (x, y) => x.Qty.CompareTo(y.Qty), false).Select(r => r.Name));

    [Fact]
    public void No_comparison_keeps_the_original_order_in_a_new_list()
    {
        var result = TableSort.Order(Rows, null, false);
        Assert.Equal(new[] { "b", "a", "c", "d" }, result.Select(r => r.Name));
        Assert.NotSame(Rows, result);
    }

    [Fact]
    public void Input_is_not_modified()
    {
        var copy = Rows.ToArray();
        TableSort.Order(Rows, (x, y) => TableSort.CompareText(x.Name, y.Name), true);
        Assert.Equal(copy, Rows);
    }

    [Fact]
    public void Empty_and_single_row_lists_work()
    {
        Assert.Empty(TableSort.Order(Array.Empty<int>(), (a, b) => a.CompareTo(b), true));
        Assert.Equal(new[] { 5 }, TableSort.Order(new[] { 5 }, (a, b) => a.CompareTo(b), false));
    }

    [Fact]
    public void Descending_is_right_for_a_comparison_that_returns_int_MinValue()
        => Assert.Equal(new[] { 3, 2, 1 }, TableSort.Order(new[] { 1, 2, 3 }, (a, b) => a < b ? int.MinValue : a > b ? int.MaxValue : 0, false));

    [Fact]
    public void Null_rows_throws()
        => Assert.Throws<ArgumentNullException>(() => TableSort.Order<int>(null!, (a, b) => a.CompareTo(b), true));

    [Theory]
    [InlineData("apple", "Banana", true)]
    [InlineData("Apple", "apple", false)]
    [InlineData(null, "a", true)]
    public void CompareText_ignores_case_and_treats_null_as_empty(string? a, string? b, bool lessThanOrDifferent)
    {
        var c = TableSort.CompareText(a, b);
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            Assert.Equal(0, c);
        else
            Assert.True(lessThanOrDifferent ? c < 0 : c != 0);
    }
}
