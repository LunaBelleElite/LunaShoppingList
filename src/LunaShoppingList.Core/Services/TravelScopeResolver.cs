namespace LunaShoppingList.Core;

/// <summary>Works out which worlds a travel scope covers.</summary>
public static class TravelScopeResolver
{
    /// <summary>Resolves the worlds reachable for a scope.</summary>
    /// <remarks>
    /// CrossWorld: every public world in the same region as <paramref name="current"/> (a main-city
    /// aetheryte reaches the whole region). If the current region is null or blank, only worlds in the
    /// current world's own data center are returned (fail safe, never everything).
    /// CrossDataCenter: every public world. The current world is always included when it is public or
    /// absent from <paramref name="all"/>, and is never duplicated. Worlds are distinct by Id and ordered
    /// by data center, name (ordinal, ignoring case), then Id. Universalis scopes are the distinct data
    /// center names of the result, sorted the same way; no data center or region name is hard-coded.
    /// </remarks>
    /// <param name="kind">CrossWorld (same region) or CrossDataCenter (every public world).</param>
    /// <param name="current">The current world of the player.</param>
    /// <param name="all">Every known world.</param>
    /// <returns>The reachable worlds and the Universalis scopes covering them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="current"/> or <paramref name="all"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a defined value.</exception>
    public static TravelScopeResult Resolve(TravelScopeKind kind, WorldInfo current, IReadOnlyList<WorldInfo> all)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(all);

        Func<WorldInfo, bool> inScope;
        switch (kind)
        {
            case TravelScopeKind.CrossWorld:
                inScope = string.IsNullOrWhiteSpace(current.Region)
                    ? w => Same(w.DataCenter, current.DataCenter)
                    : w => Same(w.Region, current.Region);
                break;
            case TravelScopeKind.CrossDataCenter:
                inScope = _ => true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown travel scope.");
        }

        var known = all.Where(w => w is not null).ToList();
        var currentAbsent = known.All(w => w.Id != current.Id);

        var candidates = new List<WorldInfo>();
        if (current.IsPublic || currentAbsent)
        {
            candidates.Add(current);
        }

        candidates.AddRange(known.Where(w => w.IsPublic && inScope(w)));

        var worlds = candidates
            .GroupBy(w => w.Id)
            .Select(g => g.First())
            .OrderBy(w => w.DataCenter, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Id)
            .ToList();

        var scopes = worlds
            .Select(w => w.DataCenter)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TravelScopeResult(worlds, scopes);
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
