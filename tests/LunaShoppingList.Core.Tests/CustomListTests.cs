namespace LunaShoppingList.Core.Tests;

public class CustomListTests
{
    private static CustomList L(string id, string name, params (uint item, int qty)[] rows)
    {
        var l = new CustomList { Id = id, Name = name };
        foreach (var r in rows) l.Items.Add(new CustomListItem { ItemId = r.item, Quantity = r.qty });
        return l;
    }

    private static (uint, int)[] Rows(CustomList l) => l.Items.Select(i => (i.ItemId, i.Quantity)).ToArray();

    [Fact]
    public void Create_TrimsAndUsesIdFactory()
    {
        var r = CustomListOps.Create(new List<CustomList>(), "  Dyes  ", () => "id-1");
        Assert.True(r.Ok);
        Assert.Equal("Dyes", r.List!.Name);
        Assert.Equal("id-1", r.List.Id);
        Assert.Empty(r.List.Items);
        Assert.Null(r.Error);
    }

    [Fact]
    public void Create_DefaultId_IsGuidAndUnique()
    {
        var a = CustomListOps.Create(new List<CustomList>(), "A").List!;
        var b = CustomListOps.Create(new List<CustomList>(), "B").List!;
        Assert.True(Guid.TryParse(a.Id, out _));
        Assert.NotEqual(a.Id, b.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t \n")]
    public void Create_EmptyOrWhitespace_Rejected(string? name)
    {
        var r = CustomListOps.Create(new List<CustomList>(), name);
        Assert.False(r.Ok);
        Assert.Equal("Enter a name for the list", r.Error);
    }

    [Fact]
    public void Create_DuplicateNameIgnoringCase_Rejected()
    {
        var r = CustomListOps.Create(new[] { L("1", "Dyes") }, " DYES ");
        Assert.False(r.Ok);
        Assert.Equal("You already have a list with that name", r.Error);
    }

    [Fact]
    public void Create_Length60Accepted_61Rejected()
    {
        Assert.True(CustomListOps.Create(new List<CustomList>(), new string('a', 60)).Ok);
        var r = CustomListOps.Create(new List<CustomList>(), new string('a', 61));
        Assert.False(r.Ok);
        Assert.Equal("Names can be at most 60 characters", r.Error);
    }

    [Fact]
    public void Create_LengthMeasuredAfterTrim()
    {
        Assert.True(CustomListOps.Create(new List<CustomList>(), " " + new string('a', 60) + " ").Ok);
    }

    [Fact]
    public void Create_DoesNotChangeExisting()
    {
        var existing = new List<CustomList> { L("1", "A") };
        CustomListOps.Create(existing, "B");
        Assert.Single(existing);
    }

    [Fact]
    public void Rename_Succeeds_KeepsIdAndItems()
    {
        var a = L("1", "A", (5, 2));
        var r = CustomListOps.Rename(new[] { a, L("2", "B") }, a, "  C ");
        Assert.True(r.Ok);
        Assert.Same(a, r.List);
        Assert.Equal("C", a.Name);
        Assert.Equal("1", a.Id);
        Assert.Equal(new[] { (5u, 2) }, Rows(a));
    }

    [Fact]
    public void Rename_OwnNameDifferentCase_Allowed()
    {
        var a = L("1", "dyes");
        var r = CustomListOps.Rename(new[] { a }, a, "Dyes");
        Assert.True(r.Ok);
        Assert.Equal("Dyes", a.Name);
    }

    [Fact]
    public void Rename_ToOtherListsName_Rejected_AndUnchanged()
    {
        var a = L("1", "A");
        var r = CustomListOps.Rename(new[] { a, L("2", "B") }, a, "b");
        Assert.False(r.Ok);
        Assert.Equal("You already have a list with that name", r.Error);
        Assert.Equal("A", a.Name);
    }

    [Fact]
    public void Rename_EmptyAndTooLong_Rejected_AndUnchanged()
    {
        var a = L("1", "A");
        Assert.Equal("Enter a name for the list", CustomListOps.Rename(new[] { a }, a, "  ").Error);
        Assert.Equal("Names can be at most 60 characters", CustomListOps.Rename(new[] { a }, a, new string('x', 61)).Error);
        Assert.Equal("A", a.Name);
        Assert.True(CustomListOps.Rename(new[] { a }, a, new string('x', 60)).Ok);
    }

    [Fact]
    public void AddItem_NewRowThenMerge()
    {
        var l = L("1", "A");
        Assert.True(CustomListOps.AddItem(l, 10, 3));
        Assert.True(CustomListOps.AddItem(l, 11, 1));
        Assert.True(CustomListOps.AddItem(l, 10, 4));
        Assert.Equal(new[] { (10u, 7), (11u, 1) }, Rows(l));
    }

    [Fact]
    public void AddItem_SaturatesAtMax()
    {
        var l = L("1", "A", (10, 9_999_990));
        CustomListOps.AddItem(l, 10, 100);
        Assert.Equal(9_999_999, l.Items[0].Quantity);
        CustomListOps.AddItem(l, 10, int.MaxValue);
        Assert.Equal(9_999_999, l.Items[0].Quantity);
    }

    [Fact]
    public void AddItem_NewRowClampedToMax()
    {
        var l = L("1", "A");
        CustomListOps.AddItem(l, 10, int.MaxValue);
        Assert.Equal(9_999_999, l.Items[0].Quantity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(int.MinValue)]
    public void AddItem_NonPositiveQuantity_BecomesOne(int q)
    {
        var l = L("1", "A");
        CustomListOps.AddItem(l, 10, q);
        Assert.Equal(new[] { (10u, 1) }, Rows(l));
    }

    [Fact]
    public void AddItem_ItemZero_Rejected()
    {
        var l = L("1", "A");
        Assert.False(CustomListOps.AddItem(l, 0, 5));
        Assert.Empty(l.Items);
    }

    [Fact]
    public void SetQuantity_ClampsAndUnknownIsNoOp()
    {
        var l = L("1", "A", (10, 5));
        Assert.True(CustomListOps.SetQuantity(l, 10, 0));
        Assert.Equal(1, l.Items[0].Quantity);
        Assert.True(CustomListOps.SetQuantity(l, 10, -3));
        Assert.Equal(1, l.Items[0].Quantity);
        Assert.True(CustomListOps.SetQuantity(l, 10, 20_000_000));
        Assert.Equal(9_999_999, l.Items[0].Quantity);
        Assert.True(CustomListOps.SetQuantity(l, 10, 42));
        Assert.Equal(42, l.Items[0].Quantity);
        Assert.False(CustomListOps.SetQuantity(l, 99, 7));
        Assert.Equal(new[] { (10u, 42) }, Rows(l));
    }

    [Fact]
    public void RemoveItem_RemovesRowOnly()
    {
        var l = L("1", "A", (10, 1), (11, 2), (12, 3));
        Assert.True(CustomListOps.RemoveItem(l, 11));
        Assert.Equal(new[] { (10u, 1), (12u, 3) }, Rows(l));
        Assert.False(CustomListOps.RemoveItem(l, 11));
    }

    [Fact]
    public void Delete_RemovesById_UnknownIsNoOp()
    {
        var lists = new List<CustomList> { L("1", "A"), L("2", "B") };
        Assert.False(CustomListOps.Delete(lists, "9"));
        Assert.Equal(2, lists.Count);
        Assert.True(CustomListOps.Delete(lists, "1"));
        Assert.Equal("2", Assert.Single(lists).Id);
    }

    [Fact]
    public void ToRequirements_KeepsRowOrder()
    {
        var l = L("1", "A", (30, 3), (10, 1), (20, 2));
        var req = CustomListOps.ToRequirements(l);
        Assert.Equal(new[] { new Requirement(30, 3), new Requirement(10, 1), new Requirement(20, 2) }, req);
        Assert.Empty(CustomListOps.ToRequirements(L("2", "B")));
    }

    [Fact]
    public void PruneExclusions_RemovesOnlyIdsNotOnList()
    {
        var l = L("1", "A", (10, 1), (11, 1));
        l.ExcludedItemIds.UnionWith(new uint[] { 10, 99, 100 });
        Assert.Equal(2, CustomListOps.PruneExclusions(l));
        Assert.Equal(new uint[] { 10 }, l.ExcludedItemIds.ToArray());
        Assert.Equal(0, CustomListOps.PruneExclusions(l));
    }

    [Fact]
    public void RemoveItem_ThenPrune_DropsItsExclusion()
    {
        var l = L("1", "A", (10, 1));
        l.ExcludedItemIds.Add(10);
        CustomListOps.RemoveItem(l, 10);
        CustomListOps.PruneExclusions(l);
        Assert.Empty(l.ExcludedItemIds);
    }

    [Fact]
    public void Create_LengthCountsCharactersNotBytes()
    {
        Assert.True(CustomListOps.Create(new List<CustomList>(), new string('あ', 60)).Ok);
        Assert.Equal("Names can be at most 60 characters", CustomListOps.Create(new List<CustomList>(), new string('あ', 61)).Error);
    }

    [Fact]
    public void Delete_DuplicateIds_RemovesOnlyTheFirst()
    {
        var first = L("1", "A");
        var second = L("1", "B");
        var lists = new List<CustomList> { first, L("2", "C"), second };
        Assert.True(CustomListOps.Delete(lists, "1"));
        Assert.Equal(new[] { "C", "B" }, lists.Select(l => l.Name).ToArray());
        Assert.Same(second, lists[1]);
    }

    [Fact]
    public void Setters_CoalesceNull()
    {
        var l = new CustomList { Name = null!, Items = null!, ExcludedItemIds = null! };
        Assert.Equal(string.Empty, l.Name);
        Assert.NotNull(l.Items);
        Assert.Empty(l.Items);
        Assert.NotNull(l.ExcludedItemIds);
        Assert.Empty(l.ExcludedItemIds);
    }

    private static Func<string> Ids()
    {
        var n = 0;
        return () => "new-" + (++n);
    }

    [Fact]
    public void Normalize_CleanInput_Unchanged()
    {
        var lists = new List<CustomList> { L("1", "A", (10, 1)), L("2", "B") };
        Assert.Equal(0, CustomListOps.Normalize(lists, Ids()));
        Assert.Equal(new[] { "1", "2" }, lists.Select(l => l.Id).ToArray());
        Assert.Equal(new[] { (10u, 1) }, Rows(lists[0]));
    }

    [Fact]
    public void Normalize_DropsNullLists()
    {
        var lists = new List<CustomList> { null!, L("1", "A"), null! };
        Assert.Equal(2, CustomListOps.Normalize(lists, Ids()));
        Assert.Equal("1", Assert.Single(lists).Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Normalize_BlankId_GetsFreshId(string? id)
    {
        var lists = new List<CustomList> { new CustomList { Id = id!, Name = "A" } };
        Assert.Equal(1, CustomListOps.Normalize(lists, Ids()));
        Assert.Equal("new-1", lists[0].Id);
    }

    [Fact]
    public void Normalize_DuplicateId_LaterListGetsFreshId()
    {
        var lists = new List<CustomList> { L("1", "A"), L("2", "B"), L("1", "C") };
        Assert.Equal(1, CustomListOps.Normalize(lists, Ids()));
        Assert.Equal(new[] { "1", "2", "new-1" }, lists.Select(l => l.Id).ToArray());
    }

    [Fact]
    public void Normalize_DefaultFactory_GivesGuid()
    {
        var lists = new List<CustomList> { L("", "A") };
        CustomListOps.Normalize(lists);
        Assert.True(Guid.TryParse(lists[0].Id, out _));
    }

    [Fact]
    public void Normalize_DropsNullRows()
    {
        var l = L("1", "A", (10, 1));
        l.Items.Insert(0, null!);
        var lists = new List<CustomList> { l };
        Assert.Equal(1, CustomListOps.Normalize(lists, Ids()));
        Assert.Equal(new[] { (10u, 1) }, Rows(l));
    }

    private static List<Requirement> Req(params (uint item, int qty)[] rows)
        => rows.Select(r => new Requirement(r.item, r.qty)).ToList();

    [Fact]
    public void AddMaterials_NewRowsAppendedInGivenOrder_ReturnsCount()
    {
        var l = L("1", "A", (10, 1));
        var n = CustomListOps.AddMaterials(l, Req((30, 2), (20, 3)));
        Assert.Equal(2, n);
        Assert.Equal(new[] { (10u, 1), (30u, 2), (20u, 3) }, Rows(l));
    }

    [Fact]
    public void AddMaterials_ExistingRowKeepsPositionAndSums()
    {
        var l = L("1", "A", (10, 5), (11, 2));
        var n = CustomListOps.AddMaterials(l, Req((11, 3), (10, 1)));
        Assert.Equal(2, n);
        Assert.Equal(new[] { (10u, 6), (11u, 5) }, Rows(l));
    }

    [Fact]
    public void AddMaterials_SumSaturatesAtMaxQuantity()
    {
        var l = L("1", "A", (10, 9_000_000));
        Assert.Equal(1, CustomListOps.AddMaterials(l, Req((10, 5_000_000))));
        Assert.Equal(new[] { (10u, 9_999_999) }, Rows(l));
    }

    [Fact]
    public void AddMaterials_ClearsExclusionOnlyForNewOrChangedRows()
    {
        var l = L("1", "A", (10, 5), (11, 2), (12, CustomListOps.MaxQuantity));
        l.ExcludedItemIds.UnionWith(new uint[] { 10, 11, 12, 13 });
        var n = CustomListOps.AddMaterials(l, Req((10, 1), (12, 4), (13, 2)));
        Assert.Equal(2, n);
        Assert.Equal(new uint[] { 11, 12 }, l.ExcludedItemIds.OrderBy(x => x).ToArray());
        Assert.Equal(new[] { (10u, 6), (11u, 2), (12u, CustomListOps.MaxQuantity), (13u, 2) }, Rows(l));
    }

    [Fact]
    public void AddMaterials_AlreadySaturatedRow_IsNotCountedAndKeepsExclusion()
    {
        var l = L("1", "A", (10, CustomListOps.MaxQuantity));
        l.ExcludedItemIds.Add(10);
        Assert.Equal(0, CustomListOps.AddMaterials(l, Req((10, 7))));
        Assert.Contains(10u, l.ExcludedItemIds);
    }

    [Fact]
    public void AddMaterials_SkipsItemZeroAndQuantityBelowOne()
    {
        var l = L("1", "A");
        Assert.Equal(1, CustomListOps.AddMaterials(l, Req((0, 5), (10, 0), (11, -3), (12, 1))));
        Assert.Equal(new[] { (12u, 1) }, Rows(l));
    }

    [Fact]
    public void AddMaterials_NothingToAdd_ReturnsZero()
    {
        var l = L("1", "A", (10, 1));
        Assert.Equal(0, CustomListOps.AddMaterials(l, Req()));
        Assert.Equal(new[] { (10u, 1) }, Rows(l));
    }

    [Fact]
    public void AddMaterials_ThenPruneExclusions_KeepsExclusionsOfRowsStillThere()
    {
        var l = L("1", "A", (10, 1), (11, 1));
        l.ExcludedItemIds.UnionWith(new uint[] { 10, 99 });
        CustomListOps.AddMaterials(l, Req((11, 2)));
        Assert.Equal(1, CustomListOps.PruneExclusions(l));
        Assert.Equal(new uint[] { 10 }, l.ExcludedItemIds.ToArray());
    }
}
