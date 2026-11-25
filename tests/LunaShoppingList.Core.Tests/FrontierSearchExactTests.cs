using LunaShoppingList.Core;
using Xunit.Abstractions;

namespace LunaShoppingList.Core.Tests;

/// <summary>
/// <see cref="FrontierSearch.ExactFrontier"/> called directly (not through <see cref="PlanFrontier"/>), against
/// <see cref="FrontierOracle"/>. Written 2026-11-25 for task exact-t2. Every search here starts from an empty incumbent
/// (<c>long.MaxValue</c> everywhere), so a pass means the search found the optimum on its own, not that a heuristic seed already had it.
/// </summary>
public class FrontierSearchExactTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private const long Budget = 20_000_000_000;

    private readonly ITestOutputHelper _output;

    public FrontierSearchExactTests(ITestOutputHelper output) => _output = output;

    private static WorldOffer W(uint id, params (int Price, int Qty)[] steps)
        => new(id, "W" + id, steps
            .Select((s, n) => new PriceStep(s.Price, s.Qty, false, T0.AddMinutes(-n)))
            .OrderBy(p => p.UnitPrice).ThenByDescending(p => p.LastReview).ToList());

    private static ItemOffers I(uint id, int needed, params WorldOffer[] worlds) => new(id, needed, worlds.ToList());

    private static OfferBook Book(params ItemOffers[] items)
        => new(items.OrderBy(i => i.ItemId).ToList(), new List<SkippedItem>());

    private static int WorldsIn(OfferBook book) => book.Items.SelectMany(i => i.Worlds).Select(w => w.WorldId).Distinct().Count();

    private sealed record Outcome(int Proven, long[] Best, List<FrontierSearch.Candidate> Records, long Work, bool Cut);

    private static Outcome Run(OfferBook book, long budget = Budget, long[]? seed = null)
    {
        var search = new FrontierSearch(book, budget);
        var best = seed is null ? Enumerable.Repeat(long.MaxValue, WorldsIn(book) + 1).ToArray() : (long[])seed.Clone();
        var records = new List<FrontierSearch.Candidate>();
        var proven = search.ExactFrontier(best, records.Add);
        return new Outcome(proven, best, records, search.Work, search.Cut);
    }

    private static string Show(long[] values) => string.Join(",", values.Select(v => v == long.MaxValue ? "-" : v.ToString()));

    // ---- the oracle itself ----

    /// <summary>The oracle's cost for a world set equals a brute force over every subset of the stacks (the 18-line pattern of PlanFrontierTests).</summary>
    [Fact]
    public void Oracle_MatchesTheBruteForce_OnRandomSets()
    {
        var rng = new Random(20261125);
        var compared = 0;
        var feasible = 0;
        for (var b = 0; b < 12; b++)
        {
            var worlds = 11 + b % 6;
            var book = b % 2 == 0 ? FrontierOracle.MediumBook(rng, worlds) : FrontierOracle.DenseBook(rng, worlds);
            var ids = book.Items.SelectMany(i => i.Worlds).Select(w => w.WorldId).Distinct().OrderBy(x => x).ToList();
            for (var t = 0; t < 25; t++)
            {
                var size = rng.Next(1, ids.Count + 1);
                var set = ids.OrderBy(_ => rng.Next()).Take(size).ToList();
                var expected = BruteForceCost(book, set);
                var got = FrontierOracle.CostOf(book, set);
                Assert.True((expected ?? long.MaxValue) == got, $"book {b} set {string.Join(",", set)}: brute force {expected?.ToString() ?? "infeasible"}, oracle {(got == long.MaxValue ? "infeasible" : got.ToString())}");
                compared++;
                if (expected is not null)
                    feasible++;
            }
        }
        Assert.Equal(300, compared);
        Assert.True(feasible >= 60, $"only {feasible} of 300 sets were feasible, the comparison is too weak");
    }

    private static List<PriceStep> StacksOn(ItemOffers item, IEnumerable<uint> worldIds)
    {
        var set = worldIds.ToHashSet();
        return item.Worlds.GroupBy(w => w.WorldId).Select(g => g.First())
            .Where(w => set.Contains(w.WorldId)).SelectMany(w => w.Steps).ToList();
    }

    private static long? BruteForceCost(OfferBook book, IReadOnlyCollection<uint> worldIds)
    {
        long total = 0;
        foreach (var item in book.Items)
        {
            var all = StacksOn(item, item.Worlds.Select(w => w.WorldId));
            long supplyAll = all.Sum(s => (long)s.Quantity);
            var target = Math.Min(item.Needed, supplyAll);
            if (target <= 0)
                continue;
            var subsets = StackBruteForce.All(StacksOn(item, worldIds));
            var best = StackBruteForce.Cover(subsets, target);
            if (best.Units < target)
                return null;
            total += best.Cost;
        }
        return total;
    }

    // ---- the search against the oracle ----

    /// <summary>
    /// Every world count's optimum, from nothing: 60 Medium and 60 Dense books at 11 to 16 worlds (10 per world count and family) and 10 of
    /// each at 20 worlds. The search must finish (return the world count) and its totals must equal the oracle's at every k.
    /// </summary>
    [Theory]
    [InlineData("Medium", 11)]
    [InlineData("Medium", 12)]
    [InlineData("Medium", 13)]
    [InlineData("Medium", 14)]
    [InlineData("Medium", 15)]
    [InlineData("Medium", 16)]
    [InlineData("Medium", 20)]
    [InlineData("Dense", 11)]
    [InlineData("Dense", 12)]
    [InlineData("Dense", 13)]
    [InlineData("Dense", 14)]
    [InlineData("Dense", 15)]
    [InlineData("Dense", 16)]
    [InlineData("Dense", 20)]
    public void ExactFrontier_EveryWorldCountEqualsTheOracle(string family, int worlds)
    {
        var rng = new Random((family == "Medium" ? 20261124 : 5150) + worlds);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        long workMax = 0, workSum = 0;
        for (var b = 0; b < 10; b++)
        {
            var book = family == "Medium" ? FrontierOracle.MediumBook(rng, worlds) : FrontierOracle.DenseBook(rng, worlds);
            var n = WorldsIn(book);
            var exact = FrontierOracle.ExactAtMost(book);
            var run = Run(book);
            Assert.True(run.Proven == n, $"{family} {worlds} book {b}: proven {run.Proven} of {n} (cut {run.Cut})");
            for (var k = 1; k <= n; k++)
                Assert.True(run.Best[k] == exact[k], $"{family} {worlds} book {b} k={k}: search {Show(run.Best)} oracle {Show(exact)}");
            Assert.False(run.Cut);
            workMax = Math.Max(workMax, run.Work);
            workSum += run.Work;
        }
        _output.WriteLine($"{family} {worlds} worlds, 10 books: work mean {workSum / 10:N0}, max {workMax:N0} units; search+oracle {watch.ElapsedMilliseconds} ms");
    }

    /// <summary>The recorded candidates never beat the optimum, spread over every world count, and carry real lines (the cheapest-at-all-worlds plan is the caller's seed, so it is not recorded).</summary>
    [Theory]
    [InlineData("Medium", 14)]
    [InlineData("Dense", 14)]
    public void ExactFrontier_RecordedCandidates_AreValidPlansAtOrAboveTheOracle(string family, int worlds)
    {
        var rng = new Random(77 + worlds);
        var recordedTotal = 0;
        for (var b = 0; b < 10; b++)
        {
            var book = family == "Medium" ? FrontierOracle.MediumBook(rng, worlds) : FrontierOracle.DenseBook(rng, worlds);
            var exact = FrontierOracle.ExactAtMost(book);
            var n = WorldsIn(book);
            var run = Run(book);
            foreach (var c in run.Records)
            {
                recordedTotal++;
                Assert.InRange(c.Worlds, 1, n);
                Assert.True(c.Total >= exact[c.Worlds], $"book {b}: candidate {c.Total} at {c.Worlds} worlds is below the optimum {exact[c.Worlds]}");
                Assert.Equal(c.Worlds, c.Lines.Select(l => l.WorldId).Distinct().Count());
                Assert.NotEmpty(c.Lines);
            }
            for (var k = 1; k <= n; k++)
            {
                var bestRecorded = run.Records.Where(c => c.Worlds <= k).Select(c => c.Total).DefaultIfEmpty(long.MaxValue).Min();
                Assert.True(bestRecorded == exact[k] || exact[k] == exact[n] || (exact[k] == long.MaxValue && bestRecorded == long.MaxValue),
                    $"book {b} k={k}: best recorded {(bestRecorded == long.MaxValue ? "none" : bestRecorded.ToString())}, optimum {exact[k]}");
            }
        }
        Assert.True(recordedTotal > 0);
    }

    /// <summary>A seed that is already optimal means nothing is recorded: only strict improvements are.</summary>
    [Fact]
    public void ExactFrontier_SeededWithTheOptimum_RecordsNothing_AndChangesNothing()
    {
        var rng = new Random(4242);
        for (var b = 0; b < 10; b++)
        {
            var book = FrontierOracle.DenseBook(rng, 13);
            var exact = FrontierOracle.ExactAtMost(book);
            var run = Run(book, seed: exact);
            Assert.Equal(WorldsIn(book), run.Proven);
            Assert.Empty(run.Records);
            Assert.Equal(exact, run.Best);
        }
    }

    /// <summary>
    /// A heuristic that overpays by one gil at every world count it reached (a valid, monotone seed): the search must still lower every one
    /// to the optimum. With a finite incumbent from the start the lazy exact superset bound is active from the first node, and a stop rule that
    /// treats "costs one gil more than the all-worlds cost" as proven would leave world counts above their optimum.
    /// </summary>
    [Theory]
    [InlineData("Medium", 13)]
    [InlineData("Dense", 13)]
    [InlineData("Dense", 16)]
    public void ExactFrontier_SeededWithAnOverpayingHeuristic_ReachesTheOptimumAtEveryWorldCount(string family, int worlds)
    {
        var rng = new Random(606 + worlds);
        for (var b = 0; b < 10; b++)
        {
            var book = family == "Medium" ? FrontierOracle.MediumBook(rng, worlds) : FrontierOracle.DenseBook(rng, worlds);
            var exact = FrontierOracle.ExactAtMost(book);
            var seed = exact.Select(v => v == long.MaxValue ? v : v + 1).ToArray();
            var run = Run(book, seed: seed);
            Assert.Equal(WorldsIn(book), run.Proven);
            Assert.True(exact.Skip(1).SequenceEqual(run.Best.Skip(1)), $"{family} {worlds} book {b}: search {Show(run.Best)} oracle {Show(exact)}");
        }
    }

    /// <summary>
    /// Written for task exact-t4 to kill mutant M11 (the stops at the all-worlds cost widened to "at most one gil above it"). The all-worlds
    /// plan is the global minimum, but it may visit MORE worlds than an equally cheap plan: item 1 costs 5 in worlds 100 and 101 alike, item 2
    /// is only sold in one of them, so the whole-book cheapest plan may buy item 1 from the other world and use two worlds, while one world
    /// does the same job for the same total. Seeded one gil above the optimum (a heuristic that took the two-world plan), the one-world
    /// optimum must still be found. Which world the all-worlds cover picks on a tie is the solver's business, so the book is built both ways
    /// round and the pair is checked together: in at least one of them the all-worlds plan uses two worlds, which is the case that matters.
    /// </summary>
    [Theory]
    [InlineData(101u, 100u)]
    [InlineData(100u, 101u)]
    public void ExactFrontier_TieAtFewerWorldsThanTheAllWorldsPlan_IsFoundNotSkipped(uint onlyItem2, uint other)
    {
        var book = Book(
            I(1, 1, W(100, (5, 1)), W(101, (5, 1)), W(102, (9, 1))),
            I(2, 1, W(onlyItem2, (3, 1))));
        Assert.Equal(3, WorldsIn(book));
        var exact = FrontierOracle.ExactAtMost(book);
        Assert.Equal(8, exact[1]); // the tie: one world (the one that sells item 2) already costs the all-worlds total
        Assert.Equal(8, exact[3]);
        var seed = exact.Select(v => v == long.MaxValue ? v : v + 1).ToArray();
        var run = Run(book, seed: seed);
        Assert.Equal(3, run.Proven);
        Assert.True(exact.Skip(1).SequenceEqual(run.Best.Skip(1)), $"other world {other}: search {Show(run.Best)} oracle {Show(exact)}");
    }

    /// <summary>
    /// Written for task exact-t4 (from a randomized search over 20,000 tiny books that found 742 books on which a stop rule "best[k-1] at most
    /// one gil above the all-worlds cost counts as proven" is wrong). Here one world costs 9, two worlds cost 8, and the all-worlds plan of 8
    /// uses three worlds, so with no seed at all the search must not take 9 at two worlds as already settled. Kills M11 with either of its two
    /// stops widened alone (the seeded test above kills only the second). The expected arrays are the oracle's, written out.
    /// </summary>
    [Fact]
    public void ExactFrontier_OneGilAboveTheAllWorldsCost_IsNotTreatedAsProven()
    {
        var book = Book(
            I(1, 2, W(101, (2, 1)), W(102, (2, 2)), W(103, (2, 1), (3, 1)), W(104, (4, 1)), W(105, (4, 1))),
            I(2, 3, W(100, (2, 1)), W(101, (2, 2)), W(102, (3, 1), (3, 1)), W(103, (2, 1), (1, 2)), W(104, (3, 2), (2, 2)), W(105, (1, 2), (4, 1))));
        Assert.Equal(6, WorldsIn(book));
        var exact = FrontierOracle.ExactAtMost(book);
        Assert.Equal(new[] { 9L, 8, 8, 8, 8, 8 }, exact.Skip(1).ToArray());
        var run = Run(book);
        Assert.Equal(6, run.Proven);
        Assert.True(exact.Skip(1).SequenceEqual(run.Best.Skip(1)), $"search {Show(run.Best)} oracle {Show(exact)}");
    }

    [Fact]
    public void ExactFrontier_SameBookTwice_SameBestSameRecordsSameWork()
    {
        var book = FrontierOracle.DenseBook(new Random(9), 16);
        var a = Run(book);
        var b = Run(book);
        Assert.Equal(a.Best, b.Best);
        Assert.Equal(a.Work, b.Work);
        Assert.Equal(a.Proven, b.Proven);
        Assert.Equal(a.Records.Select(c => (c.Total, c.Worlds, string.Join(";", c.Lines))), b.Records.Select(c => (c.Total, c.Worlds, string.Join(";", c.Lines))));
        Assert.True(a.Work > 0);
    }

    // ---- limits and gates ----

    [Fact]
    public void ExactFrontier_MoreThan64Worlds_ReturnsZeroWithoutWork()
    {
        var items = Enumerable.Range(0, 65).Select(w => I((uint)(w + 1), 1, W((uint)(100 + w), (w + 1, 1)))).ToArray();
        var book = Book(items);
        Assert.Equal(65, WorldsIn(book));
        var run = Run(book);
        Assert.Equal(0, run.Proven);
        Assert.Equal(0, run.Work);
        Assert.Empty(run.Records);
        Assert.All(run.Best, v => Assert.Equal(long.MaxValue, v));
    }

    [Fact]
    public void ExactFrontier_Exactly64Worlds_IsSearchedAndFinishes()
    {
        // 64 worlds, one item that every world sells one unit of, needing 1: the cheapest world alone is optimal at every k.
        var book = Book(I(1, 1, Enumerable.Range(0, 64).Select(w => W((uint)(100 + w), (64 - w, 1))).ToArray()));
        Assert.Equal(64, WorldsIn(book));
        var run = Run(book);
        Assert.Equal(64, run.Proven);
        for (var k = 1; k <= 64; k++)
            Assert.Equal(1, run.Best[k]);
    }

    [Fact]
    public void ExactFrontier_ItemPastTheSizeGuard_ReturnsZeroAtOnce()
    {
        // The book of PlanFrontierTests.Build_ItemPastTheSizeGuard_..., given 11 more worlds so it is a book of the size the phase is for:
        // item 1 needs 600,001 and sells stacks of 300,000 and 450,000, which pass the exact table's size guard (greedy fallback).
        var items = new List<ItemOffers>
        {
            I(1, 600_001, W(1, (1, 300_000), (2, 300_000), (2, 200_001), (2, 100_000), (1000, 450_000))),
        };
        for (var w = 2; w <= 12; w++)
            items.Add(I((uint)w, 1, W((uint)w, (5, 1)), W(1, (6, 1))));
        var book = Book(items.ToArray());
        Assert.Equal(12, WorldsIn(book));
        var run = Run(book);
        Assert.Equal(0, run.Proven);
        Assert.Empty(run.Records);
        Assert.All(run.Best, v => Assert.Equal(long.MaxValue, v));
        Assert.True(run.Work < 500, $"the gate must stop the phase before any solve, work was {run.Work}");
    }

    /// <summary>An item with a huge supply but a small need is not past the guard: the gate looks at the stacks that decide the table, like the solver.</summary>
    [Fact]
    public void ExactFrontier_ShortItemWithHugeStacks_IsNotGated()
    {
        // Item 1 needs 600,001 but the whole book sells only 450,000 + 100,000 of it, so its target is its supply and no table is ever built.
        var items = new List<ItemOffers>
        {
            I(1, 600_001, W(1, (3, 450_000)), W(2, (4, 100_000))),
        };
        for (var w = 3; w <= 12; w++)
            items.Add(I((uint)w, 1, W((uint)w, (5, 1)), W(1, (6, 1))));
        var run = Run(Book(items.ToArray()));
        Assert.Equal(12, run.Proven);
    }

    /// <summary>
    /// A short item on two of twelve worlds pins both (R6): no set without them is feasible, and the search never records one, nor
    /// proves anything for fewer worlds than the pinned count.
    /// </summary>
    [Fact]
    public void ExactFrontier_PinnedWorlds_AreInEveryRecordedSet_AndSmallerCountsStayUnreachable()
    {
        // Item 1 needs 10 but worlds 100 and 101 sell 5 each (the whole book): both are required. The other items are everywhere else.
        var items = new List<ItemOffers>
        {
            I(1, 10, W(100, (7, 5)), W(101, (9, 5))),
        };
        for (var i = 0; i < 4; i++)
            {
            var item = i;
            items.Add(I((uint)(10 + i), 3 + i, Enumerable.Range(0, 10).Where(w => (w + item * 3) % 10 < 7)
                .Select(w => W((uint)(200 + w), (3 + (w * (item + 3)) % 11, 2), (20 + w, 5))).ToArray()));
        }
        var book = Book(items.ToArray());
        Assert.Equal(12, WorldsIn(book));
        var exact = FrontierOracle.ExactAtMost(book);
        Assert.Equal(long.MaxValue, exact[1]);
        var run = Run(book);
        _output.WriteLine($"pinned book work {run.Work}");
        // Measured 2026-11-25: 384,323 units with the pins, 552,635 with the pinning rule switched off (same answers): the rule is a speed rule, and this is its pin.
        Assert.True(run.Work < 470_000, $"work {run.Work}: pinned worlds are not being put in at the root");
        Assert.Equal(12, run.Proven);
        Assert.Equal(long.MaxValue, run.Best[1]);
        Assert.NotEmpty(run.Records);
        foreach (var c in run.Records)
        {
            var ids = c.Lines.Select(l => l.WorldId).ToHashSet();
            Assert.Contains(100u, ids);
            Assert.Contains(101u, ids);
            Assert.True(c.Worlds >= 2);
        }
        for (var k = 1; k <= 12; k++)
            Assert.Equal(exact[k], run.Best[k]);
    }

    // ---- the budget ----

    [Fact]
    public void ExactFrontier_ZeroBudget_ReturnsZeroAndChangesNothing()
    {
        var book = FrontierOracle.MediumBook(new Random(3), 14);
        var run = Run(book, budget: 0);
        Assert.Equal(0, run.Proven);
        Assert.True(run.Cut);
        Assert.Empty(run.Records);
        Assert.All(run.Best, v => Assert.Equal(long.MaxValue, v));
    }

    /// <summary>
    /// With a budget of a third of what the full run needs the search stops part way: what it returns is a prefix of proven world counts
    /// (equal to the oracle at every k up to it), everything above is merely feasible (never below the oracle), and the overrun past the
    /// budget is small.
    /// </summary>
    [Theory]
    [InlineData("Dense", 16, 31)]
    [InlineData("Dense", 20, 32)]
    public void ExactFrontier_PartialBudget_ProvesAPrefixExactly(string family, int worlds, int seed)
    {
        var book = FrontierOracle.DenseBook(new Random(seed), worlds);
        var n = WorldsIn(book);
        var exact = FrontierOracle.ExactAtMost(book);
        var full = Run(book);
        Assert.Equal(n, full.Proven);
        var budget = full.Work / 3;
        var run = Run(book, budget);
        _output.WriteLine($"{family} {worlds} worlds: full work {full.Work:N0}, budget {budget:N0}, proven {run.Proven} of {n}, work {run.Work:N0}");
        Assert.True(run.Cut);
        Assert.InRange(run.Proven, 0, n - 1);
        for (var k = 1; k <= run.Proven; k++)
            Assert.True(run.Best[k] == exact[k], $"k={k} within the proven prefix {run.Proven}: {Show(run.Best)} vs {Show(exact)}");
        for (var k = run.Proven + 1; k <= n; k++)
            Assert.True(run.Best[k] >= exact[k], $"k={k} below the oracle: {Show(run.Best)} vs {Show(exact)}");
        Assert.True(run.Work < budget + budget / 2 + 100_000, $"work {run.Work} overran the budget {budget} by more than one item solve");
    }

    /// <summary>
    /// Written for task exact-t4 to kill mutant M14 (the exact phase's spend counted against the heuristic phases' shares again). The
    /// phase has a budget of its own, so after it the improvement phase must still have its whole share: here the constructor budget is
    /// 20,000 units and the exact phase spends several times that, which without the shift leaves every later <see cref="FrontierSearch.Phase"/>
    /// already over its limit.
    /// </summary>
    [Fact]
    public void ExactFrontier_OwnBudgetSpend_IsNotChargedToTheHeuristicPhases()
    {
        var book = FrontierOracle.DenseBook(new Random(9), 16);
        var search = new FrontierSearch(book, 20_000);
        var best = Enumerable.Repeat(long.MaxValue, WorldsIn(book) + 1).ToArray();
        var proven = search.ExactFrontier(best, _ => { }, 10_000_000_000);
        Assert.Equal(16, proven);
        Assert.True(search.Work > 5 * 20_000, $"the exact phase spent only {search.Work} units, the test does not exercise the shift");
        Assert.False(search.Cut);
        search.Phase(40);
        Assert.True(search.Continue(), "a 40 percent phase after the exact phase starts out of budget");
        search.Phase(100);
        Assert.True(search.Continue(), "a 100 percent phase after the exact phase starts out of budget");
        Assert.False(search.Cut);
    }

    // ---- books shaped like all-data-center lists (no oracle: items sell more than 16 stacks) ----

    [Fact]
    public void ExactFrontier_AllDcBook32Worlds_Finishes_WithAMonotoneFrontier()
    {
        var book = FrontierOracle.AllDcBook(101, new[] { 4, 4, 4, 4, 4, 4, 4, 4 }, 10);
        var n = WorldsIn(book);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var run = Run(book);
        _output.WriteLine($"AllDc 32 worlds, 10 items: proven {run.Proven} of {n}, work {run.Work:N0} units, {watch.ElapsedMilliseconds} ms, {run.Records.Count} candidates recorded");
        Assert.Equal(n, run.Proven);
        Assert.False(run.Cut);
        for (var k = 2; k <= n; k++)
            Assert.True(run.Best[k] <= run.Best[k - 1]);
        Assert.NotEqual(long.MaxValue, run.Best[n]);
    }

    // ---- speed rules R7 to R12 (task exact-t7): each is pinned here by its work, and the unsafe variants by the oracle rows ----

    /// <summary>
    /// Written 2026-11-25 for task exact-t7. The 40-world all-data-center book (seed 102, 8 x 5 worlds, 20 items) with no seed finishes
    /// within a work bound that only holds with the speed rules R7 to R12 of <c>FrontierExact.cs</c> in place: switching any one of them
    /// off pushes the work above it (measured per rule in pass exact-t7), except R10's skip, which
    /// costs only about 2 percent here and is pinned by <c>PlanFrontierExactTests.SpeedRules_FiftySixWorldBook_ProvesMoreWorldCountsAtOneBillion</c>.
    /// Before the rules the same run took 3,550,333,464 units.
    /// </summary>
    [Fact]
    public void ExactFrontier_AllDcBook40Worlds_FinishesWithinTheSpeedRulesWorkBound()
    {
        var book = FrontierOracle.AllDcBook(102, new[] { 5, 5, 5, 5, 5, 5, 5, 5 }, 20);
        var n = WorldsIn(book);
        var run = Run(book);
        _output.WriteLine($"AllDc 40 worlds, 20 items: proven {run.Proven} of {n}, work {run.Work:N0} units");
        Assert.Equal(40, n);
        Assert.Equal(n, run.Proven);
        Assert.False(run.Cut);
        Assert.True(run.Work <= SpeedRulesWorkBound, $"work {run.Work:N0} is above {SpeedRulesWorkBound:N0}: a speed rule (R7 to R12) is not doing its job");
    }

    private const long SpeedRulesWorkBound = 505_000_000;

    /// <summary>
    /// Written 2026-11-25 for task exact-t7: the speed rules lowered the pinned book's work so far that the work bound of
    /// <see cref="ExactFrontier_PinnedWorlds_AreInEveryRecordedSet_AndSmallerCountsStayUnreachable"/> (470,000) no longer tells R6 apart
    /// (measured 244,812 units with the pins, 335,568 with the pinning switched off, both under it). This test restores the pin on the
    /// same book; the older test is left as it was.
    /// </summary>
    [Fact]
    public void ExactFrontier_PinnedWorlds_WorkBoundWithTheSpeedRules()
    {
        var items = new List<ItemOffers>
        {
            I(1, 10, W(100, (7, 5)), W(101, (9, 5))),
        };
        for (var i = 0; i < 4; i++)
        {
            var item = i;
            items.Add(I((uint)(10 + i), 3 + i, Enumerable.Range(0, 10).Where(w => (w + item * 3) % 10 < 7)
                .Select(w => W((uint)(200 + w), (3 + (w * (item + 3)) % 11, 2), (20 + w, 5))).ToArray()));
        }
        var run = Run(Book(items.ToArray()));
        _output.WriteLine($"pinned book work {run.Work}");
        Assert.Equal(12, run.Proven);
        Assert.True(run.Work < 290_000, $"work {run.Work}: pinned worlds are not being put in at the root (R6)");
    }

    /// <summary>
    /// Written 2026-11-25 for task exact-t7: 20,000 tiny random books (3 to 8 worlds, 2 to 5 items, at most 5 worlds and 15 stacks per
    /// item, prices 1 to 6), no seed: every world count equals the oracle and every count is proven. Green on the code before exact-t7
    /// too (a regression net, not a red-first test); its reds come from mutations of the speed rules' safety conditions, recorded in the dev notes.
    /// </summary>
    [Fact]
    public void ExactFrontier_TinyRandomBooks_EveryWorldCountEqualsTheOracle()
    {
        var rng = new Random(777);
        int books = 0, wrong = 0, notProven = 0;
        var first = "";
        for (var b = 0; b < 20_000; b++)
        {
            var book = TinyBook(rng);
            var exact = FrontierOracle.ExactAtMost(book);
            var n = exact.Length - 1;
            if (n == 0)
                continue;
            books++;
            var run = Run(book);
            if (run.Proven != n)
                notProven++;
            for (var k = 1; k <= n; k++)
                if (run.Best[k] != exact[k])
                {
                    wrong++;
                    if (first.Length == 0)
                        first = $"book {b}, k={k}: search {Show(run.Best)} oracle {Show(exact)}";
                }
        }
        _output.WriteLine($"{books} books, {wrong} wrong world counts, {notProven} not proven");
        Assert.True(books > 19_000, $"only {books} books had worlds");
        Assert.True(wrong == 0 && notProven == 0, $"{wrong} wrong world counts, {notProven} books not proven; first: {first}");
    }

    private static OfferBook TinyBook(Random rng)
    {
        var worlds = rng.Next(3, 9);
        var items = new List<ItemOffers>();
        var count = rng.Next(2, 6);
        for (var i = 0; i < count; i++)
        {
            var on = Enumerable.Range(0, worlds).Where(_ => rng.Next(3) > 0).Take(5).ToList();
            if (on.Count == 0)
                on.Add(rng.Next(worlds));
            var offers = on.Select(w => new WorldOffer((uint)(300 + w), "W", Enumerable.Range(0, rng.Next(1, 4))
                .Select(s => new PriceStep(rng.Next(1, 7), rng.Next(1, 6), false, T0.AddMinutes(-s)))
                .OrderBy(p => p.UnitPrice).ThenByDescending(p => p.LastReview).ToList())).ToList();
            items.Add(new ItemOffers((uint)(1 + i), rng.Next(1, 9), offers));
        }
        return new OfferBook(items, new List<SkippedItem>());
    }
}
