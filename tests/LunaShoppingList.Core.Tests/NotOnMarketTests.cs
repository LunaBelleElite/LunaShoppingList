using LunaShoppingList.Core;

namespace LunaShoppingList.Core.Tests;

/// <summary>
/// Items not sold on the market board (untradable, or no market search category) are never sent to Universalis and are
/// reported in <see cref="OfferBook.NotOnMarket"/> instead of looking like a failed lookup.
/// </summary>
public class NotOnMarketTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly WorldInfo A1 = new(1, "Alpha", "Aaa", "R1", "Aaa", true);

    private sealed class FakeWorlds : IWorldData
    {
        public WorldInfo? CurrentWorld => A1;
        public IReadOnlyList<WorldInfo> AllWorlds { get; } = [A1];
    }

    private sealed class FakeClient : IUniversalisClient
    {
        public List<uint[]> Calls { get; } = [];
        public int DcCalls { get; private set; }

        public Task<IReadOnlyList<UniversalisDataCenter>> GetDataCentersAsync(CancellationToken ct)
        {
            DcCalls++;
            return Task.FromResult<IReadOnlyList<UniversalisDataCenter>>([new("Uni1", "R1", [1])]);
        }

        // Every queried item gets one listing of 5 units at 100 gil on world 1.
        public Task<IReadOnlyList<ItemListings>> GetListingsAsync(IReadOnlyCollection<uint> itemIds, string scope, CancellationToken ct)
        {
            Calls.Add(itemIds.OrderBy(i => i).ToArray());
            return Task.FromResult<IReadOnlyList<ItemListings>>(itemIds
                .Select(i => new ItemListings(i, [new Listing(i, 1, "Alpha", 100, 5, false, T0)]))
                .ToList());
        }
    }

    // Item ids 900 and above are "not on the market board" in these tests.
    private static bool Marketable(uint id) => id < 900;

    private static ShoppingListService Svc(FakeClient c, Func<uint, bool>? isMarketable)
        => new(new FakeWorlds(), c, isMarketable);

    private static Task<OfferBook> Book(ShoppingListService s, Requirement[] r, ExclusionState? ex = null)
        => s.BuildOfferBookAsync(r, TravelScopeKind.CrossWorld, ex ?? new ExclusionState(), null, default);

    [Fact]
    public async Task NonMarketItems_AreNeverSentToUniversalis_MarketItemsAre()
    {
        var c = new FakeClient();
        await Book(Svc(c, Marketable), [new(1, 2), new(901, 3), new(2, 1), new(950, 1)]);
        Assert.Equal([new uint[] { 1, 2 }], c.Calls);
    }

    [Fact]
    public async Task NonMarketItems_GoToNotOnMarket_NotToItems()
    {
        var c = new FakeClient();
        var book = await Book(Svc(c, Marketable), [new(1, 2), new(901, 3), new(2, 1)]);
        Assert.Equal(new uint[] { 1, 2 }, book.Items.Select(i => i.ItemId).ToArray());
        Assert.Equal([new Requirement(901, 3)], book.NotOnMarket);
        Assert.Empty(book.Skipped);
    }

    [Fact]
    public async Task NotOnMarket_MergesDuplicates_InFirstAppearanceOrder()
    {
        var book = await Book(Svc(new FakeClient(), Marketable), [new(950, 1), new(1, 1), new(901, 2), new(950, 4)]);
        Assert.Equal([new Requirement(950, 5), new Requirement(901, 2)], book.NotOnMarket);
    }

    [Fact]
    public async Task NotOnMarket_IgnoresNonPositiveQuantities_LikeTheOfferBook()
    {
        var book = await Book(Svc(new FakeClient(), Marketable), [new(901, 0), new(902, -2), new(903, 3), new(903, -1), new(1, 1)]);
        Assert.Equal([new Requirement(903, 3)], book.NotOnMarket);
    }

    [Fact]
    public async Task NotOnMarket_QuantityIsClampedToIntMax()
    {
        var book = await Book(Svc(new FakeClient(), Marketable), [new(901, int.MaxValue), new(901, int.MaxValue)]);
        Assert.Equal([new Requirement(901, int.MaxValue)], book.NotOnMarket);
    }

    [Fact]
    public async Task NullMarketabilityCheck_KeepsTheOldBehaviour()
    {
        var c = new FakeClient();
        var book = await Book(Svc(c, null), [new(1, 1), new(901, 3)]);
        Assert.Equal([new uint[] { 1, 901 }], c.Calls);
        Assert.Equal(new uint[] { 1, 901 }, book.Items.Select(i => i.ItemId).ToArray());
        Assert.Empty(book.NotOnMarket);
    }

    [Fact]
    public async Task TwoArgumentConstructor_TreatsEveryItemAsMarketable()
    {
        var c = new FakeClient();
        var book = await new ShoppingListService(new FakeWorlds(), c)
            .BuildOfferBookAsync([new(901, 3)], TravelScopeKind.CrossWorld, new ExclusionState(), null, default);
        Assert.Equal([new uint[] { 901 }], c.Calls);
        Assert.Empty(book.NotOnMarket);
    }

    [Fact]
    public async Task AllItemsNotOnMarket_MakesNoUniversalisCallAtAll()
    {
        var c = new FakeClient();
        var book = await Book(Svc(c, Marketable), [new(901, 3), new(902, 1)]);
        Assert.Empty(c.Calls);
        Assert.Equal(0, c.DcCalls);
        Assert.Empty(book.Items);
        Assert.Empty(book.Skipped);
        Assert.Equal([new Requirement(901, 3), new Requirement(902, 1)], book.NotOnMarket);
    }

    [Fact]
    public async Task ExcludedNonMarketItem_StaysSkipped_AndIsNotSent()
    {
        var c = new FakeClient();
        var ex = new ExclusionState();
        ex.SetExcluded(901, true);
        ex.SetExcluded(2, true);
        var book = await Book(Svc(c, Marketable), [new(1, 1), new(2, 4), new(901, 3), new(902, 1)], ex);
        Assert.Equal([new uint[] { 1 }], c.Calls);
        Assert.Equal([new SkippedItem(2, 4), new SkippedItem(901, 3)], book.Skipped);
        Assert.Equal([new Requirement(902, 1)], book.NotOnMarket);
        Assert.Equal(new uint[] { 1 }, book.Items.Select(i => i.ItemId).ToArray());
    }

    [Fact]
    public async Task NonMarketItems_AreNotCountedAsUnmet_ButAMarketItemNobodySellsStillIs()
    {
        var c = new FakeClient();
        var svc = Svc(c, Marketable);
        // 1 needs 3 (5 for sale), 2 needs 9 (only 5 for sale: a genuine shortfall), 901 is not on the market board.
        var book = await Book(svc, [new(1, 3), new(2, 9), new(901, 50)]);
        var state = new AllocationState(book);
        state.SetQuantity(1, 1, 3);
        state.SetQuantity(2, 1, 5);
        var summary = state.Summarize();
        Assert.Equal(new uint[] { 2 }, summary.Unmet.Select(u => u.ItemId).ToArray());
        Assert.Equal(800, summary.GrandTotal);
        Assert.Equal(0, state.Unmet(901));
    }

    [Fact]
    public async Task BuildPlan_AlsoNeverSendsNonMarketItems()
    {
        var c = new FakeClient();
        await Svc(c, Marketable).BuildPlanAsync([new(1, 1), new(901, 3)], TravelScopeKind.CrossWorld, new ExclusionState(), null, default);
        Assert.Equal([new uint[] { 1 }], c.Calls);
    }

    [Fact]
    public void IsMarketable_UsesTheCheck_AndDefaultsToTrue()
    {
        Assert.True(Svc(new FakeClient(), Marketable).IsMarketable(1));
        Assert.False(Svc(new FakeClient(), Marketable).IsMarketable(901));
        Assert.True(Svc(new FakeClient(), null).IsMarketable(901));
    }

    [Fact]
    public void OfferBook_NotOnMarket_DefaultsToEmpty()
    {
        var book = new OfferBook(Array.Empty<ItemOffers>(), Array.Empty<SkippedItem>());
        Assert.NotNull(book.NotOnMarket);
        Assert.Empty(book.NotOnMarket);
    }

    [Fact]
    public void OfferBookBuilder_LeavesNotOnMarketEmpty()
    {
        var book = OfferBookBuilder.Build([new(901, 3)], [], new HashSet<uint> { 1 }, new ExclusionState());
        Assert.Empty(book.NotOnMarket);
        Assert.Equal(new uint[] { 901 }, book.Items.Select(i => i.ItemId).ToArray());
    }

    // ---- NotOnMarketNote.Text ----

    private static string N(uint id) => $"Item{id}";

    [Fact]
    public void Note_IsEmpty_WhenNothingIsOffMarket()
        => Assert.Equal(string.Empty, NotOnMarketNote.Text([], N));

    [Fact]
    public void Note_ListsNamesAndQuantities()
        => Assert.Equal("Not sold on the market board (not priced): Item901 x3, Item902 x1,200",
            NotOnMarketNote.Text([new(901, 3), new(902, 1200)], N));

    [Fact]
    public void Note_ShowsEightNames_ThenHowManyMore()
    {
        var reqs = Enumerable.Range(1, 10).Select(i => new Requirement((uint)i, 1)).ToList();
        Assert.Equal(
            "Not sold on the market board (not priced): Item1 x1, Item2 x1, Item3 x1, Item4 x1, Item5 x1, Item6 x1, Item7 x1, Item8 x1, +2 more",
            NotOnMarketNote.Text(reqs, N));
    }

    [Fact]
    public void Note_ExactlyEightNames_HasNoMoreSuffix()
    {
        var reqs = Enumerable.Range(1, 8).Select(i => new Requirement((uint)i, 2)).ToList();
        Assert.Equal(
            "Not sold on the market board (not priced): Item1 x2, Item2 x2, Item3 x2, Item4 x2, Item5 x2, Item6 x2, Item7 x2, Item8 x2",
            NotOnMarketNote.Text(reqs, N));
    }
}
