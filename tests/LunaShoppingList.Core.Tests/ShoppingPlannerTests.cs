using LunaShoppingList.Core;

namespace LunaShoppingList.Core.Tests;

public class ShoppingPlannerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static Listing L(uint item, uint world, string name, int price, int qty, bool hq = false, DateTimeOffset? seen = null)
        => new(item, world, name, price, qty, hq, seen ?? T0);

    private static List<ItemListings> Group(IEnumerable<Listing> ls) =>
        ls.GroupBy(l => l.ItemId).Select(g => new ItemListings(g.Key, g.ToList())).ToList();

    private static ShoppingPlan Run(Requirement[] reqs, Listing[] listings, uint[] worlds, ExclusionState? ex = null)
        => ShoppingPlanner.Plan(reqs, Group(listings), worlds.ToHashSet(), ex ?? new ExclusionState());

    [Fact]
    public void AllFromOneWorld()
    {
        var plan = Run([new(1, 5)], [L(1, 10, "Alpha", 100, 3), L(1, 10, "Alpha", 120, 10)], [10]);
        var g = Assert.Single(plan.Worlds);
        Assert.Equal(10u, g.WorldId);
        Assert.Equal("Alpha", g.WorldName);
        Assert.Equal(2, g.Purchases.Count);
        Assert.Equal((3, 100), (g.Purchases[0].Quantity, g.Purchases[0].PricePerUnit));
        Assert.Equal((2, 120), (g.Purchases[1].Quantity, g.Purchases[1].PricePerUnit));
        Assert.Equal(3 * 100 + 2 * 120, g.Total);
        Assert.Empty(plan.Skipped);
        Assert.Empty(plan.Shortfalls);
    }

    [Fact]
    public void SplitAcrossWorlds_OrderedByWorldName()
    {
        var plan = Run([new(1, 4)], [L(1, 20, "Zeta", 50, 2), L(1, 10, "alpha", 80, 5)], [10, 20]);
        Assert.Equal(["alpha", "Zeta"], plan.Worlds.Select(w => w.WorldName));
        Assert.Equal(2, plan.Worlds[0].Purchases[0].Quantity);
        Assert.Equal(2, plan.Worlds[1].Purchases[0].Quantity);
        Assert.Equal(160, plan.Worlds[0].Total);
        Assert.Equal(100, plan.Worlds[1].Total);
    }

    [Fact]
    public void WorldsWithSameName_OrderedByWorldId()
    {
        var plan = Run([new(1, 2)], [L(1, 30, "Same", 10, 1), L(1, 20, "Same", 10, 1)], [20, 30]);
        Assert.Equal([20u, 30u], plan.Worlds.Select(w => w.WorldId));
    }

    [Fact]
    public void CheapestAcrossNqAndHq()
    {
        var plan = Run([new(1, 1)], [L(1, 10, "A", 200, 5, hq: false), L(1, 10, "A", 150, 5, hq: true)], [10]);
        var p = Assert.Single(plan.Worlds[0].Purchases);
        Assert.True(p.Hq);
        Assert.Equal(150, p.PricePerUnit);
        var plan2 = Run([new(1, 1)], [L(1, 10, "A", 100, 5, hq: false), L(1, 10, "A", 150, 5, hq: true)], [10]);
        Assert.False(Assert.Single(plan2.Worlds[0].Purchases).Hq);
    }

    [Fact]
    public void PartialStack_NeverBuysMoreThanNeeded()
    {
        var plan = Run([new(1, 3)], [L(1, 10, "A", 10, 99)], [10]);
        Assert.Equal(3, Assert.Single(plan.Worlds[0].Purchases).Quantity);
        Assert.Empty(plan.Shortfalls);
    }

    [Fact]
    public void ShortfallPartial()
    {
        var plan = Run([new(1, 10)], [L(1, 10, "A", 10, 4)], [10]);
        Assert.Equal(4, Assert.Single(plan.Worlds[0].Purchases).Quantity);
        Assert.Equal(new ShortfallItem(1, 6), Assert.Single(plan.Shortfalls));
    }

    [Fact]
    public void ShortfallFull_NoListings_OrderedByItemId()
    {
        var plan = Run([new(9, 2), new(3, 7)], [], [10]);
        Assert.Empty(plan.Worlds);
        Assert.Equal([new ShortfallItem(3, 7), new ShortfallItem(9, 2)], plan.Shortfalls);
    }

    [Fact]
    public void DisallowedWorldIgnored_CheaperListingNotUsed()
    {
        var plan = Run([new(1, 2)], [L(1, 99, "Cheap", 1, 50), L(1, 10, "Ok", 500, 50)], [10]);
        var g = Assert.Single(plan.Worlds);
        Assert.Equal(10u, g.WorldId);
        Assert.Equal(500, g.Purchases[0].PricePerUnit);
    }

    [Fact]
    public void OnlyDisallowedListings_IsFullShortfall()
    {
        var plan = Run([new(1, 2)], [L(1, 99, "Cheap", 1, 50)], [10]);
        Assert.Empty(plan.Worlds);
        Assert.Equal(new ShortfallItem(1, 2), Assert.Single(plan.Shortfalls));
    }

    [Fact]
    public void ExcludedItem_SkippedWithQuantity_NotBought_NoShortfall()
    {
        var ex = new ExclusionState();
        ex.SetExcluded(2, true);
        ex.SetExcluded(1, true);
        var plan = Run([new(2, 6), new(1, 3), new(3, 1)], [L(2, 10, "A", 5, 100), L(3, 10, "A", 7, 1)], [10], ex);
        Assert.Equal([new SkippedItem(1, 3), new SkippedItem(2, 6)], plan.Skipped);
        var p = Assert.Single(plan.Worlds[0].Purchases);
        Assert.Equal(3u, p.ItemId);
        Assert.Empty(plan.Shortfalls);
    }

    [Fact]
    public void TieBreak_NewerReviewThenWorldIdThenNqFirst()
    {
        var plan = Run([new(1, 1)], [L(1, 10, "A", 5, 1, seen: T0), L(1, 20, "B", 5, 1, seen: T0.AddHours(1))], [10, 20]);
        Assert.Equal(20u, Assert.Single(plan.Worlds).WorldId);
        plan = Run([new(1, 1)], [L(1, 20, "B", 5, 1), L(1, 10, "A", 5, 1)], [10, 20]);
        Assert.Equal(10u, Assert.Single(plan.Worlds).WorldId);
        plan = Run([new(1, 1)], [L(1, 10, "A", 5, 1, hq: true), L(1, 10, "A", 5, 1, hq: false)], [10]);
        Assert.False(Assert.Single(plan.Worlds[0].Purchases).Hq);
    }

    private static string Render(ShoppingPlan p) =>
        string.Join("|", p.Worlds.Select(w =>
            $"{w.WorldId}:{w.WorldName}:{w.Total}:" + string.Join(",", w.Purchases.Select(x => $"{x.ItemId}/{x.Quantity}/{x.PricePerUnit}/{x.Hq}/{x.LastReview:o}"))))
        + "#" + string.Join(",", p.Shortfalls) + "#" + string.Join(",", p.Skipped);

    [Fact]
    public void Determinism_ShuffledInputGivesIdenticalOutput()
    {
        var listings = new List<Listing>();
        for (uint item = 1; item <= 4; item++)
            for (uint w = 10; w < 14; w++)
                foreach (var hq in new[] { false, true })
                    listings.Add(L(item, w, "W" + (w % 3), 10 + (int)(w % 2), 2, hq, T0.AddMinutes(w % 2)));
        var reqs = new List<Requirement> { new(1, 5), new(2, 7), new(3, 3), new(4, 20), new(2, 1) };
        var allowed = new HashSet<uint> { 10, 11, 12, 13 };

        var basePlan = ShoppingPlanner.Plan(reqs, Group(listings), allowed, new ExclusionState());
        Assert.Equal(new ShortfallItem(4, 4), Assert.Single(basePlan.Shortfalls)); // fixture has 16 units of item 4, 20 needed
        var baseline = Render(basePlan);
        var rng = new Random(1234);
        for (int i = 0; i < 25; i++)
        {
            var sl = listings.OrderBy(_ => rng.Next()).ToList();
            var sr = reqs.OrderBy(_ => rng.Next()).ToList();
            var groups = Group(sl).OrderBy(_ => rng.Next()).ToList();
            Assert.Equal(baseline, Render(ShoppingPlanner.Plan(sr, groups, allowed, new ExclusionState())));
        }
    }

    [Fact]
    public void DuplicateRequirements_AreMerged()
    {
        var plan = Run([new(1, 2), new(1, 3)], [L(1, 10, "A", 10, 100)], [10]);
        Assert.Equal(5, Assert.Single(plan.Worlds[0].Purchases).Quantity);
    }

    [Fact]
    public void DuplicateExcludedRequirements_SkippedOnceWithSum()
    {
        var ex = new ExclusionState();
        ex.SetExcluded(1, true);
        var plan = Run([new(1, 2), new(1, 3)], [], [10], ex);
        Assert.Equal(new SkippedItem(1, 5), Assert.Single(plan.Skipped));
    }

    [Fact]
    public void EmptyInputs_GiveEmptyPlan()
    {
        var plan = Run([], [], []);
        Assert.Empty(plan.Worlds);
        Assert.Empty(plan.Skipped);
        Assert.Empty(plan.Shortfalls);
        var plan2 = Run([new(1, 1)], [L(1, 10, "A", 1, 1)], []);
        Assert.Empty(plan2.Worlds);
        Assert.Single(plan2.Shortfalls);
    }

    [Fact]
    public void ZeroAndNegativeQuantities_Ignored()
    {
        var ex = new ExclusionState();
        ex.SetExcluded(3, true);
        var plan = Run([new(1, 0), new(2, -4), new(3, 0), new(4, 2), new(4, -1)],
            [L(1, 10, "A", 1, 5), L(2, 10, "A", 1, 5), L(4, 10, "A", 1, 5)], [10], ex);
        Assert.Empty(plan.Skipped);
        Assert.Empty(plan.Shortfalls);
        var p = Assert.Single(plan.Worlds[0].Purchases);
        Assert.Equal((4u, 2), (p.ItemId, p.Quantity));
    }

    [Fact]
    public void LargeQuantities_TotalIsLongAndDoesNotOverflow()
    {
        var plan = Run([new(1, 2_000_000_000)], [L(1, 10, "A", 2_000_000_000, 2_000_000_000)], [10]);
        Assert.Equal(4_000_000_000_000_000_000L, plan.Worlds[0].Total);
    }

    [Fact]
    public void PurchasesWithinWorld_OrderedByItemIdThenPrice_RowsNotMerged()
    {
        var plan = Run([new(2, 3), new(1, 3)],
            [L(2, 10, "A", 5, 3), L(1, 10, "A", 9, 1), L(1, 10, "A", 7, 1), L(1, 10, "A", 8, 1)], [10]);
        var rows = plan.Worlds[0].Purchases.Select(p => (p.ItemId, p.PricePerUnit)).ToList();
        Assert.Equal([(1u, 7), (1u, 8), (1u, 9), (2u, 5)], rows);
        Assert.Equal(7 + 8 + 9 + 15, plan.Worlds[0].Total);
    }

    // ---- qa-tester additions (2026-11-21): gaps reported by the implementers, then cross-module adversarial cases ----

    [Fact]
    public void MergedRequirementAboveIntMax_IsClampedNotWrapped_Bought()
    {
        // int.MaxValue + 5 as int wraps to a large negative number; the clamp must give exactly int.MaxValue.
        var plan = Run([new(1, int.MaxValue), new(1, 5)], [L(1, 10, "A", 1, int.MaxValue)], [10]);
        var p = Assert.Single(plan.Worlds[0].Purchases);
        Assert.Equal(int.MaxValue, p.Quantity);
        Assert.Empty(plan.Shortfalls);
    }

    [Fact]
    public void MergedRequirementAboveIntMax_IsClampedNotWrapped_Shortfall()
    {
        var plan = Run([new(1, int.MaxValue), new(1, 5)], [L(1, 10, "A", 1, 10)], [10]);
        Assert.Equal(new ShortfallItem(1, int.MaxValue - 10), Assert.Single(plan.Shortfalls));
    }

    [Fact]
    public void MergedRequirementAboveIntMax_IsClampedNotWrapped_Skipped()
    {
        var ex = new ExclusionState();
        ex.SetExcluded(1, true);
        var plan = Run([new(1, int.MaxValue), new(1, 5)], [], [10], ex);
        Assert.Equal(new SkippedItem(1, int.MaxValue), Assert.Single(plan.Skipped));
    }

    [Fact]
    public void OneWorldIdWithTwoWorldNames_YieldsOneGroup_NamedByOrdinalMinimum()
    {
        // Item 1 (first row in the group) is named "alpha", item 2 "Zed". Ordinal minimum is "Zed" (uppercase sorts
        // before lowercase), which is neither the first row's name nor the OrdinalIgnoreCase minimum ("alpha").
        var plan = Run([new(1, 1), new(2, 1)], [L(1, 10, "alpha", 5, 1), L(2, 10, "Zed", 5, 1)], [10]);
        var g = Assert.Single(plan.Worlds);
        Assert.Equal(10u, g.WorldId);
        Assert.Equal("Zed", g.WorldName);
        Assert.Equal(2, g.Purchases.Count);
        Assert.Equal(10, g.Total);
    }

    [Fact]
    public void OneWorldIdWithTwoWorldNames_GroupNameIndependentOfInputOrder()
    {
        // Same item, price, quality, review time and quantity: the two rows tie on every sort key, so only the
        // explicit ordinal-minimum rule (not the input order) can make the group name stable.
        var a = Run([new(1, 2)], [L(1, 10, "alpha", 5, 1), L(1, 10, "Zed", 5, 1)], [10]);
        var b = Run([new(1, 2)], [L(1, 10, "Zed", 5, 1), L(1, 10, "alpha", 5, 1)], [10]);
        Assert.Equal("Zed", Assert.Single(a.Worlds).WorldName);
        Assert.Equal("Zed", Assert.Single(b.Worlds).WorldName);
    }

    [Fact]
    public void ZeroOrNegativePrice_ListingsAreIgnored_NeverBoughtAndNeverCounted()
    {
        var plan = Run([new(1, 2)], [L(1, 10, "A", 100, 5), L(1, 10, "A", 0, 1), L(1, 10, "A", -7, 1)], [10]);
        var rows = plan.Worlds[0].Purchases.Select(p => (p.Quantity, p.PricePerUnit)).ToList();
        Assert.Equal([(2, 100)], rows);
        Assert.Equal(200, plan.Worlds[0].Total);
        Assert.Empty(plan.Shortfalls);
    }

    [Fact]
    public void ZeroOrNegativePrice_DoesNotCountTowardQuantity_SoOnlyValidStockCovers()
    {
        var plan = Run([new(1, 5)], [L(1, 10, "A", 0, 100), L(1, 10, "A", -3, 100), L(1, 10, "A", 20, 2)], [10]);
        Assert.Equal([(2, 20)], plan.Worlds[0].Purchases.Select(p => (p.Quantity, p.PricePerUnit)).ToList());
        Assert.Equal(new ShortfallItem(1, 3), Assert.Single(plan.Shortfalls));
    }

    [Fact]
    public void OnlyZeroOrNegativePriced_ListingsMeansNothingBought()
    {
        var plan = Run([new(1, 2)], [L(1, 10, "A", 0, 5), L(1, 10, "A", -1, 5)], [10]);
        Assert.Empty(plan.Worlds);
        Assert.Equal(new ShortfallItem(1, 2), Assert.Single(plan.Shortfalls));
    }

    [Fact]
    public void ListingsWithWorldIdZero_AreDroppedUnlessZeroIsAllowed()
    {
        // The Universalis parser yields WorldId 0 when a listing and its view both lack worldID.
        var plan = Run([new(1, 1)], [L(1, 0, "", 1, 5), L(1, 10, "A", 50, 5)], [10]);
        Assert.Equal(10u, Assert.Single(plan.Worlds).WorldId);
        var plan2 = Run([new(1, 1)], [L(1, 0, "", 1, 5)], [10]);
        Assert.Equal(new ShortfallItem(1, 1), Assert.Single(plan2.Shortfalls));
    }

    // ---- cross-module: Artisan JSON -> MaterialsCalculator -> TravelScopeResolver -> ShoppingPlanner ----

    private sealed class LambdaRecipes(Func<uint, RecipeInfo?> f) : IRecipeData
    {
        public RecipeInfo? GetRecipe(uint recipeId) => f(recipeId);
    }

    private static IReadOnlyList<Requirement> Reqs(string artisanJson, int listId, params (uint recipe, (uint item, int amount)[] ing)[] recipes)
    {
        var map = recipes.ToDictionary(r => r.recipe, r => new RecipeInfo(r.recipe, r.recipe + 1000, 1, r.ing.Select(i => new Ingredient(i.item, i.amount)).ToList()));
        var list = ArtisanConfigParser.ParseLists(artisanJson).Single(l => l.Id == listId);
        return new MaterialsCalculator(new LambdaRecipes(id => map.GetValueOrDefault(id))).Compute(list);
    }

    [Fact]
    public void Pipeline_ArtisanListWithZeroRecipes_GivesEmptyPlan()
    {
        var reqs = Reqs("{\"NewCraftingLists\":[{\"ID\":1,\"Name\":\"Empty\",\"Recipes\":[]}]}", 1);
        Assert.Empty(reqs);
        var plan = ShoppingPlanner.Plan(reqs, [], new HashSet<uint> { 10 }, new ExclusionState());
        Assert.Empty(plan.Worlds);
        Assert.Empty(plan.Skipped);
        Assert.Empty(plan.Shortfalls);
    }

    [Fact]
    public void Pipeline_NegativeZeroAndSkippedEntriesInConfig_NeverReachThePlan()
    {
        const string json = """
            {"NewCraftingLists":[{"ID":1,"Name":"L","Recipes":[
              {"ID":1,"Quantity":-5},
              {"ID":1,"Quantity":0},
              {"ID":2,"Quantity":3,"ListItemOptions":{"Skipping":true}},
              {"ID":3,"Quantity":2}]}]}
            """;
        var reqs = Reqs(json, 1, (1, [(10, 1)]), (2, [(20, 1)]), (3, [(30, 4)]));
        Assert.Equal([new Requirement(30, 8)], reqs);
        var plan = ShoppingPlanner.Plan(reqs, Group([L(10, 10, "A", 1, 99), L(20, 10, "A", 1, 99), L(30, 10, "A", 2, 99)]), new HashSet<uint> { 10 }, new ExclusionState());
        var p = Assert.Single(Assert.Single(plan.Worlds).Purchases);
        Assert.Equal((30u, 8), (p.ItemId, p.Quantity));
    }

    [Fact]
    public void Pipeline_CrossWorldFromAether_NeverBuysOnMateriaEvenWhenCheaper_CrossDcDoes()
    {
        // Listings are fetched per data center (the scopes), so a DC scope can return worlds the user cannot reach.
        WorldInfo W(uint id, string n, string dc, string region) => new(id, n, dc, region, dc, true);
        WorldInfo[] all = [W(1, "Adamantoise", "Aether", "North America"), W(2, "Behemoth", "Primal", "North America"), W(6, "Ravana", "Materia", "Oceania")];
        var listings = Group([L(1, 6, "Ravana", 1, 10), L(1, 2, "Behemoth", 90, 10)]);
        var reqs = new[] { new Requirement(1, 4) };

        var cw = TravelScopeResolver.Resolve(TravelScopeKind.CrossWorld, all[0], all);
        var planCw = ShoppingPlanner.Plan(reqs, listings, cw.Worlds.Select(w => w.Id).ToHashSet(), new ExclusionState());
        Assert.Equal(2u, Assert.Single(planCw.Worlds).WorldId);

        var cd = TravelScopeResolver.Resolve(TravelScopeKind.CrossDataCenter, all[0], all);
        var planCd = ShoppingPlanner.Plan(reqs, listings, cd.Worlds.Select(w => w.Id).ToHashSet(), new ExclusionState());
        Assert.Equal(6u, Assert.Single(planCd.Worlds).WorldId);
    }

    [Fact]
    public void Pipeline_UniversalisListingWithoutAnyWorldId_IsNotBoughtOnWorldZero()
    {
        // Real parser output flows into the planner: a listing with no worldID anywhere becomes WorldId 0 and is dropped.
        const string body = "{\"itemID\":1,\"listings\":[{\"pricePerUnit\":1,\"quantity\":9,\"hq\":false}]}";
        var parsed = UniversalisResponseParser.Parse(body, [1u]);
        Assert.Equal(0u, Assert.Single(Assert.Single(parsed).Listings).WorldId);
        var plan = ShoppingPlanner.Plan([new Requirement(1, 1)], parsed, new HashSet<uint> { 10 }, new ExclusionState());
        Assert.Empty(plan.Worlds);
        Assert.Equal(new ShortfallItem(1, 1), Assert.Single(plan.Shortfalls));
    }

    [Fact]
    public void NullArguments_Throw()
    {
        var ex = new ExclusionState();
        var allowed = new HashSet<uint>();
        Assert.Throws<ArgumentNullException>(() => ShoppingPlanner.Plan(null!, [], allowed, ex));
        Assert.Throws<ArgumentNullException>(() => ShoppingPlanner.Plan([], null!, allowed, ex));
        Assert.Throws<ArgumentNullException>(() => ShoppingPlanner.Plan([], [], null!, ex));
        Assert.Throws<ArgumentNullException>(() => ShoppingPlanner.Plan([], [], allowed, null!));
    }
}
