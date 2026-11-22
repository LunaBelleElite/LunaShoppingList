using System.Diagnostics;
using LunaShoppingList.Core;
using Xunit.Abstractions;

namespace LunaShoppingList.Core.Tests;

public class PlanFrontierTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ITestOutputHelper _output;

    public PlanFrontierTests(ITestOutputHelper output) => _output = output;

    // ---- builders ----

    private static WorldOffer W(uint id, params (int Price, int Qty)[] steps)
        => new(id, "W" + id, steps
            .Select((s, n) => new PriceStep(s.Price, s.Qty, false, T0.AddMinutes(-n)))
            .OrderBy(p => p.UnitPrice).ThenByDescending(p => p.LastReview).ToList());

    private static ItemOffers I(uint id, int needed, params WorldOffer[] worlds)
        => new(id, needed, worlds.ToList());

    private static OfferBook Book(params ItemOffers[] items)
        => new(items.OrderBy(i => i.ItemId).ToList(), new List<SkippedItem>());

    private static string Render(IEnumerable<AllocationLine> lines)
        => string.Join(";", lines.Select(l => $"{l.ItemId}@{l.WorldId}x{l.Quantity}"));

    private static string Render(PlanOption o)
        => $"{o.WorldCount}w {o.Total}g +{o.ExtraGil} +{o.ExtraPercent:R}% [{Render(o.Lines)}]";

    private static string Render(IEnumerable<PlanOption> options) => string.Join(" | ", options.Select(Render));

    private static PlanOption Opt(int worlds, long total, long extra)
        => new(worlds, total, extra, 0, new List<AllocationLine>());

    // ---- argument and edge cases ----

    [Fact]
    public void Build_NullBook_Throws()
        => Assert.Throws<ArgumentNullException>(() => PlanFrontier.Build(null!));

    [Fact]
    public void Pick_NullOptions_Throws()
        => Assert.Throws<ArgumentNullException>(() => PlanFrontier.Pick(null!, 10));

    [Fact]
    public void Build_EmptyBook_GivesOneEmptyPlan()
    {
        var options = PlanFrontier.Build(Book());
        var only = Assert.Single(options);
        Assert.Equal(0, only.WorldCount);
        Assert.Equal(0, only.Total);
        Assert.Equal(0, only.ExtraGil);
        Assert.Equal(0.0, only.ExtraPercent);
        Assert.Empty(only.Lines);
    }

    [Fact]
    public void Build_OnlyItemsWithNoOffers_GivesOneEmptyPlan()
    {
        var options = PlanFrontier.Build(Book(I(1, 5), I(2, 3)));
        var only = Assert.Single(options);
        Assert.Equal(0, only.WorldCount);
        Assert.Empty(only.Lines);
    }

    [Fact]
    public void Build_ItemNoWorldSupplies_DoesNotBlockTheRest()
    {
        // Item 2 has no offers; items 1 and 3 still get the split / merged options.
        var book = Book(
            I(1, 2, W(1, (10, 5)), W(2, (15, 5))),
            I(2, 4),
            I(3, 2, W(1, (15, 5)), W(2, (10, 5))));
        Assert.Equal("2w 40g +0 +0% [1@1x2;3@2x2] | 1w 50g +10 +25% [1@1x2;3@1x2]", Render(PlanFrontier.Build(book)));
    }

    [Fact]
    public void Build_OneWorldOnly_GivesOneOption()
    {
        var book = Book(I(1, 3, W(7, (5, 2), (9, 4))), I(2, 1, W(7, (100, 1))));
        Assert.Equal("1w 119g +0 +0% [1@7x3;2@7x1]", Render(PlanFrontier.Build(book)));
    }

    // ---- hand-worked books ----

    [Fact]
    public void Build_OneWorldCoversEverythingCheapest_SingleOption()
    {
        var book = Book(
            I(1, 5, W(1, (10, 5)), W(2, (20, 9))),
            I(2, 5, W(1, (10, 5)), W(2, (11, 9))));
        Assert.Equal("1w 100g +0 +0% [1@1x5;2@1x5]", Render(PlanFrontier.Build(book)));
    }

    [Fact]
    public void Build_TwoWorldsSplitItems_OneWorldOptionAtItsExtra()
    {
        // Cheapest: item 1 on world 1, item 2 on world 2 (40). One world: either world costs 50; the tie goes to the lower id.
        var book = Book(
            I(1, 2, W(1, (10, 5)), W(2, (15, 5))),
            I(2, 2, W(1, (15, 5)), W(2, (10, 5))));
        Assert.Equal("2w 40g +0 +0% [1@1x2;2@2x2] | 1w 50g +10 +25% [1@1x2;2@1x2]", Render(PlanFrontier.Build(book)));
    }

    [Fact]
    public void Build_ItemOnlyOneWorldSupplies_ThatWorldIsKept()
    {
        // Item 1 exists only on world 1, so world 1 can never be dropped; dropping world 2 moves item 2 to world 1 (+3).
        var book = Book(
            I(1, 1, W(1, (10, 1))),
            I(2, 1, W(1, (8, 3)), W(2, (5, 3))));
        Assert.Equal("2w 15g +0 +0% [1@1x1;2@2x1] | 1w 18g +3 +20% [1@1x1;2@1x1]", Render(PlanFrontier.Build(book)));
    }

    [Fact]
    public void Build_ItemMustBeSplit_OneWorldOptionComesFromTheOnlyWorldWithEnough()
    {
        // Cheapest splits 6+4 over worlds 1 and 2 (108); neither can be dropped (each alone is short), so the one-world option
        // is world 3, the only world with all 10 (200) -- not reachable by removing a world from the cheapest plan.
        var book = Book(I(1, 10, W(1, (10, 6)), W(2, (12, 6)), W(3, (20, 10))));
        var options = PlanFrontier.Build(book);
        Assert.Equal("2w 108g +0 +0% [1@1x6;1@2x4] | 1w 200g +92 +85.18518518518519% [1@3x10]", Render(options));
    }

    [Fact]
    public void Build_SplitWithinTheOptionWhenNoSingleWorldHasEnough()
    {
        // Need 8 of item 1; worlds 1, 2 have 5 each. Item 2 is on world 3 only at 1 gil, and on world 2 at 4 gil.
        // Cheapest: item 1 from 1 and 2 (5x1 + 3x2 = 11), item 2 on world 3 (1): 3 worlds, 12. Dropping world 3 puts item 2 on
        // world 2 (4): 2 worlds, 15. No single world has 8 of item 1.
        var book = Book(
            I(1, 8, W(1, (1, 5)), W(2, (2, 5))),
            I(2, 1, W(2, (4, 1)), W(3, (1, 1))));
        Assert.Equal("3w 12g +0 +0% [1@1x5;1@2x3;2@3x1] | 2w 15g +3 +25% [1@1x5;1@2x3;2@2x1]", Render(PlanFrontier.Build(book)));
    }

    [Fact]
    public void Build_InfeasibleRemoval_NoFewerWorldOption()
    {
        // 10 needed, 5 on each of two worlds: neither can be dropped.
        var book = Book(I(1, 10, W(1, (1, 5)), W(2, (1, 5))));
        Assert.Equal("2w 10g +0 +0% [1@1x5;1@2x5]", Render(PlanFrontier.Build(book)));
    }

    [Fact]
    public void Build_ShortItem_KeepsTheCheapestPlansUnitsInEveryOption()
    {
        // Item 1 needs 10 but only 6 exist (3 per world): every option must still carry all 6, so both worlds stay pinned and
        // there is no option with fewer worlds.
        var book = Book(
            I(1, 10, W(1, (5, 3)), W(2, (5, 3))),
            I(2, 1, W(1, (9, 1)), W(2, (3, 1))));
        Assert.Equal("2w 33g +0 +0% [1@1x3;1@2x3;2@2x1]", Render(PlanFrontier.Build(book)));
    }

    [Fact]
    public void Build_ExpensiveOutlier_EliminationDropsTheCheapWorldNotTheOutlierOne()
    {
        // Cheapest: item 1 on world 2, item 2 on world 3, item 3 on world 1 (30, 3 worlds).
        // Dropping world 1 costs +50, world 2 costs +990 (the outlier on world 1), world 3 costs +1: drop world 3 -> 31.
        // Then only world 1 alone covers everything: 1021.
        var book = Book(
            I(1, 1, W(1, (1000, 1)), W(2, (10, 1))),
            I(2, 1, W(1, (11, 1)), W(3, (10, 1))),
            I(3, 1, W(1, (10, 1)), W(2, (60, 1))));
        Assert.Equal(
            "3w 30g +0 +0% [1@2x1;2@3x1;3@1x1] | 2w 31g +1 +3.3333333333333335% [1@2x1;2@1x1;3@1x1] | 1w 1021g +991 +3303.3333333333335% [1@1x1;2@1x1;3@1x1]",
            Render(PlanFrontier.Build(book)));
    }

    [Fact]
    public void Build_EliminationBeatsGreedyCover_AtTheGreedyWorldCount()
    {
        // Greedy cover takes world 1 first (it has three items in full), then world 2 for item 4: 2 worlds, 100*3+1 = 301.
        // Cheapest uses worlds 2, 3, 4 (8); dropping world 2 moves item 4 to world 4 (+2): worlds 3+4 for 10.
        var book = Book(
            I(1, 1, W(1, (100, 1)), W(3, (2, 1))),
            I(2, 1, W(1, (100, 1)), W(3, (3, 1))),
            I(3, 1, W(1, (100, 1)), W(4, (2, 1))),
            I(4, 1, W(2, (1, 1)), W(4, (3, 1))));
        Assert.Equal(301, RouteSummarizer.Summarize(book, Suggester.Suggest(book, SuggestionMode.FewestWorlds)).GrandTotal);
        Assert.Equal("3w 8g +0 +0% [1@3x1;2@3x1;3@4x1;4@2x1] | 2w 10g +2 +25% [1@3x1;2@3x1;3@4x1;4@4x1]",
            Render(PlanFrontier.Build(book)));
    }

    [Fact]
    public void Build_EqualCostWithFewerWorlds_ReplacesTheCheapestSplit()
    {
        // Same unit price on both worlds: the cheapest-first split uses both, but one world costs the same. The first option
        // must be the min-cost plan with the fewest worlds, so the 2-world split is dominated.
        var book = Book(I(1, 4, W(1, (5, 2)), W(2, (5, 9))));
        var options = PlanFrontier.Build(book);
        var only = Assert.Single(options);
        Assert.Equal(1, only.WorldCount);
        Assert.Equal(20, only.Total);
        Assert.Equal(0, only.ExtraGil);
        Assert.Equal("1@2x4", Render(only.Lines));
    }

    [Fact]
    public void Build_SameWorldsSameCost_FewerLinesWins()
    {
        // Item 1 costs 5 a unit on both worlds; both worlds stay (items 2 and 3 each exist on one only). The cheapest-first split
        // buys item 1 on both worlds (4 lines); the fewest-worlds plan buys all of it on world 2 (3 lines) for the same 27.
        var book = Book(
            I(1, 4, W(1, (5, 2)), W(2, (5, 9))),
            I(2, 1, W(1, (3, 1))),
            I(3, 1, W(2, (4, 1))));
        Assert.Equal("1@1x2;1@2x2;2@1x1;3@2x1", Render(Suggester.Suggest(book, SuggestionMode.Cheapest)));
        Assert.Equal("2w 27g +0 +0% [1@2x4;2@1x1;3@2x1]", Render(PlanFrontier.Build(book)));
    }

    // ---- Pick ----

    private static readonly IReadOnlyList<PlanOption> Ladder = new List<PlanOption>
    {
        Opt(5, 100, 0), Opt(3, 120, 20), Opt(2, 150, 50), Opt(1, 200, 100),
    };

    [Theory]
    [InlineData(0L, 5)]
    [InlineData(-5L, 5)]
    [InlineData(long.MinValue, 5)]
    [InlineData(19L, 5)]
    [InlineData(20L, 3)]
    [InlineData(49L, 3)]
    [InlineData(50L, 2)]
    [InlineData(99L, 2)]
    [InlineData(100L, 1)]
    [InlineData(long.MaxValue, 1)]
    public void Pick_FewestWorldsWithinBudget(long budget, int expectedWorlds)
        => Assert.Equal(expectedWorlds, PlanFrontier.Pick(Ladder, budget)!.WorldCount);

    [Fact]
    public void Pick_Empty_ReturnsNull()
        => Assert.Null(PlanFrontier.Pick(new List<PlanOption>(), 100));

    [Fact]
    public void Pick_BudgetZero_ReturnsFirstEvenWhenAnotherHasZeroExtra()
    {
        var options = new List<PlanOption> { Opt(3, 100, 0), Opt(1, 100, 0) };
        Assert.Same(options[0], PlanFrontier.Pick(options, 0));
        Assert.Same(options[1], PlanFrontier.Pick(options, 1));
    }

    [Fact]
    public void Pick_TieOnWorlds_LowestTotalWins()
    {
        var options = new List<PlanOption> { Opt(2, 100, 0), Opt(1, 130, 30), Opt(1, 125, 25), Opt(1, 140, 40) };
        Assert.Same(options[2], PlanFrontier.Pick(options, 100));
    }

    [Fact]
    public void Pick_FullTie_FirstInListOrderWins()
    {
        var options = new List<PlanOption> { Opt(2, 100, 0), Opt(1, 125, 25), Opt(1, 125, 25) };
        Assert.Same(options[1], PlanFrontier.Pick(options, 100));
    }

    [Fact]
    public void Pick_NothingWithinBudget_ReturnsFirst()
    {
        var options = new List<PlanOption> { Opt(3, 110, 10), Opt(1, 150, 50) };
        Assert.Same(options[0], PlanFrontier.Pick(options, 5));
    }

    // ---- properties on seeded random books ----

    private static OfferBook RandomBook(Random rng, int maxWorlds, int maxItems, int maxQty, int maxNeed, double offerChance, int maxSteps)
    {
        var worlds = rng.Next(1, maxWorlds + 1);
        var items = rng.Next(1, maxItems + 1);
        var list = new List<ItemOffers>();
        for (var i = 0; i < items; i++)
        {
            var offers = new List<WorldOffer>();
            for (var w = 0; w < worlds; w++)
            {
                if (rng.NextDouble() >= offerChance)
                    continue;
                var steps = new List<(int, int)>();
                var n = rng.Next(1, maxSteps + 1);
                for (var s = 0; s < n; s++)
                    steps.Add((rng.Next(1, 60), rng.Next(1, maxQty + 1)));
                offers.Add(W((uint)(100 + w), steps.ToArray()));
            }
            list.Add(I((uint)(1 + i), rng.Next(1, maxNeed + 1), offers.ToArray()));
        }
        return Book(list.ToArray());
    }

    private static IEnumerable<OfferBook> SmallBooks(int seed, int count)
    {
        var rng = new Random(seed);
        for (var k = 0; k < count; k++)
            yield return RandomBook(rng, 8, 6, 6, 12, 0.6, 3);
    }

    private static Dictionary<uint, long> SupplyPerItem(IEnumerable<AllocationLine> lines)
        => lines.GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Sum(l => (long)l.Quantity));

    private static void AssertInvariants(OfferBook book, IReadOnlyList<PlanOption> options, string context)
    {
        Assert.NotEmpty(options);
        var cheapestLines = Suggester.Suggest(book, SuggestionMode.Cheapest);
        var cheapest = RouteSummarizer.Summarize(book, cheapestLines);
        var target = SupplyPerItem(cheapestLines);

        // First option is the cheapest plan's cost, with zero extra.
        Assert.True(cheapest.GrandTotal == options[0].Total, $"first total {options[0].Total} != cheapest {cheapest.GrandTotal}: {context}");
        Assert.Equal(0, options[0].ExtraGil);
        Assert.Equal(0.0, options[0].ExtraPercent);

        for (var k = 0; k < options.Count; k++)
        {
            var o = options[k];
            if (k > 0)
            {
                Assert.True(o.WorldCount < options[k - 1].WorldCount, $"worlds not strictly decreasing at {k}: {context}");
                Assert.True(o.Total > options[k - 1].Total, $"dominated option at {k}: {context}");
            }

            var summary = RouteSummarizer.Summarize(book, o.Lines);
            Assert.True(summary.GrandTotal == o.Total, $"total mismatch at {k}: {context}");
            Assert.True(summary.WorldCount == o.WorldCount, $"world count mismatch at {k}: {context}");
            Assert.Equal(o.Total - options[0].Total, o.ExtraGil);
            Assert.Equal(options[0].Total == 0 ? 0.0 : o.ExtraGil * 100.0 / options[0].Total, o.ExtraPercent);

            // Lines: ordered, positive, one per (item, world), within stock, never above Needed, same supply as cheapest.
            var keys = o.Lines.Select(l => (l.ItemId, l.WorldId)).ToList();
            Assert.Equal(keys.OrderBy(x => x.ItemId).ThenBy(x => x.WorldId).ToList(), keys);
            Assert.Equal(keys.Count, keys.Distinct().Count());
            foreach (var line in o.Lines)
            {
                Assert.True(line.Quantity > 0);
                var offer = book.Items.Single(i => i.ItemId == line.ItemId).Worlds.Single(w => w.WorldId == line.WorldId);
                Assert.True(line.Quantity <= offer.UnitsForSale, $"over stock at {k}: {context}");
            }
            var supply = SupplyPerItem(o.Lines);
            Assert.True(target.OrderBy(x => x.Key).SequenceEqual(supply.OrderBy(x => x.Key)), $"supply differs at {k}: {context}");
            foreach (var (itemId, units) in supply)
                Assert.True(units <= book.Items.Single(i => i.ItemId == itemId).Needed);
        }

        // Never worse than the two fixed plans.
        var fewest = RouteSummarizer.Summarize(book, Suggester.Suggest(book, SuggestionMode.FewestWorlds));
        Assert.True(options.Any(o => o.WorldCount <= fewest.WorldCount && o.Total <= fewest.GrandTotal),
            $"fewest-worlds plan ({fewest.WorldCount}w {fewest.GrandTotal}g) not represented: {context}");
        Assert.Contains(options, o => o.WorldCount <= cheapest.WorldCount && o.Total <= cheapest.GrandTotal);
    }

    [Fact]
    public void Build_RandomSmallBooks_Invariants()
    {
        var n = 0;
        foreach (var book in SmallBooks(20261121, 400))
        {
            var options = PlanFrontier.Build(book);
            AssertInvariants(book, options, $"book {n}: {Render(options)}");
            n++;
        }
    }

    [Fact]
    public void Build_RandomBooksWithShortItems_Invariants()
    {
        // Large needs against small stock: many items cannot be fully supplied.
        var rng = new Random(77);
        for (var n = 0; n < 200; n++)
        {
            var book = RandomBook(rng, 7, 5, 3, 20, 0.5, 2);
            AssertInvariants(book, PlanFrontier.Build(book), $"book {n}");
        }
    }

    [Fact]
    public void Build_IsDeterministicUnderShuffledInput()
    {
        var rng = new Random(5);
        var n = 0;
        foreach (var book in SmallBooks(424242, 150))
        {
            var expected = Render(PlanFrontier.Build(book));
            for (var r = 0; r < 3; r++)
            {
                // Shuffle item order, each item's world order, and the order of equal-price steps within each ladder (the ladder
                // itself must stay sorted by price: that is the WorldOffer contract).
                var items = book.Items
                    .Select(i => i with
                    {
                        Worlds = i.Worlds
                            .Select(w => w with
                            {
                                Steps = w.Steps.Select(s => (s, k: rng.Next())).OrderBy(x => x.s.UnitPrice).ThenBy(x => x.k).Select(x => x.s).ToList(),
                            })
                            .OrderBy(_ => rng.Next()).ToList(),
                    })
                    .OrderBy(_ => rng.Next()).ToList();
                var shuffled = new OfferBook(items, book.Skipped);
                Assert.True(expected == Render(PlanFrontier.Build(shuffled)), $"book {n} round {r}");
            }
            n++;
        }
    }

    // ---- brute force on tiny books ----

    /// <summary>Exact minimum cost of supplying <paramref name="target"/> per item using at most k worlds, for every k (null = infeasible).</summary>
    private static long?[] ExactMinAtMost(OfferBook book, Dictionary<uint, long> target)
    {
        var worldIds = book.Items.SelectMany(i => i.Worlds).Select(w => w.WorldId).Distinct().OrderBy(x => x).ToList();
        var bySize = new long?[worldIds.Count + 1];
        for (var mask = 0; mask < (1 << worldIds.Count); mask++)
        {
            var size = System.Numerics.BitOperations.PopCount((uint)mask);
            long total = 0;
            var feasible = true;
            foreach (var item in book.Items)
            {
                var need = (int)target.GetValueOrDefault(item.ItemId);
                if (need == 0)
                    continue;
                // dp[j] = least cost to buy exactly j units from the worlds seen so far in the subset.
                var dp = new long?[need + 1];
                dp[0] = 0;
                foreach (var offer in item.Worlds)
                {
                    var bit = worldIds.IndexOf(offer.WorldId);
                    if ((mask & (1 << bit)) == 0)
                        continue;
                    var next = (long?[])dp.Clone();
                    for (var j = 0; j <= need; j++)
                    {
                        if (dp[j] is not { } baseCost)
                            continue;
                        for (var q = 1; q <= Math.Min(offer.UnitsForSale, need - j); q++)
                        {
                            var c = baseCost + offer.CostFor(q);
                            if (next[j + q] is null || c < next[j + q])
                                next[j + q] = c;
                        }
                    }
                    dp = next;
                }
                if (dp[need] is not { } best)
                {
                    feasible = false;
                    break;
                }
                total += best;
            }
            if (feasible && (bySize[size] is null || total < bySize[size]))
                bySize[size] = total;
        }
        var atMost = new long?[bySize.Length];
        long? running = null;
        for (var k = 0; k < bySize.Length; k++)
        {
            if (bySize[k] is { } v && (running is null || v < running))
                running = v;
            atMost[k] = running;
        }
        return atMost;
    }

    [Fact]
    public void Build_TinyBooks_NeverBelowTheExactOptimum_AndReportsTheGap()
    {
        var rng = new Random(9001);
        var optionsAbove = 0;
        var optionsTotal = 0;
        var countsMissedOrAbove = 0;
        var countsTotal = 0;
        var worstGap = 0.0;
        var worstCase = "";
        for (var n = 0; n < 200; n++)
        {
            var book = RandomBook(rng, 6, 4, 4, 8, 0.6, 2);
            var options = PlanFrontier.Build(book);
            var target = SupplyPerItem(Suggester.Suggest(book, SuggestionMode.Cheapest));
            var exact = ExactMinAtMost(book, target);

            foreach (var o in options)
            {
                var floor = exact[o.WorldCount];
                Assert.True(floor is not null, $"book {n}: option with {o.WorldCount} worlds but no feasible subset that small");
                Assert.True(o.Total >= floor, $"book {n}: option {o.WorldCount}w {o.Total} below the exact optimum {floor}");
                optionsTotal++;
                if (o.Total > floor)
                    optionsAbove++;
            }

            // Every reachable world count from the smallest feasible to the cheapest plan's: best option with <= k worlds.
            for (var k = 0; k <= options[0].WorldCount; k++)
            {
                if (exact[k] is not { } best)
                    continue;
                countsTotal++;
                var mine = options.Where(o => o.WorldCount <= k).Select(o => (long?)o.Total).Min();
                if (mine is null || mine > best)
                {
                    countsMissedOrAbove++;
                    _output.WriteLine($"  book {n}, k={k}: exact {best}, heuristic {(mine is null ? "none" : mine.ToString())}");
                    var gap = mine is null ? double.PositiveInfinity : (mine.Value - best) * 100.0 / Math.Max(1, best);
                    if (gap > worstGap)
                    {
                        worstGap = gap;
                        worstCase = $"book {n}, k={k}: exact {best}, heuristic {(mine is null ? "none" : mine.ToString())}";
                    }
                }
            }
        }
        _output.WriteLine($"options above the exact optimum for their world count: {optionsAbove} of {optionsTotal}");
        _output.WriteLine($"world counts where the best option within k worlds is above the exact optimum or missing: {countsMissedOrAbove} of {countsTotal}");
        _output.WriteLine($"worst gap: {worstGap:F2}% ({worstCase})");

        // Measured quality on this fixed seed: the heuristic matches the exact optimum at every reachable world count. Without the
        // shrink and swap improvements six counts were missed or above. A change that loses this is a regression to look at,
        // not a number to update.
        Assert.Equal(0, optionsAbove);
        Assert.Equal(0, countsMissedOrAbove);
    }

    // ---- performance ----

    [Fact]
    public void Build_LargeBook_FinishesWithinAGenerousBound()
    {
        var rng = new Random(123456);
        var items = new List<ItemOffers>();
        for (var i = 0; i < 100; i++)
        {
            var offers = new List<WorldOffer>();
            for (var w = 0; w < 150; w++)
            {
                if (rng.NextDouble() >= 0.8)
                    continue;
                var steps = new List<(int, int)>();
                var n = rng.Next(3, 8);
                for (var s = 0; s < n; s++)
                    steps.Add((rng.Next(50, 5000), rng.Next(1, 20)));
                offers.Add(W((uint)(1000 + w), steps.ToArray()));
            }
            items.Add(I((uint)(1 + i), rng.Next(1, 120), offers.ToArray()));
        }
        var book = Book(items.ToArray());

        var sw = Stopwatch.StartNew();
        var options = PlanFrontier.Build(book);
        sw.Stop();

        _output.WriteLine($"large book: {options.Count} options, {options[0].WorldCount} -> {options[^1].WorldCount} worlds, {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        Assert.True(options.Count > 1);
        AssertInvariants(book, options, "large book");

        // Measured on this seed: the options reach 4 worlds. Elimination that misjudges which removals are still feasible stops
        // at 5 here, so a rise is a regression to look at, not a number to update.
        Assert.True(options[^1].WorldCount <= 4, $"smallest option has {options[^1].WorldCount} worlds");
    }
}
