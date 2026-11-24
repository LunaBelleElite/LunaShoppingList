using System.Diagnostics;
using LunaShoppingList.Core;
using Xunit.Abstractions;

namespace LunaShoppingList.Core.Tests;

/// <summary>
/// <see cref="PlanFrontier"/>. Whole stacks (release 0.16.0.0): a market listing can only be bought as a whole stack, so every
/// option buys whole stacks, a line's quantity is a legal total for its world and can exceed the item's need, and an option's
/// total is the exact price of the stacks it buys. Expectations changed for that on 2026-11-24 carry the old value beside them.
/// </summary>
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

    /// <summary>Whole-stack price of <paramref name="lines"/>: each line at its world's exact cost; null when a line is not a legal total.</summary>
    private static long? ExactPrice(OfferBook book, IEnumerable<AllocationLine> lines)
    {
        long total = 0;
        foreach (var line in lines)
        {
            var offer = book.Items.Single(i => i.ItemId == line.ItemId).Worlds.First(w => w.WorldId == line.WorldId);
            if (offer.ExactCost(line.Quantity) is not { } cost)
                return null;
            total += cost;
        }
        return total;
    }

    // ---- argument and edge cases ----

    [Fact]
    public void Build_NullBook_Throws()
        => Assert.Throws<ArgumentNullException>(() => PlanFrontier.Build(null!));

    [Fact]
    public void BuildDetailed_NullBook_Throws()
        => Assert.Throws<ArgumentNullException>(() => PlanFrontier.BuildDetailed(null!));

    [Fact]
    public void BuildDetailed_NegativeBudget_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => PlanFrontier.BuildDetailed(Book(), -1));

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
        // Item 2 has no offers; items 1 and 3 still get the split / merged options. Whole stacks: each item must buy a stack of 5,
        // so the cheapest is 50 + 50 and one world costs 50 + 75 either way (the tie goes to the lower world id).
        var book = Book(
            I(1, 2, W(1, (10, 5)), W(2, (15, 5))),
            I(2, 4),
            I(3, 2, W(1, (15, 5)), W(2, (10, 5))));
        // 2026-11-24 whole stacks; was "2w 40g +0 +0% [1@1x2;3@2x2] | 1w 50g +10 +25% [1@1x2;3@1x2]" (partial stacks).
        Assert.Equal("2w 100g +0 +0% [1@1x5;3@2x5] | 1w 125g +25 +25% [1@1x5;3@1x5]", Render(PlanFrontier.Build(book)));
    }

    [Fact]
    public void Build_OneWorldOnly_GivesOneOption()
    {
        // Item 1 needs 3: the 4-stack at 9 (36) beats both stacks (46); the 2-stack alone is short. So it buys 4 (surplus 1).
        var book = Book(I(1, 3, W(7, (5, 2), (9, 4))), I(2, 1, W(7, (100, 1))));
        // 2026-11-24 whole stacks; was "1w 119g +0 +0% [1@7x3;2@7x1]" (partial stacks).
        Assert.Equal("1w 136g +0 +0% [1@7x4;2@7x1]", Render(PlanFrontier.Build(book)));
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
        // Cheapest: item 1 on world 1, item 2 on world 2, a whole stack of 5 each (100). One world: either costs 125; the tie goes
        // to the lower id.
        var book = Book(
            I(1, 2, W(1, (10, 5)), W(2, (15, 5))),
            I(2, 2, W(1, (15, 5)), W(2, (10, 5))));
        // 2026-11-24 whole stacks; was "2w 40g +0 +0% [1@1x2;2@2x2] | 1w 50g +10 +25% [1@1x2;2@1x2]" (partial stacks).
        Assert.Equal("2w 100g +0 +0% [1@1x5;2@2x5] | 1w 125g +25 +25% [1@1x5;2@1x5]", Render(PlanFrontier.Build(book)));
    }

    [Fact]
    public void Build_ItemOnlyOneWorldSupplies_ThatWorldIsKept()
    {
        // Item 1 exists only on world 1, so world 1 can never be dropped; dropping world 2 moves item 2 to world 1, whose only stack
        // is 3 at 8 (24 against 15).
        var book = Book(
            I(1, 1, W(1, (10, 1))),
            I(2, 1, W(1, (8, 3)), W(2, (5, 3))));
        // 2026-11-24 whole stacks; was "2w 15g +0 +0% [1@1x1;2@2x1] | 1w 18g +3 +20% [1@1x1;2@1x1]" (partial stacks).
        Assert.Equal("2w 25g +0 +0% [1@1x1;2@2x3] | 1w 34g +9 +36% [1@1x1;2@1x3]", Render(PlanFrontier.Build(book)));
    }

    [Fact]
    public void Build_ItemMustBeSplit_OneWorldOptionComesFromTheOnlyWorldWithEnough()
    {
        // Need 10. Cheapest takes both 6-stacks (60 + 72 = 132, 12 units); neither world alone has 10, so the one-world option is
        // world 3, the only world with all 10 (200), not reachable by removing a world from the cheapest plan.
        var book = Book(I(1, 10, W(1, (10, 6)), W(2, (12, 6)), W(3, (20, 10))));
        var options = PlanFrontier.Build(book);
        // 2026-11-24 whole stacks; was "2w 108g +0 +0% [1@1x6;1@2x4] | 1w 200g +92 +85.18518518518519% [1@3x10]" (partial stacks).
        Assert.Equal("2w 132g +0 +0% [1@1x6;1@2x6] | 1w 200g +68 +51.515151515151516% [1@3x10]", Render(options));
    }

    [Fact]
    public void Build_SplitWithinTheOptionWhenNoSingleWorldHasEnough()
    {
        // Need 8 of item 1; worlds 1, 2 have one 5-stack each, so both are bought (5 + 10 = 15, 10 units). Item 2 is on world 3 at 1
        // gil and on world 2 at 4. Cheapest: 3 worlds, 16. Dropping world 3 puts item 2 on world 2: 2 worlds, 19.
        var book = Book(
            I(1, 8, W(1, (1, 5)), W(2, (2, 5))),
            I(2, 1, W(2, (4, 1)), W(3, (1, 1))));
        // 2026-11-24 whole stacks; was "3w 12g +0 +0% [1@1x5;1@2x3;2@3x1] | 2w 15g +3 +25% [1@1x5;1@2x3;2@2x1]" (partial stacks).
        Assert.Equal("3w 16g +0 +0% [1@1x5;1@2x5;2@3x1] | 2w 19g +3 +18.75% [1@1x5;1@2x5;2@2x1]", Render(PlanFrontier.Build(book)));
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
        // Same unit price everywhere: the cheapest plan splits 2 + 2 over both worlds (that pair has the newer oldest review), but
        // world 2's 4-stack alone costs the same 20. The first option must be the min-cost plan with the fewest worlds, so the
        // 2-world split is dominated.
        // 2026-11-24 whole stacks: the fixture changed. Was I(1, 4, W(1, (5, 2)), W(2, (5, 9))) with the single option
        // "1@2x4" at 20; with whole stacks that book no longer splits (world 2 must buy its 9-stack), so the premise needs stacks
        // that tie exactly.
        var book = Book(I(1, 4, W(1, (5, 2)), W(2, (5, 2), (5, 4))));
        Assert.Equal("1@1x2;1@2x2", Render(Suggester.Suggest(book, SuggestionMode.Cheapest)));
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
        // Item 1 costs 5 a unit on both worlds; both worlds stay (items 2 and 3 each exist on one only). The cheapest plan buys
        // item 1 on both worlds (4 lines); the fewest-worlds plan buys world 2's 4-stack (3 lines) for the same 27.
        // 2026-11-24 whole stacks: item 1's fixture changed. Was W(1, (5, 2)), W(2, (5, 9)); with whole stacks that book no longer
        // splits. The expected strings are unchanged.
        var book = Book(
            I(1, 4, W(1, (5, 2)), W(2, (5, 2), (5, 4))),
            I(2, 1, W(1, (3, 1))),
            I(3, 1, W(2, (4, 1))));
        Assert.Equal("1@1x2;1@2x2;2@1x1;3@2x1", Render(Suggester.Suggest(book, SuggestionMode.Cheapest)));
        Assert.Equal("2w 27g +0 +0% [1@2x4;2@1x1;3@2x1]", Render(PlanFrontier.Build(book)));
    }

    [Fact]
    public void Build_SurplusIsCheapest_TheOptionBuysTheBiggerStack()
    {
        // Item 1 needs 3. World 1 has one 5-stack at 2 (10 gil, 2 surplus); world 2 sells exactly 3 as 1 + 2 at 4 (12 gil). Item 2
        // is 1 gil on world 2 and 6 on world 1. Cheapest: the surplus stack on world 1 plus world 2 for item 2 (11). One world:
        // world 2 for both (13) beats world 1 for both (16).
        var book = Book(
            I(1, 3, W(1, (2, 5)), W(2, (4, 1), (4, 2))),
            I(2, 1, W(1, (6, 1)), W(2, (1, 1))));
        var options = PlanFrontier.Build(book);
        Assert.Equal("2w 11g +0 +0% [1@1x5;2@2x1] | 1w 13g +2 +18.181818181818183% [1@2x3;2@2x1]", Render(options));
        Assert.True(options[0].Lines.Where(l => l.ItemId == 1).Sum(l => l.Quantity) > 3, "the cheapest option should buy surplus");
    }

    [Fact]
    public void Build_LineQuantitiesAreWholeStacksNotTheNeed()
    {
        // Need 7 from one world whose stacks are 4, 4 and 10 (same unit price): the cheapest cover is 4 + 4 = 8, never 7.
        var book = Book(I(1, 7, W(3, (5, 4), (5, 4), (5, 10))));
        Assert.Equal("1w 40g +0 +0% [1@3x8]", Render(PlanFrontier.Build(book)));
    }

    [Fact]
    public void Build_ElevenWorlds_HeuristicPathsFindTheHandWorkedOptions()
    {
        // The expensive-outlier book plus an item 4 that eight more worlds sell dearly: eleven worlds, above the exhaustive limit
        // (10), so the elimination, forward and improvement paths choose the sets. Cheapest {1,2,3}: 31. Two worlds {1,2}: 32
        // (item 2 moves to world 1 at 11). One world {1}: 1000 + 11 + 10 + 5 = 1026.
        var dear = Enumerable.Range(10, 8).Select(w => W((uint)w, (50, 1))).ToArray();
        var book = Book(
            I(1, 1, W(1, (1000, 1)), W(2, (10, 1))),
            I(2, 1, W(1, (11, 1)), W(3, (10, 1))),
            I(3, 1, W(1, (10, 1)), W(2, (60, 1))),
            I(4, 1, new[] { W(1, (5, 1)), W(2, (1, 1)) }.Concat(dear).ToArray()));
        Assert.Equal(11, book.Items.SelectMany(i => i.Worlds).Select(w => w.WorldId).Distinct().Count());
        Assert.Equal(
            "3w 31g +0 +0% [1@2x1;2@3x1;3@1x1;4@2x1] | 2w 32g +1 +3.225806451612903% [1@2x1;2@1x1;3@1x1;4@2x1] | 1w 1026g +995 +3209.6774193548385% [1@1x1;2@1x1;3@1x1;4@1x1]",
            Render(PlanFrontier.Build(book)));
    }

    [Fact]
    public void Build_ItemPastTheSizeGuard_UsesTheSameGreedyCoverAsTheCheapestPlan()
    {
        // Need 600,001 from one world. Its largest stack (450,000 at 1,000) makes the exact table pass the size guard (more than
        // 1,000,000 totals), so the cheapest plan uses the greedy fallback: 300,000 at 1, 300,000 at 2, then 200,001 at 2 =
        // 1,300,002 for 800,001 units. The exact answer would be 900,002 (300,000 + 200,001 + 100,000 units). With one world the
        // frontier's only set is the cheapest plan's, so it prices the item the same greedy way. (2026-11-24: with the 450,000 stack on a
        // second world instead, world 1 alone fits the guard and the frontier beats the cheapest plan; see
        // BuildDetailed_FirstOptionCanBeatACheapestPlanThatWentGreedy.)
        var book = Book(I(1, 600_001, W(1, (1, 300_000), (2, 300_000), (2, 200_001), (2, 100_000), (1000, 450_000))));
        var cheapest = Suggester.SuggestDetailed(book, SuggestionMode.Cheapest);
        Assert.Equal([1u], cheapest.ApproximateItemIds);
        Assert.Equal("1@1x800001", Render(cheapest.Lines));
        Assert.Equal("1w 1300002g +0 +0% [1@1x800001]", Render(PlanFrontier.Build(book)));
    }

    // ---- BuildDetailed and the work budget ----

    private static OfferBook BudgetBook()
        // The cheapest plan uses three worlds; elimination can reach one.
        => Book(
            I(1, 1, W(1, (1000, 1)), W(2, (10, 1))),
            I(2, 1, W(1, (11, 1)), W(3, (10, 1))),
            I(3, 1, W(1, (10, 1)), W(2, (60, 1))));

    [Fact]
    public void BuildDetailed_DefaultBudget_SameOptionsAsBuild_AndNotExhausted()
    {
        var book = BudgetBook();
        var detailed = PlanFrontier.BuildDetailed(book);
        Assert.False(detailed.BudgetExhausted);
        Assert.Equal(Render(PlanFrontier.Build(book)), Render(detailed.Options));
    }

    [Fact]
    public void BuildDetailed_ZeroBudget_DegradesToTheFixedPlans_AndSaysSo()
    {
        // No search at all: the cheapest plan and the fewest-worlds plan (world 1 for everything, 1021) are all that is left.
        var book = BudgetBook();
        var detailed = PlanFrontier.BuildDetailed(book, 0);
        Assert.True(detailed.BudgetExhausted);
        Assert.Equal(
            "3w 30g +0 +0% [1@2x1;2@3x1;3@1x1] | 1w 1021g +991 +3303.3333333333335% [1@1x1;2@1x1;3@1x1]",
            Render(detailed.Options));
        AssertInvariants(book, detailed.Options, "zero budget");
    }

    /// <summary>
    /// Three items over worlds 1 to 3. The cheapest plan uses all three worlds (12); the fewest-worlds plan buys item 1 on world 1
    /// alone (worlds {1, 2}, 102), but the same two worlds re-optimised split item 1 over both (17), which only the search finds.
    /// </summary>
    private static OfferBook FixedPlansBook()
        => Book(
            I(1, 10, W(1, (1, 5), (10, 10)), W(2, (2, 5), (10, 10)), W(3, (1, 5))),
            I(2, 1, W(1, (1, 1))),
            I(3, 1, W(2, (1, 1))));

    [Fact]
    public void BuildDetailed_ZeroBudget_EvaluatesOnlyTheFixedPlans()
    {
        // 2026-11-24: a budget of 0 used to still re-optimise both fixed plans' worlds (one solve per item each, not counted against
        // the budget), which gave 2w 17g here. Now nothing is solved: the two suggestions as priced are all there is.
        var book = FixedPlansBook();
        var zero = PlanFrontier.BuildDetailed(book, 0);
        Assert.True(zero.BudgetExhausted);
        Assert.Equal(
            "3w 12g +0 +0% [1@1x5;1@3x5;2@1x1;3@2x1] | 2w 102g +90 +750% [1@1x10;2@1x1;3@2x1]",
            Render(zero.Options));
        Assert.Equal(Render(Suggester.Suggest(book, SuggestionMode.FewestWorlds)), Render(zero.Options[1].Lines));
    }

    [Fact]
    public void BuildDetailed_DefaultBudget_ReoptimisesTheFewestWorldsPlansWorlds()
    {
        var detailed = PlanFrontier.BuildDetailed(FixedPlansBook());
        Assert.False(detailed.BudgetExhausted);
        Assert.Equal(2, detailed.Options.Count);
        Assert.Equal(12, detailed.Options[0].Total);
        Assert.Equal("2w 17g [1@1x5;1@2x5;2@1x1;3@2x1]",
            $"{detailed.Options[1].WorldCount}w {detailed.Options[1].Total}g [{Render(detailed.Options[1].Lines)}]");
    }

    [Fact]
    public void BuildDetailed_FirstOptionCanBeatACheapestPlanThatWentGreedy()
    {
        // The review's counterexample (2026-11-24). Need 600,001; world 1 sells 300,000 at 1, 300,000, 200,001 and 100,000 at 2;
        // world 2 sells 450,000 at 1,000. Over both worlds the exact table would need totals up to 1,050,000 (past the guard), so the
        // cheapest plan is greedy: 1,300,002 for 800,001 units on world 1. World 1 alone fits the guard and is solved exactly:
        // 300,000 + 200,001 + 100,000 units for 900,002. The first option is therefore cheaper than the cheapest plan.
        var book = Book(I(1, 600_001,
            W(1, (1, 300_000), (2, 300_000), (2, 200_001), (2, 100_000)),
            W(2, (1000, 450_000))));
        var cheapest = Suggester.SuggestDetailed(book, SuggestionMode.Cheapest);
        Assert.Equal([1u], cheapest.ApproximateItemIds);
        Assert.Equal("1@1x800001", Render(cheapest.Lines));
        Assert.Equal((long?)1_300_002, ExactPrice(book, cheapest.Lines));
        Assert.Equal("1w 900002g +0 +0% [1@1x600001]", Render(PlanFrontier.Build(book)));
    }

    [Fact]
    public void Build_SetWithAHugeCheapUnitStack_IsSolvedExactlyInsideTheSearch()
    {
        // Item 1 needs 50. World 2 sells one 9,999 stack at 2 and 1,999 stacks of 30 at 5: before 2026-11-24 the search's own size
        // guard (totals up to 10,048 over 2,000 stacks) sent world 2 to the greedy fallback, which bought the big stack (19,998). Only
        // the search reaches the two-world option {2, 3} (the cheapest plan uses worlds 1, 2, 3; the fewest-worlds plan world 4):
        // two 30 stacks on world 2 (300) plus items 2 and 3 at 1 each = 302. With the greedy answer the best two-world plan was {1, 4}
        // at 2,060.
        var world2 = new[] { (2, 9_999) }.Concat(Enumerable.Repeat((5, 30), 1_999)).ToArray();
        var book = Book(
            I(1, 50, W(1, (1, 30), (1, 30)), W(2, world2), W(4, (1000, 50))),
            I(2, 1, W(2, (1, 1)), W(4, (1000, 1))),
            I(3, 1, W(3, (1, 1)), W(4, (1000, 1))));
        Assert.Equal(
            "3w 62g [1@1x60;2@2x1;3@3x1] | 2w 302g [1@2x60;2@2x1;3@3x1] | 1w 52000g [1@4x50;2@4x1;3@4x1]",
            string.Join(" | ", PlanFrontier.Build(book).Select(o => $"{o.WorldCount}w {o.Total}g [{Render(o.Lines)}]")));
    }

    [Fact]
    public void Build_LineBeyondAWorldsExactLimit_RePricesToTheSameQuantityAndTotal()
    {
        // World 1 sells 1,000,000 at 1 and 5 at 1: 1,000,005 units, so its exact limit is 999,999 and a line of 1,000,000 (the big stack
        // alone) is legal but above the limit. Every option's lines must re-price, through AllocationState, to the same quantities and
        // total: the frontier must not emit a line the allocation would store differently.
        var book = Book(
            I(1, 1_000_000, W(1, (1, 1_000_000), (1, 5)), W(2, (3, 1_000_000))),
            I(2, 2, W(2, (1, 2))));
        foreach (var budget in new[] { 0L, PlanFrontier.DefaultWorkBudget })
        {
            var options = PlanFrontier.BuildDetailed(book, budget).Options;
            Assert.Equal(1_000_000L + 2, options[0].Total);
            foreach (var o in options)
            {
                var state = new AllocationState(book);
                state.Apply(o.Lines);
                foreach (var line in o.Lines)
                    Assert.Equal(line.Quantity, state.GetQuantity(line.ItemId, line.WorldId));
                Assert.Equal(o.Total, state.Summarize().GrandTotal);
            }
        }
    }

    [Fact]
    public void BuildDetailed_SmallBudgetOnRandomBooks_StillHoldsEveryInvariant()
    {
        var n = 0;
        var exhausted = 0;
        foreach (var book in SmallBooks(31337, 150))
        {
            var detailed = PlanFrontier.BuildDetailed(book, 200);
            if (detailed.BudgetExhausted)
                exhausted++;
            AssertInvariants(book, detailed.Options, $"book {n}: {Render(detailed.Options)}");
            n++;
        }
        _output.WriteLine($"budget 200: {exhausted} of {n} books cut short");
        Assert.True(exhausted > 0, "a budget of 200 should cut some searches short");
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

    private static long SupplyOn(ItemOffers item, IEnumerable<uint> worldIds)
    {
        var set = worldIds.ToHashSet();
        return item.Worlds.Where(w => set.Contains(w.WorldId)).Sum(w => (long)w.UnitsForSale);
    }

    /// <summary>
    /// The whole-stack invariants (2026-11-24; they replace the partial-stack ones, under which every option supplied exactly the
    /// cheapest plan's units, never above <c>Needed</c>, priced cheapest-first).
    /// </summary>
    private static void AssertInvariants(OfferBook book, IReadOnlyList<PlanOption> options, string context)
    {
        Assert.NotEmpty(options);
        var cheapestLines = Suggester.Suggest(book, SuggestionMode.Cheapest);
        var cheapestTotal = ExactPrice(book, cheapestLines);
        Assert.True(cheapestTotal is not null, $"cheapest plan not whole stacks: {context}");

        // First option is the cheapest plan's cost, with zero extra.
        Assert.True(cheapestTotal == options[0].Total, $"first total {options[0].Total} != cheapest {cheapestTotal}: {context}");
        Assert.Equal(0, options[0].ExtraGil);
        Assert.Equal(0.0, options[0].ExtraPercent);

        var allWorlds = book.Items.SelectMany(i => i.Worlds).Select(w => w.WorldId).Distinct().ToList();
        for (var k = 0; k < options.Count; k++)
        {
            var o = options[k];
            if (k > 0)
            {
                Assert.True(o.WorldCount < options[k - 1].WorldCount, $"worlds not strictly decreasing at {k}: {context}");
                Assert.True(o.Total > options[k - 1].Total, $"dominated option at {k}: {context}");
            }

            // The total is the exact whole-stack price of the lines; every line is a legal total for its world.
            Assert.True(ExactPrice(book, o.Lines) == o.Total, $"total is not the exact price of the stacks at {k}: {context}");
            var optionWorlds = o.Lines.Select(l => l.WorldId).Distinct().ToList();
            Assert.True(optionWorlds.Count == o.WorldCount, $"world count mismatch at {k}: {context}");
            Assert.Equal(o.Total - options[0].Total, o.ExtraGil);
            Assert.Equal(options[0].Total == 0 ? 0.0 : o.ExtraGil * 100.0 / options[0].Total, o.ExtraPercent);

            // Lines: ordered, positive, one per (item, world), within stock.
            var keys = o.Lines.Select(l => (l.ItemId, l.WorldId)).ToList();
            Assert.Equal(keys.OrderBy(x => x.ItemId).ThenBy(x => x.WorldId).ToList(), keys);
            Assert.Equal(keys.Count, keys.Distinct().Count());
            foreach (var line in o.Lines)
            {
                Assert.True(line.Quantity > 0);
                var offer = book.Items.Single(i => i.ItemId == line.ItemId).Worlds.Single(w => w.WorldId == line.WorldId);
                Assert.True(line.Quantity <= offer.UnitsForSale, $"over stock at {k}: {context}");
            }

            // Coverage: every item buys at least min(Needed, its supply on the option's worlds), and, capped at Needed, exactly what
            // the cheapest plan covers (min(Needed, supply on every world)): an option never covers less than the cheapest plan.
            foreach (var item in book.Items)
            {
                var bought = o.Lines.Where(l => l.ItemId == item.ItemId).Sum(l => (long)l.Quantity);
                Assert.True(bought >= Math.Min(item.Needed, SupplyOn(item, optionWorlds)), $"item {item.ItemId} under-covered at {k}: {context}");
                Assert.True(Math.Min(bought, item.Needed) == Math.Min(item.Needed, SupplyOn(item, allWorlds)),
                    $"item {item.ItemId} covers less than the cheapest plan at {k}: {context}");
            }
        }

        // Never worse than the two fixed plans.
        var fewestLines = Suggester.Suggest(book, SuggestionMode.FewestWorlds);
        var fewestTotal = ExactPrice(book, fewestLines)!.Value;
        var fewestWorlds = fewestLines.Select(l => l.WorldId).Distinct().Count();
        Assert.True(options.Any(o => o.WorldCount <= fewestWorlds && o.Total <= fewestTotal),
            $"fewest-worlds plan ({fewestWorlds}w {fewestTotal}g) not represented: {context}");
        var cheapestWorlds = cheapestLines.Select(l => l.WorldId).Distinct().Count();
        Assert.Contains(options, o => o.WorldCount <= cheapestWorlds && o.Total <= cheapestTotal);
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
        var shortItems = 0;
        for (var n = 0; n < 200; n++)
        {
            var book = RandomBook(rng, 7, 5, 3, 20, 0.5, 2);
            shortItems += book.Items.Count(i => i.Worlds.Count > 0 && i.Worlds.Sum(w => w.UnitsForSale) < i.Needed);
            AssertInvariants(book, PlanFrontier.Build(book), $"book {n}");
        }
        Assert.True(shortItems > 100, $"only {shortItems} short items: the fixture no longer exercises short supply");
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
                // Shuffle item order and each item's world order. The ladder itself stays as built: whole-stack tie-breaks depend on
                // a stack's position in its world's ladder, which the builder fixes.
                // 2026-11-24 whole stacks: equal-price steps are no longer shuffled within a ladder (they were under partial stacks,
                // where the position of equal-price steps could not change a cost or a quantity).
                var items = book.Items
                    .Select(i => i with { Worlds = i.Worlds.OrderBy(_ => rng.Next()).ToList() })
                    .OrderBy(_ => rng.Next()).ToList();
                var shuffled = new OfferBook(items, book.Skipped);
                Assert.True(expected == Render(PlanFrontier.Build(shuffled)), $"book {n} round {r}");
            }
            n++;
        }
    }

    [Fact]
    public void Build_SameBookTwice_IdenticalOptions()
    {
        // The same instance twice (its offers' stack tables are warm the second time) and an independently built copy.
        var rngA = new Random(8080);
        var rngB = new Random(8080);
        for (var n = 0; n < 100; n++)
        {
            var book = RandomBook(rngA, 8, 6, 9, 20, 0.6, 4);
            var copy = RandomBook(rngB, 8, 6, 9, 20, 0.6, 4);
            var first = Render(PlanFrontier.Build(book));
            Assert.Equal(first, Render(PlanFrontier.Build(book)));
            Assert.Equal(first, Render(PlanFrontier.Build(copy)));
        }
    }

    // ---- brute force on tiny books ----

    /// <summary>Every whole stack of <paramref name="item"/> on the worlds in <paramref name="worldIds"/> (each world's first offer).</summary>
    private static List<PriceStep> StacksOn(ItemOffers item, IEnumerable<uint> worldIds)
    {
        var set = worldIds.ToHashSet();
        return item.Worlds.GroupBy(w => w.WorldId).Select(g => g.First())
            .Where(w => set.Contains(w.WorldId)).SelectMany(w => w.Steps).ToList();
    }

    /// <summary>
    /// The exact whole-stack cost of supplying every item from the worlds in <paramref name="worldIds"/>: per item, the cheapest
    /// subset of those worlds' stacks with at least min(Needed, supply in the whole book) units; null when some item cannot reach
    /// that from these worlds.
    /// </summary>
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

    [Fact]
    public void Build_EveryOptionCostsTheBruteForceMinimumOverItsWorlds_On300Books()
    {
        // Oracle: for the worlds an option visits, no subset of those worlds' whole stacks supplies every item for less.
        var rng = new Random(20261124);
        var options = 0;
        for (var n = 0; n < 300; n++)
        {
            var book = RandomBook(rng, 5, 4, 8, 15, 0.7, 3);
            foreach (var o in PlanFrontier.Build(book))
            {
                var worlds = o.Lines.Select(l => l.WorldId).Distinct().ToList();
                var exact = BruteForceCost(book, worlds);
                Assert.True(exact == o.Total, $"book {n}: option {Render(o)} costs {o.Total}, brute force over its worlds {exact}");
                options++;
            }
        }
        _output.WriteLine($"{options} options checked");
        Assert.True(options > 300);
    }

    /// <summary>
    /// A book with 11 to 18 worlds, above the exhaustive-search limit (10), so the heuristic paths choose the world sets. Each item is
    /// sold on 1 to 5 of them with 1 to 3 stacks, so a brute force over one item's stacks stays at 15 stacks or fewer.
    /// </summary>
    private static OfferBook MediumBook(Random rng)
    {
        var worlds = rng.Next(11, 19);
        var items = new List<ItemOffers>();
        // Every world sells something: item k (k < worlds) is always offered on world k.
        var itemCount = rng.Next(4, 9);
        var anchors = Enumerable.Range(0, worlds).OrderBy(_ => rng.Next()).ToList();
        for (var i = 0; i < itemCount; i++)
        {
            var on = new HashSet<int>();
            for (var a = i; a < worlds; a += itemCount)
                if (on.Count < 5)
                    on.Add(anchors[a]);
            var extra = rng.Next(0, 5 - on.Count + 1);
            while (extra-- > 0)
                on.Add(rng.Next(worlds));
            var offers = on.Take(5).Select(w => W((uint)(300 + w), Enumerable.Range(0, rng.Next(1, 4))
                .Select(_ => (rng.Next(1, 60), rng.Next(1, 9))).ToArray())).ToArray();
            items.Add(I((uint)(1 + i), rng.Next(1, 16), offers));
        }
        return Book(items.ToArray());
    }

    [Fact]
    public void Build_MediumBooks_HeuristicOptions_CostTheBruteForceMinimumOverTheirWorlds_AndHoldEveryInvariant()
    {
        var rng = new Random(1124);
        var options = 0;
        var aboveLimit = 0;
        for (var n = 0; n < 150; n++)
        {
            var book = MediumBook(rng);
            if (book.Items.SelectMany(i => i.Worlds).Select(w => w.WorldId).Distinct().Count() > 10)
                aboveLimit++;
            var built = PlanFrontier.Build(book);
            AssertInvariants(book, built, $"book {n}: {Render(built)}");
            foreach (var o in built)
            {
                var exact = BruteForceCost(book, o.Lines.Select(l => l.WorldId).Distinct().ToList());
                Assert.True(exact == o.Total, $"book {n}: option {Render(o)} costs {o.Total}, brute force over its worlds {exact}");
                options++;
            }
        }
        _output.WriteLine($"{options} options checked, {aboveLimit} of 150 books above the exhaustive limit");
        Assert.True(aboveLimit >= 140, $"only {aboveLimit} books above the exhaustive limit");
    }

    /// <summary>Exact whole-stack minimum cost using at most k worlds, for every k (null = infeasible).</summary>
    private static long?[] ExactMinAtMost(OfferBook book)
    {
        var worldIds = book.Items.SelectMany(i => i.Worlds).Select(w => w.WorldId).Distinct().OrderBy(x => x).ToList();
        var bySize = new long?[worldIds.Count + 1];
        for (var mask = 0; mask < (1 << worldIds.Count); mask++)
        {
            var size = System.Numerics.BitOperations.PopCount((uint)mask);
            var subset = worldIds.Where((_, b) => (mask & (1 << b)) != 0).ToList();
            if (BruteForceCost(book, subset) is { } total && (bySize[size] is null || total < bySize[size]))
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
        // 2026-11-24 whole stacks: the exact optimum is now over whole stacks (it was over partial stacks, priced cheapest-first,
        // supplying exactly the cheapest plan's units).
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
            var exact = ExactMinAtMost(book);

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

        // Measured quality on this fixed seed: the heuristic matches the exact optimum at every reachable world count. A change that
        // loses this is a regression to look at, not a number to update.
        Assert.Equal(0, optionsAbove);
        Assert.Equal(0, countsMissedOrAbove);
    }

    // ---- performance ----

    private static OfferBook LargeBook()
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
        return Book(items.ToArray());
    }

    [Fact]
    public void Build_LargeBook_FinishesWithinAGenerousBound()
    {
        var book = LargeBook();

        var sw = Stopwatch.StartNew();
        var detailed = PlanFrontier.BuildDetailed(book);
        sw.Stop();
        var options = detailed.Options;

        _output.WriteLine($"large book: {options.Count} options, {options[0].WorldCount} -> {options[^1].WorldCount} worlds, {sw.ElapsedMilliseconds} ms, budget exhausted: {detailed.BudgetExhausted}");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        Assert.True(options.Count > 1);
        AssertInvariants(book, options, "large book");

        // Measured on this seed: the options reach 4 worlds, with partial stacks before 2026-11-24 and with whole stacks after (1.9 s,
        // the work budget cut the search). Elimination that misjudges which removals are still feasible stops at 5 here, and so did
        // the whole-stack search before the improvement phase got its own share of the budget, so a rise is a regression to look at,
        // not a number to update.
        Assert.True(options[^1].WorldCount <= 4, $"smallest option has {options[^1].WorldCount} worlds");
    }

    [Fact]
    public void Build_HugeStacksBook_IsBoundedAndStillValid()
    {
        // 60 items by 40 worlds, 30 stacks each of 1..999 units, needs up to 3000: whole-stack covers here are large knapsacks. The
        // work budget must stop the search in bounded time and the result must still be a valid frontier.
        var rng = new Random(606040);
        var items = new List<ItemOffers>();
        for (var i = 0; i < 60; i++)
        {
            var offers = new List<WorldOffer>();
            for (var w = 0; w < 40; w++)
            {
                var steps = new List<(int, int)>();
                for (var s = 0; s < 30; s++)
                    steps.Add((rng.Next(10, 3000), rng.Next(1, 1000)));
                offers.Add(W((uint)(2000 + w), steps.ToArray()));
            }
            items.Add(I((uint)(1 + i), rng.Next(1, 3001), offers.ToArray()));
        }
        var book = Book(items.ToArray());

        var sw = Stopwatch.StartNew();
        var detailed = PlanFrontier.BuildDetailed(book);
        sw.Stop();
        var options = detailed.Options;

        _output.WriteLine($"huge-stacks book: {options.Count} options, {options[0].WorldCount} -> {options[^1].WorldCount} worlds, {sw.ElapsedMilliseconds} ms, budget exhausted: {detailed.BudgetExhausted}");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"took {sw.Elapsed}");
        AssertInvariants(book, options, "huge-stacks book");
    }
}
