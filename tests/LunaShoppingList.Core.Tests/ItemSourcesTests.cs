namespace LunaShoppingList.Core.Tests;

public class ItemSourcesTests
{
    private const uint GilId = 1;
    private const uint Bicolor = 26807;
    private const uint Poetics = 28;
    private const uint StormSeal = 20;

    private sealed class FakeSources : IItemSources
    {
        private readonly Dictionary<uint, IReadOnlyList<ItemSource>> byItem = new();
        public bool IsReady { get; set; } = true;
        public bool HasFailed { get; set; }
        public int ForCalls { get; private set; }

        public FakeSources With(uint itemId, params ItemSource[] sources)
        {
            byItem[itemId] = sources;
            return this;
        }

        public IReadOnlyList<ItemSource> For(uint itemId)
        {
            ForCalls++;
            return byItem.TryGetValue(itemId, out var list) ? list : Array.Empty<ItemSource>();
        }
    }

    private static ItemSource Gil(long cost, string npc = "Merchant", string zone = "Limsa Lominsa", float x = 9.5f, float y = 11.25f)
        => new(ItemSourceKind.GilVendor, "gil", GilId, cost, npc, zone, x, y);

    private static ItemSource Cur(string currency, uint currencyId, long cost, string npc = "Trader", string zone = "Old Sharlayan", float x = 12f, float y = 9.5f)
        => new(ItemSourceKind.CurrencyVendor, currency, currencyId, cost, npc, zone, x, y);

    private static ItemSource Seals(long cost, string npc = "Quartermaster", string zone = "Limsa Lominsa Upper Decks")
        => new(ItemSourceKind.CompanySeals, "Storm Seal", StormSeal, cost, npc, zone, 13f, 12.5f);

    // ---- Thousands ----

    [Theory]
    [InlineData(0L, "0")]
    [InlineData(999L, "999")]
    [InlineData(1000L, "1,000")]
    [InlineData(1200L, "1,200")]
    [InlineData(1234567L, "1,234,567")]
    public void Thousands_GroupsByThree(long value, string expected)
        => Assert.Equal(expected, ItemSourceSummary.Thousands(value));

    // ---- Column text ----

    [Fact]
    public void Column_NotReady_ShowsEllipsis_AndDoesNotAskForSources()
    {
        var fake = new FakeSources { IsReady = false }.With(5, Gil(10));
        Assert.Equal("...", ItemSourceSummary.ColumnText(fake, 5));
        Assert.Equal(0, fake.ForCalls);
    }

    [Fact]
    public void Column_Failed_ShowsDash_AndDoesNotAskForSources()
    {
        var fake = new FakeSources { IsReady = false, HasFailed = true }.With(5, Gil(10));
        Assert.Equal("-", ItemSourceSummary.ColumnText(fake, 5));
        Assert.Equal(0, fake.ForCalls);
    }

    [Fact]
    public void Column_NoSources_ShowsDash()
        => Assert.Equal("-", ItemSourceSummary.ColumnText(new FakeSources(), 5));

    [Fact]
    public void Column_OneGilSource()
        => Assert.Equal("1,200 gil", ItemSourceSummary.ColumnText(new FakeSources().With(5, Gil(1200)), 5));

    [Fact]
    public void Column_OneCurrencySource()
        => Assert.Equal("120 Bicolor Gemstone", ItemSourceSummary.ColumnText(new FakeSources().With(5, Cur("Bicolor Gemstone", Bicolor, 120)), 5));

    [Fact]
    public void Column_Several_ShowsCheapestGilFirst_PlusCountOfOthers()
    {
        var fake = new FakeSources().With(5,
            Cur("Bicolor Gemstone", Bicolor, 3),
            Gil(1500, npc: "B"),
            Gil(1200, npc: "A"));
        Assert.Equal("1,200 gil +2", ItemSourceSummary.ColumnText(fake, 5));
    }

    [Fact]
    public void Column_Several_CurrenciesOnly_FirstByCurrencyName()
    {
        var fake = new FakeSources().With(5,
            Cur("Allagan Tomestone of Poetics", Poetics, 50),
            Cur("Bicolor Gemstone", Bicolor, 3));
        Assert.Equal("50 Allagan Tomestone of Poetics +1", ItemSourceSummary.ColumnText(fake, 5));
    }

    [Fact]
    public void Column_CountsOnlyDistinctSources()
    {
        var fake = new FakeSources().With(5, Gil(1200), Gil(1200), Gil(1200));
        Assert.Equal("1,200 gil", ItemSourceSummary.ColumnText(fake, 5));
    }

    // ---- Ordering and duplicates ----

    [Fact]
    public void Ordered_GilVendorsFirst_ThenCurrenciesByName_ThenCost()
    {
        var input = new[]
        {
            Seals(200),
            Cur("Bicolor Gemstone", Bicolor, 40),
            Cur("Allagan Tomestone of Poetics", Poetics, 50),
            Gil(900),
            Cur("Bicolor Gemstone", Bicolor, 4),
            Gil(300),
        };
        var ordered = ItemSourceSummary.Ordered(input);
        Assert.Equal(
            new[] { "300 gil", "900 gil", "50 Allagan Tomestone of Poetics", "4 Bicolor Gemstone", "40 Bicolor Gemstone", "200 Storm Seal" },
            ordered.Select(ItemSourceSummary.CostText).ToArray());
    }

    [Fact]
    public void Ordered_GilBeforeCurrency_EvenWhenGilCostsMore()
    {
        var ordered = ItemSourceSummary.Ordered(new[] { Cur("Bicolor Gemstone", Bicolor, 1), Gil(99999) });
        Assert.Equal(ItemSourceKind.GilVendor, ordered[0].Kind);
    }

    [Fact]
    public void Ordered_CurrencyNameComparisonIgnoresCase()
    {
        var ordered = ItemSourceSummary.Ordered(new[] { Cur("bicolor Gemstone", Bicolor, 1), Cur("Allagan Tomestone", Poetics, 1), Cur("Zeta", 9, 1) });
        Assert.Equal(new[] { "Allagan Tomestone", "bicolor Gemstone", "Zeta" }, ordered.Select(s => s.CurrencyName).ToArray());
    }

    [Fact]
    public void Ordered_SameCostTie_NamedVendorsFirst_ThenByNpcName()
    {
        var ordered = ItemSourceSummary.Ordered(new[] { Gil(100, npc: "", zone: ""), Gil(100, npc: "Zed"), Gil(100, npc: "Abe") });
        Assert.Equal(new[] { "Abe", "Zed", "" }, ordered.Select(s => s.NpcName).ToArray());
    }

    [Fact]
    public void Ordered_SameCostTie_PlacedVendorsBeforeUnplaced()
    {
        var ordered = ItemSourceSummary.Ordered(new[] { Gil(100, npc: "", zone: ""), Gil(100, npc: "Abe", zone: ""), Gil(100, npc: "Zed", zone: "Limsa") });
        Assert.Equal(new[] { "Zed", "Abe", "" }, ordered.Select(s => s.NpcName).ToArray());
    }

    [Fact]
    public void Ordered_RemovesSameNpcZoneCurrencyAndCost()
    {
        var ordered = ItemSourceSummary.Ordered(new[] { Gil(100), Gil(100, x: 1, y: 1), Gil(100) });
        Assert.Single(ordered);
    }

    [Fact]
    public void Ordered_KeepsSameNameNpcsInDifferentZones()
    {
        var ordered = ItemSourceSummary.Ordered(new[] { Gil(100, npc: "Material Supplier", zone: "Limsa"), Gil(100, npc: "Material Supplier", zone: "Gridania") });
        Assert.Equal(2, ordered.Count);
    }

    [Fact]
    public void Ordered_KeepsSameCurrencyIdWithDifferentCurrencyText()
    {
        var ordered = ItemSourceSummary.Ordered(new[]
        {
            Cur("Allagan Tomestone of Poetics", Poetics, 500),
            Cur("Allagan Tomestone of Poetics + 2 Unidentifiable Shell", Poetics, 500),
            Cur("Allagan Tomestone of Poetics (for 5)", Poetics, 500),
        });
        Assert.Equal(3, ordered.Count);
    }

    [Fact]
    public void Ordered_KeepsSameCurrencyWithDifferentKind()
    {
        var seal = new ItemSource(ItemSourceKind.CompanySeals, "Storm Seal", StormSeal, 200, "Q", "Limsa", 1, 1);
        var ordered = ItemSourceSummary.Ordered(new[] { seal, seal with { Kind = ItemSourceKind.CurrencyVendor } });
        Assert.Equal(2, ordered.Count);
    }

    [Fact]
    public void Ordered_KeepsDifferentCost_DifferentCurrency_DifferentNpc()
    {
        var ordered = ItemSourceSummary.Ordered(new[]
        {
            Gil(100), Gil(101), Gil(100, npc: "Other"),
            Cur("Bicolor Gemstone", Bicolor, 100), Cur("Bicolor Gemstone", 99, 100),
        });
        Assert.Equal(5, ordered.Count);
    }

    [Fact]
    public void Ordered_UnresolvedSourcesAreNeverDropped()
    {
        var ordered = ItemSourceSummary.Ordered(new[] { Gil(100, npc: "", zone: "", x: 0, y: 0), Cur("Bicolor Gemstone", Bicolor, 5, npc: "", zone: "", x: 0, y: 0) });
        Assert.Equal(2, ordered.Count);
    }

    [Fact]
    public void Ordered_Empty()
        => Assert.Empty(ItemSourceSummary.Ordered(Array.Empty<ItemSource>()));

    // ---- Tooltip line ----

    [Fact]
    public void Line_FullyResolved()
        => Assert.Equal("1,200 gil - Merchant, Limsa Lominsa (9.5, 11.3)", ItemSourceSummary.Line(Gil(1200, x: 9.5f, y: 11.26f)));

    [Fact]
    public void Line_ZoneWithoutCoordinates()
        => Assert.Equal("40 Bicolor Gemstone - Trader, Old Sharlayan", ItemSourceSummary.Line(Cur("Bicolor Gemstone", Bicolor, 40, x: 0, y: 0)));

    [Fact]
    public void Line_NpcWithoutZone()
        => Assert.Equal("40 Bicolor Gemstone - Trader", ItemSourceSummary.Line(Cur("Bicolor Gemstone", Bicolor, 40, zone: "", x: 0, y: 0)));

    [Fact]
    public void Line_NpcWithoutZone_NeverPrintsCoordinates()
        => Assert.Equal("40 Bicolor Gemstone - Trader", ItemSourceSummary.Line(Cur("Bicolor Gemstone", Bicolor, 40, zone: "", x: 5, y: 6)));

    [Fact]
    public void Line_NoNpc_SaysVendorUnknown()
        => Assert.Equal("1,200 gil - vendor unknown", ItemSourceSummary.Line(Gil(1200, npc: "", zone: "", x: 0, y: 0)));

    [Fact]
    public void Line_UsesInvariantDecimalPoint()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("1,200 gil - Merchant, Limsa Lominsa (9.5, 11.3)", ItemSourceSummary.Line(Gil(1200, x: 9.5f, y: 11.26f)));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = saved;
        }
    }

    // ---- Cap ----

    private static IReadOnlyList<ItemSource> Many(int n) => Enumerable.Range(1, n).Select(i => Gil(i)).ToList();

    [Fact]
    public void Capped_AtLimit_ShowsAll_NoMore()
    {
        var (shown, more) = ItemSourceSummary.Capped(Many(10), 10);
        Assert.Equal(10, shown.Count);
        Assert.Equal(0, more);
    }

    [Fact]
    public void Capped_OneOver_ShowsLimit_OneMore()
    {
        var (shown, more) = ItemSourceSummary.Capped(Many(11), 10);
        Assert.Equal(10, shown.Count);
        Assert.Equal(1, more);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, shown.Select(s => s.Cost).ToArray());
    }

    [Fact]
    public void Capped_ZeroLimit_ShowsNone()
    {
        var (shown, more) = ItemSourceSummary.Capped(Many(3), 0);
        Assert.Empty(shown);
        Assert.Equal(3, more);
    }

    [Fact]
    public void Capped_NegativeLimit_TreatedAsZero()
    {
        var (shown, more) = ItemSourceSummary.Capped(Many(3), -1);
        Assert.Empty(shown);
        Assert.Equal(3, more);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(-1, "")]
    [InlineData(1, "+1 more")]
    [InlineData(25, "+25 more")]
    public void MoreText(int more, string expected) => Assert.Equal(expected, ItemSourceSummary.MoreText(more));

    [Fact]
    public void DefaultTooltipLines_IsTen() => Assert.Equal(10, ItemSourceSummary.DefaultTooltipLines);

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => ItemSourceSummary.Ordered(null!));
        Assert.Throws<ArgumentNullException>(() => ItemSourceSummary.ColumnText((IItemSources)null!, 1));
        Assert.Throws<ArgumentNullException>(() => ItemSourceSummary.ColumnText((IReadOnlyList<ItemSource>)null!));
        Assert.Throws<ArgumentNullException>(() => ItemSourceSummary.Line(null!));
        Assert.Throws<ArgumentNullException>(() => ItemSourceSummary.CostText(null!));
        Assert.Throws<ArgumentNullException>(() => ItemSourceSummary.Capped(null!, 1));
    }

    // ---- Map coordinates ----

    [Theory]
    [InlineData(0f, (ushort)100, (short)0, 21.48f)]
    [InlineData(0f, (ushort)200, (short)0, 11.24f)]
    [InlineData(100f, (ushort)100, (short)0, 23.48f)]
    [InlineData(0f, (ushort)100, (short)100, 23.48f)]
    [InlineData(-1024f, (ushort)100, (short)0, 1f)]
    [InlineData(1024f, (ushort)100, (short)0, 41.96f)]
    [InlineData(-256f, (ushort)400, (short)0, 1f)]
    [InlineData(100f, (ushort)100, (short)-50, 22.48f)]
    public void MapCoordinate_Formula(float world, ushort sizeFactor, short offset, float expected)
        => Assert.Equal(expected, MapCoordinates.ToMap(world, sizeFactor, offset), 3);

    [Fact]
    public void MapCoordinate_ZeroSizeFactor_IsZero()
        => Assert.Equal(0f, MapCoordinates.ToMap(100f, 0, 0));
}
