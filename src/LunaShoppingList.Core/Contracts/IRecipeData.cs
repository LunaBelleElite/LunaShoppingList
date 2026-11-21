namespace LunaShoppingList.Core;

/// <summary>Game recipe data (Recipe sheet).</summary>
public interface IRecipeData
{
    /// <summary>Recipe for a Recipe row id, or null if unknown.</summary>
    /// <param name="recipeId">Row id of the Recipe sheet.</param>
    RecipeInfo? GetRecipe(uint recipeId);
}
