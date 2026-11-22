namespace LunaShoppingList.Core.Tests;

public class OwnedCacheTests
{
    private static readonly DateTimeOffset T1 = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 3, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T3 = new(2026, 3, 3, 10, 0, 0, TimeSpan.Zero);

    private static Dictionary<uint, int> C(params (uint Item, int Count)[] pairs)
        => pairs.ToDictionary(p => p.Item, p => p.Count);

    private static readonly Dictionary<uint, int> None = new();

    // ---- cache ----

    [Fact]
    public void NewCacheIsEmpty()
    {
        var c = new OwnedCache();
        Assert.Equal(0, c.RetainerCount);
        Assert.Null(c.Saddlebag);
        Assert.Null(c.OldestCapture);
        Assert.Null(c.NewestCapture);
        Assert.Null(c.OldestRetainerCapture);
        Assert.Null(c.NewestRetainerCapture);
        Assert.Equal(0, c.RetainerTotal(1));
        Assert.Equal(0, c.SaddlebagCount(1));
    }

    [Fact]
    public void ReplaceRetainerDropsItemsNoLongerPresent()
    {
        var c = new OwnedCache();
        c.ReplaceRetainer(7, "Rita", T1, C((1, 10), (2, 5)), C((3, 4)));
        c.ReplaceRetainer(7, "Rita", T2, C((1, 2)), None);

        Assert.Equal(1, c.RetainerCount);
        Assert.Equal(2, c.RetainerTotal(1));
        Assert.Equal(0, c.RetainerTotal(2));
        Assert.Equal(0, c.RetainerTotal(3));
        Assert.Equal(T2, c.Retainers[0].CapturedUtc);
    }

    [Fact]
    public void ReplaceRetainerUpdatesName()
    {
        var c = new OwnedCache();
        c.ReplaceRetainer(7, "Rita", T1, None, None);
        c.ReplaceRetainer(7, "Rosa", T2, None, None);
        Assert.Equal("Rosa", Assert.Single(c.Retainers).Name);
    }

    [Fact]
    public void TwoRetainersSum()
    {
        var c = new OwnedCache();
        c.ReplaceRetainer(1, "A", T1, C((5, 10)), None);
        c.ReplaceRetainer(2, "B", T1, C((5, 7)), None);
        Assert.Equal(17, c.RetainerTotal(5));
        Assert.Equal(2, c.RetainerCount);
    }

    [Fact]
    public void MarketListingsCountInTheRetainerTotal()
    {
        var c = new OwnedCache();
        c.ReplaceRetainer(1, "A", T1, C((5, 10)), C((5, 3)));
        c.ReplaceRetainer(2, "B", T1, None, C((5, 4)));
        Assert.Equal(17, c.RetainerTotal(5));
    }

    [Fact]
    public void DismissedRetainerIsRemoved()
    {
        var c = new OwnedCache();
        c.ReplaceRetainer(1, "A", T1, C((5, 10)), None);
        c.ReplaceRetainer(2, "B", T2, C((5, 7)), None);
        Assert.True(c.RemoveRetainer(1));
        Assert.False(c.RemoveRetainer(1));
        Assert.Equal(1, c.RetainerCount);
        Assert.Equal(7, c.RetainerTotal(5));
        Assert.Equal(T2, c.OldestCapture);
    }

    [Fact]
    public void ZeroAndNegativeCountsAreIgnored()
    {
        var c = new OwnedCache();
        c.ReplaceRetainer(1, "A", T1, C((5, 0), (6, -3), (7, 2)), C((8, -1), (9, 0)));
        c.ReplaceSaddlebag(T1, C((5, 0), (6, -3), (7, 2)));

        var r = Assert.Single(c.Retainers);
        Assert.Equal(new[] { 7u }, r.Stored.Keys.ToArray());
        Assert.Empty(r.Market);
        Assert.Equal(new[] { 7u }, c.Saddlebag!.Items.Keys.ToArray());
        Assert.Equal(0, c.RetainerTotal(6));
        Assert.Equal(0, c.SaddlebagCount(6));
    }

    [Fact]
    public void ReplaceSaddlebagReplacesCompletely()
    {
        var c = new OwnedCache();
        c.ReplaceSaddlebag(T1, C((1, 9), (2, 9)));
        c.ReplaceSaddlebag(T2, C((1, 3)));
        Assert.Equal(3, c.SaddlebagCount(1));
        Assert.Equal(0, c.SaddlebagCount(2));
        Assert.Equal(T2, c.Saddlebag!.CapturedUtc);
    }

    [Fact]
    public void EmptyCaptureIsStillACapture()
    {
        var c = new OwnedCache();
        c.ReplaceSaddlebag(T1, None);
        c.ReplaceRetainer(1, "A", T2, None, None);
        Assert.NotNull(c.Saddlebag);
        Assert.Equal(1, c.RetainerCount);
    }

    [Fact]
    public void CaptureRangesCoverSaddlebagAndRetainers()
    {
        var c = new OwnedCache();
        c.ReplaceSaddlebag(T1, None);
        c.ReplaceRetainer(1, "A", T2, None, None);
        c.ReplaceRetainer(2, "B", T3, None, None);
        Assert.Equal(T1, c.OldestCapture);
        Assert.Equal(T3, c.NewestCapture);
        Assert.Equal(T2, c.OldestRetainerCapture);
        Assert.Equal(T3, c.NewestRetainerCapture);
    }

    [Fact]
    public void CaptureTimesAreStoredAsUtc()
    {
        var c = new OwnedCache();
        var local = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.FromHours(2));
        c.ReplaceRetainer(1, "A", local, None, None);
        Assert.Equal(TimeSpan.Zero, c.Retainers[0].CapturedUtc.Offset);
        Assert.Equal(local, c.Retainers[0].CapturedUtc);
    }

    [Fact]
    public void IntMaxValueSumsDoNotOverflow()
    {
        // Sums are long (no saturation): three retainers each holding int.MaxValue.
        var c = new OwnedCache();
        for (ulong i = 1; i <= 3; i++)
            c.ReplaceRetainer(i, "R" + i, T1, C((5, int.MaxValue)), C((5, int.MaxValue)));
        Assert.Equal(6L * int.MaxValue, c.RetainerTotal(5));
    }

    // ---- JSON ----

    private static OwnedCache Sample()
    {
        var c = new OwnedCache();
        c.ReplaceSaddlebag(T1, C((30, 3), (10, 1)));
        c.ReplaceRetainer(900, "Zed", T2, C((20, 5), (2, 9)), C((7, 1)));
        c.ReplaceRetainer(100, "Ann", T3, C((1, 4)), None);
        return c;
    }

    [Fact]
    public void JsonRoundTripKeepsEverything()
    {
        var back = OwnedCache.FromJson(Sample().ToJson());

        Assert.Equal(2, back.RetainerCount);
        Assert.Equal(T1, back.Saddlebag!.CapturedUtc);
        Assert.Equal(3, back.SaddlebagCount(30));
        Assert.Equal(1, back.SaddlebagCount(10));
        Assert.Equal(1, back.RetainerTotal(7));
        Assert.Equal(5, back.RetainerTotal(20));
        Assert.Equal(9, back.RetainerTotal(2));
        Assert.Equal(4, back.RetainerTotal(1));
        var zed = back.Retainers.Single(r => r.Id == 900);
        Assert.Equal("Zed", zed.Name);
        Assert.Equal(T2, zed.CapturedUtc);
        Assert.Equal(1, zed.Market[7]);
        Assert.Equal(5, zed.Stored[20]);
        Assert.Equal(Sample().ToJson(), back.ToJson());
    }

    [Fact]
    public void EmptyCacheRoundTripsEmpty()
    {
        var back = OwnedCache.FromJson(new OwnedCache().ToJson());
        Assert.Equal(0, back.RetainerCount);
        Assert.Null(back.Saddlebag);
    }

    [Fact]
    public void JsonIsDeterministicAndSorted()
    {
        var a = new OwnedCache();
        a.ReplaceRetainer(900, "Zed", T2, C((20, 5), (2, 9)), None);
        a.ReplaceRetainer(100, "Ann", T3, C((1, 4)), None);
        var b = new OwnedCache();
        b.ReplaceRetainer(100, "Ann", T3, C((1, 4)), None);
        b.ReplaceRetainer(900, "Zed", T2, C((2, 9), (20, 5)), None);

        var json = a.ToJson();
        Assert.Equal(json, b.ToJson());
        Assert.True(json.IndexOf("\"Ann\"", StringComparison.Ordinal) < json.IndexOf("\"Zed\"", StringComparison.Ordinal));
        Assert.True(json.IndexOf("\"2\"", StringComparison.Ordinal) < json.IndexOf("\"20\"", StringComparison.Ordinal));
    }

    [Fact]
    public void JsonItemKeysSortNumericallyNotAsText()
    {
        var c = new OwnedCache();
        c.ReplaceSaddlebag(T1, C((100, 1), (9, 1)));
        var json = c.ToJson();
        Assert.True(json.IndexOf("\"9\"", StringComparison.Ordinal) < json.IndexOf("\"100\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"Version\":99,\"Retainers\":[{\"Id\":1,\"Name\":\"A\",\"CapturedUtc\":\"2026-03-01T10:00:00+00:00\",\"Stored\":{\"5\":3},\"Market\":{}}]}")]
    [InlineData("{\"Version\":1,\"Retainers\":[{\"Id\":\"x\"}]}")]
    [InlineData("{\"Version\":1,\"Saddlebag\":{\"CapturedUtc\":\"not a date\",\"Items\":{}}}")]
    public void BadJsonGivesAnEmptyCacheAndNeverThrows(string json)
    {
        var c = OwnedCache.FromJson(json);
        Assert.Equal(0, c.RetainerCount);
        Assert.Null(c.Saddlebag);
    }

    [Fact]
    public void NullJsonGivesAnEmptyCache()
    {
        Assert.Equal(0, OwnedCache.FromJson(null).RetainerCount);
    }

    [Theory]
    [InlineData("{\"Version\":1,\"Retainers\":[null]}")]
    [InlineData("{\"Version\":1,\"Retainers\":[null,null]}")]
    [InlineData("{\"Version\":1,\"Retainers\":[{\"Id\":1,\"Name\":\"A\",\"CapturedUtc\":\"2026-03-01T10:00:00+00:00\",\"Stored\":{\"5\":null},\"Market\":{}}]}")]
    [InlineData("{\"Version\":1,\"Retainers\":{}}")]
    [InlineData("{\"Version\":1,\"Saddlebag\":[]}")]
    [InlineData("{\"Version\":\"1\"}")]
    public void ShapeErrorsNeverThrow(string json)
    {
        var c = OwnedCache.FromJson(json);
        Assert.Equal(0, c.RetainerCount);
        Assert.Null(c.Saddlebag);
    }

    [Fact]
    public void NullRetainerEntriesAreSkippedAndTheRestIsKept()
    {
        const string json = "{\"Version\":1,\"Retainers\":[null,{\"Id\":7,\"Name\":\"A\",\"CapturedUtc\":\"2026-03-01T10:00:00+00:00\",\"Stored\":{\"5\":3},\"Market\":{}},null]}";
        Assert.True(OwnedCache.TryFromJson(json, out var c));
        Assert.Equal(1, c.RetainerCount);
        Assert.Equal(3, c.RetainerTotal(5));
        Assert.Equal(7UL, c.Retainers[0].Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"Version\":99}")]
    [InlineData("{\"Version\":1,\"Retainers\":{}}")]
    public void TryFromJsonRejectsAnythingThatIsNotACurrentRecord(string? json)
    {
        Assert.False(OwnedCache.TryFromJson(json, out var c));
        Assert.NotNull(c);
        Assert.Equal(0, c.RetainerCount);
        Assert.Null(c.Saddlebag);
    }

    [Fact]
    public void TryFromJsonAcceptsARecordAndAnEmptyRecord()
    {
        Assert.True(OwnedCache.TryFromJson(Sample().ToJson(), out var full));
        Assert.Equal(2, full.RetainerCount);
        Assert.Equal(3, full.SaddlebagCount(30));
        Assert.True(OwnedCache.TryFromJson(new OwnedCache().ToJson(), out var empty));
        Assert.Equal(0, empty.RetainerCount);
    }

    [Fact]
    public void LoadIgnoresNonPositiveCounts()
    {
        const string json = "{\"Version\":1,\"Saddlebag\":{\"CapturedUtc\":\"2026-03-01T10:00:00+00:00\",\"Items\":{\"5\":0,\"6\":-2,\"7\":4}}," +
            "\"Retainers\":[{\"Id\":1,\"Name\":\"A\",\"CapturedUtc\":\"2026-03-01T10:00:00+00:00\",\"Stored\":{\"5\":-1,\"6\":2},\"Market\":{\"7\":0}}]}";
        var c = OwnedCache.FromJson(json);
        Assert.Equal(0, c.SaddlebagCount(5));
        Assert.Equal(0, c.SaddlebagCount(6));
        Assert.Equal(4, c.SaddlebagCount(7));
        Assert.Equal(0, c.RetainerTotal(5));
        Assert.Equal(2, c.RetainerTotal(6));
        Assert.Equal(0, c.RetainerTotal(7));
    }

    // ---- places, v1 migration, locations ----

    private static IReadOnlyDictionary<RetainerPlace, IReadOnlyDictionary<uint, int>> P(params (RetainerPlace Place, uint Item, int Count)[] entries)
    {
        var d = new Dictionary<RetainerPlace, IReadOnlyDictionary<uint, int>>();
        foreach (var (place, item, count) in entries)
        {
            if (!d.TryGetValue(place, out var inner))
                d[place] = inner = new Dictionary<uint, int>();
            ((Dictionary<uint, int>)inner)[item] = count;
        }
        return d;
    }

    private const string V1Json = "{\"Version\":1,\"Retainers\":[{\"Id\":7,\"Name\":\"Old\",\"CapturedUtc\":\"2026-03-01T10:00:00+00:00\",\"Stored\":{\"5\":3},\"Market\":{\"5\":2}}]}";

    [Fact]
    public void PlacedReplaceSplitsStoredAndMarketAndKeepsPlaces()
    {
        var c = new OwnedCache();
        c.ReplaceRetainer(1, "A", T1, P((RetainerPlace.Bag1, 5, 4), (RetainerPlace.Bag3, 5, 6), (RetainerPlace.Crystals, 5, 1), (RetainerPlace.Market, 5, 2), (RetainerPlace.Bag2, 9, 7)));
        var r = Assert.Single(c.Retainers);
        Assert.True(r.LocationRecorded);
        Assert.Equal(11, r.Stored[5]);
        Assert.Equal(7, r.Stored[9]);
        Assert.Equal(2, r.Market[5]);
        Assert.False(r.Market.ContainsKey(9));
        Assert.Equal(6, r.Places![RetainerPlace.Bag3][5]);
        Assert.Equal(13L, c.RetainerTotal(5));
    }

    [Fact]
    public void PlacedReplaceDropsNonPositiveCountsAndEmptyPlaces()
    {
        var c = new OwnedCache();
        c.ReplaceRetainer(1, "A", T1, P((RetainerPlace.Bag1, 5, 0), (RetainerPlace.Bag2, 6, -3), (RetainerPlace.Bag3, 7, 2)));
        var r = Assert.Single(c.Retainers);
        Assert.Equal(new[] { RetainerPlace.Bag3 }, r.Places!.Keys.ToArray());
        Assert.Equal(new[] { 7u }, r.Stored.Keys.ToArray());
    }

    [Fact]
    public void PlacedReplaceReplacesOldPlacesCompletely()
    {
        var c = new OwnedCache();
        c.ReplaceRetainer(1, "A", T1, P((RetainerPlace.Bag1, 5, 4), (RetainerPlace.Market, 5, 2)));
        c.ReplaceRetainer(1, "A", T2, P((RetainerPlace.Bag2, 5, 1)));
        var r = Assert.Single(c.Retainers);
        Assert.Equal(new[] { RetainerPlace.Bag2 }, r.Places!.Keys.ToArray());
        Assert.Equal(1L, c.RetainerTotal(5));
    }

    [Fact]
    public void PlacedReplaceUpgradesAnUnplacedRecord()
    {
        var c = OwnedCache.FromJson(V1Json);
        Assert.False(c.Retainers[0].LocationRecorded);
        c.ReplaceRetainer(7, "Old", T2, P((RetainerPlace.Bag1, 5, 5)));
        Assert.True(c.Retainers[0].LocationRecorded);
    }

    // Superseded 2026-11-21: this test was JsonV2RoundTripKeepsPlaces and expected "Version": 2. The saved-file version is no
    // longer bumped (a downgrade must keep every count), so the file stays Version 1 and carries Places as an extra member.
    [Fact]
    public void JsonRoundTripKeepsPlacesAndStaysVersionOne()
    {
        var c = new OwnedCache();
        c.ReplaceRetainer(900, "Zed", T2, P((RetainerPlace.Bag2, 20, 5), (RetainerPlace.Crystals, 2, 9), (RetainerPlace.Market, 7, 1)));
        c.ReplaceRetainer(100, "Ann", T3, P((RetainerPlace.Bag1, 1, 4)));
        var json = c.ToJson();
        Assert.Contains("\"Version\": 1", json);

        var back = OwnedCache.FromJson(json);
        var zed = back.Retainers.Single(r => r.Id == 900);
        Assert.True(zed.LocationRecorded);
        Assert.Equal(5, zed.Places![RetainerPlace.Bag2][20]);
        Assert.Equal(9, zed.Places[RetainerPlace.Crystals][2]);
        Assert.Equal(1, zed.Places[RetainerPlace.Market][7]);
        Assert.Equal(1, zed.Market[7]);
        Assert.Equal(json, back.ToJson());
    }

    [Fact]
    public void JsonAlwaysWritesStoredAndMarketSumsSoAnOlderReaderKeepsEveryCount()
    {
        var c = new OwnedCache();
        c.ReplaceRetainer(1, "A", T1, P((RetainerPlace.Bag1, 5, 4), (RetainerPlace.Bag3, 5, 6), (RetainerPlace.Crystals, 5, 1), (RetainerPlace.Market, 5, 2)));
        c.ReplaceRetainer(2, "Old", T1, C((5, 7)), C((5, 3)));

        using var doc = System.Text.Json.JsonDocument.Parse(c.ToJson());
        Assert.Equal(1, doc.RootElement.GetProperty("Version").GetInt32());
        long sum = 0;
        foreach (var r in doc.RootElement.GetProperty("Retainers").EnumerateArray())
        {
            sum += r.GetProperty("Stored").GetProperty("5").GetInt64() + r.GetProperty("Market").GetProperty("5").GetInt64();
        }
        Assert.Equal(c.RetainerTotal(5), sum);
        Assert.Equal(23L, sum);
    }

    [Fact]
    public void VersionOneFileLoadsWithUnplacedCounts()
    {
        Assert.True(OwnedCache.TryFromJson(V1Json, out var c));
        var r = Assert.Single(c.Retainers);
        Assert.False(r.LocationRecorded);
        Assert.Null(r.Places);
        Assert.Equal(3, r.Stored[5]);
        Assert.Equal(2, r.Market[5]);
        Assert.Equal(5L, c.RetainerTotal(5));
    }

    // Superseded 2026-11-21: this test was VersionOneFileIsWrittenBackAsVersionTwoWithoutLosingCounts and expected "Version": 2;
    // the file is now written as Version 1.
    [Fact]
    public void VersionOneFileIsWrittenBackAsVersionOneWithoutLosingCounts()
    {
        var c = OwnedCache.FromJson(V1Json);
        Assert.Contains("\"Version\": 1", c.ToJson());
        var back = OwnedCache.FromJson(c.ToJson());
        var r = Assert.Single(back.Retainers);
        Assert.False(r.LocationRecorded);
        Assert.Equal(5L, back.RetainerTotal(5));
    }

    [Fact]
    public void VersionTwoFileIsStillAccepted()
    {
        const string json = "{\"Version\":2,\"Retainers\":[{\"Id\":1,\"Name\":\"A\",\"CapturedUtc\":\"2026-03-01T10:00:00+00:00\"," +
            "\"Places\":{\"Bag2\":{\"5\":4},\"Market\":{\"5\":1}}}]}";
        Assert.True(OwnedCache.TryFromJson(json, out var c));
        var r = Assert.Single(c.Retainers);
        Assert.True(r.LocationRecorded);
        Assert.Equal(5L, c.RetainerTotal(5));
    }

    [Theory]
    [InlineData("{\"Version\":3,\"Retainers\":[]}")]
    [InlineData("{\"Version\":0,\"Retainers\":[]}")]
    public void NewerOrUnknownVersionStillFails(string json)
    {
        Assert.False(OwnedCache.TryFromJson(json, out var c));
        Assert.Equal(0, c.RetainerCount);
    }

    // Superseded 2026-11-21: this test was VersionTwoIgnoresUnknownPlaceNamesAndNonPositiveCounts and expected an unknown place
    // key to be skipped while the rest of Places was used. That silently shrinks the count, so a record with any unknown place
    // key now falls back to its Stored and Market sums and is flagged as having no known locations.
    [Theory]
    [InlineData("Bag9")]
    [InlineData("bag1")]
    [InlineData("3")]
    [InlineData("")]
    public void AnUnknownPlaceKeyFallsBackToTheSumsAndNeverShrinksTheTotal(string key)
    {
        var json = "{\"Version\":1,\"Retainers\":[{\"Id\":1,\"Name\":\"A\",\"CapturedUtc\":\"2026-03-01T10:00:00+00:00\"," +
            "\"Stored\":{\"5\":9},\"Market\":{\"5\":1},\"Places\":{\"Bag1\":{\"5\":4},\"" + key + "\":{\"5\":6}}}]}";
        var c = OwnedCache.FromJson(json);
        var r = Assert.Single(c.Retainers);
        Assert.False(r.LocationRecorded);
        Assert.Null(r.Places);
        Assert.Equal(10L, c.RetainerTotal(5));
        Assert.Equal(10L, Assert.Single(OwnedLookup.Locations(5, c)).Total);
    }

    [Fact]
    public void ValidPlacesAreUsedAndNonPositiveCountsIgnored()
    {
        const string json = "{\"Version\":1,\"Retainers\":[{\"Id\":1,\"Name\":\"A\",\"CapturedUtc\":\"2026-03-01T10:00:00+00:00\"," +
            "\"Stored\":{\"6\":2},\"Market\":{},\"Places\":{\"Bag1\":{\"5\":-1,\"6\":2}}}]}";
        var c = OwnedCache.FromJson(json);
        var r = Assert.Single(c.Retainers);
        Assert.True(r.LocationRecorded);
        Assert.Equal(new[] { RetainerPlace.Bag1 }, r.Places!.Keys.ToArray());
        Assert.Equal(0L, c.RetainerTotal(5));
        Assert.Equal(2L, c.RetainerTotal(6));
    }

    [Fact]
    public void ANullPlaceEntryFallsBackToTheSums()
    {
        const string json = "{\"Version\":1,\"Retainers\":[{\"Id\":1,\"Name\":\"A\",\"CapturedUtc\":\"2026-03-01T10:00:00+00:00\"," +
            "\"Stored\":{\"5\":9},\"Market\":{},\"Places\":{\"Bag1\":null}}]}";
        var c = OwnedCache.FromJson(json);
        Assert.False(Assert.Single(c.Retainers).LocationRecorded);
        Assert.Equal(9L, c.RetainerTotal(5));
    }

    private static OwnedCache LocationCache()
    {
        var c = new OwnedCache();
        c.ReplaceRetainer(1, "Zoe", T1, P((RetainerPlace.Bag2, 5, 12), (RetainerPlace.Market, 5, 3), (RetainerPlace.Bag1, 8, 1)));
        c.ReplaceRetainer(2, "Abe", T2, P((RetainerPlace.Crystals, 5, 15)));
        c.ReplaceRetainer(3, "Max", T3, P((RetainerPlace.Bag7, 5, 40)));
        c.ReplaceRetainer(4, "Nil", T3, P((RetainerPlace.Bag1, 8, 9)));
        c.ReplaceRetainer(5, "Kit", T1, P((RetainerPlace.Bag4, 5, 15)));
        return c;
    }

    [Fact]
    public void LocationsListOnlyRetainersHoldingTheItemSortedByTotalThenName()
    {
        var l = OwnedLookup.Locations(5, LocationCache());
        Assert.Equal(new[] { "Max", "Abe", "Kit", "Zoe" }, l.Select(x => x.Name).ToArray());
        Assert.Equal(new long[] { 40, 15, 15, 15 }, l.Select(x => x.Total).ToArray());
    }

    [Fact]
    public void LocationsCarryPerPlaceCountsInContainerOrderAndTheCaptureTime()
    {
        var zoe = OwnedLookup.Locations(5, LocationCache()).Single(x => x.Name == "Zoe");
        Assert.True(zoe.LocationRecorded);
        Assert.Equal(new[] { RetainerPlace.Bag2, RetainerPlace.Market }, zoe.Places.Keys.ToArray());
        Assert.Equal(12, zoe.Places[RetainerPlace.Bag2]);
        Assert.Equal(3, zoe.Places[RetainerPlace.Market]);
        Assert.Equal(T1, zoe.CapturedUtc);
        Assert.Equal(1UL, zoe.RetainerId);
    }

    [Fact]
    public void LocationsOfAnItemNobodyHoldsIsEmpty()
        => Assert.Empty(OwnedLookup.Locations(999, LocationCache()));

    [Fact]
    public void LocationsOfAnEmptyCacheIsEmpty()
        => Assert.Empty(OwnedLookup.Locations(5, new OwnedCache()));

    [Fact]
    public void LocationsOfAnUnplacedRecordAreFlaggedAndStillCounted()
    {
        var c = OwnedCache.FromJson(V1Json);
        var l = Assert.Single(OwnedLookup.Locations(5, c));
        Assert.False(l.LocationRecorded);
        Assert.Empty(l.Places);
        Assert.Equal(5L, l.Total);
        Assert.Empty(OwnedLookup.Locations(6, c));
    }

    [Fact]
    public void LocationTotalsAlwaysAddUpToTheRetainerTotal()
    {
        var c = LocationCache();
        c.ReplaceRetainer(6, "Old", T1, C((5, 7), (8, 2)), C((5, 3)));
        c.ReplaceRetainer(7, "Big", T1, P((RetainerPlace.Bag1, 5, int.MaxValue), (RetainerPlace.Bag2, 5, int.MaxValue), (RetainerPlace.Market, 5, 1)));
        foreach (uint item in new uint[] { 5, 8, 999 })
            Assert.Equal(c.RetainerTotal(item), OwnedLookup.Locations(item, c).Sum(x => x.Total));
    }

    [Fact]
    public void LocationsMixUnplacedAndPlacedRecords()
    {
        var c = new OwnedCache();
        c.ReplaceRetainer(1, "Old", T1, C((5, 7)), C((5, 3)));
        c.ReplaceRetainer(2, "New", T1, P((RetainerPlace.Bag1, 5, 4)));
        var l = OwnedLookup.Locations(5, c);
        Assert.Equal(new[] { "Old", "New" }, l.Select(x => x.Name).ToArray());
        Assert.False(l[0].LocationRecorded);
        Assert.True(l[1].LocationRecorded);
        Assert.Equal(14L, l.Sum(x => x.Total));
    }

    // ---- lookup ----

    private static readonly Dictionary<uint, int> Live = C((5, 12), (6, 0));

    private static OwnedCache Cached()
    {
        var c = new OwnedCache();
        c.ReplaceSaddlebag(T1, C((5, 2)));
        c.ReplaceRetainer(1, "A", T2, C((5, 10), (8, 4)), C((5, 1)));
        c.ReplaceRetainer(2, "B", T3, C((5, 20)), None);
        return c;
    }

    [Fact]
    public void ExternalValueWinsAndIsNeverAddedToTheCache()
    {
        var s = OwnedLookup.Summarize(5, Live, Cached(), id => id == 5 ? 50 : null);
        Assert.Equal(RetainerSource.External, s.RetainerSource);
        Assert.Equal(50L, s.Retainers);
        Assert.Equal(50L, s.RetainersExternal);
        Assert.Equal(31L, s.RetainersCached);
    }

    [Fact]
    public void ExternalNullFallsBackToTheCache()
    {
        var s = OwnedLookup.Summarize(5, Live, Cached(), _ => null);
        Assert.Equal(RetainerSource.Cache, s.RetainerSource);
        Assert.Equal(31L, s.Retainers);
        Assert.Null(s.RetainersExternal);
        Assert.Equal(31L, s.RetainersCached);
    }

    [Fact]
    public void NoExternalSourceAtAllFallsBackToTheCache()
    {
        var s = OwnedLookup.Summarize(5, Live, Cached(), null);
        Assert.Equal(RetainerSource.Cache, s.RetainerSource);
        Assert.Equal(31L, s.Retainers);
    }

    // Superseded 2026-11-21: this test was ExternalKnownZeroBeatsANonZeroCache and expected
    // RetainerSource.External with Retainers == 0. The external source answers 0 for retainers it has
    // never seen, so a zero from it must not hide what this plugin's own record holds: the larger wins.
    [Fact]
    public void ExternalZeroNeverHidesAPositiveCache()
    {
        var s = OwnedLookup.Summarize(5, Live, Cached(), _ => 0);
        Assert.Equal(RetainerSource.Cache, s.RetainerSource);
        Assert.Equal(31L, s.Retainers);
        Assert.Equal(0L, s.RetainersExternal);
        Assert.Equal(31L, s.RetainersCached);
    }

    [Fact]
    public void LargerCacheWinsOverASmallerExternalAndBothAreKept()
    {
        var s = OwnedLookup.Summarize(5, Live, Cached(), _ => 20);
        Assert.Equal(RetainerSource.Cache, s.RetainerSource);
        Assert.Equal(31L, s.Retainers);
        Assert.Equal(20L, s.RetainersExternal);
        Assert.Equal(31L, s.RetainersCached);
    }

    [Fact]
    public void EqualCacheAndExternalReportTheExternalSource()
    {
        var s = OwnedLookup.Summarize(5, Live, Cached(), _ => 31);
        Assert.Equal(RetainerSource.External, s.RetainerSource);
        Assert.Equal(31L, s.Retainers);
    }

    [Fact]
    public void ExternalZeroWithoutACacheIsAKnownZero()
    {
        var s = OwnedLookup.Summarize(5, Live, new OwnedCache(), _ => 0);
        Assert.Equal(RetainerSource.External, s.RetainerSource);
        Assert.Equal(0L, s.Retainers);
        Assert.Null(s.RetainersCached);
    }

    [Fact]
    public void LiveNotReadYetIsUnknownNotZero()
    {
        var s = OwnedLookup.Summarize(5, null, Cached(), null);
        Assert.Null(s.OnYou);
        Assert.Equal(2, s.Saddlebag);
        Assert.Equal(31L, s.Retainers);
    }

    [Fact]
    public void NegativeExternalIsTreatedAsUnavailable()
    {
        var s = OwnedLookup.Summarize(5, Live, Cached(), _ => -1);
        Assert.Equal(RetainerSource.Cache, s.RetainerSource);
        Assert.Null(s.RetainersExternal);
    }

    [Fact]
    public void NeitherSourceIsNotAvailableAndNotZero()
    {
        var s = OwnedLookup.Summarize(5, Live, new OwnedCache(), null);
        Assert.Equal(RetainerSource.NotAvailable, s.RetainerSource);
        Assert.Null(s.Retainers);
        Assert.Null(s.RetainersCached);
        Assert.Null(s.RetainersExternal);
        Assert.Null(s.Saddlebag);
        Assert.Null(s.SaddlebagCapturedUtc);
        Assert.Null(s.RetainersCapturedOldestUtc);
        Assert.Null(s.RetainersCapturedNewestUtc);
    }

    [Fact]
    public void CapturedRetainersButItemAbsentIsAKnownZero()
    {
        var s = OwnedLookup.Summarize(99, Live, Cached(), null);
        Assert.Equal(RetainerSource.Cache, s.RetainerSource);
        Assert.Equal(0L, s.Retainers);
        Assert.Equal(0, s.OnYou);
        Assert.Equal(0, s.Saddlebag);
    }

    [Fact]
    public void CapturedSaddlebagWithoutTheItemIsAKnownZero()
    {
        var s = OwnedLookup.Summarize(8, Live, Cached(), null);
        Assert.Equal(0, s.Saddlebag);
        Assert.Equal(T1, s.SaddlebagCapturedUtc);
    }

    [Fact]
    public void ZeroLiveButCachedOnARetainer()
    {
        var s = OwnedLookup.Summarize(8, Live, Cached(), null);
        Assert.Equal(0, s.OnYou);
        Assert.Equal(4L, s.Retainers);
    }

    [Fact]
    public void LiveCountsAreReportedAndNonPositiveBecomeZero()
    {
        var live = C((5, 12), (6, -4));
        Assert.Equal(12, OwnedLookup.Summarize(5, live, new OwnedCache(), null).OnYou);
        Assert.Equal(0, OwnedLookup.Summarize(6, live, new OwnedCache(), null).OnYou);
        Assert.Equal(0, OwnedLookup.Summarize(7, live, new OwnedCache(), null).OnYou);
    }

    [Fact]
    public void SaddlebagAndCaptureRangeAreReported()
    {
        var s = OwnedLookup.Summarize(5, Live, Cached(), null);
        Assert.Equal(2, s.Saddlebag);
        Assert.Equal(T1, s.SaddlebagCapturedUtc);
        Assert.Equal(T2, s.RetainersCapturedOldestUtc);
        Assert.Equal(T3, s.RetainersCapturedNewestUtc);
    }

    [Fact]
    public void LookupSumsDoNotOverflow()
    {
        var c = new OwnedCache();
        c.ReplaceRetainer(1, "A", T1, C((5, int.MaxValue)), C((5, int.MaxValue)));
        c.ReplaceRetainer(2, "B", T1, C((5, int.MaxValue)), None);
        Assert.Equal(3L * int.MaxValue, OwnedLookup.Summarize(5, None, c, null).Retainers);
    }
}
