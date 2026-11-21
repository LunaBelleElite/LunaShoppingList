namespace LunaShoppingList.Core;

/// <summary>An item the user excluded from buying.</summary>
/// <param name="ItemId">Item row id.</param>
/// <param name="Quantity">Units that would have been needed.</param>
public record SkippedItem(uint ItemId, int Quantity);
