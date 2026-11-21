namespace LunaShoppingList.Core;

/// <summary>Game item data (Item sheet).</summary>
public interface IItemInfo
{
    /// <summary>Display name of an item; a placeholder such as "Item #id" if unknown.</summary>
    /// <param name="itemId">Item row id.</param>
    string GetName(uint itemId);

    /// <summary>True for crystals, shards and clusters.</summary>
    /// <param name="itemId">Item row id.</param>
    bool IsCrystalLike(uint itemId);

    /// <summary>Item row id for an exact (case-insensitive) name, or null if none matches.</summary>
    /// <param name="name">Item name.</param>
    uint? FindByName(string name);
}
