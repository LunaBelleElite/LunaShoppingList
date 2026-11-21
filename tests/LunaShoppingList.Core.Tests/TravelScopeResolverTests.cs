using LunaShoppingList.Core;
using Xunit;

namespace LunaShoppingList.Core.Tests;

public class TravelScopeResolverTests
{
    // Fixture only: realistic example names, not Core data.
    private static readonly WorldInfo[] Worlds =
    [
        W(1, "Adamantoise", "Aether", "North America"),
        W(2, "Cactuar", "Aether", "North America"),
        W(3, "Ragnarok", "Chaos", "Europe"),
        W(4, "Cerberus", "Chaos", "Europe"),
        W(5, "Alpha", "Light", "Europe"),
        W(6, "Ravana", "Materia", "Oceania"),
        W(7, "Bismarck", "Materia", "Oceania"),
        W(8, "Behemoth", "Primal", "North America"),
        W(9, "Halicarnassus", "Crystal", "North America"),
        W(10, "Cuchulainn", "Dynamis", "North America"),
        W(99, "TestWorld", "Aether", "North America", isPublic: false),
    ];

    private static WorldInfo W(uint id, string name, string dc, string region, bool isPublic = true) =>
        new(id, name, dc, region, dc, isPublic);

    private static WorldInfo ById(uint id) => Worlds.Single(w => w.Id == id);

    private static string[] Names(TravelScopeResult r) => r.Worlds.Select(w => w.Name).ToArray();

    [Fact]
    public void CrossWorld_AetherWorld_SeesAllNorthAmericanDcsButNotMateria()
    {
        var r = TravelScopeResolver.Resolve(TravelScopeKind.CrossWorld, ById(1), Worlds);
        Assert.Equal(
            ["Adamantoise", "Cactuar", "Halicarnassus", "Cuchulainn", "Behemoth"],
            Names(r));
        Assert.Equal(["Aether", "Crystal", "Dynamis", "Primal"], r.UniversalisScopes);
        Assert.DoesNotContain(r.Worlds, w => w.DataCenter == "Materia");
    }

    [Fact]
    public void CrossWorld_MateriaWorld_SeesOnlyOceania()
    {
        var r = TravelScopeResolver.Resolve(TravelScopeKind.CrossWorld, ById(6), Worlds);
        Assert.Equal(["Bismarck", "Ravana"], Names(r));
        Assert.Equal(["Materia"], r.UniversalisScopes);
    }

    [Fact]
    public void CrossWorld_ChaosWorld_SeesEurope()
    {
        var r = TravelScopeResolver.Resolve(TravelScopeKind.CrossWorld, ById(3), Worlds);
        Assert.Equal(["Cerberus", "Ragnarok", "Alpha"], Names(r));
        Assert.Equal(["Chaos", "Light"], r.UniversalisScopes);
    }

    [Fact]
    public void CrossDataCenter_ReturnsEveryRegionPublicWorldsOnly()
    {
        var r = TravelScopeResolver.Resolve(TravelScopeKind.CrossDataCenter, ById(1), Worlds);
        Assert.Equal(10, r.Worlds.Count);
        Assert.DoesNotContain(r.Worlds, w => w.Id == 99);
        Assert.Equal(
            ["Aether", "Chaos", "Crystal", "Dynamis", "Light", "Materia", "Primal"],
            r.UniversalisScopes);
    }

    [Fact]
    public void CrossDataCenter_FromMateria_StillReturnsEveryRegion()
    {
        var r = TravelScopeResolver.Resolve(TravelScopeKind.CrossDataCenter, ById(6), Worlds);
        Assert.Equal(10, r.Worlds.Count);
        Assert.Contains(r.Worlds, w => w.Region == "North America");
        Assert.Contains(r.Worlds, w => w.Region == "Europe");
    }

    [Theory]
    [InlineData(TravelScopeKind.CrossWorld)]
    [InlineData(TravelScopeKind.CrossDataCenter)]
    public void NonPublicWorldsExcluded(TravelScopeKind kind)
    {
        var r = TravelScopeResolver.Resolve(kind, ById(1), Worlds);
        Assert.DoesNotContain(r.Worlds, w => !w.IsPublic);
    }

    [Fact]
    public void NonPublicCurrentWorldPresentInAll_IsNotIncluded()
    {
        var r = TravelScopeResolver.Resolve(TravelScopeKind.CrossWorld, ById(99), Worlds);
        Assert.DoesNotContain(r.Worlds, w => w.Id == 99);
        Assert.Contains(r.Worlds, w => w.Id == 1);
    }

    [Fact]
    public void CurrentAbsentFromAll_IsIncludedEvenIfNonPublic()
    {
        var current = W(500, "Elsewhere", "Aether", "North America", isPublic: false);
        var r = TravelScopeResolver.Resolve(TravelScopeKind.CrossWorld, current, Worlds);
        Assert.Equal(1, r.Worlds.Count(w => w.Id == 500));
    }

    [Fact]
    public void CurrentAbsentFromAll_CrossDataCenter_IsIncluded()
    {
        var current = W(500, "Elsewhere", "Zed", "Mars");
        var r = TravelScopeResolver.Resolve(TravelScopeKind.CrossDataCenter, current, Worlds);
        Assert.Equal(11, r.Worlds.Count);
        Assert.Contains("Zed", r.UniversalisScopes);
    }

    [Fact]
    public void EmptyAll_ReturnsJustCurrent()
    {
        foreach (var kind in new[] { TravelScopeKind.CrossWorld, TravelScopeKind.CrossDataCenter })
        {
            var r = TravelScopeResolver.Resolve(kind, ById(1), []);
            Assert.Equal(["Adamantoise"], Names(r));
            Assert.Equal(["Aether"], r.UniversalisScopes);
        }
    }

    [Fact]
    public void CurrentInAll_IsNotDuplicated()
    {
        var r = TravelScopeResolver.Resolve(TravelScopeKind.CrossWorld, ById(1), Worlds);
        Assert.Equal(1, r.Worlds.Count(w => w.Id == 1));
    }

    [Fact]
    public void DuplicateIdsInAll_AreCollapsed()
    {
        var dup = Worlds.Concat(Worlds).ToArray();
        var r = TravelScopeResolver.Resolve(TravelScopeKind.CrossDataCenter, ById(1), dup);
        Assert.Equal(10, r.Worlds.Count);
        Assert.Equal(r.Worlds.Count, r.Worlds.Select(w => w.Id).Distinct().Count());
    }

    // ---- qa-tester additions (2026-11-21): duplicate Ids that are NOT identical copies ----

    [Fact]
    public void DuplicateId_CurrentWorldWinsOverItsConflictingCopyInAll()
    {
        var current = W(1, "Adamantoise", "Aether", "North America");
        var all = new[] { W(1, "Renamed", "Primal", "North America"), W(2, "Cactuar", "Aether", "North America") };
        var r = TravelScopeResolver.Resolve(TravelScopeKind.CrossWorld, current, all);
        var one = Assert.Single(r.Worlds, w => w.Id == 1);
        Assert.Equal("Adamantoise", one.Name);
        Assert.Equal("Aether", one.DataCenter);
        Assert.Equal(["Aether"], r.UniversalisScopes);
    }

    [Fact]
    public void DuplicateId_FirstPublicCopyInAllWinsAndInputOrderDecides()
    {
        var current = W(1, "Cur", "Aether", "R");
        var first = W(5, "First", "Aether", "R");
        var second = W(5, "Second", "Aether", "R");
        Assert.Equal("First", Assert.Single(TravelScopeResolver.Resolve(TravelScopeKind.CrossDataCenter, current, [current, first, second]).Worlds, w => w.Id == 5).Name);
        Assert.Equal("Second", Assert.Single(TravelScopeResolver.Resolve(TravelScopeKind.CrossDataCenter, current, [current, second, first]).Worlds, w => w.Id == 5).Name);
    }

    [Fact]
    public void DuplicateId_NonPublicCopyDoesNotShadowAPublicCopy()
    {
        var current = W(1, "Cur", "Aether", "R");
        var hidden = W(5, "Hidden", "Aether", "R", isPublic: false);
        var visible = W(5, "Visible", "Aether", "R");
        var r = TravelScopeResolver.Resolve(TravelScopeKind.CrossDataCenter, current, [hidden, visible]);
        Assert.Equal("Visible", Assert.Single(r.Worlds, w => w.Id == 5).Name);
    }

    [Fact]
    public void NullEntriesInAll_AreIgnored()
    {
        var r = TravelScopeResolver.Resolve(TravelScopeKind.CrossDataCenter, ById(1), [null!, ById(2), null!]);
        Assert.Equal(["Adamantoise", "Cactuar"], Names(r));
    }

    [Fact]
    public void Order_IsDeterministicUnderShuffle()
    {
        string[] expected =
        [
            "Adamantoise", "Cactuar", "Cerberus", "Ragnarok", "Halicarnassus", "Cuchulainn",
            "Alpha", "Bismarck", "Ravana", "Behemoth",
        ];
        var rng = new Random(12345);
        for (var i = 0; i < 20; i++)
        {
            var shuffled = Worlds.OrderBy(_ => rng.Next()).ToArray();
            var r = TravelScopeResolver.Resolve(TravelScopeKind.CrossDataCenter, ById(1), shuffled);
            Assert.Equal(expected, Names(r));
        }
    }

    [Fact]
    public void Order_IsDataCenterThenNameThenId_IgnoreCase()
    {
        // Ids 1 and 2 share DC and name (case differs), so Id decides; "aether"/"beta" sorts after "Aether"/"Alpha".
        var all = new[]
        {
            W(3, "beta", "aether", "R"),
            W(2, "alpha", "Aether", "R"),
            W(1, "Alpha", "Aether", "R"),
            W(4, "Zed", "Aether", "R"),
            W(5, "Mid", "bravo", "R"),
        };
        var r = TravelScopeResolver.Resolve(TravelScopeKind.CrossWorld, all[1], all);
        Assert.Equal([1u, 2u, 3u, 4u, 5u], r.Worlds.Select(w => w.Id).ToArray());
    }

    [Fact]
    public void RegionAndDataCenterComparison_IgnoreCase()
    {
        var all = new[]
        {
            W(1, "A", "Aether", "North America"),
            W(2, "B", "PRIMAL", "NORTH AMERICA"),
            W(3, "C", "aether", "north america"),
        };
        var r = TravelScopeResolver.Resolve(TravelScopeKind.CrossWorld, all[0], all);
        Assert.Equal(3, r.Worlds.Count);
        // Aether and aether collapse to one scope.
        Assert.Equal(2, r.UniversalisScopes.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void CrossWorld_EmptyRegion_FailsSafeToOwnDataCenter(string? region)
    {
        var current = W(1, "Adamantoise", "Aether", region!);
        var all = new[]
        {
            current,
            W(2, "Cactuar", "Aether", region!),
            W(8, "Behemoth", "Primal", "North America"),
            W(6, "Ravana", "Materia", region!),
        };
        var r = TravelScopeResolver.Resolve(TravelScopeKind.CrossWorld, current, all);
        Assert.Equal(["Adamantoise", "Cactuar"], Names(r));
        Assert.Equal(["Aether"], r.UniversalisScopes);
    }

    [Fact]
    public void CrossWorld_EmptyRegionOnOthers_DoesNotMatchRealRegionCurrent()
    {
        var all = new[] { W(1, "A", "X", "Reg"), W(2, "B", "Y", "") };
        var r = TravelScopeResolver.Resolve(TravelScopeKind.CrossWorld, all[0], all);
        Assert.Equal(["A"], Names(r));
    }

    [Fact]
    public void CrossDataCenter_EmptyRegion_StillReturnsEverything()
    {
        var current = W(1, "Adamantoise", "Aether", "");
        var r = TravelScopeResolver.Resolve(TravelScopeKind.CrossDataCenter, current, Worlds);
        Assert.Equal(10, r.Worlds.Count);
        Assert.Equal(7, r.UniversalisScopes.Count);
    }

    [Fact]
    public void Scopes_AreDistinctAndSortedOrdinalIgnoreCase()
    {
        var all = new[]
        {
            W(1, "A", "crystal", "R"), W(2, "B", "Aether", "R"), W(3, "C", "Crystal", "R"),
            W(4, "D", "Primal", "R"), W(5, "E", "Aether", "R"),
        };
        var r = TravelScopeResolver.Resolve(TravelScopeKind.CrossWorld, all[1], all);
        Assert.Equal(3, r.UniversalisScopes.Count);
        Assert.Equal("Aether", r.UniversalisScopes[0]);
        Assert.Equal("Primal", r.UniversalisScopes[2]);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => TravelScopeResolver.Resolve(TravelScopeKind.CrossWorld, null!, Worlds));
        Assert.Throws<ArgumentNullException>(() => TravelScopeResolver.Resolve(TravelScopeKind.CrossWorld, ById(1), null!));
    }

    [Fact]
    public void UndefinedKind_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TravelScopeResolver.Resolve((TravelScopeKind)99, ById(1), Worlds));
    }
}
