using LunaShoppingList.Core;

namespace LunaShoppingList.Core.Tests;

public class ShoppingListServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    // Region "R1" has DCs Aaa (worlds 1,2) and Bbb (world 3); region "R2" has DC Ccc (world 4).
    private static readonly WorldInfo A1 = new(1, "Alpha", "Aaa", "R1", "Aaa", true);
    private static readonly WorldInfo A2 = new(2, "Beta", "Aaa", "R1", "Aaa", true);
    private static readonly WorldInfo B1 = new(3, "Gamma", "Bbb", "R1", "Bbb", true);
    private static readonly WorldInfo C1 = new(4, "Delta", "Ccc", "R2", "Ccc", true);
    // A world the game lists but Universalis does not (a test world).
    private static readonly WorldInfo Cloud = new(5, "Zeta", "Cloud DC", "R1", "Cloud DC", true);
    private static readonly WorldInfo[] All = [A1, A2, B1, C1, Cloud];

    // Universalis's own grouping differs from the game's: Uni2 spans worlds from two game data centers.
    // Listed out of name order on purpose; world 99 is unknown to the game.
    private static readonly UniversalisDataCenter[] DefaultDcs =
    [
        new("Uni2", "R1", [3, 2]),
        new("Uni3", "R2", [4, 99]),
        new("Uni1", "R1", [1]),
    ];

    private sealed class FakeWorlds(WorldInfo? current) : IWorldData
    {
        public WorldInfo? CurrentWorld { get; } = current;
        public IReadOnlyList<WorldInfo> AllWorlds { get; } = All;
    }

    private sealed class FakeClient : IUniversalisClient
    {
        public List<(string Scope, uint[] Ids)> Calls { get; } = [];
        public Func<string, IReadOnlyCollection<uint>, IReadOnlyList<ItemListings>> Respond { get; set; }
            = (_, ids) => ids.Select(i => new ItemListings(i, [])).ToList();
        public Exception? Throw { get; set; }
        public IReadOnlyList<UniversalisDataCenter> DataCenters { get; set; } = DefaultDcs;
        public Exception? DcThrow { get; set; }
        public int DcCalls { get; private set; }

        public Task<IReadOnlyList<UniversalisDataCenter>> GetDataCentersAsync(CancellationToken ct)
        {
            DcCalls++;
            ct.ThrowIfCancellationRequested();
            if (DcThrow is not null) throw DcThrow;
            return Task.FromResult(DataCenters);
        }

        public Task<IReadOnlyList<ItemListings>> GetListingsAsync(IReadOnlyCollection<uint> itemIds, string scope, CancellationToken ct)
        {
            Calls.Add((scope, itemIds.OrderBy(i => i).ToArray()));
            if (Throw is not null) throw Throw;
            return Task.FromResult(Respond(scope, itemIds));
        }
    }

    private sealed class ListProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];
        public void Report(string value) => Messages.Add(value);
    }

    private static Listing L(uint item, WorldInfo w, int price, int qty = 100)
        => new(item, w.Id, w.Name, price, qty, false, T0);

    private static ShoppingListService Svc(FakeClient c, WorldInfo? cur = null)
        => new(new FakeWorlds(cur ?? A1), c);

    private static Task<ShoppingPlan> Build(ShoppingListService s, Requirement[] r, TravelScopeKind k = TravelScopeKind.CrossWorld,
        ExclusionState? ex = null, IProgress<string>? p = null, CancellationToken ct = default)
        => s.BuildPlanAsync(r, k, ex ?? new ExclusionState(), p, ct);

    [Fact]
    public async Task CrossWorld_CallsOncePerDataCenterWithNonExcludedIdsInOrder()
    {
        var c = new FakeClient();
        var ex = new ExclusionState();
        ex.SetExcluded(2, true);
        await Build(Svc(c), [new(1, 1), new(2, 1), new(3, 1)], ex: ex);
        Assert.Equal(2, c.Calls.Count);
        Assert.Equal("Uni1", c.Calls[0].Scope);
        Assert.Equal("Uni2", c.Calls[1].Scope);
        Assert.Equal(new uint[] { 1, 3 }, c.Calls[0].Ids);
        Assert.Equal(new uint[] { 1, 3 }, c.Calls[1].Ids);
    }

    [Fact]
    public async Task CrossDataCenter_CallsOncePerDistinctDataCenter()
    {
        var c = new FakeClient();
        await Build(Svc(c), [new(1, 1)], TravelScopeKind.CrossDataCenter);
        Assert.Equal(new[] { "Uni1", "Uni2", "Uni3" }, c.Calls.Select(x => x.Scope).ToArray());
    }

    [Fact]
    public async Task ExcludedItemsNeverReachClientAndAreSkipped()
    {
        var c = new FakeClient();
        var ex = new ExclusionState();
        ex.SetExcluded(2, true);
        var plan = await Build(Svc(c), [new(1, 1), new(2, 7)], ex: ex);
        Assert.NotEmpty(c.Calls);
        Assert.All(c.Calls, x => Assert.DoesNotContain(2u, x.Ids));
        Assert.Equal([new SkippedItem(2, 7)], plan.Skipped);
    }

    [Fact]
    public async Task AllExcluded_MakesZeroClientCalls_AndStillSkips()
    {
        var c = new FakeClient();
        var ex = new ExclusionState();
        ex.ExcludeAll([1u, 2u], true);
        var plan = await Build(Svc(c), [new(1, 3), new(2, 4)], ex: ex);
        Assert.Empty(c.Calls);
        Assert.Equal(0, c.DcCalls);
        Assert.Equal([new SkippedItem(1, 3), new SkippedItem(2, 4)], plan.Skipped);
        Assert.Empty(plan.Worlds);
    }

    [Fact]
    public async Task OnlyNonPositiveQuantities_MakesZeroClientCalls()
    {
        var c = new FakeClient();
        await Build(Svc(c), [new(1, 0), new(2, -3)]);
        Assert.Empty(c.Calls);
    }

    [Fact]
    public async Task ListingsMergedAcrossScopes_AndBoughtCheapestAcross()
    {
        var c = new FakeClient
        {
            Respond = (scope, ids) => scope == "Uni1"
                ? [new ItemListings(1, [L(1, A1, 100, 2)])]
                : [new ItemListings(1, [L(1, B1, 50, 2)])],
        };
        var plan = await Build(Svc(c), [new(1, 3)]);
        Assert.Equal(2, plan.Worlds.Count);
        var b = plan.Worlds.Single(w => w.WorldId == 3);
        var a = plan.Worlds.Single(w => w.WorldId == 1);
        Assert.Equal((2, 50), (b.Purchases[0].Quantity, b.Purchases[0].PricePerUnit));
        Assert.Equal((1, 100), (a.Purchases[0].Quantity, a.Purchases[0].PricePerUnit));
        Assert.Empty(plan.Shortfalls);
    }

    [Fact]
    public async Task ItemOnlyInLaterScope_IsStillMerged()
    {
        var c = new FakeClient
        {
            Respond = (scope, ids) => scope == "Uni1"
                ? [new ItemListings(1, [L(1, A1, 10)])]
                : [new ItemListings(2, [L(2, B1, 20)])],
        };
        var plan = await Build(Svc(c), [new(1, 1), new(2, 1)]);
        Assert.Equal(2, plan.Worlds.Count);
        Assert.Empty(plan.Shortfalls);
    }

    [Fact]
    public async Task ListingsOutsideAllowedWorlds_AreIgnored()
    {
        var c = new FakeClient
        {
            Respond = (_, _) => [new ItemListings(1, [L(1, C1, 1), L(1, A2, 500)])],
        };
        var plan = await Build(Svc(c), [new(1, 1)]);
        var g = Assert.Single(plan.Worlds);
        Assert.Equal(2u, g.WorldId);
        Assert.Equal(500, g.Purchases[0].PricePerUnit);
    }

    [Fact]
    public async Task CurrentWorldUnknown_Throws_WithoutCallingClient()
    {
        var c = new FakeClient();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ShoppingListService(new FakeWorlds(null), c).BuildPlanAsync([new(1, 1)], TravelScopeKind.CrossWorld, new ExclusionState(), null, default));
        Assert.Contains("current world unknown", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(c.Calls);
    }

    [Fact]
    public async Task Cancellation_Propagates_AndStopsBetweenScopes()
    {
        var c = new FakeClient();
        using var cts = new CancellationTokenSource();
        c.Respond = (_, ids) => { cts.Cancel(); return ids.Select(i => new ItemListings(i, [])).ToList(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Build(Svc(c), [new(1, 1)], ct: cts.Token));
        Assert.Single(c.Calls);
    }

    [Fact]
    public async Task ClientException_PropagatesUnchanged()
    {
        var boom = new HttpRequestException("boom");
        var c = new FakeClient { Throw = boom };
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Build(Svc(c), [new(1, 1)]));
        Assert.Same(boom, ex);
    }

    [Fact]
    public async Task ClientOperationCanceled_PropagatesUnchanged()
    {
        var oce = new OperationCanceledException("x");
        var c = new FakeClient { Throw = oce };
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Build(Svc(c), [new(1, 1)]));
        Assert.Same(oce, ex);
    }

    [Fact]
    public async Task Progress_ReportsOneMessagePerScope()
    {
        var c = new FakeClient();
        var p = new ListProgress();
        await Build(Svc(c), [new(1, 1)], p: p);
        Assert.Equal(["Fetching prices from Uni1 (1/2)", "Fetching prices from Uni2 (2/2)"], p.Messages);
    }

    [Fact]
    public async Task DuplicateRequirements_AreMerged()
    {
        var c = new FakeClient
        {
            Respond = (_, _) => [new ItemListings(1, [L(1, A1, 10, 100)])],
        };
        var plan = await Build(Svc(c), [new(1, 2), new(1, 3)]);
        Assert.Single(c.Calls[0].Ids);
        Assert.Equal(5, plan.Worlds.Single().Purchases.Sum(p => p.Quantity));
    }

    [Fact]
    public async Task Scopes_AreUniversalisDataCentersContainingAllowedWorlds_SortedByName()
    {
        var c = new FakeClient();
        await Build(Svc(c), [new(1, 1)]);
        Assert.Equal(["Uni1", "Uni2"], c.Calls.Select(x => x.Scope).ToArray());
    }

    [Fact]
    public async Task WorldUnknownToUniversalis_IsNeverQueriedAndItsListingsAreIgnored()
    {
        var c = new FakeClient
        {
            DataCenters = [new("Uni1", "R1", [1, 2])],
            Respond = (_, _) => [new ItemListings(1, [L(1, Cloud, 1), L(1, A1, 70)])],
        };
        var plan = await Build(Svc(c), [new(1, 1)]);
        Assert.Equal(["Uni1"], c.Calls.Select(x => x.Scope).ToArray());
        var g = Assert.Single(plan.Worlds);
        Assert.Equal(1u, g.WorldId);
    }

    [Fact]
    public async Task DataCenterLargerThanReachableSet_IsStillQueriedButOnlyAllowedWorldsAreBoughtFrom()
    {
        // "Big" also holds world 4 (game data center Ccc, region R2), which CrossWorld from world 1 cannot reach.
        var c = new FakeClient
        {
            DataCenters = [new("Big", "R1", [3, 4, 5, 99])],
            Respond = (_, _) => [new ItemListings(1, [L(1, C1, 1), L(1, B1, 90)])],
        };
        var plan = await Build(Svc(c), [new(1, 1)]);
        Assert.Equal(["Big"], c.Calls.Select(x => x.Scope).ToArray());
        var g = Assert.Single(plan.Worlds);
        Assert.Equal(3u, g.WorldId);
    }

    [Fact]
    public async Task DataCentersAreFetchedOncePerBuild()
    {
        var c = new FakeClient();
        await Build(Svc(c), [new(1, 1)]);
        Assert.Equal(1, c.DcCalls);
        Assert.Equal(2, c.Calls.Count);
    }

    [Fact]
    public async Task NothingToLookUp_DoesNotFetchDataCenters()
    {
        var c = new FakeClient();
        await Build(Svc(c), [new(1, 0)]);
        Assert.Equal(0, c.DcCalls);
        Assert.Empty(c.Calls);
    }

    [Fact]
    public async Task EmptyIntersection_Throws_AndMakesNoListingsRequest()
    {
        var c = new FakeClient { DataCenters = [new("Other", "R9", [200, 201])] };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Build(Svc(c), [new(1, 1)]));
        Assert.Equal("Universalis does not list any world in the selected scope.", ex.Message);
        Assert.Empty(c.Calls);
    }

    [Fact]
    public async Task NoDataCentersAtAll_Throws()
    {
        var c = new FakeClient { DataCenters = [] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Build(Svc(c), [new(1, 1)]));
        Assert.Empty(c.Calls);
    }

    [Fact]
    public async Task DataCenterFetchFailure_PropagatesUnchanged_AndNoListingsRequestIsMade()
    {
        var boom = new HttpRequestException("dc boom");
        var c = new FakeClient { DcThrow = boom };
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Build(Svc(c), [new(1, 1)]));
        Assert.Same(boom, ex);
        Assert.Empty(c.Calls);
    }

    [Fact]
    public async Task PreCancelledToken_ThrowsAndMakesNoListingsRequest()
    {
        var c = new FakeClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Build(Svc(c), [new(1, 1)], ct: cts.Token));
        Assert.Empty(c.Calls);
    }

    [Fact]
    public async Task NullArguments_Throw()
    {
        var s = Svc(new FakeClient());
        await Assert.ThrowsAsync<ArgumentNullException>(() => s.BuildPlanAsync(null!, TravelScopeKind.CrossWorld, new ExclusionState(), null, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => s.BuildPlanAsync([new(1, 1)], TravelScopeKind.CrossWorld, null!, null, default));
    }

    // ---- BuildOfferBookAsync ----

    private static Task<OfferBook> Book(ShoppingListService s, Requirement[] r, TravelScopeKind k = TravelScopeKind.CrossWorld,
        ExclusionState? ex = null, IProgress<string>? p = null, CancellationToken ct = default)
        => s.BuildOfferBookAsync(r, k, ex ?? new ExclusionState(), p, ct);

    [Theory]
    [InlineData(TravelScopeKind.CrossWorld)]
    [InlineData(TravelScopeKind.CrossDataCenter)]
    public async Task OfferBook_MakesTheSameCallsAsBuildPlan(TravelScopeKind kind)
    {
        var ex = new ExclusionState();
        ex.SetExcluded(2, true);
        Requirement[] reqs = [new(3, 1), new(1, 1), new(2, 1)];
        var planClient = new FakeClient();
        var bookClient = new FakeClient();
        await Build(Svc(planClient), reqs, kind, ex);
        await Book(Svc(bookClient), reqs, kind, ex);
        Assert.Equal(planClient.Calls.Count, bookClient.Calls.Count);
        for (var i = 0; i < planClient.Calls.Count; i++)
        {
            Assert.Equal(planClient.Calls[i].Scope, bookClient.Calls[i].Scope);
            Assert.Equal(planClient.Calls[i].Ids, bookClient.Calls[i].Ids);
        }
        Assert.Equal(planClient.DcCalls, bookClient.DcCalls);
        Assert.Equal(new uint[] { 1, 3 }, bookClient.Calls[0].Ids);
    }

    [Fact]
    public async Task OfferBook_ReturnsOffersPerWorldMergedAcrossScopes()
    {
        var c = new FakeClient
        {
            Respond = (scope, _) => scope == "Uni1"
                ? [new ItemListings(1, [L(1, A1, 100, 2)])]
                : [new ItemListings(1, [L(1, B1, 50, 2), L(1, A2, 70, 1)])],
        };
        var book = await Book(Svc(c), [new(1, 3), new(1, 1)]);
        var item = Assert.Single(book.Items);
        Assert.Equal(1u, item.ItemId);
        Assert.Equal(4, item.Needed);
        // Need 4 and no world can supply all of it: more units for sale first, then lower average price.
        // World 3 (2 units, 50 each), world 1 (2 units, 100 each), world 2 (1 unit, 70).
        // 2026-11-21: this used to expect [2, 3, 1] (ranked by the cost of the clamped quantity); superseded
        // because that ranking put a world selling fewer units ahead of one that could supply more of the need.
        Assert.Equal(new uint[] { 3, 1, 2 }, item.Worlds.Select(w => w.WorldId).ToArray());
        Assert.Empty(book.Skipped);
    }

    [Fact]
    public async Task OfferBook_ExcludedItemsNeverReachClientAndAreSkipped()
    {
        var c = new FakeClient();
        var ex = new ExclusionState();
        ex.SetExcluded(2, true);
        var book = await Book(Svc(c), [new(1, 1), new(2, 7)], ex: ex);
        Assert.NotEmpty(c.Calls);
        Assert.All(c.Calls, x => Assert.DoesNotContain(2u, x.Ids));
        Assert.Equal([new SkippedItem(2, 7)], book.Skipped);
        Assert.Equal(new uint[] { 1 }, book.Items.Select(i => i.ItemId).ToArray());
    }

    [Fact]
    public async Task OfferBook_NothingToBuy_MakesZeroClientCalls_AndFillsSkipped()
    {
        var c = new FakeClient();
        var ex = new ExclusionState();
        ex.ExcludeAll([1u, 2u], true);
        var book = await Book(Svc(c), [new(1, 3), new(2, 4), new(5, 0)], ex: ex);
        Assert.Empty(c.Calls);
        Assert.Equal(0, c.DcCalls);
        Assert.Empty(book.Items);
        Assert.Equal([new SkippedItem(1, 3), new SkippedItem(2, 4)], book.Skipped);
    }

    [Fact]
    public async Task OfferBook_OnlyNonPositiveQuantities_MakesZeroClientCalls()
    {
        var c = new FakeClient();
        var book = await Book(Svc(c), [new(1, 0), new(2, -3)]);
        Assert.Empty(c.Calls);
        Assert.Equal(0, c.DcCalls);
        Assert.Empty(book.Items);
        Assert.Empty(book.Skipped);
    }

    [Fact]
    public async Task OfferBook_EmptyIntersection_Throws_AndMakesNoListingsRequest()
    {
        var c = new FakeClient { DataCenters = [new("Other", "R9", [200, 201])] };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Book(Svc(c), [new(1, 1)]));
        Assert.Equal("Universalis does not list any world in the selected scope.", ex.Message);
        Assert.Empty(c.Calls);
    }

    [Fact]
    public async Task OfferBook_CurrentWorldUnknown_Throws_WithoutCallingClient()
    {
        var c = new FakeClient();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ShoppingListService(new FakeWorlds(null), c).BuildOfferBookAsync([new(1, 1)], TravelScopeKind.CrossWorld, new ExclusionState(), null, default));
        Assert.Contains("current world unknown", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, c.DcCalls);
        Assert.Empty(c.Calls);
    }

    [Fact]
    public async Task OfferBook_WorldsUnknownToUniversalisAreDropped()
    {
        var c = new FakeClient
        {
            DataCenters = [new("Uni1", "R1", [1, 2])],
            Respond = (_, _) => [new ItemListings(1, [L(1, Cloud, 1), L(1, C1, 2), L(1, A1, 70)])],
        };
        var book = await Book(Svc(c), [new(1, 1)]);
        Assert.Equal(["Uni1"], c.Calls.Select(x => x.Scope).ToArray());
        var w = Assert.Single(Assert.Single(book.Items).Worlds);
        Assert.Equal(1u, w.WorldId);
    }

    [Fact]
    public async Task OfferBook_PreCancelledToken_ThrowsAndMakesNoListingsRequest()
    {
        var c = new FakeClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Book(Svc(c), [new(1, 1)], ct: cts.Token));
        Assert.Empty(c.Calls);
    }

    [Fact]
    public async Task OfferBook_Cancellation_StopsBetweenScopes()
    {
        var c = new FakeClient();
        using var cts = new CancellationTokenSource();
        c.Respond = (_, ids) => { cts.Cancel(); return ids.Select(i => new ItemListings(i, [])).ToList(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Book(Svc(c), [new(1, 1)], ct: cts.Token));
        Assert.Single(c.Calls);
    }

    [Fact]
    public async Task OfferBook_ClientAndDataCenterFailures_PropagateUnchanged()
    {
        var boom = new HttpRequestException("boom");
        var ex1 = await Assert.ThrowsAsync<HttpRequestException>(() => Book(Svc(new FakeClient { Throw = boom }), [new(1, 1)]));
        Assert.Same(boom, ex1);

        var dcBoom = new HttpRequestException("dc boom");
        var c = new FakeClient { DcThrow = dcBoom };
        var ex2 = await Assert.ThrowsAsync<HttpRequestException>(() => Book(Svc(c), [new(1, 1)]));
        Assert.Same(dcBoom, ex2);
        Assert.Empty(c.Calls);
    }

    [Fact]
    public async Task OfferBook_Progress_ReportsOneMessagePerScope()
    {
        var p = new ListProgress();
        await Book(Svc(new FakeClient()), [new(1, 1)], p: p);
        Assert.Equal(["Fetching prices from Uni1 (1/2)", "Fetching prices from Uni2 (2/2)"], p.Messages);
    }

    [Fact]
    public async Task OfferBook_NullArguments_Throw()
    {
        var s = Svc(new FakeClient());
        await Assert.ThrowsAsync<ArgumentNullException>(() => s.BuildOfferBookAsync(null!, TravelScopeKind.CrossWorld, new ExclusionState(), null, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => s.BuildOfferBookAsync([new(1, 1)], TravelScopeKind.CrossWorld, null!, null, default));
    }

    // World 2 (Beta) is listed by both Universalis data centers below.
    private static readonly UniversalisDataCenter[] OverlappingDcs =
    [
        new("Uni2", "R1", [2, 3]),
        new("Uni1", "R1", [1, 2]),
    ];

    [Fact]
    public async Task WorldInTwoDataCenters_IsCountedOnce()
    {
        var c = new FakeClient
        {
            DataCenters = OverlappingDcs,
            Respond = (_, _) => [new ItemListings(1, [L(1, A2, 10, 2)])],
        };
        var plan = await Build(Svc(c), [new(1, 4)]);
        var g = Assert.Single(plan.Worlds);
        Assert.Equal(2u, g.WorldId);
        Assert.Equal(2, g.Purchases.Sum(p => p.Quantity));
        Assert.Equal(["Uni1", "Uni2"], c.Calls.Select(x => x.Scope).ToArray());
    }

    [Fact]
    public async Task WorldInTwoDataCenters_IsAttributedToTheFirstScope()
    {
        var c = new FakeClient
        {
            DataCenters = OverlappingDcs,
            Respond = (scope, _) => scope == "Uni1"
                ? [new ItemListings(1, [L(1, A2, 10, 2)])]
                : [new ItemListings(1, [L(1, A2, 5, 3)])],
        };
        var plan = await Build(Svc(c), [new(1, 4)]);
        var g = Assert.Single(plan.Worlds);
        var p = Assert.Single(g.Purchases);
        Assert.Equal((2, 10), (p.Quantity, p.PricePerUnit));
    }

    [Fact]
    public async Task LeakedListingOfAWorldOutsideTheScopesDataCenter_IsDropped()
    {
        var c = new FakeClient
        {
            Respond = (scope, _) => scope == "Uni1"
                ? [new ItemListings(1, [L(1, A1, 20, 1), L(1, B1, 1, 5)])]
                : [new ItemListings(1, [L(1, B1, 30, 1)])],
        };
        var plan = await Build(Svc(c), [new(1, 6)]);
        var a = plan.Worlds.Single(w => w.WorldId == 1);
        var b = plan.Worlds.Single(w => w.WorldId == 3);
        Assert.Equal(1, a.Purchases.Sum(p => p.Quantity));
        Assert.Equal(1, b.Purchases.Sum(p => p.Quantity));
        Assert.Equal(30, b.Purchases[0].PricePerUnit);
    }

    [Fact]
    public async Task IdenticalListingsInOneWorldAndScope_AreBothKept()
    {
        var c = new FakeClient
        {
            Respond = (scope, _) => scope == "Uni1"
                ? [new ItemListings(1, [L(1, A1, 10, 1), L(1, A1, 10, 1)])]
                : [new ItemListings(1, [])],
        };
        var plan = await Build(Svc(c), [new(1, 2)]);
        var g = Assert.Single(plan.Worlds);
        Assert.Equal(2, g.Purchases.Sum(p => p.Quantity));
        Assert.Empty(plan.Shortfalls);
    }
}
