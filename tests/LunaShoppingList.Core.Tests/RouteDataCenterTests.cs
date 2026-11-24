namespace LunaShoppingList.Core.Tests;

public class RouteDataCenterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static WorldOffer World(uint id, string name, int price) => new(id, name, new[] { new PriceStep(price, 99, false, T0) });

    // Worlds: 1 Zodiark(Light) 2 Alpha(Light) 3 Famfrit(Primal) 4 Aether1(Primal) 5 Mystery(unknown)
    private static OfferBook Book() => new(new[]
    {
        new ItemOffers(1, 1, new[]
        {
            World(1, "Zodiark", 10), World(2, "Alpha", 20), World(3, "Famfrit", 30),
            World(4, "Aaa", 40), World(5, "Mystery", 50),
        }),
    }, Array.Empty<SkippedItem>());

    private static AllocationLine[] All() => new[]
    {
        new AllocationLine(1, 1, 1), new AllocationLine(1, 2, 2), new AllocationLine(1, 3, 3),
        new AllocationLine(1, 4, 4), new AllocationLine(1, 5, 5),
    };

    private static string? Dc(uint w) => w switch { 1 or 2 => "Light", 3 or 4 => "Primal", _ => null };

    private static string[] Names(RouteSummary s) => s.Worlds.Select(w => w.WorldName).ToArray();

    [Fact]
    public void NullMapKeepsTodaysOrderAndHasNoGroups()
    {
        var s = RouteSummarizer.Summarize(Book(), All());
        Assert.Equal(new[] { "Aaa", "Alpha", "Famfrit", "Mystery", "Zodiark" }, Names(s));
        Assert.Empty(s.DataCenters);
        Assert.All(s.Worlds, w => Assert.Null(w.DataCenter));
    }

    [Fact]
    public void TwoDataCentersAreOrderedByNameAndUnknownIsLast()
    {
        var s = RouteSummarizer.Summarize(Book(), All(), Dc);
        Assert.Equal(new[] { "Alpha", "Zodiark", "Aaa", "Famfrit", "Mystery" }, Names(s));
        Assert.Equal(new string?[] { "Light", "Primal", null }, s.DataCenters.Select(g => g.Name).ToArray());
    }

    [Fact]
    public void DataCenterNameOrderIsOrdinalNotWorldOrder()
    {
        // "Zeta" DC holds the alphabetically first world, "Alpha" DC the last; DC name wins.
        var s = RouteSummarizer.Summarize(Book(), All(), w => w == 1 ? "Alpha" : "Zeta");
        Assert.Equal("Zodiark", s.Worlds[0].WorldName);
        Assert.Equal(new string?[] { "Alpha", "Zeta" }, s.DataCenters.Select(g => g.Name).ToArray());
    }

    [Fact]
    public void WithinDataCenterOrderIsWorldNameThenId()
    {
        var s = RouteSummarizer.Summarize(Book(), All(), Dc);
        Assert.Equal(new[] { "Alpha", "Zodiark" }, s.DataCenters[0].Worlds.Select(w => w.WorldName).ToArray());
        Assert.Equal(new[] { "Aaa", "Famfrit" }, s.DataCenters[1].Worlds.Select(w => w.WorldName).ToArray());
    }

    [Fact]
    public void WorldsCarryTheirDataCenterAndGroupsMatchWorldsList()
    {
        var s = RouteSummarizer.Summarize(Book(), All(), Dc);
        Assert.Equal(new string?[] { "Light", "Light", "Primal", "Primal", null }, s.Worlds.Select(w => w.DataCenter).ToArray());
        Assert.Equal(s.Worlds, s.DataCenters.SelectMany(g => g.Worlds).ToList());
    }

    [Fact]
    public void SubtotalsAddUpToGrandTotal()
    {
        var s = RouteSummarizer.Summarize(Book(), All(), Dc);
        Assert.Equal(new long[] { 10 + 40, 90 + 160, 250 }, s.DataCenters.Select(g => g.Subtotal).ToArray());
        Assert.Equal(s.GrandTotal, s.DataCenters.Sum(g => g.Subtotal));
        Assert.Equal(550, s.GrandTotal);
    }

    [Fact]
    public void OneDataCenterGivesOneGroup()
    {
        var s = RouteSummarizer.Summarize(Book(), All(), _ => "Light");
        var g = Assert.Single(s.DataCenters);
        Assert.Equal("Light", g.Name);
        Assert.Equal(5, g.Worlds.Count);
        Assert.Equal(550, g.Subtotal);
    }

    [Fact]
    public void EmptyRouteHasNoGroups()
    {
        var s = RouteSummarizer.Summarize(Book(), Array.Empty<AllocationLine>(), Dc);
        Assert.Empty(s.Worlds);
        Assert.Empty(s.DataCenters);
    }

    [Fact]
    public void AllocationStatePassesTheLookupThrough()
    {
        var state = new AllocationState(Book());
        state.Apply(All());
        var s = state.Summarize(Dc);
        Assert.Equal(new string?[] { "Light", "Primal", null }, s.DataCenters.Select(g => g.Name).ToArray());
        Assert.Empty(state.Summarize().DataCenters);
    }
}
