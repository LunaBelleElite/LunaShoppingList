namespace LunaShoppingList.Core;

/// <summary>Finds the recipe that makes an item, so a craftable item can be replaced by its ingredients.</summary>
public interface IRecipeLookup
{
    /// <summary>
    /// The recipe whose result is <paramref name="itemId"/>, or null when the item is not crafted.
    /// When several recipes make the same item, the one with the lowest recipe id.
    /// </summary>
    RecipeInfo? FindRecipeForItem(uint itemId);
}
