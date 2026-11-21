namespace LunaShoppingList.Core;

/// <summary>Everything the user can choose from: per item the quantity needed and the offers per world, plus excluded items.</summary>
/// <param name="Items">One entry per non-excluded item that is needed, ordered by item id. An item with no offers anywhere has an empty <see cref="ItemOffers.Worlds"/>.</param>
/// <param name="Skipped">Excluded items with the quantity that would have been needed, ordered by item id.</param>
public record OfferBook(IReadOnlyList<ItemOffers> Items, IReadOnlyList<SkippedItem> Skipped);
