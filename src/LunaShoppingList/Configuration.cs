using Dalamud.Configuration;
using LunaShoppingList.Core;

namespace LunaShoppingList;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Excluded item ids per Artisan list id.</summary>
    public Dictionary<int, HashSet<uint>> ExcludedItemsByList { get; set; } = new();

    public TravelScopeKind LastScope { get; set; } = TravelScopeKind.CrossWorld;

    /// <summary>Last chosen Artisan list id, or null if none.</summary>
    public int? LastListId { get; set; }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);

    /// <summary>
    /// Drops saved exclusions for lists Artisan no longer has, and saves if anything was dropped.
    /// An empty set is ignored: it is what an unloaded Artisan or a failed IPC call returns.
    /// </summary>
    internal void PruneExcludedLists(IEnumerable<int> currentListIds)
    {
        var current = currentListIds.ToHashSet();
        if (current.Count == 0)
            return;
        var stale = ExcludedItemsByList.Keys.Where(id => !current.Contains(id)).ToList();
        if (stale.Count == 0)
            return;
        foreach (var id in stale)
            ExcludedItemsByList.Remove(id);
        Save();
    }
}
