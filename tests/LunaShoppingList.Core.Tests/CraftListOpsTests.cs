namespace LunaShoppingList.Core.Tests;

public class CraftListOpsTests
{
    private static KeyValuePair<uint, uint> P(uint id, uint qty) => new(id, qty);

    private static (uint, int)[] G(IReadOnlyList<Requirement> r) => r.Select(x => (x.ItemId, x.Quantity)).ToArray();

    private static CustomList L(IEnumerable<(uint, int)> rows, IEnumerable<(uint, int)>? goals = null)
    {
        var l = new CustomList { Id = "x", Name = "x" };
        foreach (var (i, q) in rows) l.Items.Add(new CustomListItem { ItemId = i, Quantity = q });
        foreach (var (i, q) in goals ?? []) l.Goals.Add(new CustomListItem { ItemId = i, Quantity = q });
        return l;
    }

    // ---- ToGoals ----

    [Fact]
    public void ToGoals_Null_IsEmpty() => Assert.Empty(CraftListOps.ToGoals(null));

    [Fact]
    public void ToGoals_OrdersByItemId()
        => Assert.Equal(new[] { (5u, 1), (9u, 3), (200u, 2) }, G(CraftListOps.ToGoals([P(200, 2), P(5, 1), P(9, 3)])));

    [Fact]
    public void ToGoals_DropsZeroQuantity()
        => Assert.Equal(new[] { (7u, 4) }, G(CraftListOps.ToGoals([P(5, 0), P(7, 4), P(9, 0)])));

    [Fact]
    public void ToGoals_DropsItemIdZero()
        => Assert.Equal(new[] { (7u, 4) }, G(CraftListOps.ToGoals([P(0, 3), P(7, 4)])));

    [Fact]
    public void ToGoals_SumsDuplicates()
        => Assert.Equal(new[] { (7u, 10), (8u, 1) }, G(CraftListOps.ToGoals([P(7, 4), P(8, 1), P(7, 6)])));

    [Fact]
    public void ToGoals_ClampsHugeQuantityToIntRange()
        => Assert.Equal(new[] { (7u, int.MaxValue) }, G(CraftListOps.ToGoals([P(7, uint.MaxValue)])));

    [Fact]
    public void ToGoals_ClampsSumThatPassesIntRange()
        => Assert.Equal(new[] { (7u, int.MaxValue) }, G(CraftListOps.ToGoals([P(7, 2_000_000_000), P(7, 2_000_000_000)])));

    [Fact]
    public void ToGoals_ZeroDuplicatesDoNotCreateARow()
        => Assert.Empty(CraftListOps.ToGoals([P(7, 0), P(7, 0)]));

    // ---- ForExport ----

    [Fact]
    public void ForExport_NullOrEmptyList_IsEmpty()
    {
        Assert.Empty(CraftListOps.ForExport(null));
        Assert.Empty(CraftListOps.ForExport(L([])));
    }

    [Fact]
    public void ForExport_WithGoals_SendsOnlyTheGoals_NotTheRows()
        => Assert.Equal(new[] { (10u, 2) }, G(CraftListOps.ForExport(L(rows: [(1, 50), (2, 60)], goals: [(10, 2)]))));

    [Fact]
    public void ForExport_NoGoals_SendsThePlainRows()
        => Assert.Equal(new[] { (1u, 50), (2u, 60) }, G(CraftListOps.ForExport(L(rows: [(2, 60), (1, 50)]))));

    [Fact]
    public void ForExport_GoalsUnusable_FallsBackToRows()
        => Assert.Equal(new[] { (1u, 50) }, G(CraftListOps.ForExport(L(rows: [(1, 50)], goals: [(10, 0), (0, 3)]))));

    [Fact]
    public void ForExport_SumsPerItemId()
        => Assert.Equal(new[] { (10u, 5), (11u, 1) }, G(CraftListOps.ForExport(L(rows: [], goals: [(10, 2), (11, 1), (10, 3)]))));

    [Fact]
    public void ForExport_SkipsQuantityZeroAndNegative_AndItemIdZero()
        => Assert.Equal(new[] { (4u, 1) }, G(CraftListOps.ForExport(L(rows: [(1, 0), (2, -5), (0, 9), (4, 1)]))));

    [Fact]
    public void ForExport_SumIsClampedToTheListMaximum()
        => Assert.Equal(new[] { (10u, CustomListOps.MaxQuantity) }, G(CraftListOps.ForExport(L(rows: [], goals: [(10, 9_000_000), (10, 9_000_000)]))));

    [Fact]
    public void ForExport_SingleHugeRowIsClamped()
        => Assert.Equal(new[] { (10u, CustomListOps.MaxQuantity) }, G(CraftListOps.ForExport(L(rows: [(10, int.MaxValue)]))));

    [Fact]
    public void ForExport_SkipsNullRows()
    {
        var l = L(rows: [], goals: [(10, 1)]);
        l.Goals.Add(null!);
        l.Items.Add(null!);
        Assert.Equal(new[] { (10u, 1) }, G(CraftListOps.ForExport(l)));
    }

    [Fact]
    public void ForExport_DoesNotChangeTheList()
    {
        var l = L(rows: [(2, 60), (1, 50)], goals: [(10, 2), (10, 3)]);
        CraftListOps.ForExport(l);
        Assert.Equal(2, l.Items.Count);
        Assert.Equal(2, l.Goals.Count);
        Assert.Equal(2, l.Goals[0].Quantity);
    }

    // ---- ToIpcItems ----

    [Fact]
    public void ToIpcItems_MapsEveryGoal()
    {
        var d = CraftListOps.ToIpcItems([new Requirement(3, 7), new Requirement(1, 2)]);
        Assert.Equal(2, d.Count);
        Assert.Equal(7u, d[3]);
        Assert.Equal(2u, d[1]);
    }

    [Fact]
    public void ToIpcItems_SumsRepeatedIdsAndSkipsNonPositive()
    {
        var d = CraftListOps.ToIpcItems([new Requirement(3, 7), new Requirement(3, 1), new Requirement(4, 0), new Requirement(5, -2), new Requirement(0, 4)]);
        Assert.Single(d);
        Assert.Equal(8u, d[3]);
    }

    // ---- ExportName ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExportName_BlankUsesFallback(string? name) => Assert.Equal("Luna Shopping List", CraftListOps.ExportName(name));

    [Fact]
    public void ExportName_Trims() => Assert.Equal("Dyes", CraftListOps.ExportName("  Dyes \t"));

    [Fact]
    public void ExportName_Exactly60Characters_IsKept() => Assert.Equal(new string('a', 60), CraftListOps.ExportName(new string('a', 60)));

    [Fact]
    public void ExportName_Over60Characters_IsCutTo60() => Assert.Equal(new string('a', 60), CraftListOps.ExportName(new string('a', 61)));

    [Fact]
    public void ExportName_CutNeverSplitsASurrogatePair()
    {
        var cut = CraftListOps.ExportName(new string('a', 59) + "\U0001F600");
        Assert.Equal(new string('a', 59), cut);
    }

    [Fact]
    public void ExportName_CutThatEndsInASpaceIsTrimmed()
        => Assert.Equal(new string('a', 59), CraftListOps.ExportName(new string('a', 59) + " bbb"));

    [Fact]
    public void ExportName_AllSpacesAfterTrimUsesFallback_EvenWhenLong()
        => Assert.Equal("Luna Shopping List", CraftListOps.ExportName(new string(' ', 100)));

    // ---- ToLists ----

    [Fact]
    public void ToLists_Null_IsEmpty() => Assert.Empty(CraftListOps.ToLists(null));

    [Fact]
    public void ToLists_OrdersByNameIgnoringCase_ThenKey()
    {
        var lists = CraftListOps.ToLists([new("k3", "beta"), new("k2", "Alpha"), new("k1", "alpha")]);
        Assert.Equal(new[] { "k1", "k2", "k3" }, lists.Select(l => l.Key).ToArray());
    }

    [Fact]
    public void ToLists_DropsBlankKeys_AndShowsBlankNameAsKey()
    {
        var lists = CraftListOps.ToLists([new("", "x"), new("  ", "y"), new("k1", ""), new("k2", null!)]);
        Assert.Equal(new[] { new CraftListInfo("k1", "k1"), new CraftListInfo("k2", "k2") }, lists.ToArray());
    }

    // ---- SameItems ----

    [Fact]
    public void SameItems_EqualSets_True()
        => Assert.True(CraftListOps.SameItems([new Requirement(1, 2), new Requirement(3, 4)], [P(3, 4), P(1, 2)]));

    [Fact]
    public void SameItems_DifferentQuantity_False()
        => Assert.False(CraftListOps.SameItems([new Requirement(1, 2)], [P(1, 3)]));

    [Fact]
    public void SameItems_ExtraItemInReread_False()
        => Assert.False(CraftListOps.SameItems([new Requirement(1, 2)], [P(1, 2), P(9, 1)]));

    [Fact]
    public void SameItems_MissingItemInReread_False()
        => Assert.False(CraftListOps.SameItems([new Requirement(1, 2), new Requirement(3, 4)], [P(1, 2)]));

    [Fact]
    public void SameItems_ZeroQuantityInRereadIsIgnored()
        => Assert.True(CraftListOps.SameItems([new Requirement(1, 2)], [P(1, 2), P(9, 0)]));

    [Fact]
    public void SameItems_SentDuplicatesAreSummed()
        => Assert.True(CraftListOps.SameItems([new Requirement(1, 2), new Requirement(1, 3)], [P(1, 5)]));

    [Fact]
    public void SameItems_NullReread_IsEmpty()
    {
        Assert.False(CraftListOps.SameItems([new Requirement(1, 2)], null));
        Assert.True(CraftListOps.SameItems([], null));
    }

    // ---- VerifyCreated ----

    [Fact]
    public void VerifyCreated_Match_IsOkWithKeyAndReadableMessage()
    {
        var r = CraftListOps.VerifyCreated("Dyes", "k-1", [new Requirement(1, 2), new Requirement(3, 4)], [P(1, 2), P(3, 4)]);
        Assert.True(r.Ok);
        Assert.Equal(CraftListStatus.Ok, r.Status);
        Assert.Equal("k-1", r.Key);
        Assert.Equal("Sent \"Dyes\" to Allagan Tools (2 items).", r.Message);
    }

    [Fact]
    public void VerifyCreated_OneItem_UsesSingular()
        => Assert.Equal("Sent \"X\" to Allagan Tools (1 item).", CraftListOps.VerifyCreated("X", "k", [new Requirement(1, 2)], [P(1, 2)]).Message);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void VerifyCreated_NoKey_IsFailed(string? key)
    {
        var r = CraftListOps.VerifyCreated("Dyes", key, [new Requirement(1, 2)], [P(1, 2)]);
        Assert.False(r.Ok);
        Assert.Equal(CraftListStatus.Failed, r.Status);
        Assert.Null(r.Key);
        Assert.Equal("Allagan Tools did not accept the list", r.Message);
    }

    [Fact]
    public void VerifyCreated_Mismatch_IsFailedAndNeverClaimsSuccess()
    {
        var r = CraftListOps.VerifyCreated("Dyes", "k-1", [new Requirement(1, 2)], [P(1, 1)]);
        Assert.False(r.Ok);
        Assert.Equal(CraftListStatus.Failed, r.Status);
        Assert.Null(r.Key);
        // Superseded 2026-11-24: was "Allagan Tools did not accept the list". A key came back, so the list may exist; saying it was
        // not accepted would invite a duplicate send.
        Assert.Equal("Allagan Tools may have created the list, but its contents did not match what was sent. Check its craft lists before sending again.", r.Message);
    }

    [Fact]
    public void VerifyCreated_EmptyReread_IsFailedAndSaysItMayExist()
    {
        var r = CraftListOps.VerifyCreated("Dyes", "k-1", [new Requirement(1, 2)], new Dictionary<uint, uint>());
        Assert.False(r.Ok);
        Assert.Contains("may have created the list", r.Message);
    }

    [Fact]
    public void SendThrewMessage_SaysTheListMayExist()
        => Assert.Equal("Something went wrong while sending. Allagan Tools may have created the list, so check its craft lists before sending again.", CraftListOps.SendThrewMessage);

    // ---- ReadResult ----

    private static readonly IReadOnlyList<CraftListInfo> Known = [new("k1", "One"), new("k2", "Two")];

    [Fact]
    public void ReadResult_UnknownKey_IsFailedWithEmptyGoals()
    {
        var r = CraftListOps.ReadResult("nope", Known, new Dictionary<uint, uint>());
        Assert.False(r.Ok);
        Assert.Equal(CraftListStatus.Failed, r.Status);
        Assert.Equal("Allagan Tools has no craft list with that key.", r.Message);
        Assert.Empty(r.Goals);
    }

    [Fact]
    public void ReadResult_UnknownKey_IgnoresWhateverItemsCameBack()
        => Assert.Empty(CraftListOps.ReadResult("nope", Known, [P(1, 1)]).Goals);

    [Fact]
    public void ReadResult_KnownKey_GivesOrderedGoals()
    {
        var r = CraftListOps.ReadResult("k2", Known, [P(9, 1), P(2, 0), P(3, 5)]);
        Assert.True(r.Ok);
        Assert.Equal(string.Empty, r.Message);
        Assert.Equal(new[] { (3u, 5), (9u, 1) }, G(r.Goals));
    }

    [Fact]
    public void ReadResult_KnownKeyWithNoItems_IsOkAndEmpty()
    {
        Assert.True(CraftListOps.ReadResult("k1", Known, null).Ok);
        Assert.Empty(CraftListOps.ReadResult("k1", Known, new Dictionary<uint, uint>()).Goals);
    }

    [Fact]
    public void ReadResult_KeyMatchIsCaseSensitive()
        => Assert.False(CraftListOps.ReadResult("K1", Known, [P(1, 1)]).Ok);

    // ---- A fake source, round trip through the helpers ----

    private sealed class FakeSource : ICraftListSource
    {
        private readonly Dictionary<string, (string Name, Dictionary<uint, uint> Items)> store = new();
        public bool IsAvailable { get; set; } = true;
        public bool Corrupt { get; set; }

        public IReadOnlyList<CraftListInfo> GetLists()
            => IsAvailable ? CraftListOps.ToLists(store.ToDictionary(k => k.Key, k => k.Value.Name)) : [];

        public CraftListGoals GetGoals(string key)
        {
            if (!IsAvailable) return new(CraftListStatus.NotAvailable, CraftListOps.NotAvailableMessage, []);
            return CraftListOps.ReadResult(key, GetLists(), store.TryGetValue(key, out var v) ? v.Items : null);
        }

        public CraftListResult CreateList(string name, IReadOnlyList<Requirement> goals)
        {
            if (!IsAvailable) return new(CraftListStatus.NotAvailable, CraftListOps.NotAvailableMessage);
            var key = "key-" + store.Count;
            var items = CraftListOps.ToIpcItems(goals);
            if (Corrupt) foreach (var k in items.Keys.ToList()) items[k] = 1;
            store[key] = (name, items);
            return CraftListOps.VerifyCreated(name, key, goals, store[key].Items);
        }
    }

    [Fact]
    public void Fake_ExportThenImport_RoundTripsTheGoals()
    {
        var src = new FakeSource();
        var list = L(rows: [(1, 5)], goals: [(30, 2), (20, 3), (30, 1)]);
        var res = src.CreateList(CraftListOps.ExportName(" Mine "), CraftListOps.ForExport(list));
        Assert.True(res.Ok);
        Assert.Equal(new[] { new CraftListInfo(res.Key!, "Mine") }, src.GetLists().ToArray());
        Assert.Equal(new[] { (20u, 3), (30u, 3) }, G(src.GetGoals(res.Key!).Goals));
    }

    [Fact]
    public void Fake_CorruptedStore_IsReportedFailed()
    {
        var src = new FakeSource { Corrupt = true };
        Assert.Equal(CraftListStatus.Failed, src.CreateList("A", [new Requirement(1, 9)]).Status);
    }

    [Fact]
    public void Fake_NotAvailable_AnswersNotAvailable()
    {
        var src = new FakeSource { IsAvailable = false };
        Assert.Empty(src.GetLists());
        Assert.Equal(CraftListStatus.NotAvailable, src.GetGoals("k").Status);
        Assert.Equal(CraftListStatus.NotAvailable, src.CreateList("A", [new Requirement(1, 1)]).Status);
    }
}
