using LunaShoppingList.Core;

namespace LunaShoppingList.Core.Tests;

/// <summary>
/// End-to-end tests over the real services (offer book, suggester, route summarizer, allocation state, and the original planner as
/// a reference). Only the Universalis client and the world data are faked. Every expected number comes from an oracle that works
/// straight from the raw listings, never from production helpers, so a wrong result in any one module cannot agree with itself.
/// </summary>
public class RedesignPipelineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    // 21 game worlds. Worlds 1-20 are listed by Universalis in three data centers; world 21 is a game world Universalis does not list.
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

    // Universalis's own grouping, listed out of name order. World 99 is unknown to the game.
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

        /// <summary>Worlds a purchase may legitimately happen on, stated by hand: game worlds that Universalis also lists.</summary>
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

        // ---- the oracle: everything below works from the raw listings only ----
        private static bool Valid(Listing l) => l.PricePerUnit > 0 && l.Quantity > 0 && ValidWorlds.Contains(l.WorldId);

        public Dictionary<uint, int> Need()
        {
            var need = new Dictionary<uint, int>();
            foreach (var r in Reqs.Where(r => r.Quantity > 0 && !Excluded.Contains(r.ItemId)))
                need[r.ItemId] = need.GetValueOrDefault(r.ItemId) + r.Quantity;
            return need;
        }

        public Dictionary<uint, int> SkippedExpected()
        {
            var s = new Dictionary<uint, int>();
            foreach (var r in Reqs.Where(r => r.Quantity > 0 && Excluded.Contains(r.ItemId)))
                s[r.ItemId] = s.GetValueOrDefault(r.ItemId) + r.Quantity;
            return s;
        }

        /// <summary>Unit prices (ascending, one entry per unit) of an item on one world.</summary>
        public List<int> Units(uint item, uint world)
            => (Pool.GetValueOrDefault(item) ?? [])
                .Where(l => Valid(l) && l.WorldId == world)
                .SelectMany(l => Enumerable.Repeat(l.PricePerUnit, l.Quantity))
                .Order().ToList();

        public int Stock(uint item, uint world) => Units(item, world).Count;

        public int Supply(uint item) => ValidWorlds.Sum(w => Stock(item, w));

        public long LineCost(uint item, uint world, int qty) => Units(item, world).Take(qty).Sum(p => (long)p);

        public long CheapestCost(uint item, int need)
            => ValidWorlds.SelectMany(w => Units(item, w)).Order().Take(need).Sum(p => (long)p);
    }

    private sealed class FakeWorlds(Fx fx) : IWorldData
    {
        public WorldInfo? CurrentWorld { get; } = fx.Current;
        public IReadOnlyList<WorldInfo> AllWorlds { get; } = fx.Games;
    }

    private sealed class FakeClient(Fx fx) : IUniversalisClient
    {
        public List<(string Scope, uint[] Ids)> Calls { get; } = [];

        public Task<IReadOnlyList<UniversalisDataCenter>> GetDataCentersAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<UniversalisDataCenter>>(fx.Dcs);

        public Task<IReadOnlyList<ItemListings>> GetListingsAsync(IReadOnlyCollection<uint> itemIds, string scope, CancellationToken ct)
        {
            Calls.Add((scope, itemIds.OrderBy(i => i).ToArray()));
            var dc = fx.Dcs.First(d => d.Name == scope);
            // A world the game lists but Universalis does not leaks into one scope's answer: the pipeline must still drop it.
            IReadOnlyList<ItemListings> result = itemIds
                .Select(id => new ItemListings(id,
                    (fx.Pool.GetValueOrDefault(id) ?? [])
                        .Where(l => dc.WorldIds.Contains(l.WorldId) || (l.WorldId == UnlistedWorld && scope == "UniA"))
                        .ToList()))
                .ToList();
            return Task.FromResult(result);
        }
    }

    private sealed record Run(OfferBook Book, IReadOnlyList<AllocationLine> Cheap, IReadOnlyList<AllocationLine> Fewest,
        SuggestionComparison Cmp, ShoppingPlan ServicePlan, ShoppingPlan PlannerPlan, FakeClient Client);

    private static async Task<Run> RunAsync(Fx fx)
    {
        var client = new FakeClient(fx);
        var svc = new ShoppingListService(new FakeWorlds(fx), client);
        var ex = new ExclusionState();
        foreach (var id in fx.Excluded)
            ex.SetExcluded(id, true);

        var book = await svc.BuildOfferBookAsync(fx.Reqs, TravelScopeKind.CrossWorld, ex, null, default);
        // the plan path gets its own fake so the offer-book client's recorded calls stay one set
        var servicePlan = await new ShoppingListService(new FakeWorlds(fx), new FakeClient(fx))
            .BuildPlanAsync(fx.Reqs, TravelScopeKind.CrossWorld, ex, null, default);
        var cheap = Suggester.Suggest(book, SuggestionMode.Cheapest);
        var fewest = Suggester.Suggest(book, SuggestionMode.FewestWorlds);
        var cmp = RouteSummarizer.Compare(book, cheap, fewest);

        // The original planner, fed the raw pool (invalid and leaked listings included) and the hand-stated allowed worlds.
        var raw = fx.Pool.Select(kv => new ItemListings(kv.Key, kv.Value)).ToList();
        var plannerPlan = ShoppingPlanner.Plan(fx.Reqs, raw, Fx.ValidWorlds, ex);
        return new Run(book, cheap, fewest, cmp, servicePlan, plannerPlan, client);
    }

    private static string Flat(IEnumerable<AllocationLine> ls) => string.Join(";", ls.Select(l => $"{l.ItemId}@{l.WorldId}x{l.Quantity}"));

    private static string Flat(RouteSummary s)
        => $"{s.GrandTotal}|"
           + string.Join(";", s.Worlds.Select(w => $"{w.WorldId}:{w.WorldName}:{w.Total}["
               + string.Join(",", w.Lines.Select(l => $"{l.ItemId}x{l.Quantity}={l.Cost}/{l.MinUnitPrice}-{l.MaxUnitPrice}/{l.OldestReview.UtcTicks}")) + "]"))
           + "|" + string.Join(",", s.Unmet.Select(u => $"{u.ItemId}-{u.Missing}"));

    private static string Flat(OfferBook b)
        => string.Join(";", b.Items.Select(i => $"{i.ItemId}n{i.Needed}["
               + string.Join(",", i.Worlds.Select(w => $"{w.WorldId}:{w.WorldName}:" + string.Join("/", w.Steps.Select(s => $"{s.UnitPrice}x{s.Quantity}{(s.Hq ? "H" : "N")}{s.LastReview.UtcTicks}")))) + "]"))
           + "|" + string.Join(",", b.Skipped.Select(s => $"{s.ItemId}x{s.Quantity}"));

    private static string FlatAll(Run r)
        => string.Join("\n", Flat(r.Book), Flat(r.Cheap), Flat(r.Fewest), Flat(r.Cmp.Cheapest), Flat(r.Cmp.FewestWorlds),
            r.Cmp.ExtraGilForFewestWorlds, r.Cmp.WorldsSaved);

    private static void AssertLines(Fx fx, IReadOnlyList<AllocationLine> lines, string mode)
    {
        var need = fx.Need();
        Assert.Equal(lines.OrderBy(l => l.ItemId).ThenBy(l => l.WorldId).Select(l => (l.ItemId, l.WorldId)), lines.Select(l => (l.ItemId, l.WorldId)));
        Assert.Equal(lines.Count, lines.Select(l => (l.ItemId, l.WorldId)).Distinct().Count());
        foreach (var l in lines)
        {
            Assert.True(l.Quantity > 0, $"{mode}: non-positive quantity {l}");
            Assert.True(need.ContainsKey(l.ItemId), $"{mode}: line for an item that is excluded, zero-quantity or unknown: {l}");
            Assert.True(Fx.ValidWorlds.Contains(l.WorldId), $"{mode}: line on a world outside the allowed set: {l}");
            Assert.True(l.Quantity <= fx.Stock(l.ItemId, l.WorldId), $"{mode}: {l} exceeds stock {fx.Stock(l.ItemId, l.WorldId)}");
        }
        foreach (var (item, n) in need)
        {
            var total = lines.Where(l => l.ItemId == item).Sum(l => l.Quantity);
            Assert.True(total == Math.Min(n, fx.Supply(item)), $"{mode}: item {item} gets {total}, expected min({n},{fx.Supply(item)})");
        }
    }

    private static void AssertSummary(Fx fx, RouteSummary s, IReadOnlyList<AllocationLine> lines)
    {
        var expectedCost = lines.Sum(l => fx.LineCost(l.ItemId, l.WorldId, l.Quantity));
        Assert.Equal(expectedCost, s.GrandTotal);
        Assert.Equal(s.Worlds.Sum(w => w.Total), s.GrandTotal);
        Assert.Equal(lines.Select(l => l.WorldId).Distinct().Count(), s.WorldCount);
        foreach (var w in s.Worlds)
            Assert.Equal(lines.Where(l => l.WorldId == w.WorldId).Sum(l => fx.LineCost(l.ItemId, l.WorldId, l.Quantity)), w.Total);

        var expectedUnmet = fx.Need()
            .Where(kv => fx.Supply(kv.Key) < kv.Value)
            .OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key}-{kv.Value - fx.Supply(kv.Key)}");
        Assert.Equal(expectedUnmet, s.Unmet.Select(u => $"{u.ItemId}-{u.Missing}"));
    }

    private static void AssertBook(Fx fx, OfferBook book)
    {
        var need = fx.Need();
        Assert.Equal(need.Keys.Order(), book.Items.Select(i => i.ItemId));
        foreach (var item in book.Items)
        {
            Assert.Equal(need[item.ItemId], item.Needed);
            var expectedWorlds = Fx.ValidWorlds.Where(w => fx.Stock(item.ItemId, w) > 0).Order().ToList();
            Assert.Equal(expectedWorlds, item.Worlds.Select(w => w.WorldId).Order());
            Assert.Equal(item.Worlds.Count, item.Worlds.Select(w => w.WorldId).Distinct().Count());
            foreach (var w in item.Worlds)
            {
                Assert.Equal(fx.Stock(item.ItemId, w.WorldId), w.UnitsForSale);
                Assert.Equal(fx.LineCost(item.ItemId, w.WorldId, w.UnitsForSale), w.CostFor(w.UnitsForSale));
            }
        }
        var skipped = fx.SkippedExpected();
        Assert.Equal(skipped.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}x{kv.Value}"), book.Skipped.Select(s => $"{s.ItemId}x{s.Quantity}"));
    }

    private static AllocationState Fresh(OfferBook book, IReadOnlyList<AllocationLine> lines)
    {
        var s = new AllocationState(book);
        s.Apply(lines);
        return s;
    }

    private static void AssertStateConsistent(Fx fx, AllocationState s)
    {
        var sum = s.Summarize();
        Assert.Equal(s.Lines.Sum(l => fx.LineCost(l.ItemId, l.WorldId, l.Quantity)), sum.GrandTotal);
        Assert.Equal(s.Lines.Sum(l => s.Cost(l.ItemId, l.WorldId)), sum.GrandTotal);
        foreach (var (item, need) in fx.Need())
        {
            var allocated = s.Lines.Where(l => l.ItemId == item).Sum(l => l.Quantity);
            Assert.Equal(allocated, s.Allocated(item));
            var missing = Math.Max(0, need - allocated);
            Assert.Equal(missing, s.Unmet(item));
            Assert.Equal(missing, sum.Unmet.FirstOrDefault(u => u.ItemId == item)?.Missing ?? 0);
        }
    }

    private static void AssertEdits(Fx fx, OfferBook book, IReadOnlyList<AllocationLine> lines)
    {
        if (lines.Count == 0)
            return;
        var picks = new[] { lines[0], lines[lines.Count / 2], lines[^1] }.Distinct().ToList();
        foreach (var line in picks)
        {
            var (item, world, q) = (line.ItemId, line.WorldId, line.Quantity);
            var before = Fresh(book, lines).Summarize();
            var otherLinesOnWorld = lines.Count(l => l.WorldId == world && l.ItemId != item);
            var beforeMissing = before.Unmet.FirstOrDefault(u => u.ItemId == item)?.Missing ?? 0;
            var need = fx.Need()[item];

            // zero and a negative quantity both remove the line
            foreach (var target in new[] { 0, -7 })
            {
                var s = Fresh(book, lines);
                s.SetQuantity(item, world, target);
                Assert.Equal(0, s.GetQuantity(item, world));
                Assert.Equal(lines.Count - 1, s.Lines.Count);
                var after = s.Summarize();
                Assert.Equal(before.GrandTotal - fx.LineCost(item, world, q), after.GrandTotal);
                Assert.Equal(before.WorldCount - (otherLinesOnWorld == 0 ? 1 : 0), after.WorldCount);
                Assert.Equal(otherLinesOnWorld > 0, after.Worlds.Any(w => w.WorldId == world));
                Assert.Equal(beforeMissing + q, after.Unmet.FirstOrDefault(u => u.ItemId == item)?.Missing ?? 0);
                AssertStateConsistent(fx, s);
            }

            // reduced by one unit
            if (q > 1)
            {
                var s = Fresh(book, lines);
                s.SetQuantity(item, world, q - 1);
                var after = s.Summarize();
                Assert.Equal(before.GrandTotal - fx.LineCost(item, world, q) + fx.LineCost(item, world, q - 1), after.GrandTotal);
                Assert.Equal(before.WorldCount, after.WorldCount);
                Assert.Equal(beforeMissing + 1, after.Unmet.FirstOrDefault(u => u.ItemId == item)?.Missing ?? 0);
                AssertStateConsistent(fx, s);
            }

            // raised above stock: clamped to what the world has
            {
                var stock = fx.Stock(item, world);
                var s = Fresh(book, lines);
                s.SetQuantity(item, world, stock + 50);
                Assert.Equal(stock, s.GetQuantity(item, world));
                Assert.Equal(fx.LineCost(item, world, stock), s.Cost(item, world));
                var after = s.Summarize();
                Assert.Equal(before.GrandTotal - fx.LineCost(item, world, q) + fx.LineCost(item, world, stock), after.GrandTotal);
                Assert.Equal(Math.Max(0, need - (lines.Where(l => l.ItemId == item).Sum(l => l.Quantity) - q + stock)),
                    after.Unmet.FirstOrDefault(u => u.ItemId == item)?.Missing ?? 0);
                AssertStateConsistent(fx, s);
            }

            // a world the item is not sold on, and an unknown item, change nothing
            {
                var s = Fresh(book, lines);
                s.SetQuantity(item, 12345, 5);
                s.SetQuantity(987654, world, 5);
                Assert.Equal(Flat(before), Flat(s.Summarize()));
            }
        }

        // emptying a whole world removes it from the route and costs exactly that world's total
        var victim = lines.Select(l => l.WorldId).Distinct().Order().First();
        var st = Fresh(book, lines);
        var beforeAll = st.Summarize();
        foreach (var l in lines.Where(l => l.WorldId == victim))
            st.SetQuantity(l.ItemId, victim, 0);
        var afterAll = st.Summarize();
        Assert.DoesNotContain(afterAll.Worlds, w => w.WorldId == victim);
        Assert.Equal(beforeAll.WorldCount - 1, afterAll.WorldCount);
        Assert.Equal(beforeAll.GrandTotal - beforeAll.Worlds.First(w => w.WorldId == victim).Total, afterAll.GrandTotal);
        AssertStateConsistent(fx, st);
    }

    /// <summary>Every cross-module invariant over one fixture. Runs the pipeline four times (plain, shuffled twice, plain again).</summary>
    private static async Task AssertPipelineAsync(Fx fx)
    {
        var r = await RunAsync(fx);

        // the client was asked once per Universalis data center that holds an allowed world, for the buyable items only
        var expectedIds = fx.Need().Keys.Order().ToArray();
        if (expectedIds.Length == 0)
            Assert.Empty(r.Client.Calls);
        else
        {
            Assert.Equal(new[] { "UniA", "UniB", "UniC" }, r.Client.Calls.Select(c => c.Scope));
            Assert.All(r.Client.Calls, c => Assert.Equal(expectedIds, c.Ids));
        }

        AssertBook(fx, r.Book);
        AssertLines(fx, r.Cheap, "Cheapest");
        AssertLines(fx, r.Fewest, "FewestWorlds");

        var cheapSum = r.Cmp.Cheapest;
        var fewSum = r.Cmp.FewestWorlds;
        AssertSummary(fx, cheapSum, r.Cheap);
        AssertSummary(fx, fewSum, r.Fewest);

        // Cheapest equals the original planner, the service's own plan, and a plain sorted-prices oracle
        Assert.Equal(r.PlannerPlan.Worlds.Sum(w => w.Total), cheapSum.GrandTotal);
        Assert.Equal(r.ServicePlan.Worlds.Sum(w => w.Total), cheapSum.GrandTotal);
        Assert.Equal(fx.Need().Sum(kv => fx.CheapestCost(kv.Key, Math.Min(kv.Value, fx.Supply(kv.Key)))), cheapSum.GrandTotal);
        Assert.Equal(r.PlannerPlan.Shortfalls.Select(s => (s.ItemId, s.Missing)), cheapSum.Unmet.Select(u => (u.ItemId, u.Missing)));
        Assert.Equal(r.PlannerPlan.Skipped.Select(s => (s.ItemId, s.Quantity)), r.Book.Skipped.Select(s => (s.ItemId, s.Quantity)));

        // Fewest-worlds never visits more worlds and never pays less
        Assert.True(fewSum.WorldCount <= cheapSum.WorldCount, $"fewest visits {fewSum.WorldCount}, cheapest {cheapSum.WorldCount}");
        Assert.True(fewSum.GrandTotal >= cheapSum.GrandTotal, $"fewest pays {fewSum.GrandTotal}, cheapest {cheapSum.GrandTotal}");

        // the comparison agrees with the two summaries it was built from
        Assert.Equal(fewSum.GrandTotal - cheapSum.GrandTotal, r.Cmp.ExtraGilForFewestWorlds);
        Assert.Equal(cheapSum.WorldCount - fewSum.WorldCount, r.Cmp.WorldsSaved);
        Assert.True(r.Cmp.ExtraGilForFewestWorlds >= 0);
        Assert.True(r.Cmp.WorldsSaved >= 0);
        Assert.Equal(Flat(RouteSummarizer.Summarize(r.Book, r.Cheap)), Flat(cheapSum));
        Assert.Equal(Flat(RouteSummarizer.Summarize(r.Book, r.Fewest)), Flat(fewSum));

        // applying a suggestion and summarizing gives the same route, and the lines survive the round trip
        foreach (var (lines, sum) in new[] { (r.Cheap, cheapSum), (r.Fewest, fewSum) })
        {
            var state = Fresh(r.Book, lines);
            Assert.Equal(Flat(lines), Flat(state.Lines));
            Assert.Equal(Flat(sum), Flat(state.Summarize()));
            AssertStateConsistent(fx, state);
        }

        // an excluded item shows up only in Skipped
        foreach (var id in fx.Excluded)
        {
            Assert.DoesNotContain(r.Book.Items, i => i.ItemId == id);
            Assert.DoesNotContain(r.Cheap, l => l.ItemId == id);
            Assert.DoesNotContain(r.Fewest, l => l.ItemId == id);
            Assert.DoesNotContain(cheapSum.Unmet, u => u.ItemId == id);
            Assert.DoesNotContain(fewSum.Unmet, u => u.ItemId == id);
            Assert.DoesNotContain(r.Client.Calls, c => c.Ids.Contains(id));
        }

        AssertEdits(fx, r.Book, r.Cheap);
        AssertEdits(fx, r.Book, r.Fewest);

        // deterministic: same input twice, and the same listings, requirements and worlds in another order
        var flat = FlatAll(r);
        Assert.Equal(flat, FlatAll(await RunAsync(fx)));
        Assert.Equal(flat, FlatAll(await RunAsync(fx.Shuffled(1))));
        Assert.Equal(flat, FlatAll(await RunAsync(fx.Shuffled(2))));
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
        // a duplicated world row in the game's own data
        fx.Games.Add(fx.Games.First(w => w.Id == 3));

        fx.Add(101, 3, 300, 5); fx.Add(101, 5, 260, 3); fx.Add(101, 16, 400, 1);

        // (a) the only cheap world also holds one unit priced 1,000 times over
        fx.Add(102, 7, 100, 4); fx.Add(102, 7, 100_000, 1); fx.Add(102, 12, 5000, 3); fx.Add(102, 5, 9000, 2);

        // (b) no single world can supply 99
        fx.Add(103, 2, 50, 40); fx.Add(103, 4, 60, 30); fx.Add(103, 10, 55, 30); fx.Add(103, 15, 70, 25);

        // all worlds together are still 15 short of 75
        fx.Add(104, 1, 10, 20); fx.Add(104, 8, 12, 20); fx.Add(104, 18, 11, 20);

        // (c) 105 has no listings at all
        // (d) excluded, though listed cheaply
        fx.Add(106, 1, 5, 33);

        // (g) HQ and NQ mixed
        fx.Add(107, 1, 100, 10); fx.Add(107, 1, 500, 10, hq: true);
        fx.Add(107, 8, 90, 5); fx.Add(107, 8, 120, 20, hq: true); fx.Add(107, 14, 150, 20);

        // (h) price 0 and quantity 0 listings are ignored
        fx.Add(108, 2, 0, 100); fx.Add(108, 6, 0, 100, hq: true); fx.Add(108, 11, 10, 0); fx.Add(108, 13, 40, 48); fx.Add(108, 17, 45, 60);

        fx.Add(109, 5, 20, 70); fx.Add(109, 9, 18, 10); fx.Add(109, 20, 22, 64);

        // (e) worlds that must be dropped: a game-only world and one unknown to the game, both absurdly cheap
        fx.Add(110, 3, 7, 50); fx.Add(110, 12, 8, 50);
        fx.Add(110, UnlistedWorld, 1, 200); fx.Add(110, UnknownToGame, 1, 200);

        fx.Add(111, 14, 1000, 12); fx.Add(111, 1, 900, 6); fx.Add(111, 2, 950, 6);
        fx.Add(112, 6, 3000, 2); fx.Add(112, 19, 2900, 1);
        fx.Add(113, 1, 5, 10);
        return fx;
    }

    private static Dictionary<uint, List<AllocationLine>> ByItem(IReadOnlyList<AllocationLine> ls)
        => ls.GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.ToList());

    [Fact]
    public async Task Realistic_AllCrossModuleInvariantsHold()
        => await AssertPipelineAsync(Realistic());

    [Fact]
    public async Task Realistic_CheapestLinesAreTheHandWorkedAnswers()
    {
        var r = await RunAsync(Realistic());
        var by = ByItem(r.Cheap);
        var lines = r.Cmp.Cheapest.Worlds.SelectMany(w => w.Lines).ToList();

        // 40@50 + 30@55 + 29@60
        Assert.Equal("103@2x40;103@4x29;103@10x30", Flat(by[103]));
        Assert.Equal(40 * 50 + 30 * 55 + 29 * 60, lines.Where(l => l.ItemId == 103).Sum(l => l.Cost));
        // the 100,000 outlier is never bought: 4@100 on world 7 and one 5000 unit on world 12
        Assert.Equal("102@7x4;102@12x1", Flat(by[102]));
        Assert.Equal(400 + 5000, lines.Where(l => l.ItemId == 102).Sum(l => l.Cost));
        // mixed quality: 5 NQ@90 and 5 HQ@120 on world 8, 10 NQ@100 on world 1
        Assert.Equal("107@1x10;107@8x10", Flat(by[107]));
        Assert.Equal(10 * 100 + 5 * 90 + 5 * 120, lines.Where(l => l.ItemId == 107).Sum(l => l.Cost));
        // price-0 listings are not free units: all 48 from world 13
        Assert.Equal("108@13x48", Flat(by[108]));
        Assert.Equal(48 * 40, lines.Single(l => l.ItemId == 108).Cost);
        // the leaked world 21 and the unknown world 99 never receive a line
        Assert.Equal("110@3x50;110@12x40", Flat(by[110]));
        // the two requirement rows for 112 were merged
        Assert.Equal(2, r.Book.Items.Single(i => i.ItemId == 112).Needed);
        Assert.Equal("112@6x1;112@19x1", Flat(by[112]));
    }

    [Fact]
    public async Task Realistic_UnmetIsExactlyTheItemsNobodyCanFullySupply_InBothModes()
    {
        var r = await RunAsync(Realistic());
        // 104: 60 of 75 exist; 105: nothing exists. 106 is excluded and 113 needs nothing, so neither appears.
        Assert.Equal([(104u, 15), (105u, 9)], r.Cmp.Cheapest.Unmet.Select(u => (u.ItemId, u.Missing)));
        Assert.Equal([(104u, 15), (105u, 9)], r.Cmp.FewestWorlds.Unmet.Select(u => (u.ItemId, u.Missing)));
        Assert.Equal(60, r.Cheap.Where(l => l.ItemId == 104).Sum(l => l.Quantity));
        Assert.Equal(60, r.Fewest.Where(l => l.ItemId == 104).Sum(l => l.Quantity));
        Assert.DoesNotContain(r.Cheap.Concat(r.Fewest), l => l.ItemId == 105);
        Assert.Contains(r.Book.Items, i => i.ItemId == 105 && i.Worlds.Count == 0);
    }

    [Fact]
    public async Task Realistic_ExcludedItemAppearsOnlyInSkipped_AndZeroQuantityItemNowhere()
    {
        var r = await RunAsync(Realistic());
        Assert.Equal([new SkippedItem(106, 33)], r.Book.Skipped);
        Assert.Equal([new SkippedItem(106, 33)], r.ServicePlan.Skipped);
        Assert.DoesNotContain(r.Book.Items, i => i.ItemId is 106 or 113);
        Assert.All(r.Client.Calls, c => Assert.DoesNotContain(106u, c.Ids));
        Assert.All(r.Client.Calls, c => Assert.DoesNotContain(113u, c.Ids));
        Assert.Equal(11, r.Client.Calls[0].Ids.Length);
        Assert.DoesNotContain(r.Cheap.Concat(r.Fewest), l => l.ItemId is 106 or 113);
    }

    [Fact]
    public async Task Realistic_FewestWorldsActuallySavesWorlds_AndComparisonMatchesSummaries()
    {
        var r = await RunAsync(Realistic());
        Assert.True(r.Cmp.WorldsSaved > 0, $"cheapest {r.Cmp.Cheapest.WorldCount} worlds, fewest {r.Cmp.FewestWorlds.WorldCount}");
        Assert.True(r.Cmp.ExtraGilForFewestWorlds > 0, $"extra {r.Cmp.ExtraGilForFewestWorlds}");
        Assert.Equal(r.Cmp.FewestWorlds.GrandTotal - r.Cmp.Cheapest.GrandTotal, r.Cmp.ExtraGilForFewestWorlds);
        Assert.Equal(r.Cmp.Cheapest.Worlds.Count - r.Cmp.FewestWorlds.Worlds.Count, r.Cmp.WorldsSaved);
    }

    [Fact]
    public async Task Realistic_EditingQuantitiesKeepsTheSummaryConsistent()
    {
        var fx = Realistic();
        var r = await RunAsync(fx);
        var state = Fresh(r.Book, r.Cheap);
        var before = state.Summarize();

        // world 4 holds only item 103's 29 units in this plan: zero it and the world drops out of the route
        Assert.Equal(29, state.GetQuantity(103, 4));
        Assert.DoesNotContain(r.Cheap, l => l.WorldId == 4 && l.ItemId != 103);
        state.SetQuantity(103, 4, 0);
        var after = state.Summarize();
        Assert.DoesNotContain(after.Worlds, w => w.WorldId == 4);
        Assert.Equal(before.WorldCount - 1, after.WorldCount);
        Assert.Equal(before.GrandTotal - 29 * 60, after.GrandTotal);
        Assert.Equal(29, after.Unmet.Single(u => u.ItemId == 103).Missing);

        // raising above stock clamps: world 10 sells 30 of 103
        state.SetQuantity(103, 10, 500);
        Assert.Equal(30, state.GetQuantity(103, 10));
        Assert.Equal(30 * 55, state.Cost(103, 10));
        AssertStateConsistent(fx, state);
    }

    [Fact]
    public async Task Realistic_SuggestionsCanBeReappliedAfterEditing()
    {
        var r = await RunAsync(Realistic());
        var state = Fresh(r.Book, r.Fewest);
        var original = Flat(state.Summarize());
        var first = r.Fewest[0];
        state.SetQuantity(first.ItemId, first.WorldId, 0);
        Assert.NotEqual(original, Flat(state.Summarize()));
        state.Apply(r.Fewest);
        Assert.Equal(original, Flat(state.Summarize()));
        state.Apply(r.Cheap);
        Assert.Equal(Flat(r.Cmp.Cheapest), Flat(state.Summarize()));
    }

    [Fact]
    public async Task Realistic_WorldListedInTwoUniversalisScopes_IsNotCountedTwice()
    {
        var fx = Realistic();
        // world 7 appears in two Universalis data centers, so both scope answers carry its listings
        fx.Dcs = fx.Dcs.Select(d => d.Name == "UniB" ? d with { WorldIds = d.WorldIds.Append(7u).ToList() } : d).ToList();
        var r = await RunAsync(fx);
        Assert.Equal(5, r.Book.Items.Single(i => i.ItemId == 102).Worlds.Single(w => w.WorldId == 7).UnitsForSale);
        await AssertPipelineAsync(fx);
    }

    [Fact]
    public async Task Realistic_EveryItemExcluded_MakesNoClientCallAndNoRoute()
    {
        var fx = Realistic();
        foreach (var id in fx.Need().Keys.ToList())
            fx.Excluded.Add(id);
        var r = await RunAsync(fx);
        Assert.Empty(r.Client.Calls);
        Assert.Empty(r.Book.Items);
        Assert.Empty(r.Cheap);
        Assert.Empty(r.Fewest);
        Assert.Equal(0L, r.Cmp.Cheapest.GrandTotal);
        Assert.Equal(0, r.Cmp.WorldsSaved);
        await AssertPipelineAsync(fx);
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
                continue; // no listings at all

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
    public async Task SeededRandomFixtures_HoldEveryInvariant()
    {
        for (var seed = 1; seed <= 150; seed++)
        {
            try
            {
                await AssertPipelineAsync(RandomFx(seed));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new Xunit.Sdk.XunitException($"seed {seed}: {e.GetType().Name}: {e.Message}", e);
            }
        }
    }

    [Fact]
    public void SeededRandomFixtures_AreVariedEnoughToMeanSomething()
    {
        // guards the loop against quietly degenerating into trivial fixtures
        int unmet = 0, excluded = 0, split = 0, zeroPrice = 0, outliers = 0;
        for (var seed = 1; seed <= 150; seed++)
        {
            var fx = RandomFx(seed);
            excluded += fx.Excluded.Count(id => fx.Reqs.Any(r => r.ItemId == id && r.Quantity > 0));
            unmet += fx.Need().Count(kv => fx.Supply(kv.Key) < kv.Value);
            split += fx.Need().Count(kv => fx.Supply(kv.Key) >= kv.Value && Fx.ValidWorlds.All(w => fx.Stock(kv.Key, w) < kv.Value));
            zeroPrice += fx.Pool.Values.SelectMany(x => x).Count(l => l.PricePerUnit == 0);
            outliers += fx.Pool.Values.SelectMany(x => x).Count(l => l.PricePerUnit >= 8000);
        }
        Assert.True(unmet > 100, $"unmet {unmet}");
        Assert.True(excluded > 50, $"excluded {excluded}");
        Assert.True(split > 100, $"split {split}");
        Assert.True(zeroPrice > 20, $"zero price {zeroPrice}");
        Assert.True(outliers > 20, $"outliers {outliers}");
    }
}
