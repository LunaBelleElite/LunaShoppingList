using LunaShoppingList.Core;

namespace LunaShoppingList.Core.Tests;

// 2026-11-24 (release 0.16.0.0, whole stacks): a market listing can only be bought as a whole stack. Every expectation below that
// changed records its before-value in a comment beside it. Quantities are now units actually bought, which can exceed Needed.
public class SuggesterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static WorldOffer W(uint id, string name, params (int Price, int Qty)[] steps)
        => WO(id, name, steps.Select(s => (s.Price, s.Qty, 0)).ToArray());

    private static WorldOffer WO(uint id, string name, params (int Price, int Qty, int DaysAgo)[] steps)
        => new(id, name, steps
            .Select(s => new PriceStep(s.Price, s.Qty, false, T0.AddDays(-s.DaysAgo)))
            .OrderBy(p => p.UnitPrice).ThenByDescending(p => p.LastReview).ToList());

    private static ItemOffers I(uint id, int needed, params WorldOffer[] worlds)
        => new(id, needed, worlds.ToList());

    private static OfferBook Book(params ItemOffers[] items)
        => new(items.OrderBy(i => i.ItemId).ToList(), new List<SkippedItem>());

    private static string Render(IEnumerable<AllocationLine> lines)
        => string.Join(";", lines.Select(l => $"{l.ItemId}@{l.WorldId}x{l.Quantity}"));

    /// <summary>Whole-stack cost of the lines: each line's quantity must be a legal total on its world.</summary>
    private static long Cost(OfferBook book, IEnumerable<AllocationLine> lines)
        => lines.Sum(l => book.Items.Single(i => i.ItemId == l.ItemId)
            .Worlds.Single(w => w.WorldId == l.WorldId).ExactCost(l.Quantity)
            ?? throw new InvalidOperationException($"{l.ItemId}@{l.WorldId}x{l.Quantity} is not a legal total"));

    private static int WorldsVisited(IEnumerable<AllocationLine> lines) => lines.Select(l => l.WorldId).Distinct().Count();

    /// <summary>Units of each item covered, capped at what the item needs.</summary>
    private static long Covered(OfferBook book, IEnumerable<AllocationLine> lines)
        => book.Items.Sum(i => Math.Min(i.Needed, lines.Where(l => l.ItemId == i.ItemId).Sum(l => (long)l.Quantity)));

    // ---- both modes ----

    [Fact]
    public void EmptyBook_BothModesReturnNothing()
    {
        var book = new OfferBook(new List<ItemOffers>(), new List<SkippedItem>());
        Assert.Empty(Suggester.Suggest(book, SuggestionMode.Cheapest));
        Assert.Empty(Suggester.Suggest(book, SuggestionMode.FewestWorlds));
    }

    [Fact]
    public void NullBook_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Suggester.Suggest(null!, SuggestionMode.Cheapest));
        Assert.Throws<ArgumentNullException>(() => Suggester.Suggest(null!, SuggestionMode.FewestWorlds));
        Assert.Throws<ArgumentNullException>(() => Suggester.SuggestDetailed(null!, SuggestionMode.Cheapest));
    }

    [Fact]
    public void UnknownMode_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Suggester.Suggest(Book(), (SuggestionMode)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Suggester.SuggestDetailed(Book(), (SuggestionMode)99));
    }

    [Theory]
    [InlineData(SuggestionMode.Cheapest)]
    [InlineData(SuggestionMode.FewestWorlds)]
    public void SingleWorldCoversEverything(SuggestionMode mode)
    {
        var a = W(1, "Alpha", (10, 5));
        var book = Book(I(20, 3, a), I(10, 2, a));
        // 2026-11-24 superseded: was "10@1x2;20@1x3" (partial stacks). The only stack is 5 units, bought whole.
        Assert.Equal("10@1x5;20@1x5", Render(Suggester.Suggest(book, mode)));
    }

    [Theory]
    [InlineData(SuggestionMode.Cheapest)]
    [InlineData(SuggestionMode.FewestWorlds)]
    public void ItemWithNoOffers_OmittedOthersStillAllocated(SuggestionMode mode)
    {
        var a = W(1, "Alpha", (10, 5));
        var book = Book(I(10, 2), I(20, 3, a));
        // 2026-11-24 superseded: was "20@1x3".
        Assert.Equal("20@1x5", Render(Suggester.Suggest(book, mode)));
    }

    [Theory]
    [InlineData(SuggestionMode.Cheapest)]
    [InlineData(SuggestionMode.FewestWorlds)]
    public void InsufficientSupply_GetsAllSupply(SuggestionMode mode)
    {
        var a = W(1, "Alpha", (30, 2));
        var b = W(2, "Beta", (10, 1), (50, 1));
        var book = Book(I(10, 10, a, b));
        Assert.Equal("10@1x2;10@2x2", Render(Suggester.Suggest(book, mode)));
    }

    // 2026-11-24 renamed from QuantityClamping_NeverAboveNeededOrStock (expected "10@1x7"): the name became false, a 100-unit stack
    // is now bought whole for a need of 7. Still never above the stock.
    [Theory]
    [InlineData(SuggestionMode.Cheapest)]
    [InlineData(SuggestionMode.FewestWorlds)]
    public void WholeStackOnly_BuysTheWholeStackAboveNeeded_NeverAboveStock(SuggestionMode mode)
    {
        var a = W(1, "Alpha", (10, 100));
        var book = Book(I(10, 7, a));
        var lines = Suggester.Suggest(book, mode);
        Assert.Equal("10@1x100", Render(lines));
        Assert.All(lines, l => Assert.True(l.Quantity > 0));
    }

    [Theory]
    [InlineData(SuggestionMode.Cheapest)]
    [InlineData(SuggestionMode.FewestWorlds)]
    public void NeededLargerThanAnySingleWorld_SplitsAcrossWorlds(SuggestionMode mode)
    {
        var a = W(1, "Alpha", (10, 4));
        var b = W(2, "Beta", (12, 4));
        var c = W(3, "Gamma", (14, 4));
        var book = Book(I(10, 9, a, b, c));
        // 2026-11-24 superseded: was "10@1x4;10@2x4;10@3x1". Any two stacks make 8 < 9, so all three are bought whole.
        Assert.Equal("10@1x4;10@2x4;10@3x4", Render(Suggester.Suggest(book, mode)));
    }

    [Theory]
    [InlineData(SuggestionMode.Cheapest)]
    [InlineData(SuggestionMode.FewestWorlds)]
    public void NonPositiveNeeded_Ignored(SuggestionMode mode)
    {
        var a = W(1, "Alpha", (10, 5));
        var book = new OfferBook(new List<ItemOffers> { new(10, 0, new List<WorldOffer> { a }), I(20, 1, a) }, new List<SkippedItem>());
        // 2026-11-24 superseded: was "20@1x1".
        Assert.Equal("20@1x5", Render(Suggester.Suggest(book, mode)));
    }

    // ---- Cheapest ----

    // 2026-11-24 superseded: Cheapest_MergesLaddersAcrossWorlds expected "10@1x1;10@2x3" (cheapest single units across worlds).
    // With whole stacks the same book buys Beta's 5-stack alone (150 gil, 1 surplus): every cover using Alpha's 1 @ 10 also needs a
    // 5-stack. Renamed, since nothing is merged unit by unit any more.
    [Fact]
    public void Cheapest_WholeStackCoverCanBeOneWorldWithSurplus()
    {
        var a = W(1, "Alpha", (10, 1), (100, 5));
        var b = W(2, "Beta", (20, 1), (30, 5));
        var book = Book(I(10, 4, a, b));
        Assert.Equal("10@2x5", Render(Suggester.Suggest(book, SuggestionMode.Cheapest)));
    }

    [Fact]
    public void Cheapest_CombinesStacksAcrossWorlds()
    {
        // need 4: 2 @ 10 on Alpha + 2 @ 11 on Beta = 42; Gamma's 5 @ 20 = 100.
        var book = Book(I(10, 4, W(1, "Alpha", (10, 2)), W(2, "Beta", (11, 2)), W(3, "Gamma", (20, 5))));
        Assert.Equal("10@1x2;10@2x2", Render(Suggester.Suggest(book, SuggestionMode.Cheapest)));
    }

    [Fact]
    public void Cheapest_SurplusStackBeatsExactFit()
    {
        // need 3: 3 @ 50 (150) fits exactly; 10 @ 2 (20) is cheaper with 7 surplus.
        var book = Book(I(10, 3, W(1, "Alpha", (50, 3)), W(2, "Beta", (2, 10))));
        Assert.Equal("10@2x10", Render(Suggester.Suggest(book, SuggestionMode.Cheapest)));
    }

    // 2026-11-24 superseded: Cheapest_TieOnPrice_NewerReviewFirst_ThenLowerWorldId expected "10@1x1;10@5x1" (newest unit first,
    // then lower id). Under whole stacks the review tie-break compares the OLDEST review of the whole choice, and any two of those
    // three stacks include a 3-day-old one, so review no longer separates them and lower world ids win: "10@1x1;10@2x1".
    // The three tests below pin each tie-break separately.
    [Fact]
    public void Cheapest_TieOnCost_FewerUnitsWins_BeforeReview()
    {
        // need 2: Alpha 2 @ 6 (12, 5 days old) or Beta 3 @ 4 (12, new): the same cost, Alpha leaves no surplus.
        var book = Book(I(10, 2, WO(1, "Alpha", (6, 2, 5)), WO(2, "Beta", (4, 3, 0))));
        Assert.Equal("10@1x2", Render(Suggester.Suggest(book, SuggestionMode.Cheapest)));
    }

    [Fact]
    public void Cheapest_TieOnCostAndUnits_NewerReviewWins_BeforeWorldId()
    {
        var book = Book(I(10, 1, WO(2, "Beta", (10, 1, 3)), WO(5, "Echo", (10, 1, 1))));
        Assert.Equal("10@5x1", Render(Suggester.Suggest(book, SuggestionMode.Cheapest)));
    }

    [Fact]
    public void Cheapest_FullTie_LowerWorldIdWins()
    {
        var book = Book(I(10, 2, WO(2, "Beta", (10, 1, 3)), WO(5, "Echo", (10, 1, 1)), WO(1, "Alpha", (10, 1, 3))));
        Assert.Equal("10@1x1;10@2x1", Render(Suggester.Suggest(book, SuggestionMode.Cheapest)));
        Assert.Equal("10@4x1", Render(Suggester.Suggest(Book(I(10, 1, W(9, "Nine", (10, 1)), W(4, "Four", (10, 1)))), SuggestionMode.Cheapest)));
    }

    // 2026-11-24 retired: Cheapest_EqualsShoppingPlannerPurchasesAggregated compared Cheapest against ShoppingPlanner, which still buys
    // partial stacks, so it is no longer a valid reference (on its own book the planner buys Alpha 2, Beta 2, Gamma 2 for item 10,
    // a partial Gamma stack). Its successor is the brute-force oracle below, which compares against every subset of whole stacks.
    [Fact]
    public void Cheapest_MatchesBruteForceOverAllStackSubsets_On300Books()
    {
        var rng = new Random(160_0000);
        for (var n = 0; n < 300; n++)
        {
            var items = new List<ItemOffers>();
            for (uint i = 1; i <= 3; i++)
            {
                var worlds = new List<WorldOffer>();
                for (uint w = 1; w <= (uint)rng.Next(1, 5); w++)
                    worlds.Add(new WorldOffer(w * 2, "W" + w, Enumerable.Range(0, rng.Next(1, 4))
                        .Select(_ => StackBruteForce.RandomStep(rng, T0, maxQty: 9, maxPrice: 30)).OrderBy(s => s.UnitPrice).ToList()));
                items.Add(new ItemOffers(i, rng.Next(1, 25), worlds));
            }
            var book = new OfferBook(items, new List<SkippedItem>());
            var result = Suggester.SuggestDetailed(book, SuggestionMode.Cheapest);
            Assert.Empty(result.ApproximateItemIds);
            foreach (var item in items)
            {
                var all = StackBruteForce.All(item.Worlds.SelectMany(w => w.Steps).ToList());
                var best = StackBruteForce.Cover(all, item.Needed);
                var mine = result.Lines.Where(l => l.ItemId == item.ItemId).ToList();
                Assert.Equal(best.Cost, Cost(book, mine));
                Assert.Equal(best.Units, mine.Sum(l => (long)l.Quantity));
                // Each world's lines must be bought at that world's best subset for the total; the oldest review across them must be
                // the best achievable for this cost and unit count.
                var oldest = DateTimeOffset.MaxValue;
                foreach (var l in mine)
                {
                    var wo = item.Worlds.Single(w => w.WorldId == l.WorldId);
                    foreach (var ix in wo.Stacks(l.Quantity)!)
                        if (wo.Steps[ix].LastReview < oldest)
                            oldest = wo.Steps[ix].LastReview;
                }
                Assert.Equal(best.Oldest, oldest);
            }
        }
    }

    // ---- FewestWorlds ----

    [Fact]
    public void FewestWorlds_OneWorldCoversAll_WhereCheapestSplits()
    {
        var a = W(1, "Alpha", (10, 5));
        var b = W(2, "Beta", (50, 5));
        var book = Book(
            I(10, 2, a, b),
            I(20, 2, W(3, "Gamma", (12, 5)), b),
            I(30, 2, W(3, "Gamma", (14, 5)), b));
        var cheap = Suggester.Suggest(book, SuggestionMode.Cheapest);
        var fewest = Suggester.Suggest(book, SuggestionMode.FewestWorlds);
        // 2026-11-24 superseded: quantities were x2 (partial stacks); every stack here is 5 units.
        Assert.Equal("10@1x5;20@3x5;30@3x5", Render(cheap));
        Assert.Equal(2, WorldsVisited(cheap));
        Assert.Equal("10@2x5;20@2x5;30@2x5", Render(fewest));
        Assert.Equal(1, WorldsVisited(fewest));
        Assert.True(Cost(book, fewest) >= Cost(book, cheap));
    }

    [Fact]
    public void FewestWorlds_Safeguard_GreedyVisitsMoreWorlds_ReturnsCheapest()
    {
        // X fully supplies items 1-4, Y supplies 1,2,5, Z supplies 3,4,6. Greedy opens with X
        // then needs Y and Z (3 worlds); the cheapest plan only needs Y and Z (2 worlds).
        var x = W(10, "X", (50, 5));
        var y = W(11, "Y", (1, 5));
        var z = W(12, "Z", (1, 5));
        var book = Book(
            I(1, 1, x, y), I(2, 1, x, y), I(3, 1, x, z), I(4, 1, x, z), I(5, 1, y), I(6, 1, z));
        var cheap = Suggester.Suggest(book, SuggestionMode.Cheapest);
        Assert.Equal(2, WorldsVisited(cheap));
        Assert.Equal(Render(cheap), Render(Suggester.Suggest(book, SuggestionMode.FewestWorlds)));
    }

    [Fact]
    public void FewestWorlds_Safeguard_SameWorldCountButCostlier_ReturnsCheapest()
    {
        // item 1 is cheap on B (1) and dear on A (10); item 2 only on B (50); item 3 only on A (1).
        // Greedy ties A and B on two items, picks A (11 < 51), then B: 2 worlds, 61 gil.
        // Cheapest: item 1 and 2 on B, item 3 on A: 2 worlds, 52 gil.
        var a = W(1, "A", (10, 1));
        var a3 = W(1, "A", (1, 1));
        var b = W(2, "B", (1, 1));
        var b2 = W(2, "B", (50, 1));
        var book = Book(I(1, 1, a, b), I(2, 1, b2), I(3, 1, a3));
        var cheap = Suggester.Suggest(book, SuggestionMode.Cheapest);
        Assert.Equal("1@2x1;2@2x1;3@1x1", Render(cheap));
        Assert.Equal(Render(cheap), Render(Suggester.Suggest(book, SuggestionMode.FewestWorlds)));
    }

    [Fact]
    public void FewestWorlds_InsufficientSupplyItem_CoveredAfterOthers()
    {
        var a = W(1, "Alpha", (10, 5));
        var book = Book(I(10, 3, a), I(20, 4, W(2, "Beta", (10, 1)), W(3, "Gamma", (10, 1))));
        // 2026-11-24 superseded: was "10@1x3;20@2x1;20@3x1".
        Assert.Equal("10@1x5;20@2x1;20@3x1", Render(Suggester.Suggest(book, SuggestionMode.FewestWorlds)));
    }

    [Fact]
    public void FewestWorlds_FallbackPrefersAlreadyChosenWorlds()
    {
        // Item 1 is only on Alpha. Item 2 needs 4 and no world has 4: Alpha has 2 (dear), Beta 2, Gamma 2 (cheapest).
        // Cheapest uses Gamma+Beta for item 2 (3 worlds overall); the fallback uses Alpha (already chosen) then Gamma (2 worlds).
        var book = Book(
            I(1, 1, W(1, "Alpha", (10, 5))),
            I(2, 4, W(1, "Alpha", (90, 2)), W(2, "Beta", (20, 2)), W(3, "Gamma", (5, 2))));
        var cheap = Suggester.Suggest(book, SuggestionMode.Cheapest);
        var fewest = Suggester.Suggest(book, SuggestionMode.FewestWorlds);
        Assert.Equal(3, WorldsVisited(cheap));
        // 2026-11-24 superseded: was "1@1x1;2@1x2;2@3x2".
        Assert.Equal("1@1x5;2@1x2;2@3x2", Render(fewest));
    }

    [Fact]
    public void FewestWorlds_FallbackCoversFromChosenWorldsWithWholeStacks()
    {
        // Items 1 and 2 put Alpha and Beta in the chosen set (each is the only seller of one item). Item 3 needs 5 and no world has
        // 5: the chosen worlds together do (Alpha 3 @ 10, Beta 3 @ 1 + 1 @ 2), so it is covered from them alone, with whole stacks:
        // Alpha 3 + Beta 3 = 6 units for 33 gil. Gamma (4 @ 1, which the cheapest plan uses with Beta's 1 @ 2) is never added.
        var book = Book(
            I(1, 1, W(1, "Alpha", (1, 1))),
            I(2, 1, W(2, "Beta", (1, 1))),
            I(3, 5, W(1, "Alpha", (10, 3)), W(2, "Beta", (1, 3), (2, 1)), W(3, "Gamma", (1, 4))));
        var cheap = Suggester.Suggest(book, SuggestionMode.Cheapest);
        Assert.Equal("1@1x1;2@2x1;3@2x1;3@3x4", Render(cheap));
        var fewest = Suggester.Suggest(book, SuggestionMode.FewestWorlds);
        Assert.Equal("1@1x1;2@2x1;3@1x3;3@2x3", Render(fewest));
        Assert.Equal(1 + 1 + 33, Cost(book, fewest));
    }

    [Fact]
    public void FewestWorlds_TieOnItemCount_LowestCostThenNameThenId()
    {
        // lowest cost: Zed and Alpha both cover items 1 and 2, Zed is cheaper; R and S would split them (2 worlds)
        // 2026-11-24 superseded: was "1@1x1;2@1x1" (partial stacks).
        Assert.Equal("1@1x5;2@1x5", Render(Suggester.Suggest(
            Book(
                I(1, 1, W(1, "Zed", (10, 5)), W(2, "Alpha", (20, 5)), W(3, "R", (1, 5))),
                I(2, 1, W(1, "Zed", (10, 5)), W(2, "Alpha", (20, 5)), W(4, "S", (1, 5)))),
            SuggestionMode.FewestWorlds)));
        // same cost: name decides (ordinal ignore case), not world id
        Assert.Equal("1@2x2", Render(Suggester.Suggest(
            Book(I(1, 2, W(1, "Beta", (5, 2)), W(2, "alpha", (5, 2)))), SuggestionMode.FewestWorlds)));
        // same cost and name ignoring case: lower world id
        Assert.Equal("1@4x2", Render(Suggester.Suggest(
            Book(I(1, 2, W(9, "Same", (5, 2)), W(4, "same", (5, 2)))), SuggestionMode.FewestWorlds)));
    }

    [Fact]
    public void FewestWorlds_WorldCostIsTheWholeStackCover_NotThePartialPrice()
    {
        // A and B each cover items 1 and 2 (need 1). A sells 1 @ 10 per item (cover 10, total 20); B sells 3 @ 4 per item: partial
        // pricing would say 4 each (8), but the whole stack costs 12 each (24). A must win the tie on item count. C and D make the
        // cheapest plan visit two worlds, so the greedy one-world answer is the one returned.
        var book = Book(
            I(1, 1, W(1, "A", (10, 1)), W(2, "B", (4, 3)), W(3, "C", (1, 1))),
            I(2, 1, W(1, "A", (10, 1)), W(2, "B", (4, 3)), W(4, "D", (1, 1))));
        Assert.Equal("1@1x1;2@1x1", Render(Suggester.Suggest(book, SuggestionMode.FewestWorlds)));
    }

    [Fact]
    public void FewestWorlds_LessSurplusThanCheapestIsNotCoveringLess()
    {
        // Item 1: A sells 2 @ 10 (exact fit, 20), B sells 5 @ 1 (5 gil, 3 surplus): cheapest buys B's 5. Item 2 is only on A, so
        // the fewest-worlds plan buys both on A: 2 units of item 1, fewer than cheapest's 5 but the whole need. The safeguard compares
        // units covered (capped at the need), so this one-world plan is kept.
        var book = Book(
            I(1, 2, W(1, "A", (10, 2)), W(2, "B", (1, 5))),
            I(2, 1, W(1, "A", (3, 1))));
        Assert.Equal("1@2x5;2@1x1", Render(Suggester.Suggest(book, SuggestionMode.Cheapest)));
        Assert.Equal("1@1x2;2@1x1", Render(Suggester.Suggest(book, SuggestionMode.FewestWorlds)));
    }

    [Fact]
    public void FewestWorlds_MostItemsBeatsCheaperWorld()
    {
        var many = W(1, "Many", (50, 5));
        var one = W(2, "One", (1, 5));
        var book = Book(I(1, 1, many, one), I(2, 1, many));
        // 2026-11-24 superseded: was "1@1x1;2@1x1".
        Assert.Equal("1@1x5;2@1x5", Render(Suggester.Suggest(book, SuggestionMode.FewestWorlds)));
    }

    private static OfferBook ReassignBook()
    {
        // X(10) supplies items 1,2,3,6 so it is chosen first; Y(11) is chosen second for items 4,5. Item 1 costs 1000 on X, 1 on Y.
        // Cheapest spreads over Y, Z(12), W(13) and X: four worlds.
        var x = (Func<int, WorldOffer>)(p => W(10, "X", (p, 5)));
        return Book(
            I(1, 1, x(1000), W(11, "Y", (1, 5))),
            I(2, 1, x(5), W(12, "Z", (1, 5))),
            I(3, 1, x(5), W(13, "W", (1, 5))),
            I(4, 1, W(11, "Y", (50, 5))),
            I(5, 1, W(11, "Y", (50, 5))),
            I(6, 1, x(5)));
    }

    [Fact]
    public void FewestWorlds_ItemMovesToCheaperLaterChosenWorld()
    {
        var book = ReassignBook();
        var cheap = Suggester.Suggest(book, SuggestionMode.Cheapest);
        var fewest = Suggester.Suggest(book, SuggestionMode.FewestWorlds);
        Assert.Equal(4, WorldsVisited(cheap));
        // 2026-11-24 superseded: was x1 on every line and 116 gil; every stack is 5 units, so x5 and 580 gil.
        Assert.Equal("1@11x5;2@10x5;3@10x5;4@11x5;5@11x5;6@10x5", Render(fewest));
        Assert.Equal(580, Cost(book, fewest));
        Assert.Equal(2, WorldsVisited(fewest));
        Assert.True(WorldsVisited(fewest) <= WorldsVisited(cheap));
    }

    [Fact]
    public void FewestWorlds_ReassignmentUsesTheWholeStackCover()
    {
        // X covers items 1,2,3,6 (chosen first), Y covers 1,4,5 (chosen second). Item 1: X sells 3 @ 4 (whole stack 12, partial 4),
        // Y sells 1 @ 10 (10): the item moves to Y. Z sells item 2 cheaply so the cheapest plan needs three worlds and the greedy
        // two-world plan is the one returned.
        var book = Book(
            I(1, 1, W(10, "X", (4, 3)), W(11, "Y", (10, 1))),
            I(2, 1, W(10, "X", (5, 1)), W(12, "Z", (1, 1))),
            I(3, 1, W(10, "X", (5, 1))),
            I(4, 1, W(11, "Y", (5, 1))),
            I(5, 1, W(11, "Y", (5, 1))),
            I(6, 1, W(10, "X", (5, 1))));
        var fewest = Suggester.Suggest(book, SuggestionMode.FewestWorlds);
        Assert.Equal(2, WorldsVisited(fewest));
        Assert.Contains("1@11x1", Render(fewest));
    }

    [Fact]
    public void FewestWorlds_ReassignmentNeverAddsAWorld()
    {
        // Item 1 is dear on X (100) and cheap on Q (1), but Q is not chosen: X covers both items and is the only world visited.
        var book = Book(
            I(1, 1, W(1, "X", (100, 5)), W(2, "Q", (1, 5))),
            I(2, 1, W(1, "X", (5, 5))));
        // 2026-11-24 superseded: was "1@1x1;2@1x1".
        Assert.Equal("1@1x5;2@1x5", Render(Suggester.Suggest(book, SuggestionMode.FewestWorlds)));
    }

    [Fact]
    public void FewestWorlds_ReassignmentTieBrokenByNameThenId()
    {
        // X(1) is chosen first (4 items). Items 1: equally cheap on Beta(5) and alpha(6) (both chosen later): alpha wins on name;
        // then the same price on two worlds named alike: lower id wins.
        var book = Book(
            I(1, 1, W(1, "X", (100, 5)), W(5, "Beta", (1, 5)), W(6, "alpha", (1, 5))),
            I(2, 1, W(1, "X", (5, 5))),
            I(3, 1, W(1, "X", (5, 5))),
            I(4, 1, W(1, "X", (5, 5))),
            I(5, 1, W(5, "Beta", (50, 5))),
            I(6, 1, W(6, "alpha", (50, 5))),
            I(7, 1, W(1, "X", (100, 5)), W(9, "Same", (1, 5)), W(8, "same", (1, 5))),
            I(8, 1, W(9, "Same", (50, 5))),
            I(9, 1, W(8, "same", (50, 5))));
        var fewest = Render(Suggester.Suggest(book, SuggestionMode.FewestWorlds));
        // 2026-11-24 superseded: were "1@6x1" and "7@8x1".
        Assert.Contains("1@6x5", fewest);
        Assert.Contains("7@8x5", fewest);
    }

    [Fact]
    public void FewestWorlds_ReassignmentCanEmptyAWorldWhichIsThenDropped()
    {
        // X covers items 1,2 and is chosen first (cheapest of the three 2-item ties); Y then covers item 3 and Z item 4,
        // and each of them also sells one of X's items for 1 gil, so X ends up with nothing and is dropped.
        var book = Book(
            I(1, 1, W(1, "X", (50, 5)), W(2, "Y", (1, 5))),
            I(2, 1, W(1, "X", (50, 5)), W(3, "Z", (1, 5))),
            I(3, 1, W(2, "Y", (1000, 5))),
            I(4, 1, W(3, "Z", (1000, 5))));
        var fewest = Suggester.Suggest(book, SuggestionMode.FewestWorlds);
        // 2026-11-24 superseded: was "1@2x1;2@3x1;3@2x1;4@3x1".
        Assert.Equal("1@2x5;2@3x5;3@2x5;4@3x5", Render(fewest));
        Assert.Equal(2, WorldsVisited(fewest));
    }

    [Fact]
    public void FewestWorlds_Safeguard_SameWorldCountCostlierEvenAfterReassignment_ReturnsCheapest()
    {
        // item 1 needs 2: A sells 2 at 10 (20), B and C sell 1 at 1 each (2 total). Items 4 only on A, 3 only on B, 2 only on C.
        // Greedy: A, B, C (3 worlds, item 1 on A at 20: A is the only world that can supply it alone, so it cannot move).
        // Cheapest: item 1 split over B and C, also 3 worlds, 2 gil: cheaper, so the safeguard returns Cheapest.
        var book = Book(
            I(1, 2, W(1, "A", (10, 2)), W(2, "B", (1, 1)), W(3, "C", (1, 1))),
            I(2, 1, W(3, "C", (1, 5))),
            I(3, 1, W(2, "B", (1, 5))),
            I(4, 1, W(1, "A", (1, 5))));
        var cheap = Suggester.Suggest(book, SuggestionMode.Cheapest);
        Assert.Equal(3, WorldsVisited(cheap));
        Assert.Equal(Render(cheap), Render(Suggester.Suggest(book, SuggestionMode.FewestWorlds)));
        Assert.Contains("1@2x1;1@3x1", Render(cheap));
    }

    [Fact]
    public void FewestWorlds_ReassignmentIsIndependentOfInputOrder()
    {
        var book = ReassignBook();
        var expected = Render(Suggester.Suggest(book, SuggestionMode.FewestWorlds));
        var rng = new Random(5);
        for (var k = 0; k < 30; k++)
        {
            var shuffled = new OfferBook(
                book.Items.OrderBy(_ => rng.Next())
                    .Select(i => i with { Worlds = i.Worlds.OrderBy(_ => rng.Next()).ToList() }).ToList(),
                book.Skipped);
            Assert.Equal(expected, Render(Suggester.Suggest(shuffled, SuggestionMode.FewestWorlds)));
        }
    }

    // ---- size guard ----

    [Theory]
    [InlineData(SuggestionMode.Cheapest)]
    [InlineData(SuggestionMode.FewestWorlds)]
    public void SuggestDetailed_FlagsItemsSolvedByTheGreedyFallback(SuggestionMode mode)
    {
        // Fixture changed 2026-11-24: it was item 7 needing 1 from A (1,000,000 at 1) and B (1 at 5), which is now solved exactly
        // because each stack covers the need alone (see SuggestDetailed_OneStackThatCoversAlone_IsExact). Expectations unchanged.
        // Item 7 needs 500,001 from stacks below the need (500,000 twice at 1, 2 at 5): the exact table would need totals up to
        // 1,000,000, past the 1,000,000-total bound, so the greedy fallback takes the two big stacks. Item 3 is small and exact.
        var book = Book(
            I(3, 2, W(1, "A", (5, 2))),
            I(7, 500_001, W(1, "A", (1, 500_000), (1, 500_000), (5, 2))));
        var result = Suggester.SuggestDetailed(book, mode);
        Assert.Equal(new uint[] { 7 }, result.ApproximateItemIds);
        Assert.Equal("3@1x2;7@1x1000000", Render(result.Lines));
        Assert.Equal(Render(Suggester.Suggest(book, mode)), Render(result.Lines));
    }

    [Fact]
    public void SuggestDetailed_OneStackThatCoversAlone_IsExact()
    {
        // The fixture the test above used before 2026-11-24 (it expected item 7 flagged and 1,000,000 units bought on A for one
        // needed unit). Every stack covers a need of 1 alone, so no table is needed: the cheapest plan buys B's 1 unit at 5 gil.
        var book = Book(
            I(3, 2, W(1, "A", (5, 2))),
            I(7, 1, W(1, "A", (1, 1_000_000)), W(2, "B", (5, 1))));
        var result = Suggester.SuggestDetailed(book, SuggestionMode.Cheapest);
        Assert.Empty(result.ApproximateItemIds);
        Assert.Equal("3@1x2;7@2x1", Render(result.Lines));
    }

    [Fact]
    public void SuggestDetailed_ExactBookHasNoApproximateItems()
        => Assert.Empty(Suggester.SuggestDetailed(ReassignBook(), SuggestionMode.FewestWorlds).ApproximateItemIds);

    // ---- determinism ----

    [Theory]
    [InlineData(SuggestionMode.Cheapest)]
    [InlineData(SuggestionMode.FewestWorlds)]
    public void InputOrderDoesNotChangeOutput(SuggestionMode mode)
    {
        var rng = new Random(77);
        for (var n = 0; n < 30; n++)
        {
            var book = RandomBook(rng, ties: true);
            var expected = Render(Suggester.Suggest(book, mode));
            for (var k = 0; k < 4; k++)
            {
                var shuffled = new OfferBook(
                    book.Items.OrderBy(_ => rng.Next())
                        .Select(i => i with { Worlds = i.Worlds.OrderBy(_ => rng.Next()).ToList() }).ToList(),
                    book.Skipped);
                Assert.Equal(expected, Render(Suggester.Suggest(shuffled, mode)));
            }
        }
    }

    // ---- invariants over generated books ----

    private static OfferBook RandomBook(Random rng, bool ties)
    {
        var worldCount = rng.Next(1, 7);
        var itemCount = rng.Next(0, 8);
        var items = new List<ItemOffers>();
        for (uint i = 1; i <= itemCount; i++)
        {
            var needed = rng.Next(1, 12);
            var worlds = new List<WorldOffer>();
            for (uint w = 1; w <= worldCount; w++)
            {
                if (rng.Next(3) == 0) continue;
                var steps = Enumerable.Range(0, rng.Next(1, 4))
                    .Select(_ => (Price: ties ? rng.Next(1, 4) * 10 : rng.Next(1, 500), Qty: rng.Next(1, 6), DaysAgo: ties ? rng.Next(0, 2) : rng.Next(0, 30)))
                    .ToArray();
                worlds.Add(WO(w * 3, "W" + (w % 3), steps)); // duplicate names on purpose
            }
            items.Add(new ItemOffers(i * 5, needed, worlds));
        }
        return new OfferBook(items, new List<SkippedItem>());
    }

    [Fact]
    public void Invariants_Over200SeededBooks()
    {
        var rng = new Random(20261121);
        for (var n = 0; n < 200; n++)
        {
            var book = RandomBook(rng, ties: n % 2 == 0);
            var cheap = Suggester.Suggest(book, SuggestionMode.Cheapest);
            var fewest = Suggester.Suggest(book, SuggestionMode.FewestWorlds);
            foreach (var lines in new[] { cheap, fewest })
            {
                Assert.All(lines, l => Assert.True(l.Quantity > 0));
                Assert.Equal(lines.OrderBy(l => l.ItemId).ThenBy(l => l.WorldId).Select(l => (l.ItemId, l.WorldId)),
                    lines.Select(l => (l.ItemId, l.WorldId)));
                Assert.Equal(lines.Count, lines.Select(l => (l.ItemId, l.WorldId)).Distinct().Count());
                foreach (var l in lines)
                {
                    var offer = book.Items.Single(i => i.ItemId == l.ItemId).Worlds.Single(w => w.WorldId == l.WorldId);
                    Assert.True(l.Quantity <= offer.UnitsForSale);
                    // 2026-11-24 new: every line is a legal total (a sum of whole stacks) on its world.
                    Assert.NotNull(offer.ExactCost(l.Quantity));
                }
                foreach (var item in book.Items)
                {
                    var got = lines.Where(l => l.ItemId == item.ItemId).Sum(l => l.Quantity);
                    // 2026-11-24 superseded: was Assert.Equal(min(Needed, supply), got). Whole stacks can overshoot, never undershoot.
                    Assert.True(got >= Math.Min(item.Needed, item.Worlds.Sum(w => w.UnitsForSale)), $"book {n} item {item.ItemId}");
                }
            }
            Assert.True(WorldsVisited(fewest) <= WorldsVisited(cheap), $"book {n}");
            Assert.True(Cost(book, cheap) <= Cost(book, fewest), $"book {n}");
            // 2026-11-24 superseded: was Supplied(cheap) == Supplied(fewest) (raw units). Surplus may differ; covered units may not.
            Assert.Equal(Covered(book, cheap), Covered(book, fewest));
        }
    }
}
