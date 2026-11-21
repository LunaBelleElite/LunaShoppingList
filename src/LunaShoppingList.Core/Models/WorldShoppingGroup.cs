namespace LunaShoppingList.Core;

/// <summary>All purchases to make on one world.</summary>
/// <param name="WorldId">World row id.</param>
/// <param name="WorldName">World name.</param>
/// <param name="Purchases">Purchases to make there.</param>
/// <param name="Total">Sum of Quantity * PricePerUnit over Purchases, in gil.</param>
public record WorldShoppingGroup(uint WorldId, string WorldName, IReadOnlyList<Purchase> Purchases, long Total);
