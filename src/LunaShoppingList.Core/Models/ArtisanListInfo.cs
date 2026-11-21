namespace LunaShoppingList.Core;

/// <summary>Id and name of an Artisan list, for the list picker.</summary>
/// <param name="Id">Artisan list id.</param>
/// <param name="Name">Display name of the list.</param>
public record ArtisanListInfo(int Id, string Name);
