namespace LunaShoppingList.Core;

/// <summary>A decision to buy some units of one item on one world.</summary>
/// <param name="ItemId">Item row id.</param>
/// <param name="WorldId">World row id.</param>
/// <param name="Quantity">Units to buy there; always above zero in a result.</param>
public record AllocationLine(uint ItemId, uint WorldId, int Quantity);
