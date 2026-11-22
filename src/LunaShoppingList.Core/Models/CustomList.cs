namespace LunaShoppingList.Core;

/// <summary>A row of a player-made shopping list.</summary>
public sealed class CustomListItem
{
    /// <summary>The game item id.</summary>
    public uint ItemId { get; set; }

    /// <summary>How many of the item the player wants to buy.</summary>
    public int Quantity { get; set; }
}

/// <summary>
/// A shopping list the player made and named. Plain properties with a parameterless constructor so the plugin's
/// settings file can store it. A null from a hand-edited file reads as empty.
/// </summary>
public sealed class CustomList
{
    /// <summary>Stable id (a GUID string) that never changes when the list is renamed.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The name the player gave the list.</summary>
    public string Name
    {
        get => name;
        set => name = value ?? string.Empty;
    }

    private string name = string.Empty;

    /// <summary>The rows of the list, in the order the player added them.</summary>
    public List<CustomListItem> Items
    {
        get => items;
        set => items = value ?? new();
    }

    private List<CustomListItem> items = new();

    /// <summary>Items the player unticked in the list window; kept with the list.</summary>
    public HashSet<uint> ExcludedItemIds
    {
        get => excludedItemIds;
        set => excludedItemIds = value ?? new();
    }

    private HashSet<uint> excludedItemIds = new();
}
