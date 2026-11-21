namespace LunaShoppingList.Core;

/// <summary>Reads Artisan crafting lists.</summary>
public interface IArtisanListSource
{
    /// <summary>Lists available for the picker; empty if Artisan is absent.</summary>
    IReadOnlyList<ArtisanListInfo> GetLists();

    /// <summary>Loads the contents of one list, or null if it is not found.</summary>
    /// <param name="id">Artisan list id.</param>
    CraftingList? LoadList(int id);
}
