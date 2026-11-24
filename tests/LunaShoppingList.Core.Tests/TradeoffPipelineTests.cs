using LunaShoppingList.Core;

namespace LunaShoppingList.Core.Tests;

/// <summary>
/// End-to-end tests of the world-count versus gil trade-off: the real offer book, plan frontier, option picker, allocation state and
/// route summarizer, with only the Universalis client and the world data faked. Expected numbers come from an oracle that works
/// straight from the raw listings, or are worked out by hand in the comments.
/// </summary>
/// <remarks>
/// 2026-11-24 (release 0.16.0.0, whole stacks): a listing can only be bought whole. The oracle enumerates every subset of an item's
/// valid listings (<see cref="StackBruteForce"/>); before that date it took units cheapest-first, and ShoppingPlanner (removed the
/// same day) gave the cheapest total.
/// </remarks>
public class TradeoffPipelineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly string[] Names =
    [
        "", "Zodiark", "Alpha", "Odin", "Phoenix", "Raiden", "Shiva", "Twintania", "Lich", "Cerberus", "Louisoix",
        "Moogle", "Omega", "Ragnarok", "Spriggan", "Sagittarius", "Alexander", "Bahamut", "Durandal", "Fenrir", "Ifrit", "Ultros",
    ];

    private const uint UnlistedWorld = 21;
    private const uint UnknownToGame = 99;

    private static string GameDc(uint id) => id <= 7 ? "Aaa" : id <= 14 ? "Bbb" : "Ccc";

    private static List<WorldInfo> GameWorlds()
    {
        var all = new List<WorldInfo>();
        for (uint id = 1; id <= 21; id++)
            all.Add(new WorldInfo(id, Names[id], GameDc(id), "R1", GameDc(id), true));
        return all;
    }

    private static List<UniversalisDataCenter> UniDcs() =>
    [
        new("UniC", "R1", [20, 19, 18, 17, 16, 15, UnknownToGame]),
        new("UniA", "R1", [1, 2, 3, 4, 5, 6, 7]),
        new("UniB", "R1", [8, 9, 10, 11, 12, 13, 14]),
    ];

    private sealed class Fx
    {
        public List<Requirement> Reqs { get; set; } = [];
        public Dictionary<uint, List<Listing>> Pool { get; set; } = new();
        public HashSet<uint> Excluded { get; set; } = [];
        public List<UniversalisDataCenter> Dcs { get; set; } = UniDcs();
        public List<WorldInfo> Games { get; set; } = GameWorlds();
        public WorldInfo Current => Games.First(w => w.Id == 1);

        public static readonly HashSet<uint> ValidWorlds = Enumerable.Range(1, 20).Select(i => (uint)i).ToHashSet();

        public void Add(uint item, uint world, int price, int qty, bool hq = false, DateTimeOffset? review = null)
        {
            if (!Pool.TryGetValue(item, out var list))
                Pool[item] = list = [];
            list.Add(new Listing(item, world, world < Names.Length ? Names[world] : "Unknown", price, qty, hq, review ?? T0));
        }

        public Fx Shuffled(int seed)
        {
            var rnd = new Random(seed);
            return new Fx
            {
                Reqs = Shuffle(Reqs, rnd),
                Pool = Pool.Keys.OrderBy(_ => rnd.Next()).ToDictionary(k => k, k => Shuffle(Pool[k], rnd)),
                Excluded = Excluded,
                Dcs = Shuffle(Dcs, rnd),
                Games = Shuffle(Games, rnd),
            };
        }

        private static List<T> Shuffle<T>(List<T> xs, Random rnd) => xs.OrderBy(_ => rnd.Next()).ToList();

        private static bool Valid(Listing l) => l.PricePerUnit > 0 && l.Quantity > 0 && ValidWorlds.Contains(l.WorldId);

        public Dictionary<uint, int> Need()
        {
            var need = new Dictionary<uint, int>();
            foreach (var r in Reqs.Where(r => r.Quantity > 0 && !Excluded.Contains(r.ItemId)))
                need[r.ItemId] = need.GetValueOrDefault(r.ItemId) + r.Quantity;
            return need;
        }

        public List<int> Units(uint item, uint world)
            => (Pool.GetValueOrDefault(item) ?? [])
                .Where(l => Valid(l) && l.WorldId == world)
                .SelectMany(l => Enumerable.Repeat(l.PricePerUnit, l.Quantity))
                .Order().ToList();

        public int Stock(uint item, uint world) => Units(item, world).Count;

        public int Supply(uint item) => ValidWorlds.Sum(w => Stock(item, w));

        // 2026-11-24: whole stacks. Each valid listing is one stack; prices below come from every subset of the stacks.
        // Before: LineCost took the cheapest qty units on the world, part of a stack allowed.

        /// <summary>The item's valid listings on the worlds <paramref name="onWorld"/> accepts, one whole stack each.</summary>
        public List<PriceStep> Stacks(uint item, Func<uint, bool> onWorld)
            => (Pool.GetValueOrDefault(item) ?? [])
                .Where(l => Valid(l) && onWorld(l.WorldId))
                .Select(l => new PriceStep(l.PricePerUnit, l.Quantity, l.Hq, l.LastReview))
                .ToList();

        /// <summary>Gil for exactly <paramref name="qty"/> units on one world as whole stacks; a quantity no set of stacks sums to fails the test.</summary>
        public long LineCost(uint item, uint world, int qty)
            => StackBruteForce.Exact(StackBruteForce.All(Stacks(item, w => w == world)), qty)?.Cost
               ?? throw new Xunit.Sdk.XunitException($"{item}@{world}x{qty} is not a sum of whole stacks there");

        /// <summary>Cheapest set of the item's stacks on the accepted worlds with at least <paramref name="need"/> units (all of them when short).</summary>
        public long CoverCost(uint item, int need, Func<uint, bool> onWorld)
            => StackBruteForce.Cover(StackBruteForce.All(Stacks(item, onWorld)), need).Cost;

        /// <summary>The cheapest whole-stack plan's total over every valid world.</summary>
        public long CheapestTotal() => Need().Sum(kv => CoverCost(kv.Key, kv.Value, ValidWorlds.Contains));
    }

    private sealed class FakeWorlds(Fx fx) : IWorldData
    {
        public WorldInfo? CurrentWorld { get; } = fx.Current;
        public IReadOnlyList<WorldInfo> AllWorlds { get; } = fx.Games;
    }

    private sealed class FakeClient(Fx fx) : IUniversalisClient
    {
        public Task<IReadOnlyList<UniversalisDataCenter>> GetDataCentersAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<UniversalisDataCenter>>(fx.Dcs);

        public Task<IReadOnlyList<ItemListings>> GetListingsAsync(IReadOnlyCollection<uint> itemIds, string scope, CancellationToken ct)
        {
            var dc = fx.Dcs.First(d => d.Name == scope);
            IReadOnlyList<ItemListings> result = itemIds
                .Select(id => new ItemListings(id,
                    (fx.Pool.GetValueOrDefault(id) ?? [])
                        .Where(l => dc.WorldIds.Contains(l.WorldId) || (l.WorldId == UnlistedWorld && scope == "UniA"))
                        .ToList()))
                .ToList();
            return Task.FromResult(result);
        }
    }

    // 2026-11-24: PlannerTotal (ShoppingPlanner's partial-stack total, planner removed) became OracleTotal (Fx.CheapestTotal, whole
    // stacks), and SuggestedTotal (the Suggester's Cheapest plan, summarized) was added as the in-pipeline second reference.
    private sealed record Run(OfferBook Book, IReadOnlyList<PlanOption> Options, long OracleTotal, long SuggestedTotal);

    private static async Task<Run> RunAsync(Fx fx)
    {
        var svc = new ShoppingListService(new FakeWorlds(fx), new FakeClient(fx));
        var ex = new ExclusionState();
        foreach (var id in fx.Excluded)
            ex.SetExcluded(id, true);
        var book = await svc.BuildOfferBookAsync(fx.Reqs, TravelScopeKind.CrossWorld, ex, null, default);
        var options = PlanFrontier.Build(book);
        var suggested = RouteSummarizer.Summarize(book, Suggester.Suggest(book, SuggestionMode.Cheapest)).GrandTotal;
        return new Run(book, options, fx.CheapestTotal(), suggested);
    }

    private static RouteSummary Applied(OfferBook book, PlanOption o)
    {
        var s = new AllocationState(book);
        s.Apply(o.Lines);
        return s.Summarize();
    }

    private static IEnumerable<(uint Item, int Missing)> Unmet(RouteSummary s) => s.Unmet.Select(u => (u.ItemId, u.Missing));

    private static string FlatLines(PlanOption o) => string.Join(";", o.Lines.Select(l => $"{l.ItemId}@{l.WorldId}x{l.Quantity}"));

    private static string FlatOptions(IEnumerable<PlanOption> os)
        => string.Join("|", os.Select(o => $"{o.WorldCount},{o.Total},{o.ExtraGil},{o.ExtraPercent:R}:{FlatLines(o)}"));

    private static Dictionary<uint, int> Units(PlanOption o)
        => o.Lines.GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));

    /// <summary>What the picker is documented to choose: the fewest-worlds option within the budget; options are strictly fewer worlds as the total rises.</summary>
    private static PlanOption ExpectedPick(IReadOnlyList<PlanOption> os, long budget)
        => budget <= 0 ? os[0] : os.Last(o => o.ExtraGil <= budget);

    // ---------------------------------------------------------------- invariants shared by every fixture

    private static async Task AssertTradeoffAsync(Fx fx)
    {
        var r = await RunAsync(fx);
        var os = r.Options;
        Assert.NotEmpty(os);

        var first = os[0];
        Assert.Equal(r.OracleTotal, first.Total);
        Assert.Equal(r.SuggestedTotal, first.Total);
        Assert.Equal(0L, first.ExtraGil);
        Assert.Equal(0.0, first.ExtraPercent);
        var cheapSummary = Applied(r.Book, first);
        Assert.Equal(r.OracleTotal, cheapSummary.GrandTotal);
        var need = fx.Need();
        // items that get any line at all: needed and sold somewhere in scope
        var buyable = need.Keys.Where(item => fx.Supply(item) > 0).Order().ToList();

        for (var i = 0; i < os.Count; i++)
        {
            var o = os[i];
            var s = Applied(r.Book, o);
            Assert.Equal(o.Total, s.GrandTotal);
            // exact stack costs: the lines' whole-stack prices (LineCost fails on a line that is not a sum of whole stacks) ...
            Assert.Equal(o.Total, o.Lines.Sum(l => fx.LineCost(l.ItemId, l.WorldId, l.Quantity)));
            Assert.Equal(o.WorldCount, s.WorldCount);
            Assert.Equal(o.WorldCount, o.Lines.Select(l => l.WorldId).Distinct().Count());
            Assert.Equal(Unmet(cheapSummary), Unmet(s));
            Assert.Equal(o.Total - first.Total, o.ExtraGil);
            Assert.True(o.ExtraGil >= 0);
            Assert.Equal(first.Total == 0 ? 0.0 : o.ExtraGil * 100.0 / first.Total, o.ExtraPercent, 9);

            // 2026-11-24: was "every option buys exactly the cheapest option's units per item" (and so no fewer in total). With
            // whole stacks an option may carry a different surplus; capped at the need, every item still gets exactly
            // min(Needed, supply in the book), and every buyable item gets a line.
            var units = Units(o);
            Assert.Equal(buyable, units.Keys.Order());
            foreach (var item in buyable)
                Assert.Equal(Math.Min(need[item], fx.Supply(item)), Math.Min(units[item], need[item]));
            // ... and, new 2026-11-24, the cheapest whole stacks on the option's own worlds for that target, by brute force
            var worlds = o.Lines.Select(l => l.WorldId).ToHashSet();
            Assert.Equal(buyable.Sum(item => fx.CoverCost(item, Math.Min(need[item], fx.Supply(item)), worlds.Contains)), o.Total);
            foreach (var l in o.Lines)
            {
                Assert.True(l.Quantity > 0);
                Assert.Contains(l.WorldId, Fx.ValidWorlds);
                Assert.True(l.Quantity <= fx.Stock(l.ItemId, l.WorldId));
            }
            Assert.Equal(o.Lines.OrderBy(l => l.ItemId).ThenBy(l => l.WorldId).Select(l => (l.ItemId, l.WorldId)),
                o.Lines.Select(l => (l.ItemId, l.WorldId)));

            if (i > 0)
            {
                Assert.True(o.WorldCount < os[i - 1].WorldCount, $"worlds not strictly decreasing at {i}");
                Assert.True(o.Total > os[i - 1].Total, $"total not strictly increasing at {i}");
            }
        }

        // Pick at 0, at each option's extra, one below, one above, and beyond the largest
        var budgets = new SortedSet<long> { long.MinValue, -1, 0, 1, os[^1].ExtraGil + 1, os[^1].ExtraGil * 10 + 1000, long.MaxValue };
        foreach (var o in os)
            foreach (var b in new[] { o.ExtraGil - 1, o.ExtraGil, o.ExtraGil + 1 })
                budgets.Add(b);
        foreach (var b in budgets)
            Assert.Same(ExpectedPick(os, b), PlanFrontier.Pick(os, b));
        foreach (var o in os.Skip(1))
        {
            Assert.Same(o, PlanFrontier.Pick(os, o.ExtraGil));
            Assert.NotSame(o, PlanFrontier.Pick(os, o.ExtraGil - 1));
        }
        Assert.Same(os[0], PlanFrontier.Pick(os, 0));
        Assert.Same(os[^1], PlanFrontier.Pick(os, os[^1].ExtraGil + 1_000_000));

        // the slider arithmetic the results window uses
        foreach (var p in new[] { 0, 1, 2, 3, 5, 7, 10, 25, 50, 99, 100, 250, 1000, 100000 })
        {
            var budget = (long)Math.Floor(first.Total * (double)p / 100.0);
            var pick = PlanFrontier.Pick(os, budget)!;
            Assert.True(pick.ExtraGil <= budget, $"p={p}: extra {pick.ExtraGil} over budget {budget}");
            Assert.True(pick.ExtraPercent <= p + 1e-9, $"p={p}: percent {pick.ExtraPercent}");
        }

        // deterministic: twice, and with everything in another order
        var flat = FlatOptions(os);
        Assert.Equal(flat, FlatOptions((await RunAsync(fx)).Options));
        Assert.Equal(flat, FlatOptions((await RunAsync(fx.Shuffled(1))).Options));
        Assert.Equal(flat, FlatOptions((await RunAsync(fx.Shuffled(2))).Options));
    }

    // ---------------------------------------------------------------- the realistic fixture

    private static Fx Realistic()
    {
        var fx = new Fx();
        fx.Reqs =
        [
            new(101, 1), new(102, 5), new(103, 99), new(104, 75), new(105, 9), new(106, 33), new(107, 20),
            new(108, 48), new(109, 64), new(110, 90), new(111, 12), new(112, 1), new(112, 1), new(113, 0),
        ];
        fx.Excluded = [106];
        fx.Games.Add(fx.Games.First(w => w.Id == 3));

        fx.Add(101, 3, 300, 5); fx.Add(101, 5, 260, 3); fx.Add(101, 16, 400, 1);
        fx.Add(102, 7, 100, 4); fx.Add(102, 7, 100_000, 1); fx.Add(102, 12, 5000, 3); fx.Add(102, 5, 9000, 2);
        fx.Add(103, 2, 50, 40); fx.Add(103, 4, 60, 30); fx.Add(103, 10, 55, 30); fx.Add(103, 15, 70, 25);
        fx.Add(104, 1, 10, 20); fx.Add(104, 8, 12, 20); fx.Add(104, 18, 11, 20);
        fx.Add(106, 1, 5, 33);
        fx.Add(107, 1, 100, 10); fx.Add(107, 1, 500, 10, hq: true);
        fx.Add(107, 8, 90, 5); fx.Add(107, 8, 120, 20, hq: true); fx.Add(107, 14, 150, 20);
        fx.Add(108, 2, 0, 100); fx.Add(108, 6, 0, 100, hq: true); fx.Add(108, 11, 10, 0); fx.Add(108, 13, 40, 48); fx.Add(108, 17, 45, 60);
        fx.Add(109, 5, 20, 70); fx.Add(109, 9, 18, 10); fx.Add(109, 20, 22, 64);
        fx.Add(110, 3, 7, 50); fx.Add(110, 12, 8, 50);
        fx.Add(110, UnlistedWorld, 1, 200); fx.Add(110, UnknownToGame, 1, 200);
        fx.Add(111, 14, 1000, 12); fx.Add(111, 1, 900, 6); fx.Add(111, 2, 950, 6);
        fx.Add(112, 6, 3000, 2); fx.Add(112, 19, 2900, 1);
        fx.Add(113, 1, 5, 10);
        return fx;
    }

    [Fact]
    public async Task Realistic_EveryTradeoffInvariantHolds()
        => await AssertTradeoffAsync(Realistic());

    [Fact]
    public async Task Realistic_FirstOptionIsTheCheapestPlanWithNoExtra()
    {
        var r = await RunAsync(Realistic());
        // 2026-11-24: was r.PlannerTotal (ShoppingPlanner, partial stacks, removed). Hand-worked whole-stack cheapest plan, per item
        // (see RedesignPipelineTests.Realistic_CheapestLinesAreTheHandWorkedAnswers, same listings): 101 400, 102 15,400, 103 5,450,
        // 104 660, 107 2,400, 108 1,920, 109 1,400, 110 750, 111 11,100, 112 6,000.
        Assert.Equal(45_480L, r.Options[0].Total);
        Assert.Equal(r.OracleTotal, r.Options[0].Total);
        Assert.Equal(0L, r.Options[0].ExtraGil);
        Assert.Equal(0.0, r.Options[0].ExtraPercent);
    }

    [Fact]
    public async Task Realistic_OffersAtLeastOneFewerWorldsOption()
    {
        var r = await RunAsync(Realistic());
        Assert.True(r.Options.Count >= 2, $"only {r.Options.Count} option(s)");
        Assert.True(r.Options[^1].WorldCount < r.Options[0].WorldCount);
    }

    [Fact]
    public async Task Realistic_ShortAndMissingItemsStayUnmetInEveryOption()
    {
        var r = await RunAsync(Realistic());
        // 104: 60 of 75 exist; 105: nothing exists
        foreach (var o in r.Options)
        {
            Assert.Equal([(104u, 15), (105u, 9)], Unmet(Applied(r.Book, o)));
            Assert.Equal(60, o.Lines.Where(l => l.ItemId == 104).Sum(l => l.Quantity));
            Assert.DoesNotContain(o.Lines, l => l.ItemId is 105 or 106 or 113);
            Assert.DoesNotContain(o.Lines, l => l.WorldId is UnlistedWorld or UnknownToGame);
        }
    }

    // ---------------------------------------------------------------- the hand-worked outlier fixture

    // Items 1-4 (need 10 each): worlds 3, 4, 5, 6 each sell one stack of 10 of one of them at 10; the hub world 2 sells a 10-stack
    // of all four at 11.
    // Item 5 (need 5): world 7 sells a 4-stack at 100 and a 1-stack at 100,000 (the outlier); world 9 sells a 1-stack and a 4-stack
    // at 1,000.
    // Cheapest: items 1-4 on their own worlds = 4 x 100 = 400; item 5 = the 4-stack at 100 + the 1-stack at 1,000 = 1,400
    // (the other covers of 5: both of world 9's stacks 5,000, 4@100 + 4@1,000 = 4,400, world 7's two 100,400). Total 1,800 over six
    // worlds (3, 4, 5, 6, 7, 9).
    // Moving k of the four items to the hub (110 each instead of 100) while still buying item 5 on 7 and 9:
    //   k=1: 1,810 on six worlds (dominated), k=2: 1,820 on five worlds, k=3: 1,830 on four, k=4: 1,840 on three.
    // Visiting only the hub and world 9 buys both of world 9's stacks: 440 + 5,000 = 5,440 on two worlds (extra 3,640, 202.2%);
    // the hub and world 7 would cost 440 + 100,400. One world cannot supply everything. The 100,000 outlier is never bought.
    //
    // 2026-11-24 (whole stacks), fixture changed: world 9 used to sell ONE 5-stack at 1,000, and the cheapest plan bought one unit
    // of it. Whole, that stack makes world 9 alone the cheapest cover of item 5 (5,000, against 4@100 + 5@1,000 = 5,400), every
    // option then pays 5,000 for item 5, and the expensive two-world tail this fixture exists for disappears (options 5,400 /
    // 5,420 / 5,430 / 5,440; pinned by Outlier_WithOneFiveStackOnWorld9_EveryOptionPaysForTheWholeStack below). Splitting it into
    // a 1-stack and a 4-stack restores the scenario with whole stacks, and every number above is re-derived for whole stacks.
    private static Fx Outlier()
    {
        var fx = new Fx { Reqs = [new(1, 10), new(2, 10), new(3, 10), new(4, 10), new(5, 5)] };
        for (uint i = 1; i <= 4; i++)
        {
            fx.Add(i, 2, 11, 10);
            fx.Add(i, 2 + i, 10, 10);
        }
        fx.Add(5, 7, 100, 4); fx.Add(5, 7, 100_000, 1);
        fx.Add(5, 9, 1000, 1); fx.Add(5, 9, 1000, 4);
        return fx;
    }

    [Fact]
    public async Task Outlier_WithOneFiveStackOnWorld9_EveryOptionPaysForTheWholeStack()
    {
        // The original outlier fixture (before 2026-11-24): world 9 sells item 5 as one 5-stack at 1,000. With whole stacks item 5's
        // cheapest cover is that stack alone (5,000; 4@100 + 5@1,000 = 5,400 and world 7's two stacks 100,400 are dearer), so the
        // cheapest plan visits worlds 3, 4, 5, 6 and 9 for 400 + 5,000 = 5,400. Moving k of items 1-4 to the hub then costs 10 each:
        // k=1 5,410 on five worlds (dominated), k=2 5,420 on four, k=3 5,430 on three, k=4 5,440 on two (hub and world 9).
        var fx = Outlier();
        fx.Pool[5].RemoveAll(l => l.WorldId == 9);
        fx.Add(5, 9, 1000, 5);
        var r = await RunAsync(fx);
        Assert.Equal([(5, 5400L, 0L), (4, 5420L, 20L), (3, 5430L, 30L), (2, 5440L, 40L)],
            r.Options.Select(o => (o.WorldCount, o.Total, o.ExtraGil)));
        Assert.Equal("1@2x10;2@2x10;3@2x10;4@2x10;5@9x5", FlatLines(r.Options[^1]));
        Assert.Equal(2, PlanFrontier.Pick(r.Options, 40)!.WorldCount);
        await AssertTradeoffAsync(fx);
    }

    [Fact]
    public async Task Outlier_ExpectedOptionsAreTheHandWorkedOnes()
    {
        var r = await RunAsync(Outlier());
        Assert.Equal([(6, 1800L, 0L), (5, 1820L, 20L), (4, 1830L, 30L), (3, 1840L, 40L), (2, 5440L, 3640L)],
            r.Options.Select(o => (o.WorldCount, o.Total, o.ExtraGil)));
    }

    [Fact]
    public async Task Outlier_ThreeWorldsCostsTwoPointTwoPercentMoreThanSix()
    {
        var r = await RunAsync(Outlier());
        var three = r.Options.Single(o => o.WorldCount == 3);
        Assert.Equal(1840L, three.Total);
        Assert.Equal(40.0 * 100 / 1800, three.ExtraPercent, 9);
        Assert.Equal("1@2x10;2@2x10;3@2x10;4@2x10;5@7x4;5@9x1", FlatLines(three));
    }

    [Fact]
    public async Task Outlier_TheExpensiveTailIsTwoWorldsAtAboutTwoHundredPercentExtra()
    {
        var r = await RunAsync(Outlier());
        var last = r.Options[^1];
        Assert.Equal(2, last.WorldCount);
        Assert.Equal(5440L, last.Total);
        Assert.InRange(last.ExtraPercent, 202.0, 202.5);
        Assert.Equal("1@2x10;2@2x10;3@2x10;4@2x10;5@9x5", FlatLines(last));
    }

    [Fact]
    public async Task Outlier_NoOptionPaysTheOutlierPrice()
    {
        var r = await RunAsync(Outlier());
        Assert.All(r.Options, o => Assert.True(o.Total < 100_000));
    }

    [Theory]
    [InlineData(-5, 6, 1800)]
    [InlineData(0, 6, 1800)]
    [InlineData(19, 6, 1800)]
    [InlineData(20, 5, 1820)]
    [InlineData(29, 5, 1820)]
    [InlineData(30, 4, 1830)]
    [InlineData(39, 4, 1830)]
    [InlineData(40, 3, 1840)]
    [InlineData(3639, 3, 1840)]
    [InlineData(3640, 2, 5440)]
    [InlineData(1_000_000, 2, 5440)]
    public async Task Outlier_PickAtBudgetGivesTheHandWorkedOption(long budget, int worlds, long total)
    {
        var r = await RunAsync(Outlier());
        var pick = PlanFrontier.Pick(r.Options, budget)!;
        Assert.Equal((worlds, total), (pick.WorldCount, pick.Total));
    }

    // 1% of 1,800 is 18 gil, 2% is 36, 3% is 54, 202% is 3,636, 203% is 3,654.
    [Theory]
    [InlineData(0, 6)]
    [InlineData(1, 6)]
    [InlineData(2, 4)]
    [InlineData(3, 3)]
    [InlineData(202, 3)]
    [InlineData(203, 2)]
    public async Task Outlier_SliderPercentBudgetIsFlooredGilOfTheCheapestTotal(int percent, int worlds)
    {
        var r = await RunAsync(Outlier());
        var budget = (long)Math.Floor(r.Options[0].Total * (double)percent / 100.0);
        var pick = PlanFrontier.Pick(r.Options, budget)!;
        Assert.Equal(worlds, pick.WorldCount);
        Assert.True(pick.ExtraGil <= budget);
        Assert.True(pick.ExtraPercent <= percent);
    }

    [Fact]
    public async Task Outlier_AllInvariantsHold()
        => await AssertTradeoffAsync(Outlier());

    // ---------------------------------------------------------------- degenerate books

    [Fact]
    public async Task EmptyBook_GivesOneEmptyOptionAndPickWorksOnIt()
    {
        var fx = new Fx { Reqs = [new(1, 5), new(2, 0)], Excluded = [1] };
        var r = await RunAsync(fx);
        var only = Assert.Single(r.Options);
        Assert.Equal((0, 0L, 0L, 0.0), (only.WorldCount, only.Total, only.ExtraGil, only.ExtraPercent));
        Assert.Empty(only.Lines);
        foreach (var b in new long[] { long.MinValue, -1, 0, 1, 1000, long.MaxValue })
            Assert.Same(only, PlanFrontier.Pick(r.Options, b));
        var s = Applied(r.Book, only);
        Assert.Equal((0L, 0), (s.GrandTotal, s.WorldCount));
        await AssertTradeoffAsync(fx);
    }

    [Fact]
    public async Task BookWhereNothingIsListed_GivesOneEmptyOptionWithEverythingUnmet()
    {
        var fx = new Fx { Reqs = [new(1, 5), new(2, 3)] };
        var r = await RunAsync(fx);
        var only = Assert.Single(r.Options);
        Assert.Empty(only.Lines);
        Assert.Equal([(1u, 5), (2u, 3)], Unmet(Applied(r.Book, only)));
        Assert.Same(only, PlanFrontier.Pick(r.Options, 500));
    }

    [Fact]
    public void Pick_OnNoOptionsAtAll_ReturnsNull()
        => Assert.Null(PlanFrontier.Pick([], 100));

    [Fact]
    public async Task OneWorldBook_GivesASingleOptionOnThatWorld()
    {
        var fx = new Fx { Reqs = [new(1, 4), new(2, 6)] };
        fx.Add(1, 3, 10, 9); fx.Add(1, 3, 20, 9);
        fx.Add(2, 3, 5, 6);
        var r = await RunAsync(fx);
        var only = Assert.Single(r.Options);
        // 2026-11-24 (whole stacks): was 4 * 10 + 6 * 5 = 70 (4 units of the 9-stack). Item 1 needs 4 and must take a whole stack:
        // 9@10 = 90 beats 9@20 = 180; item 2 takes its 6-stack at 5. 90 + 30 = 120.
        Assert.Equal((1, 9 * 10L + 6 * 5), (only.WorldCount, only.Total));
        Assert.Equal("1@3x9;2@3x6", FlatLines(only));
        Assert.Equal(0L, only.ExtraGil);
        Assert.All(only.Lines, l => Assert.Equal(3u, l.WorldId));
        Assert.Same(only, PlanFrontier.Pick(r.Options, 1_000_000));
        await AssertTradeoffAsync(fx);
    }

    [Fact]
    public async Task OnlyOneWorldHasAnything_ShortItemsStayUnmet()
    {
        var fx = new Fx { Reqs = [new(1, 10), new(2, 3), new(3, 2)] };
        fx.Add(1, 8, 100, 4);
        fx.Add(2, 8, 50, 3);
        fx.Add(3, UnlistedWorld, 1, 50); // listed only on a world the plan may not use
        var r = await RunAsync(fx);
        var only = Assert.Single(r.Options);
        Assert.Equal((1, 400L + 150), (only.WorldCount, only.Total));
        Assert.Equal([(1u, 6), (3u, 2)], Unmet(Applied(r.Book, only)));
        await AssertTradeoffAsync(fx);
    }

    // ---------------------------------------------------------------- the seeded random loop

    private static Fx RandomFx(int seed)
    {
        var rnd = new Random(seed);
        var fx = new Fx();
        var items = rnd.Next(6, 15);
        for (var n = 0; n < items; n++)
        {
            var id = (uint)(200 + n);
            var qty = rnd.Next(1, 100);
            if (qty > 1 && rnd.Next(8) == 0)
            {
                fx.Reqs.Add(new Requirement(id, qty / 2));
                fx.Reqs.Add(new Requirement(id, qty - qty / 2));
            }
            else
                fx.Reqs.Add(new Requirement(id, rnd.Next(25) == 0 ? 0 : qty));

            if (rnd.Next(10) == 0)
                fx.Excluded.Add(id);
            if (rnd.Next(12) == 0)
                continue;

            var basePrice = rnd.Next(10, 3000);
            var listings = rnd.Next(1, 10);
            for (var k = 0; k < listings; k++)
            {
                var world = (uint)rnd.Next(1, 21);
                var price = (int)(basePrice * (0.8 + rnd.NextDouble() * 0.7));
                var q = rnd.Next(4) == 0 ? rnd.Next(1, 6) : rnd.Next(1, 41);
                var roll = rnd.Next(40);
                if (roll == 0) price = 0;
                else if (roll == 1) q = 0;
                else if (roll == 2) price *= 1000;
                fx.Add(id, world, price, q, rnd.Next(2) == 0, T0.AddDays(-rnd.Next(0, 4)));
            }
            if (rnd.Next(8) == 0)
                fx.Add(id, rnd.Next(2) == 0 ? UnlistedWorld : UnknownToGame, 1, 500);
        }
        return fx;
    }

    [Fact]
    public async Task SeededRandomFixtures_HoldEveryTradeoffInvariant()
    {
        for (var seed = 1; seed <= 150; seed++)
        {
            try
            {
                await AssertTradeoffAsync(RandomFx(seed));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new Xunit.Sdk.XunitException($"seed {seed}: {e.GetType().Name}: {e.Message}", e);
            }
        }
    }

    [Fact]
    public async Task SeededRandomFixtures_ProduceMultiOptionFrontiersOftenEnoughToMeanSomething()
    {
        var multi = 0;
        for (var seed = 1; seed <= 150; seed++)
            if ((await RunAsync(RandomFx(seed))).Options.Count >= 2)
                multi++;
        Assert.True(multi > 40, $"only {multi} of 150 fixtures had a real trade-off");
    }
}
