namespace LunaShoppingList.Core;

/// <summary>An item for which the scope has too few listings.</summary>
/// <param name="ItemId">Item row id.</param>
/// <param name="Missing">Units that could not be covered by listings in scope.</param>
public record ShortfallItem(uint ItemId, int Missing);
