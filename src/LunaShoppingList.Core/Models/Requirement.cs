namespace LunaShoppingList.Core;

/// <summary>A total quantity of one item that must be bought.</summary>
/// <param name="ItemId">Item row id.</param>
/// <param name="Quantity">Total units needed (summed over the whole list).</param>
public record Requirement(uint ItemId, int Quantity);
