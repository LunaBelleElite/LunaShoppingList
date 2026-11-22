namespace LunaShoppingList.Core;

/// <summary>The items a player can look up by name when building their own list: only items that can be sold on the market board.</summary>
public interface IItemCatalog
{
    /// <summary>True once <see cref="MarketableItems"/> is filled. The list is empty before that.</summary>
    bool IsReady { get; }

    /// <summary>Every item that can be listed on the market board, one entry per distinct name. Empty until <see cref="IsReady"/>.</summary>
    IReadOnlyList<ItemSearchEntry> MarketableItems { get; }
}
