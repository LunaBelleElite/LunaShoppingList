namespace LunaShoppingList.Core;

/// <summary>A saved Artisan crafting list.</summary>
/// <param name="Id">Artisan list id.</param>
/// <param name="Name">Display name of the list.</param>
/// <param name="Items">Entries of the list, in Artisan order.</param>
public record CraftingList(int Id, string Name, IReadOnlyList<CraftItem> Items);
