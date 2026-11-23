using LunaShoppingList.Core;
using Xunit;

namespace LunaShoppingList.Core.Tests;

public class ItemSourceOrderTests
{
    private static ItemSource Gil(long cost) => new(ItemSourceKind.GilVendor, "gil", 1, cost, "Npc", "Zone", 0, 0);
    private static ItemSource Cur(string currency, long cost) => new(ItemSourceKind.CurrencyVendor, currency, 100, cost, "Npc", "Zone", 0, 0);

    private static string[] Sorted(params (string Name, ItemSource[] Sources)[] rows)
        => TableSort.Order(rows, (a, b) => ItemSourceOrder.Compare(a.Sources, b.Sources), true).Select(r => r.Name).ToArray();

    [Fact]
    public void A_row_with_a_vendor_sorts_before_a_row_without()
    {
        Assert.True(ItemSourceOrder.Compare([Gil(5000)], []) < 0);
        Assert.True(ItemSourceOrder.Compare([], [Gil(5000)]) > 0);
    }

    [Fact]
    public void Two_rows_without_a_vendor_compare_equal()
        => Assert.Equal(0, ItemSourceOrder.Compare([], []));

    [Fact]
    public void Gil_vendors_come_before_currency_vendors_even_when_the_currency_cost_is_lower()
        => Assert.Equal(new[] { "gil", "cur" }, Sorted(("cur", [Cur("Gemstone", 1)]), ("gil", [Gil(99999)])));

    [Fact]
    public void Gil_vendors_order_by_cheapest_cost()
        => Assert.Equal(new[] { "cheap", "dear" }, Sorted(("dear", [Gil(900)]), ("cheap", [Gil(100)])));

    [Fact]
    public void Currency_vendors_order_by_currency_name_then_cost()
        => Assert.Equal(new[] { "a1", "a2", "b1" }, Sorted(("b1", [Cur("Beta", 1)]), ("a2", [Cur("alpha", 20)]), ("a1", [Cur("Alpha", 10)])));

    [Fact]
    public void A_row_is_judged_by_its_best_source_not_the_order_it_lists_them()
        => Assert.Equal(new[] { "x", "y" }, Sorted(("y", [Gil(500), Gil(400)]), ("x", [Gil(900), Gil(300)])));

    [Fact]
    public void Full_ordering_vendor_rows_first_then_none_and_ties_keep_original_order()
        => Assert.Equal(new[] { "g1", "g2", "c", "n1", "n2" },
            Sorted(("n1", []), ("c", [Cur("Gem", 5)]), ("g2", [Gil(200)]), ("n2", []), ("g1", [Gil(100)])));
}
