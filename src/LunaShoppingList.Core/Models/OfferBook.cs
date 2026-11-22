namespace LunaShoppingList.Core;

/// <summary>Everything the user can choose from: per item the quantity needed and the offers per world, plus excluded items.</summary>
/// <param name="Items">One entry per non-excluded item that is needed, ordered by item id. An item with no offers anywhere has an empty <see cref="ItemOffers.Worlds"/>.</param>
/// <param name="Skipped">Excluded items with the quantity that would have been needed, ordered by item id.</param>
public record OfferBook(IReadOnlyList<ItemOffers> Items, IReadOnlyList<SkippedItem> Skipped)
{
    /// <summary>
    /// Needed, non-excluded items that are not sold on the market board, with their merged quantity, in the order they first
    /// appear in the requirements. They were not looked up and are in neither <see cref="Items"/> nor <see cref="Skipped"/>, so
    /// they never count as unmet and add nothing to any total. Filled by <see cref="ShoppingListService.BuildOfferBookAsync"/>;
    /// empty (never null) by default, including from <see cref="OfferBookBuilder.Build"/>.
    /// </summary>
    public IReadOnlyList<Requirement> NotOnMarket { get; init; } = [];
}
