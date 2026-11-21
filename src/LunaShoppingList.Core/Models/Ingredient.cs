namespace LunaShoppingList.Core;

/// <summary>One ingredient of a recipe.</summary>
/// <param name="ItemId">Item row id of the ingredient.</param>
/// <param name="Amount">Units of the item consumed by ONE craft.</param>
public record Ingredient(uint ItemId, int Amount);
