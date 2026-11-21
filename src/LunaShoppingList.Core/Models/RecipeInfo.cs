namespace LunaShoppingList.Core;

/// <summary>Recipe data needed to compute materials.</summary>
/// <param name="RecipeId">Row id of the Recipe sheet.</param>
/// <param name="ResultItemId">Item row id the recipe produces.</param>
/// <param name="Yield">Items produced by one craft.</param>
/// <param name="Ingredients">Ingredients consumed by one craft (sub-crafts appear as plain ingredients, not expanded).</param>
public record RecipeInfo(uint RecipeId, uint ResultItemId, int Yield, IReadOnlyList<Ingredient> Ingredients);
