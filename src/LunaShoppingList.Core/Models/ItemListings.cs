namespace LunaShoppingList.Core;

/// <summary>All fetched listings for one item.</summary>
/// <param name="ItemId">Item row id.</param>
/// <param name="Listings">Listings across the queried scope, in no guaranteed order.</param>
public record ItemListings(uint ItemId, IReadOnlyList<Listing> Listings);
