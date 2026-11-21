namespace LunaShoppingList.Core;

/// <summary>
/// Runs the whole pipeline from ingredient totals to a <see cref="ShoppingPlan"/>:
/// resolves the travel scope, fetches Universalis listings for the items that will
/// actually be bought, and plans the purchases grouped by world.
/// </summary>
public sealed class ShoppingListService
{
    private readonly IWorldData _worlds;
    private readonly IUniversalisClient _universalis;

    /// <summary>Creates the service over the game's world data and a Universalis client.</summary>
    public ShoppingListService(IWorldData worlds, IUniversalisClient universalis)
    {
        _worlds = worlds;
        _universalis = universalis;
    }

    /// <summary>
    /// Builds a shopping plan for <paramref name="requirements"/>.
    /// Excluded items are not looked up and appear in <see cref="ShoppingPlan.Skipped"/>.
    /// </summary>
    /// <param name="requirements">Ingredient totals (item id, quantity).</param>
    /// <param name="scope">Which worlds may be shopped at.</param>
    /// <param name="exclusions">Items the user chose not to buy.</param>
    /// <param name="progress">Optional human-readable progress messages (may be called from any thread).</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="InvalidOperationException">The current world is unknown (not logged in).</exception>
    public async Task<ShoppingPlan> BuildPlanAsync(
        IReadOnlyList<Requirement> requirements,
        TravelScopeKind scope,
        ExclusionState exclusions,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var (merged, allowedWorldIds) = await FetchListingsAsync(requirements, scope, exclusions, progress, ct).ConfigureAwait(false);
        return ShoppingPlanner.Plan(requirements, merged, allowedWorldIds, exclusions);
    }

    /// <summary>
    /// Fetches prices exactly as <see cref="BuildPlanAsync"/> does (same scope rules, same Universalis data-center mapping,
    /// same early return when nothing is to be bought) but returns what the user can choose from, per item and world,
    /// instead of one fixed plan.
    /// </summary>
    /// <param name="requirements">Ingredient totals (item id, quantity).</param>
    /// <param name="scope">Which worlds may be shopped at.</param>
    /// <param name="exclusions">Items the user chose not to buy; they appear in <see cref="OfferBook.Skipped"/>.</param>
    /// <param name="progress">Optional human-readable progress messages (may be called from any thread).</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="InvalidOperationException">The current world is unknown, or Universalis lists no world in the scope.</exception>
    public async Task<OfferBook> BuildOfferBookAsync(
        IReadOnlyList<Requirement> requirements,
        TravelScopeKind scope,
        ExclusionState exclusions,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var (merged, allowedWorldIds) = await FetchListingsAsync(requirements, scope, exclusions, progress, ct).ConfigureAwait(false);
        return OfferBookBuilder.Build(requirements, merged, allowedWorldIds, exclusions);
    }

    /// <summary>
    /// Shared by both public methods: resolves the scope, returns no listings (and makes no client call) when nothing is to be
    /// bought, otherwise fetches Universalis listings per data center and merges them per item.
    /// </summary>
    private async Task<(IReadOnlyList<ItemListings> Merged, HashSet<uint> AllowedWorldIds)> FetchListingsAsync(
        IReadOnlyList<Requirement> requirements,
        TravelScopeKind scope,
        ExclusionState exclusions,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(exclusions);

        var current = _worlds.CurrentWorld
            ?? throw new InvalidOperationException("Current world unknown; log in to a character.");

        var resolved = TravelScopeResolver.Resolve(scope, current, _worlds.AllWorlds);
        var allowedWorldIds = resolved.Worlds.Select(w => w.Id).ToHashSet();

        // Items worth looking up: positive total quantity and not excluded.
        var lookUp = requirements
            .Where(r => r.Quantity > 0 && !exclusions.IsExcluded(r.ItemId))
            .Select(r => r.ItemId)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        if (lookUp.Count == 0)
        {
            return (Array.Empty<ItemListings>(), allowedWorldIds);
        }

        var order = new List<uint>();
        var byItem = new Dictionary<uint, List<Listing>>();

        // Scopes come from Universalis's own data-center list, not from the game's grouping.
        var dataCenters = await _universalis.GetDataCentersAsync(ct).ConfigureAwait(false);
        var universalisWorldIds = dataCenters.SelectMany(d => d.WorldIds).ToHashSet();
        allowedWorldIds.IntersectWith(universalisWorldIds);
        if (allowedWorldIds.Count == 0)
        {
            throw new InvalidOperationException("Universalis does not list any world in the selected scope.");
        }
        var scopes = dataCenters
            .Where(d => d.WorldIds.Any(allowedWorldIds.Contains))
            .Select(d => d.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // A world belongs to the first scope (in sorted order) whose data center lists it; only that scope's
        // listings for it are kept, so a world listed in two scopes is never counted twice.
        var claimedWorlds = new HashSet<uint>();
        for (var i = 0; i < scopes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Fetching prices from {scopes[i]} ({i + 1}/{scopes.Count})");

            var scopeName = scopes[i];
            var scopeWorlds = dataCenters
                .Where(d => string.Equals(d.Name, scopeName, StringComparison.OrdinalIgnoreCase))
                .SelectMany(d => d.WorldIds)
                .Where(id => !claimedWorlds.Contains(id))
                .ToHashSet();
            claimedWorlds.UnionWith(scopeWorlds);

            var result = await _universalis.GetListingsAsync(lookUp, scopeName, ct).ConfigureAwait(false);
            foreach (var il in result)
            {
                if (!byItem.TryGetValue(il.ItemId, out var list))
                {
                    list = new List<Listing>();
                    byItem[il.ItemId] = list;
                    order.Add(il.ItemId);
                }
                list.AddRange(il.Listings.Where(l => scopeWorlds.Contains(l.WorldId)));
            }
        }

        var merged = order.Select(id => new ItemListings(id, byItem[id])).ToList();
        return (merged, allowedWorldIds);
    }
}
