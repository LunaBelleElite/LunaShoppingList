namespace LunaShoppingList.Core;

/// <summary>
/// Runs the pipeline from ingredient totals to an <see cref="OfferBook"/>:
/// resolves the travel scope, fetches Universalis listings for the items that will
/// actually be bought, and groups what each world offers per item.
/// </summary>
public sealed class ShoppingListService
{
    private readonly IWorldData _worlds;
    private readonly IUniversalisClient _universalis;

    private readonly Func<uint, bool>? _isMarketable;

    /// <summary>Creates the service over the game's world data and a Universalis client.</summary>
    /// <param name="worlds">The game's world data.</param>
    /// <param name="universalis">The Universalis client.</param>
    /// <param name="isMarketable">
    /// Whether an item can be sold on the market board. Items it rejects are never sent to Universalis and are reported in
    /// <see cref="OfferBook.NotOnMarket"/>. Null treats every item as marketable (no item is ever skipped). It should answer
    /// true whenever it does not know, so an item is never left unpriced on a guess.
    /// </param>
    public ShoppingListService(IWorldData worlds, IUniversalisClient universalis, Func<uint, bool>? isMarketable = null)
    {
        _worlds = worlds;
        _universalis = universalis;
        _isMarketable = isMarketable;
    }

    /// <summary>Whether <paramref name="itemId"/> is treated as sold on the market board: the check given to the constructor, or true without one.</summary>
    public bool IsMarketable(uint itemId) => _isMarketable?.Invoke(itemId) ?? true;

    /// <summary>
    /// Fetches prices for the scope (Universalis's own data-center mapping; no client call when nothing is to be bought) and
    /// returns what the user can choose from, per item and world.
    /// </summary>
    /// <param name="requirements">Ingredient totals (item id, quantity).</param>
    /// <param name="scope">Which worlds may be shopped at.</param>
    /// <param name="exclusions">Items the user chose not to buy; they appear in <see cref="OfferBook.Skipped"/>.</param>
    /// <param name="progress">Optional human-readable progress messages (may be called from any thread).</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="listingFilter">Optional listing-age filter passed to <see cref="OfferBookBuilder.Build"/>; <c>null</c> keeps every listing.</param>
    /// <exception cref="InvalidOperationException">The current world is unknown, or Universalis lists no world in the scope.</exception>
    public async Task<OfferBook> BuildOfferBookAsync(
        IReadOnlyList<Requirement> requirements,
        TravelScopeKind scope,
        ExclusionState exclusions,
        IProgress<string>? progress,
        CancellationToken ct,
        ListingFilter? listingFilter = null)
    {
        var (merged, allowedWorldIds) = await FetchListingsAsync(requirements, scope, exclusions, progress, ct).ConfigureAwait(false);

        // Needed, non-excluded items not sold on the market board: kept out of the offers (so they never count as unmet)
        // and reported on their own. Merged per item like the offer book, in first-appearance order. Excluded items stay in Skipped.
        var notOnMarket = new List<Requirement>();
        var offMarketIds = new HashSet<uint>();
        foreach (var r in requirements)
        {
            if (r.Quantity <= 0 || exclusions.IsExcluded(r.ItemId) || IsMarketable(r.ItemId))
                continue;
            var at = notOnMarket.FindIndex(n => n.ItemId == r.ItemId);
            if (at < 0)
            {
                notOnMarket.Add(r);
                offMarketIds.Add(r.ItemId);
            }
            else
            {
                notOnMarket[at] = r with { Quantity = (int)Math.Min((long)notOnMarket[at].Quantity + r.Quantity, int.MaxValue) };
            }
        }

        var forOffers = offMarketIds.Count == 0 ? requirements : requirements.Where(r => !offMarketIds.Contains(r.ItemId)).ToList();
        return OfferBookBuilder.Build(forOffers, merged, allowedWorldIds, exclusions, listingFilter) with { NotOnMarket = notOnMarket };
    }

    /// <summary>
    /// Resolves the scope, returns no listings (and makes no client call) when nothing is to be
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
            ?? throw new InvalidOperationException("Current world unknown. Log in to a character.");

        var resolved = TravelScopeResolver.Resolve(scope, current, _worlds.AllWorlds);
        var allowedWorldIds = resolved.Worlds.Select(w => w.Id).ToHashSet();

        // Items worth looking up: positive total quantity, not excluded, and sold on the market board.
        var lookUp = requirements
            .Where(r => r.Quantity > 0 && !exclusions.IsExcluded(r.ItemId) && IsMarketable(r.ItemId))
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
            throw new InvalidOperationException("Universalis doesn't list any of the worlds you chose.");
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
