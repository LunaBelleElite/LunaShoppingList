namespace LunaShoppingList.Core;

/// <summary>The resolved set of worlds for a travel scope.</summary>
/// <param name="Worlds">Worlds the user may shop on (includes the current world).</param>
/// <param name="UniversalisScopes">Universalis path segments to query: the distinct data-centre names (WorldInfo.DataCenter) of Worlds, sorted ordinal ignoring case. Universalis accepts data-centre names directly, so no region mapping is needed.</param>
public record TravelScopeResult(IReadOnlyList<WorldInfo> Worlds, IReadOnlyList<string> UniversalisScopes);
