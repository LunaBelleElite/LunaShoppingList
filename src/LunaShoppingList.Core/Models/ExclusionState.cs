namespace LunaShoppingList.Core;

/// <summary>The set of items the user chose not to buy (remembered per Artisan list by the plugin).</summary>
public class ExclusionState
{
    /// <summary>Item row ids currently excluded from buying.</summary>
    public HashSet<uint> ExcludedItemIds { get; } = new();

    /// <summary>Excludes or re-includes one item.</summary>
    /// <param name="itemId">Item row id.</param>
    /// <param name="excluded">True to exclude, false to include.</param>
    public void SetExcluded(uint itemId, bool excluded)
    {
        if (excluded) ExcludedItemIds.Add(itemId);
        else ExcludedItemIds.Remove(itemId);
    }

    /// <summary>True if the item is currently excluded.</summary>
    /// <param name="itemId">Item row id.</param>
    public bool IsExcluded(uint itemId) => ExcludedItemIds.Contains(itemId);

    /// <summary>Excludes or re-includes many items at once (for example the crystals group button).</summary>
    /// <param name="itemIds">Item row ids.</param>
    /// <param name="excluded">True to exclude all, false to include all.</param>
    public void ExcludeAll(IEnumerable<uint> itemIds, bool excluded)
    {
        foreach (var id in itemIds) SetExcluded(id, excluded);
    }
}
