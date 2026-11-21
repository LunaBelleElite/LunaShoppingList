namespace LunaShoppingList.Core;

/// <summary>A data center as Universalis defines it (not the game's physical grouping).</summary>
/// <param name="Name">Universalis data-center name.</param>
/// <param name="Region">Universalis region name.</param>
/// <param name="WorldIds">Ids of the worlds Universalis places in this data center.</param>
public sealed record UniversalisDataCenter(string Name, string Region, IReadOnlyList<uint> WorldIds);
