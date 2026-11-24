namespace LunaShoppingList.Core;

/// <summary>
/// Reads and creates craft lists in another plugin (Allagan Tools). Every member is safe to call when that plugin is absent:
/// nothing throws, and the answer says it is not available. Implementations call the other plugin over IPC, so call on the
/// framework thread (the draw thread is one).
/// </summary>
public interface ICraftListSource
{
    /// <summary>Whether the other plugin is installed, loaded and finished starting, so its lists can be read now.</summary>
    bool IsAvailable { get; }

    /// <summary>The player's craft lists, ordered by name; empty when the plugin is not available.</summary>
    IReadOnlyList<CraftListInfo> GetLists();

    /// <summary>The finished items of one list (not its ingredients), ordered by item id, quantities above zero only.</summary>
    /// <param name="key">A key from <see cref="GetLists"/>.</param>
    CraftListGoals GetGoals(string key);

    /// <summary>Creates a new craft list holding <paramref name="goals"/> and verifies the other plugin really took them.</summary>
    /// <param name="name">The list name, already made safe with <see cref="CraftListOps.ExportName"/>.</param>
    /// <param name="goals">The finished items to craft, as built by <see cref="CraftListOps.ForExport"/>.</param>
    CraftListResult CreateList(string name, IReadOnlyList<Requirement> goals);
}
