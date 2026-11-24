using LunaShoppingList.Core;

namespace LunaShoppingList.Core.Tests;

public class ListingFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    private static Listing L(uint item, int price, TimeSpan age, uint world = 1, int qty = 1)
        => new(item, world, "W" + world, price, qty, false, Now - age);

    private static OfferBook Build(Listing[] listings, ListingFilter? filter, Requirement[]? req = null, ExclusionState? ex = null)
        => OfferBookBuilder.Build(req ?? [new(1, 1)], [new ItemListings(1, listings)], new HashSet<uint> { 1, 2 }, ex ?? new ExclusionState(), filter);

    private static int[] Prices(OfferBook b) => b.Items.Single().Worlds.SelectMany(w => w.Steps).Select(s => s.UnitPrice).OrderBy(p => p).ToArray();

    [Fact]
    public void NewerListingIsKept_OlderIsDropped()
    {
        var book = Build([L(1, 10, TimeSpan.FromHours(23)), L(1, 20, TimeSpan.FromHours(25))], new ListingFilter(Day, Now));
        Assert.Equal([10], Prices(book));
    }

    [Fact]
    public void ListingExactlyMaxAgeOld_IsKept_InclusiveBoundary()
    {
        var book = Build([L(1, 10, Day)], new ListingFilter(Day, Now));
        Assert.Equal([10], Prices(book));
        Assert.Equal(0, book.Items.Single().DroppedListings);
    }

    [Fact]
    public void ListingOneTickOlderThanMaxAge_IsDropped()
    {
        var book = Build([L(1, 10, Day + TimeSpan.FromTicks(1))], new ListingFilter(Day, Now));
        Assert.Empty(book.Items.Single().Worlds);
        Assert.Equal(1, book.Items.Single().DroppedListings);
    }

    [Fact]
    public void UnknownReviewTime_IsKept_ForEpochAndDefault()
    {
        var epoch = new Listing(1, 1, "W1", 10, 1, false, DateTimeOffset.UnixEpoch);
        var dflt = new Listing(1, 1, "W1", 20, 1, false, default);
        var book = Build([epoch, dflt], new ListingFilter(TimeSpan.FromMinutes(1), Now));
        Assert.Equal([10, 20], Prices(book));
        Assert.Equal(0, book.Items.Single().DroppedListings);
    }

    [Fact]
    public void NullFilter_AndNullMaxAge_BehaveLikeNoFilter()
    {
        Listing[] ls = [L(1, 10, TimeSpan.FromDays(400)), L(1, 20, TimeSpan.FromHours(1))];
        var none = Build(ls, null);
        var nullAge = Build(ls, new ListingFilter(null, Now));
        Assert.Equal([10, 20], Prices(none));
        Assert.Equal([10, 20], Prices(nullAge));
        Assert.Equal(0, nullAge.Items.Single().DroppedListings);
        Assert.Equal(none.Items.Single().Worlds.SelectMany(w => w.Steps), nullAge.Items.Single().Worlds.SelectMany(w => w.Steps));
    }

    [Fact]
    public void DroppedListings_CountsEachDroppedListing_AcrossWorlds()
    {
        var book = Build(
            [L(1, 10, TimeSpan.FromHours(1)), L(1, 11, TimeSpan.FromDays(3), world: 1), L(1, 12, TimeSpan.FromDays(5), world: 2), L(1, 13, TimeSpan.FromDays(9), world: 2)],
            new ListingFilter(Day, Now));
        Assert.Equal(3, book.Items.Single().DroppedListings);
        Assert.Equal([10], Prices(book));
    }

    [Fact]
    public void AllDropped_ItemStillAppears_WithNoWorldsAndTheCount()
    {
        var book = Build([L(1, 10, TimeSpan.FromDays(2)), L(1, 11, TimeSpan.FromDays(3), world: 2)], new ListingFilter(Day, Now), [new(1, 4)]);
        var item = Assert.Single(book.Items);
        Assert.Equal(1u, item.ItemId);
        Assert.Equal(4, item.Needed);
        Assert.Empty(item.Worlds);
        Assert.Equal(2, item.DroppedListings);
    }

    [Fact]
    public void ListingsAlreadyUnusable_AreNotCountedAsDropped()
    {
        // price 0, quantity 0 and a world outside the scope were never usable, so age did not hide them.
        var old = TimeSpan.FromDays(5);
        var book = Build([L(1, 0, old), L(1, 10, old, qty: 0), L(1, 10, old, world: 9), L(1, 10, old)], new ListingFilter(Day, Now));
        Assert.Equal(1, book.Items.Single().DroppedListings);
    }

    [Fact]
    public void ExcludedItem_IsSkipped_AndOtherItemsKeepTheirOwnCounts()
    {
        var ex = new ExclusionState();
        ex.ExcludeAll([1u], true);
        var listings = new[]
        {
            new ItemListings(1, [L(1, 10, TimeSpan.FromDays(3))]),
            new ItemListings(2, [new Listing(2, 1, "W1", 5, 1, false, Now - TimeSpan.FromDays(3)), new Listing(2, 1, "W1", 6, 1, false, Now)]),
        };
        var book = OfferBookBuilder.Build([new(1, 1), new(2, 1)], listings, new HashSet<uint> { 1 }, ex, new ListingFilter(Day, Now));
        Assert.Equal(1u, book.Skipped.Single().ItemId);
        var two = book.Items.Single();
        Assert.Equal(2u, two.ItemId);
        Assert.Equal(1, two.DroppedListings);
    }

    [Fact]
    public void DroppedListings_DefaultsToZero()
    {
        Assert.Equal(0, new ItemOffers(1, 1, []).DroppedListings);
    }
}
