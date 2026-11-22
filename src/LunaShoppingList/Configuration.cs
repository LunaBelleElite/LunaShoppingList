using System.Runtime.Serialization;
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

    /// <summary>
    /// Shopping lists the player made. A settings file without the member, or with null, loads as an empty list;
    /// missing entries are dropped and blank or repeated ids replaced (see <see cref="CustomListOps.Normalize"/>).
    /// </summary>
    public List<CustomList> CustomLists
    {
        get => customLists;
        set
        {
            customLists = value ?? new();
            CustomListOps.Normalize(customLists);
        }
    }

    private List<CustomList> customLists = new();

    /// <summary>The saved custom list with this id, or null if there is none.</summary>
    public CustomList? FindCustomList(string id) => CustomLists.FirstOrDefault(l => l?.Id == id);

    // The settings reader may fill the existing list in place without calling the setter, so repair after loading too.
    [OnDeserialized]
    internal void OnDeserialized(StreamingContext context) => CustomListOps.Normalize(customLists);

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
