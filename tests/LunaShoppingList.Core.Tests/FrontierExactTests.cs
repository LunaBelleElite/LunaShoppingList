using System.Text;
using LunaShoppingList.Core;
using Xunit.Abstractions;

namespace LunaShoppingList.Core.Tests;

/// <summary>
/// Exactness of <see cref="PlanFrontier.BuildDetailed(OfferBook)"/> above ten worlds, against <see cref="FrontierOracle"/>
/// (the true cheapest total over every set of at most k worlds). Written 2026-11-25 as the RED tests for the exact world-set search (above ten worlds the options came from
/// a heuristic and these failed
/// until the exact phase was wired in); green since it was wired (2026-11-25), and the trait that marked them red is gone.
/// </summary>
public class FrontierExactTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ITestOutputHelper _output;

    public FrontierExactTests(ITestOutputHelper output) => _output = output;

    private static WorldOffer W(uint id, params (int Price, int Qty)[] steps)
        => new(id, "W" + id, steps
            .Select((s, n) => new PriceStep(s.Price, s.Qty, false, T0.AddMinutes(-n)))
            .OrderBy(p => p.UnitPrice).ThenByDescending(p => p.LastReview).ToList());

    private static ItemOffers I(uint id, int needed, params WorldOffer[] worlds) => new(id, needed, worlds.ToList());

    private static OfferBook Book(params ItemOffers[] items)
        => new(items.OrderBy(i => i.ItemId).ToList(), new List<SkippedItem>());

    /// <summary>The cheapest total among options with at most <paramref name="k"/> worlds; <c>long.MaxValue</c> when there is none.</summary>
    private static long BestAtMost(IReadOnlyList<PlanOption> options, int k)
    {
        var best = long.MaxValue;
        foreach (var o in options)
            if (o.WorldCount <= k && o.Total < best)
                best = o.Total;
        return best;
    }

    private sealed record Findings(int Books, int Overpaid, int Missed, int BelowOracle, int OffOptimumOptions, double WorstGapPercent, string Detail);

    /// <summary>Compares the options of one book with the oracle at every world count 1..n.</summary>
    private static (int Overpaid, int Missed, int Below, int Off, double Worst, string Detail) Compare(OfferBook book, string name)
    {
        var result = PlanFrontier.BuildDetailed(book);
        var options = result.Options;
        var exact = FrontierOracle.ExactAtMost(book);
        Assert.True(result.ProvenWorldCount == exact.Length - 1, $"{name}: ProvenWorldCount {result.ProvenWorldCount}, expected {exact.Length - 1} (every world count proven)");
        int overpaid = 0, missed = 0, below = 0, off = 0;
        double worst = 0;
        var detail = new StringBuilder();
        for (var k = 1; k < exact.Length; k++)
        {
            var got = BestAtMost(options, k);
            if (got == exact[k])
                continue;
            if (exact[k] == long.MaxValue)
            {
                below++;
                detail.Append($" [{name} k={k}: options {got} but the oracle has no plan]");
            }
            else if (got < exact[k])
            {
                below++;
                detail.Append($" [{name} k={k}: options {got} BELOW oracle {exact[k]}]");
            }
            else if (got == long.MaxValue)
            {
                missed++;
                detail.Append($" [{name} k={k}: no option, oracle {exact[k]}]");
            }
            else
            {
                overpaid++;
                var gap = 100.0 * (got - exact[k]) / exact[k];
                worst = Math.Max(worst, gap);
                detail.Append($" [{name} k={k}: options {got} oracle {exact[k]} (+{gap:F2}%)]");
            }
        }
        foreach (var o in options)
            if (o.WorldCount < exact.Length && o.WorldCount > 0 && o.Total != exact[o.WorldCount])
                off++;
        return (overpaid, missed, below, off, worst, detail.ToString());
    }

    private static readonly Dictionary<string, Findings> Cache = new();

    private static Findings Run(string family, Func<Random, int, OfferBook> gen, int seed, int wc, int books)
    {
        var key = $"{family}/{seed}/{wc}/{books}";
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var hit))
                return hit;
            var rng = new Random(seed + wc);
            int overpaid = 0, missed = 0, below = 0, off = 0, affected = 0;
            double worst = 0;
            var detail = new StringBuilder();
            for (var b = 0; b < books; b++)
            {
                var book = gen(rng, wc);
                var r = Compare(book, $"book {b}");
                overpaid += r.Overpaid;
                missed += r.Missed;
                below += r.Below;
                off += r.Off;
                worst = Math.Max(worst, r.Worst);
                if (r.Overpaid + r.Missed + r.Below + r.Off > 0)
                    affected++;
                detail.Append(r.Detail);
            }
            var findings = new Findings(books, overpaid, missed, below, off, worst,
                $"{family} {wc} worlds seed {seed + wc}: {affected} of {books} books differ; {overpaid} world counts overpaid (worst +{worst:F2}%), {missed} world counts missing, {below} below the oracle, {off} options not at their world count's optimum.{detail}");
            Cache[key] = findings;
            return findings;
        }
    }

    private const int Books = 60;

    /// <summary>Oracle test: every world count's best option is the true optimum. RED today above ten worlds (heuristic).</summary>
    [Theory]
    [InlineData("Medium", 11)]
    [InlineData("Medium", 14)]
    [InlineData("Medium", 16)]
    [InlineData("Dense", 11)]
    [InlineData("Dense", 14)]
    [InlineData("Dense", 16)]
    public void BuildDetailed_AboveTenWorlds_EveryWorldCountIsTheExactOptimum(string family, int worlds)
    {
        var f = family == "Medium"
            ? Run("Medium", FrontierOracle.MediumBook, 20261124, worlds, Books)
            : Run("Dense", FrontierOracle.DenseBook, 5150, worlds, Books);
        _output.WriteLine(f.Detail);
        Assert.True(f.Overpaid == 0 && f.Missed == 0 && f.OffOptimumOptions == 0, f.Detail);
    }

    /// <summary>Sanity (green today): an option can never beat the true optimum, so the oracle and the options agree on what is possible.</summary>
    [Theory]
    [InlineData("Medium", 11)]
    [InlineData("Medium", 14)]
    [InlineData("Medium", 16)]
    [InlineData("Dense", 11)]
    [InlineData("Dense", 14)]
    [InlineData("Dense", 16)]
    public void BuildDetailed_AboveTenWorlds_NeverBelowTheOracle(string family, int worlds)
    {
        var f = family == "Medium"
            ? Run("Medium", FrontierOracle.MediumBook, 20261124, worlds, Books)
            : Run("Dense", FrontierOracle.DenseBook, 5150, worlds, Books);
        Assert.True(f.BelowOracle == 0, f.Detail);
    }

    // ---- constructed books where the heuristic is known to be wrong (found by scanning the generators on 2026-11-25) ----

    /// <summary>
    /// Pins one book individually: the best option with at most <paramref name="k"/> worlds costs exactly <paramref name="optimum"/>
    /// (a literal, also checked against the oracle), and every other world count agrees with the oracle too.
    /// </summary>
    private void AssertExactAt(OfferBook book, int k, long optimum)
    {
        var exact = FrontierOracle.ExactAtMost(book);
        Assert.Equal(optimum, exact[k]);
        var result = PlanFrontier.BuildDetailed(book);
        Assert.Equal(exact.Length - 1, result.ProvenWorldCount);
        var options = result.Options;
        _output.WriteLine(string.Join(" | ", options.Select(o => $"{o.WorldCount}w {o.Total}g")));
        var got = BestAtMost(options, k);
        Assert.True(got == optimum, got == long.MaxValue
            ? $"no option with at most {k} worlds, but {optimum} gil is possible"
            : $"best option with at most {k} worlds costs {got}, the optimum is {optimum} (+{100.0 * (got - optimum) / optimum:F2}%)");
        var all = Compare(book, "book");
        Assert.True(all.Overpaid == 0 && all.Missed == 0 && all.Below == 0, all.Detail);
    }

    /// <summary>11 worlds, 5 items. No option has 4 or fewer worlds, though 4 worlds cost 825 (Medium generator, seed 70).</summary>
    [Fact]
    public void BuildDetailed_ElevenWorldBook_FourWorldPlanExistsAndIsFound()
    {
        var book = Book(
            I(1, 2, W(308, (7, 1), (17, 4), (34, 4)), W(306, (4, 8), (29, 5), (33, 1)), W(309, (2, 8), (27, 6)), W(310, (48, 1))),
            I(2, 3, W(310, (10, 2)), W(307, (49, 4))),
            I(3, 15, W(305, (16, 4), (45, 2)), W(301, (32, 2)), W(310, (11, 1), (13, 6), (16, 5))),
            I(4, 2, W(300, (44, 2)), W(304, (52, 2)), W(308, (24, 5), (40, 1), (51, 6))),
            I(5, 3, W(302, (16, 7), (24, 6), (32, 7)), W(303, (5, 1)), W(310, (29, 8))));
        AssertExactAt(book, 4, 825);
    }

    /// <summary>11 worlds, 4 items. The five-world option costs 918 where 696 is possible, 32 percent too much (Medium generator, seed 215).</summary>
    [Fact]
    public void BuildDetailed_ElevenWorldBook_FiveWorldPlanIsNotOverpaid()
    {
        var book = Book(
            I(1, 11, W(301, (18, 2), (18, 4)), W(310, (20, 2), (23, 5)), W(307, (14, 5), (48, 7), (54, 8)), W(303, (45, 4))),
            I(2, 2, W(305, (54, 2)), W(304, (34, 4)), W(309, (36, 3))),
            I(3, 9, W(303, (38, 4)), W(302, (15, 4), (52, 3)), W(300, (28, 6), (54, 6)), W(309, (22, 3), (59, 5))),
            I(4, 14, W(306, (20, 2), (24, 2)), W(308, (1, 8), (28, 6))));
        AssertExactAt(book, 5, 696);
    }

    /// <summary>11 worlds, 4 items. The four-world option costs 895 where 792 is possible, 13 percent too much (Medium generator, seed 303).</summary>
    [Fact]
    public void BuildDetailed_ElevenWorldBook_FourWorldPlanIsNotOverpaid()
    {
        var book = Book(
            I(1, 9, W(308, (14, 7)), W(304, (11, 2), (38, 1), (53, 4)), W(301, (15, 1), (27, 4)), W(300, (16, 2))),
            I(2, 10, W(303, (52, 2)), W(306, (7, 8)), W(305, (34, 6), (38, 4)), W(309, (45, 3))),
            I(3, 6, W(310, (13, 7), (40, 7), (50, 5)), W(307, (13, 3), (54, 2)), W(300, (11, 2), (16, 8), (21, 6))),
            I(4, 8, W(302, (42, 6)), W(309, (46, 8)), W(305, (41, 8))));
        AssertExactAt(book, 4, 792);
    }
}
