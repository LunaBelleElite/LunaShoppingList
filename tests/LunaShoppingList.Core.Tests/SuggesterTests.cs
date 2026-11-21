using LunaShoppingList.Core;

namespace LunaShoppingList.Core.Tests;

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

    private static long Cost(OfferBook book, IEnumerable<AllocationLine> lines)
        => lines.Sum(l => book.Items.Single(i => i.ItemId == l.ItemId)
            .Worlds.Single(w => w.WorldId == l.WorldId).CostFor(l.Quantity));

    private static int WorldsVisited(IEnumerable<AllocationLine> lines) => lines.Select(l => l.WorldId).Distinct().Count();

    private static int Supplied(IEnumerable<AllocationLine> lines) => lines.Sum(l => l.Quantity);

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
    }

    [Fact]
    public void UnknownMode_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Suggester.Suggest(Book(), (SuggestionMode)99));
    }

    [Theory]
    [InlineData(SuggestionMode.Cheapest)]
    [InlineData(SuggestionMode.FewestWorlds)]
    public void SingleWorldCoversEverything(SuggestionMode mode)
    {
        var a = W(1, "Alpha", (10, 5));
        var book = Book(I(20, 3, a), I(10, 2, a));
        Assert.Equal("10@1x2;20@1x3", Render(Suggester.Suggest(book, mode)));
    }

    [Theory]
    [InlineData(SuggestionMode.Cheapest)]
    [InlineData(SuggestionMode.FewestWorlds)]
    public void ItemWithNoOffers_OmittedOthersStillAllocated(SuggestionMode mode)
    {
        var a = W(1, "Alpha", (10, 5));
        var book = Book(I(10, 2), I(20, 3, a));
        Assert.Equal("20@1x3", Render(Suggester.Suggest(book, mode)));
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

    [Theory]
    [InlineData(SuggestionMode.Cheapest)]
    [InlineData(SuggestionMode.FewestWorlds)]
    public void QuantityClamping_NeverAboveNeededOrStock(SuggestionMode mode)
    {
        var a = W(1, "Alpha", (10, 100));
        var book = Book(I(10, 7, a));
        var lines = Suggester.Suggest(book, mode);
        Assert.Equal("10@1x7", Render(lines));
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
        Assert.Equal("10@1x4;10@2x4;10@3x1", Render(Suggester.Suggest(book, mode)));
    }

    [Theory]
    [InlineData(SuggestionMode.Cheapest)]
    [InlineData(SuggestionMode.FewestWorlds)]
    public void NonPositiveNeeded_Ignored(SuggestionMode mode)
    {
        var a = W(1, "Alpha", (10, 5));
        var book = new OfferBook(new List<ItemOffers> { new(10, 0, new List<WorldOffer> { a }), I(20, 1, a) }, new List<SkippedItem>());
        Assert.Equal("20@1x1", Render(Suggester.Suggest(book, mode)));
    }

    // ---- Cheapest ----

    [Fact]
    public void Cheapest_MergesLaddersAcrossWorlds()
    {
        var a = W(1, "Alpha", (10, 1), (100, 5));
        var b = W(2, "Beta", (20, 1), (30, 5));
        var book = Book(I(10, 4, a, b));
        // cheapest four units: 10 (Alpha), 20, 30, 30 (Beta)
        Assert.Equal("10@1x1;10@2x3", Render(Suggester.Suggest(book, SuggestionMode.Cheapest)));
    }

    [Fact]
    public void Cheapest_TieOnPrice_NewerReviewFirst_ThenLowerWorldId()
    {
        var a = WO(2, "Beta", (10, 1, 3));
        var b = WO(5, "Echo", (10, 1, 1));   // newer review, higher id: taken first
        var c = WO(1, "Alpha", (10, 1, 3));  // same review as Beta, lower id: wins the second unit
        var book = Book(I(10, 2, a, b, c));
        Assert.Equal("10@1x1;10@5x1", Render(Suggester.Suggest(book, SuggestionMode.Cheapest)));
    }

    [Fact]
    public void Cheapest_EqualsShoppingPlannerPurchasesAggregated()
    {
        var listings = new List<Listing>
        {
            new(10, 1, "Alpha", 10, 2, false, T0),
            new(10, 1, "Alpha", 40, 3, true, T0),
            new(10, 2, "Beta", 15, 2, false, T0.AddDays(-1)),
            new(10, 2, "Beta", 15, 2, false, T0),
            new(10, 3, "Gamma", 15, 5, false, T0),
            new(20, 2, "Beta", 7, 1, false, T0),
            new(20, 3, "Gamma", 7, 1, false, T0),
            new(20, 3, "Gamma", 9, 4, false, T0),
            new(30, 1, "Alpha", 5, 2, false, T0),
        };
        var reqs = new[] { new Requirement(10, 6), new Requirement(20, 4), new Requirement(30, 9) };
        var plan = ShoppingPlanner.Plan(reqs,
            listings.GroupBy(l => l.ItemId).Select(g => new ItemListings(g.Key, g.ToList())).ToList(),
            new HashSet<uint> { 1, 2, 3 }, new ExclusionState());
        var expected = plan.Worlds.SelectMany(w => w.Purchases)
            .GroupBy(p => (p.ItemId, p.WorldId))
            .OrderBy(g => g.Key.ItemId).ThenBy(g => g.Key.WorldId)
            .Select(g => new AllocationLine(g.Key.ItemId, g.Key.WorldId, g.Sum(p => p.Quantity)))
            .ToList();

        var items = reqs.OrderBy(r => r.ItemId).Select(r => new ItemOffers(r.ItemId, r.Quantity,
            listings.Where(l => l.ItemId == r.ItemId).GroupBy(l => l.WorldId).OrderBy(g => g.Key)
                .Select(g => new WorldOffer(g.Key, g.First().WorldName,
                    g.OrderBy(l => l.PricePerUnit).ThenByDescending(l => l.LastReview)
                        .Select(l => new PriceStep(l.PricePerUnit, l.Quantity, l.Hq, l.LastReview)).ToList()))
                .ToList())).ToList();
        var book = new OfferBook(items, new List<SkippedItem>());

        Assert.Equal(Render(expected), Render(Suggester.Suggest(book, SuggestionMode.Cheapest)));
        // item 30 is short in the planner too (needs 9, 2 for sale): both buy the 2 that exist
        Assert.Single(plan.Shortfalls);
        Assert.Contains("30@1x2", Render(expected));
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
        Assert.Equal("10@1x2;20@3x2;30@3x2", Render(cheap));
        Assert.Equal(2, WorldsVisited(cheap));
        Assert.Equal("10@2x2;20@2x2;30@2x2", Render(fewest));
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
        Assert.Equal("10@1x3;20@2x1;20@3x1", Render(Suggester.Suggest(book, SuggestionMode.FewestWorlds)));
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
        Assert.Equal("1@1x1;2@1x2;2@3x2", Render(fewest));
    }

    [Fact]
    public void FewestWorlds_TieOnItemCount_LowestCostThenNameThenId()
    {
        // lowest cost: Zed and Alpha both cover items 1 and 2, Zed is cheaper; R and S would split them (2 worlds)
        Assert.Equal("1@1x1;2@1x1", Render(Suggester.Suggest(
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
    public void FewestWorlds_MostItemsBeatsCheaperWorld()
    {
        var many = W(1, "Many", (50, 5));
        var one = W(2, "One", (1, 5));
        var book = Book(I(1, 1, many, one), I(2, 1, many));
        Assert.Equal("1@1x1;2@1x1", Render(Suggester.Suggest(book, SuggestionMode.FewestWorlds)));
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
        Assert.Equal("1@11x1;2@10x1;3@10x1;4@11x1;5@11x1;6@10x1", Render(fewest));
        Assert.Equal(116, Cost(book, fewest));
        Assert.Equal(2, WorldsVisited(fewest));
        Assert.True(WorldsVisited(fewest) <= WorldsVisited(cheap));
    }

    [Fact]
    public void FewestWorlds_ReassignmentNeverAddsAWorld()
    {
        // Item 1 is dear on X (100) and cheap on Q (1), but Q is not chosen: X covers both items and is the only world visited.
        var book = Book(
            I(1, 1, W(1, "X", (100, 5)), W(2, "Q", (1, 5))),
            I(2, 1, W(1, "X", (5, 5))));
        Assert.Equal("1@1x1;2@1x1", Render(Suggester.Suggest(book, SuggestionMode.FewestWorlds)));
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
        Assert.Contains("1@6x1", fewest);
        Assert.Contains("7@8x1", fewest);
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
        Assert.Equal("1@2x1;2@3x1;3@2x1;4@3x1", Render(fewest));
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
                    Assert.True(l.Quantity <= book.Items.Single(i => i.ItemId == l.ItemId).Worlds.Single(w => w.WorldId == l.WorldId).UnitsForSale);
                foreach (var item in book.Items)
                {
                    var got = lines.Where(l => l.ItemId == item.ItemId).Sum(l => l.Quantity);
                    Assert.Equal(Math.Min(item.Needed, item.Worlds.Sum(w => w.UnitsForSale)), got);
                }
            }
            Assert.True(WorldsVisited(fewest) <= WorldsVisited(cheap), $"book {n}");
            Assert.True(Cost(book, cheap) <= Cost(book, fewest), $"book {n}");
            Assert.Equal(Supplied(cheap), Supplied(fewest));
        }
    }
}
