using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using LunaShoppingList.Core;
using Xunit.Abstractions;

namespace LunaShoppingList.Core.Tests;

/// <summary>
/// The exact world-set phase as wired into <see cref="PlanFrontier"/> (budget, proven count, determinism, cancellation). The oracle
/// equality tests live in <see cref="FrontierExactTests"/>; the phase itself is tested directly in <see cref="FrontierSearchExactTests"/>.
/// </summary>
public class PlanFrontierExactTests
{
    private readonly ITestOutputHelper _output;

    public PlanFrontierExactTests(ITestOutputHelper output) => _output = output;

    private static OfferBook Forty() => FrontierOracle.AllDcBook(102, new[] { 5, 5, 5, 5, 5, 5, 5, 5 }, 20);

    private static int WorldsIn(OfferBook book) => book.Items.SelectMany(i => i.Worlds).Select(w => w.WorldId).Distinct().Count();

    private static string OptionsOf(PlanFrontierResult r)
        => string.Join("|", r.Options.Select(o =>
            $"{o.WorldCount}w{o.Total}g[{string.Join(",", o.Lines.Select(l => $"{l.ItemId}@{l.WorldId}x{l.Quantity}"))}]"));

    private static string Describe(PlanFrontierResult r) => $"proven={r.ProvenWorldCount};cut={r.BudgetExhausted};{OptionsOf(r)}";

    /// <summary>Structural checks of a result that do not need an oracle: ordering, extras, world counts, stock, and each total is the exact whole-stack price of its lines.</summary>
    private static void AssertValid(OfferBook book, PlanFrontierResult result)
    {
        var options = result.Options;
        Assert.NotEmpty(options);
        Assert.Equal(0, options[0].ExtraGil);
        for (var k = 0; k < options.Count; k++)
        {
            var o = options[k];
            if (k > 0)
            {
                Assert.True(o.WorldCount < options[k - 1].WorldCount, $"worlds not strictly decreasing at {k}");
                Assert.True(o.Total > options[k - 1].Total, $"dominated option at {k}");
            }
            var worlds = o.Lines.Select(l => l.WorldId).Distinct().ToList();
            Assert.Equal(o.WorldCount, worlds.Count);
            Assert.Equal(o.Total - options[0].Total, o.ExtraGil);
            foreach (var line in o.Lines)
            {
                Assert.True(line.Quantity > 0);
                var offer = book.Items.Single(i => i.ItemId == line.ItemId).Worlds.Single(w => w.WorldId == line.WorldId);
                Assert.True(line.Quantity <= offer.UnitsForSale, $"over stock at {k}");
            }
            long priced = 0;
            foreach (var line in o.Lines)
            {
                var offer = book.Items.Single(i => i.ItemId == line.ItemId).Worlds.Single(w => w.WorldId == line.WorldId);
                priced += offer.ExactCost(line.Quantity) ?? throw new Xunit.Sdk.XunitException($"line {line.ItemId}x{line.Quantity} is not a legal whole-stack total");
            }
            Assert.Equal(priced, o.Total);
        }
        Assert.InRange(result.ProvenWorldCount, 0, WorldsIn(book));
    }

    /// <summary>
    /// Written 2026-11-25 for task exact-t7 to pin R10 (skip exact upgrades that cannot prune), which pays only on the large books: on
    /// the 40-world book switching it off costs about 2 percent and no work bound tells it apart. A 56-world all-data-center book
    /// (seed 2104, 8 x 7 worlds, 45 items) with an exact budget of 1e9: measured 8 world counts proven with R10, 3 without it.
    /// </summary>
    [Fact]
    public void SpeedRules_FiftySixWorldBook_ProvesMoreWorldCountsAtOneBillion()
    {
        var book = FrontierOracle.AllDcBook(2104, new[] { 7, 7, 7, 7, 7, 7, 7, 7 }, 45);
        Assert.Equal(56, WorldsIn(book));
        var result = PlanFrontier.BuildDetailed(book, PlanFrontier.DefaultWorkBudget, 1_000_000_000, CancellationToken.None);
        _output.WriteLine($"56 worlds: proven {result.ProvenWorldCount}, cut {result.BudgetExhausted}");
        Assert.True(result.ProvenWorldCount >= 7, $"proven {result.ProvenWorldCount} of 56 at 1e9: R10 is not skipping futile upgrades (measured 8 with it, 3 without)");
        AssertValid(book, result);
    }

    [Fact]
    public void Determinism_SameBookTwice_SameOptionsSameProvenCount()
    {
        var book = Forty();
        var a = PlanFrontier.BuildDetailed(book, PlanFrontier.DefaultWorkBudget, 1_000_000_000, CancellationToken.None);
        var b = PlanFrontier.BuildDetailed(book, PlanFrontier.DefaultWorkBudget, 1_000_000_000, CancellationToken.None);
        _output.WriteLine($"{WorldsIn(book)} worlds: proven {a.ProvenWorldCount}, cut {a.BudgetExhausted}, {a.Options.Count} options");
        Assert.True(a.ProvenWorldCount > 0, "the phase proved nothing at 1e9, so the test does not exercise it");
        Assert.Equal(Describe(a), Describe(b));
    }

    [Fact]
    public void Budget_TooSmall_ProvenBelowN_AndOptionsStillValid()
    {
        var book = Forty();
        var n = WorldsIn(book);
        var result = PlanFrontier.BuildDetailed(book, PlanFrontier.DefaultWorkBudget, 300_000_000, CancellationToken.None);
        _output.WriteLine($"{n} worlds: proven {result.ProvenWorldCount}, cut {result.BudgetExhausted}, {result.Options.Count} options");
        Assert.Equal(40, n);
        Assert.InRange(result.ProvenWorldCount, 1, n - 1);
        Assert.True(result.BudgetExhausted);
        AssertValid(book, result);
    }

    /// <summary>
    /// Written for task exact-t4 (the proven count was only asserted above ten worlds). A book of at most ten worlds searched to the end
    /// reports every world count as proven, the exhaustive branch of the wiring.
    /// </summary>
    [Fact]
    public void ProvenWorldCount_BookOfTenWorldsOrFewer_IsTheWorldCount()
    {
        var book = FrontierOracle.MediumBook(new Random(5), 8);
        var n = WorldsIn(book);
        Assert.InRange(n, 2, 10);
        var result = PlanFrontier.BuildDetailed(book);
        Assert.False(result.BudgetExhausted);
        Assert.Equal(n, result.ProvenWorldCount);
    }

    /// <summary>
    /// Written for task exact-t4: through <see cref="PlanFrontier"/> a book with an item past the exact solver's size guard (the gate of the
    /// exact phase) reports nothing proven, whatever the budget, and still returns its heuristic options.
    /// </summary>
    [Fact]
    public void ProvenWorldCount_ItemPastTheSizeGuard_IsZero()
    {
        static WorldOffer W(uint id, params (int Price, int Qty)[] steps)
            => new(id, "W" + id, steps.Select((s, n) => new PriceStep(s.Price, s.Qty, false, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(-n)))
                .OrderBy(p => p.UnitPrice).ThenByDescending(p => p.LastReview).ToList());
        var items = new List<ItemOffers>
        {
            new(1, 600_001, new List<WorldOffer> { W(1, (1, 300_000), (2, 300_000), (2, 200_001), (2, 100_000), (1000, 450_000)) }),
        };
        for (var w = 2; w <= 12; w++)
            items.Add(new ItemOffers((uint)w, 1, new List<WorldOffer> { W((uint)w, (5, 1)), W(1, (6, 1)) }));
        var book = new OfferBook(items.OrderBy(i => i.ItemId).ToList(), new List<SkippedItem>());
        Assert.Equal(12, WorldsIn(book));
        var result = PlanFrontier.BuildDetailed(book);
        Assert.Equal(0, result.ProvenWorldCount);
        Assert.NotEmpty(result.Options);
    }

    [Fact]
    public void Cancellation_PreCancelledToken_Throws()
    {
        var book = FrontierOracle.MediumBook(new Random(5), 12);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(
            () => PlanFrontier.BuildDetailed(book, PlanFrontier.DefaultWorkBudget, PlanFrontier.DefaultExactWorkBudget, cts.Token));
    }

    /// <summary>
    /// Written for task exact-t4 to kill mutant M12 (the check at the top of <c>BuildDetailed</c> removed). The 12-world test above
    /// cannot tell that check from the one in <c>Continue</c>, because the first <c>Continue</c> of any real search throws too. A book
    /// with no items asks for no search at all, so only the top check can throw here (the documented "cancelled before the search").
    /// </summary>
    [Fact]
    public void Cancellation_PreCancelledToken_EmptyBook_StillThrows()
    {
        var book = new OfferBook(new List<ItemOffers>(), new List<SkippedItem>());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(
            () => PlanFrontier.BuildDetailed(book, PlanFrontier.DefaultWorkBudget, PlanFrontier.DefaultExactWorkBudget, cts.Token));
    }

    [Fact]
    public void Cancellation_DuringExactPhase_ThrowsQuickly()
    {
        // 48 worlds, 30 items, all-data-center shape: the exact phase here needs billions of units, far more than 100 ms.
        var book = FrontierOracle.AllDcBook(104, new[] { 8, 8, 8, 8, 8, 8 }, 30);
        Assert.Equal(48, WorldsIn(book));
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));
        var sw = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(
            () => PlanFrontier.BuildDetailed(book, PlanFrontier.DefaultWorkBudget, PlanFrontier.DefaultExactWorkBudget, cts.Token));
        sw.Stop();
        _output.WriteLine($"cancelled after {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    /// <summary>
    /// With an exact budget of zero the options are exactly what the heuristic alone gave before the exact phase existed. The expected
    /// fingerprint is a literal taken on 2026-11-25 from the code before it was wired (the same 20 books, the heuristic only).
    /// </summary>
    [Fact]
    public void ZeroExactBudget_SameOptionsAsToday()
    {
        var text = new StringBuilder();
        for (var i = 0; i < 20; i++)
        {
            var book = FrontierOracle.MediumBook(new Random(7000 + i), 11 + i % 6);
            var r = PlanFrontier.BuildDetailed(book, PlanFrontier.DefaultWorkBudget, 0, CancellationToken.None);
            Assert.Equal(0, r.ProvenWorldCount);
            text.Append(OptionsOf(r)).Append('\n');
        }
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
        _output.WriteLine(fingerprint);
        Assert.Equal("313DC3C3101A3252", fingerprint);
    }
}
