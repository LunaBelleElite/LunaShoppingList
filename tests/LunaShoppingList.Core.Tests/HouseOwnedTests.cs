namespace LunaShoppingList.Core.Tests;

public class HouseOwnedTests
{
    private static readonly DateTimeOffset T1 = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 3, 2, 10, 0, 0, TimeSpan.Zero);

    private static Dictionary<uint, int> C(params (uint Item, int Count)[] pairs)
        => pairs.ToDictionary(p => p.Item, p => p.Count);

    private static IReadOnlyDictionary<HousePlace, IReadOnlyDictionary<uint, int>> H(Dictionary<uint, int>? placed, Dictionary<uint, int>? storeroom = null)
    {
        var d = new Dictionary<HousePlace, IReadOnlyDictionary<uint, int>>();
        if (placed is not null) d[HousePlace.Placed] = placed;
        if (storeroom is not null) d[HousePlace.Storeroom] = storeroom;
        return d;
    }

    // ---- cache ----

    [Fact]
    public void NewCacheHasNoHousesAndAnUnknownTotal()
    {
        var c = new OwnedCache();
        Assert.Equal(0, c.HouseCount);
        Assert.Empty(c.Houses);
        Assert.Null(c.HouseTotal(1));
    }

    [Fact]
    public void ReplaceHouseKeepsPlacesLabelAndUtcTime()
    {
        var c = new OwnedCache();
        c.ReplaceHouse(77, "Mist W5 P12", new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.FromHours(2)), H(C((10, 3)), C((10, 4), (11, 1))));
        var h = Assert.Single(c.Houses);
        Assert.Equal(77UL, h.Id);
        Assert.Equal("Mist W5 P12", h.Label);
        Assert.Equal(T1, h.CapturedUtc);
        Assert.Equal(TimeSpan.Zero, h.CapturedUtc.Offset);
        Assert.Equal(3, h.Places[HousePlace.Placed][10]);
        Assert.Equal(4, h.Places[HousePlace.Storeroom][10]);
        Assert.Equal(1, c.HouseCount);
    }

    [Fact]
    public void HouseTotalSumsPlacedAndStoreroomOverEveryHouse()
    {
        var c = new OwnedCache();
        c.ReplaceHouse(1, "A", T1, H(C((10, 3)), C((10, 4))));
        c.ReplaceHouse(2, "B", T1, H(C((10, 5)), null));
        Assert.Equal(12L, c.HouseTotal(10));
        Assert.Equal(0L, c.HouseTotal(99));
    }

    [Fact]
    public void HouseTotalIsLongAndDoesNotOverflow()
    {
        var c = new OwnedCache();
        c.ReplaceHouse(1, "A", T1, H(C((10, int.MaxValue)), C((10, int.MaxValue))));
        c.ReplaceHouse(2, "B", T1, H(C((10, int.MaxValue)), null));
        Assert.Equal(3L * int.MaxValue, c.HouseTotal(10));
    }

    [Fact]
    public void ReplaceHouseReplacesTheOldSnapshotCompletely()
    {
        var c = new OwnedCache();
        c.ReplaceHouse(1, "Old label", T1, H(C((10, 3), (11, 2)), C((12, 9))));
        c.ReplaceHouse(1, "New label", T2, H(C((10, 1)), null));
        Assert.Equal(1, c.HouseCount);
        Assert.Equal(1L, c.HouseTotal(10));
        Assert.Equal(0L, c.HouseTotal(11));
        Assert.Equal(0L, c.HouseTotal(12));
        Assert.Equal("New label", c.Houses[0].Label);
        Assert.Equal(T2, c.Houses[0].CapturedUtc);
        Assert.False(c.Houses[0].Places.ContainsKey(HousePlace.Storeroom));
    }

    [Fact]
    public void ReplaceHouseWithIdenticalContentsStillRestampsLikeReplaceRetainer()
    {
        var c = new OwnedCache();
        c.ReplaceHouse(1, "A", T1, H(C((10, 3)), null));
        c.ReplaceHouse(1, "A", T2, H(C((10, 3)), null));
        Assert.Equal(T2, c.Houses[0].CapturedUtc);
        Assert.Equal(3L, c.HouseTotal(10));
    }

    [Fact]
    public void AnEmptyCapturedHouseGivesZeroNotUnknown()
    {
        var c = new OwnedCache();
        c.ReplaceHouse(1, "A", T1, H(C(), C()));
        Assert.Equal(1, c.HouseCount);
        Assert.Equal(0L, c.HouseTotal(10));
        Assert.Empty(c.Houses[0].Places);
    }

    [Fact]
    public void ReplaceHouseDropsNonPositiveCountsEmptyPlacesAndNullLabel()
    {
        var c = new OwnedCache();
        c.ReplaceHouse(1, null!, T1, H(C((10, 0), (11, -4), (12, 2)), C((10, 0))));
        var h = c.Houses[0];
        Assert.Equal("", h.Label);
        Assert.Equal(new[] { HousePlace.Placed }, h.Places.Keys.ToArray());
        Assert.Equal(new[] { 12u }, h.Places[HousePlace.Placed].Keys.ToArray());
        Assert.Equal(0L, c.HouseTotal(10));
    }

    [Fact]
    public void ReplaceHouseCopiesTheCallersDictionaries()
    {
        var c = new OwnedCache();
        var placed = C((10, 3));
        c.ReplaceHouse(1, "A", T1, H(placed, null));
        placed[10] = 99;
        Assert.Equal(3L, c.HouseTotal(10));
    }

    [Fact]
    public void ReplaceHouseIgnoresAnUndefinedPlace()
    {
        var c = new OwnedCache();
        var places = H(C((10, 3)), null);
        var withBogus = new Dictionary<HousePlace, IReadOnlyDictionary<uint, int>>(places) { [(HousePlace)99] = C((10, 50)) };
        c.ReplaceHouse(1, "A", T1, withBogus);
        Assert.Equal(3L, c.HouseTotal(10));
        Assert.Equal(new[] { HousePlace.Placed }, c.Houses[0].Places.Keys.ToArray());
    }

    [Fact]
    public void ReplaceHouseRejectsNullPlaces()
    {
        Assert.Throws<ArgumentNullException>(() => new OwnedCache().ReplaceHouse(1, "A", T1, null!));
    }

    [Fact]
    public void HousesAreOrderedById()
    {
        var c = new OwnedCache();
        c.ReplaceHouse(30, "C", T1, H(C((1, 1)), null));
        c.ReplaceHouse(10, "A", T1, H(C((1, 1)), null));
        c.ReplaceHouse(20, "B", T1, H(C((1, 1)), null));
        Assert.Equal(new ulong[] { 10, 20, 30 }, c.Houses.Select(h => h.Id).ToArray());
    }

    [Fact]
    public void HousesDoNotTouchRetainerOrSaddlebagFigures()
    {
        var c = new OwnedCache();
        c.ReplaceHouse(1, "A", T1, H(C((10, 3)), null));
        Assert.Equal(0, c.RetainerCount);
        Assert.Equal(0L, c.RetainerTotal(10));
        Assert.Null(c.Saddlebag);
        Assert.Null(c.OldestCapture);
    }

    // ---- snapshot ----

    [Fact]
    public void SnapshotTotalsAndCountsAreDerivedAcrossPlaces()
    {
        var c = new OwnedCache();
        c.ReplaceHouse(1, "A", T1, H(C((10, 3), (11, 2)), C((10, 4))));
        var h = c.Houses[0];
        Assert.Equal(7L, h.Total(10));
        Assert.Equal(2L, h.Total(11));
        Assert.Equal(0L, h.Total(12));
        Assert.Equal(3, h.Count(HousePlace.Placed, 10));
        Assert.Equal(4, h.Count(HousePlace.Storeroom, 10));
        Assert.Equal(0, h.Count(HousePlace.Storeroom, 11));
        Assert.Equal(7L, h.Totals[10]);
        Assert.Equal(2L, h.Totals[11]);
        Assert.Equal(2, h.Totals.Count);
    }

    // ---- JSON ----

    private static OwnedCache Sample()
    {
        var c = new OwnedCache();
        c.ReplaceSaddlebag(T1, C((5, 1)));
        c.ReplaceRetainer(100, "Alpha", T1, C((5, 2)), C((5, 1)));
        c.ReplaceHouse(900, "Mist W5 P12", T2, H(C((10, 3), (11, 1)), C((10, 4))));
        c.ReplaceHouse(800, "Shirogane W1 P2", T1, H(null, C((12, 6))));
        return c;
    }

    [Fact]
    public void JsonRoundTripKeepsHousesAndStaysVersionOne()
    {
        var json = Sample().ToJson();
        Assert.Contains("\"Version\": 1", json);
        Assert.True(OwnedCache.TryFromJson(json, out var back));
        Assert.Equal(2, back.HouseCount);
        Assert.Equal(7L, back.HouseTotal(10));
        Assert.Equal(6L, back.HouseTotal(12));
        var mist = back.Houses.Single(h => h.Id == 900);
        Assert.Equal("Mist W5 P12", mist.Label);
        Assert.Equal(T2, mist.CapturedUtc);
        Assert.Equal(3, mist.Places[HousePlace.Placed][10]);
        Assert.Equal(4, mist.Places[HousePlace.Storeroom][10]);
        Assert.Equal(1, back.RetainerCount);
        Assert.Equal(json, back.ToJson());
    }

    [Fact]
    public void JsonWritesHousesSortedByIdWithPlaceNames()
    {
        var json = Sample().ToJson();
        var house = json.IndexOf("\"Houses\"", StringComparison.Ordinal);
        Assert.True(house >= 0);
        var a = json.IndexOf("\"Id\": 800", house, StringComparison.Ordinal);
        var b = json.IndexOf("\"Id\": 900", house, StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a);
        Assert.Contains("\"Placed\"", json);
        Assert.Contains("\"Storeroom\"", json);
    }

    [Fact]
    public void ACacheWithoutHousesWritesNoHousesMember()
    {
        var c = new OwnedCache();
        c.ReplaceRetainer(1, "A", T1, C((5, 2)), C());
        Assert.DoesNotContain("Houses", c.ToJson());
    }

    [Fact]
    public void AFileWithoutAHousesMemberLoadsWithNoHouses()
    {
        const string old = "{\"Version\":1,\"Retainers\":[{\"Id\":7,\"Name\":\"Old\",\"CapturedUtc\":\"2026-03-01T10:00:00+00:00\",\"Stored\":{\"5\":3},\"Market\":{\"5\":2}}]}";
        Assert.True(OwnedCache.TryFromJson(old, out var c));
        Assert.Equal(0, c.HouseCount);
        Assert.Null(c.HouseTotal(5));
        Assert.Equal(5L, c.RetainerTotal(5));
    }

    [Fact]
    public void ANullHousesMemberOrNullEntryIsTolerated()
    {
        Assert.True(OwnedCache.TryFromJson("{\"Version\":1,\"Houses\":null}", out var a));
        Assert.Equal(0, a.HouseCount);
        const string json = "{\"Version\":1,\"Houses\":[null,{\"Id\":5,\"Label\":\"X\",\"CapturedUtc\":\"2026-03-01T10:00:00+00:00\",\"Places\":{\"Placed\":{\"9\":2}}}]}";
        Assert.True(OwnedCache.TryFromJson(json, out var b));
        Assert.Equal(1, b.HouseCount);
        Assert.Equal(2L, b.HouseTotal(9));
    }

    [Fact]
    public void AnUnknownPlaceNameInAHouseIsSkippedAndTheRestKept()
    {
        const string json = "{\"Version\":1,\"Houses\":[{\"Id\":5,\"Label\":\"X\",\"CapturedUtc\":\"2026-03-01T10:00:00+00:00\"," +
            "\"Places\":{\"Placed\":{\"9\":2},\"Attic\":{\"9\":50},\"storeroom\":{\"9\":70},\"Storeroom\":null}}]}";
        Assert.True(OwnedCache.TryFromJson(json, out var c));
        Assert.Equal(2L, c.HouseTotal(9));
    }

    [Fact]
    public void AHouseWithNoPlacesMemberLoadsAsAnEmptyCapturedHouse()
    {
        Assert.True(OwnedCache.TryFromJson("{\"Version\":1,\"Houses\":[{\"Id\":5,\"Label\":\"X\",\"CapturedUtc\":\"2026-03-01T10:00:00+00:00\"}]}", out var c));
        Assert.Equal(1, c.HouseCount);
        Assert.Equal(0L, c.HouseTotal(9));
    }

    [Fact]
    public void AnOlderReaderShapeIgnoresTheHousesMember()
    {
        // The Version 1 reader's own shape knows no Houses member; deserializing with default options must skip it and keep the rest.
        var json = Sample().ToJson();
        var old = System.Text.Json.JsonSerializer.Deserialize<OldShape>(json)!;
        Assert.Equal(1, old.Version);
        Assert.Single(old.Retainers!);
    }

    private sealed class OldShape
    {
        public int Version { get; set; }
        public List<System.Text.Json.JsonElement>? Retainers { get; set; }
    }

    // ---- Copy ----

    [Fact]
    public void CopyKeepsHousesAndIsIndependentOfTheSource()
    {
        var source = Sample();
        var copy = OwnedCounting.Copy(source);
        Assert.Equal(source.ToJson(), copy.ToJson());
        source.ReplaceHouse(900, "Mist W5 P12", T2, H(C(), null));
        source.ReplaceHouse(1000, "New", T2, H(C((10, 50)), null));
        Assert.Equal(2, copy.HouseCount);
        Assert.Equal(7L, copy.HouseTotal(10));
    }

    // ---- lookup ----

    [Fact]
    public void SummarizeReportsHouseTotalOrNullWhenNeverCaptured()
    {
        var none = OwnedLookup.Summarize(10, null, new OwnedCache(), null);
        Assert.Null(none.House);
        var c = Sample();
        Assert.Equal(7L, OwnedLookup.Summarize(10, null, c, null).House);
        Assert.Equal(0L, OwnedLookup.Summarize(99, null, c, null).House);
    }

    [Fact]
    public void SummarizeKeepsTheRetainerFiguresUnaffectedByHouses()
    {
        var c = Sample();
        var s = OwnedLookup.Summarize(5, new Dictionary<uint, int> { [5] = 4 }, c, null);
        Assert.Equal(4, s.OnYou);
        Assert.Equal(1, s.Saddlebag);
        Assert.Equal(3L, s.Retainers);
        Assert.Equal(0L, s.House);
    }

    [Fact]
    public void HouseLocationsListOnlyHousesHoldingTheItemSortedByTotalThenLabel()
    {
        var c = new OwnedCache();
        c.ReplaceHouse(1, "Beta", T1, H(C((10, 2)), C((10, 3))));
        c.ReplaceHouse(2, "Alpha", T2, H(C((10, 5)), null));
        c.ReplaceHouse(3, "Gamma", T1, H(C((10, 9)), null));
        c.ReplaceHouse(4, "Empty", T1, H(C((11, 1)), null));
        var locs = OwnedLookup.HouseLocations(10, c);
        Assert.Equal(new[] { "Gamma", "Alpha", "Beta" }, locs.Select(l => l.Label).ToArray());
        Assert.Equal(9, locs[0].Placed);
        Assert.Equal(0, locs[0].Storeroom);
        Assert.Equal(2, locs[2].Placed);
        Assert.Equal(3, locs[2].Storeroom);
        Assert.Equal(5L, locs[2].Total);
        Assert.Equal(T2, locs[1].CapturedUtc);
        Assert.Equal(2UL, locs[1].HouseId);
    }

    [Fact]
    public void HouseLocationsBreakTiesByLabelThenId()
    {
        var c = new OwnedCache();
        c.ReplaceHouse(9, "Same", T1, H(C((10, 4)), null));
        c.ReplaceHouse(3, "Same", T1, H(C((10, 4)), null));
        c.ReplaceHouse(5, "Aaa", T1, H(C((10, 4)), null));
        var locs = OwnedLookup.HouseLocations(10, c);
        Assert.Equal(new ulong[] { 5, 3, 9 }, locs.Select(l => l.HouseId).ToArray());
    }

    [Fact]
    public void HouseLocationTotalsAddUpToHouseTotal()
    {
        var c = Sample();
        Assert.Equal(c.HouseTotal(10), OwnedLookup.HouseLocations(10, c).Sum(l => l.Total));
        Assert.Empty(OwnedLookup.HouseLocations(99, c));
        Assert.Empty(OwnedLookup.HouseLocations(10, new OwnedCache()));
    }

    // ---- SameHousePlaces ----

    [Fact]
    public void SameHousePlacesComparesEveryPlaceAndTreatsMissingAsEmpty()
    {
        Assert.True(OwnedCounting.SameHousePlaces(H(C((1, 2)), C()), H(C((1, 2)), null)));
        Assert.True(OwnedCounting.SameHousePlaces(H(null, null), H(C(), C())));
        Assert.False(OwnedCounting.SameHousePlaces(H(C((1, 2)), null), H(C((1, 3)), null)));
        Assert.False(OwnedCounting.SameHousePlaces(H(C((1, 2)), null), H(null, C((1, 2)))));
        Assert.False(OwnedCounting.SameHousePlaces(H(null, C((1, 2))), H(C((1, 2)), null)));
        Assert.False(OwnedCounting.SameHousePlaces(null, H(null, null)));
        Assert.False(OwnedCounting.SameHousePlaces(H(C((1, 2)), null), H(C((1, 2)), C((3, 1)))));
    }
}
