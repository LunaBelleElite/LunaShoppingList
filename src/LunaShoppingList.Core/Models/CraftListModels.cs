namespace LunaShoppingList.Core;

/// <summary>Key and display name of one craft list in another plugin (Allagan Tools), for a picker.</summary>
/// <param name="Key">The other plugin's own identifier for the list; pass it back to read or verify the list.</param>
/// <param name="Name">Display name.</param>
public sealed record CraftListInfo(string Key, string Name);

/// <summary>How a craft-list operation ended.</summary>
public enum CraftListStatus
{
    /// <summary>It worked.</summary>
    Ok,

    /// <summary>The other plugin is not installed, not loaded or not finished starting. Nothing was attempted.</summary>
    NotAvailable,

    /// <summary>The other plugin was reachable but the operation did not give the expected result.</summary>
    Failed,
}

/// <summary>Outcome of creating a craft list in the other plugin: a status, a plain-language message and, on success, the new list's key.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Message">A sentence the player can read.</param>
/// <param name="Key">The new list's key; null unless <paramref name="Status"/> is <see cref="CraftListStatus.Ok"/>.</param>
public sealed record CraftListResult(CraftListStatus Status, string Message, string? Key = null)
{
    /// <summary>True when the list was created and verified.</summary>
    public bool Ok => Status == CraftListStatus.Ok;
}

/// <summary>Outcome of reading one craft list: a status, a message and the finished-item goals (empty unless it worked).</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Message">A sentence the player can read; empty when it worked.</param>
/// <param name="Goals">The list's finished items and how many of each, ordered by item id, quantities above zero only.</param>
public sealed record CraftListGoals(CraftListStatus Status, string Message, IReadOnlyList<Requirement> Goals)
{
    /// <summary>True when the list was read.</summary>
    public bool Ok => Status == CraftListStatus.Ok;
}
