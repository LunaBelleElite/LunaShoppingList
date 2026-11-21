using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using LunaShoppingList.Core;

namespace LunaShoppingList.Adapters;

/// <summary>Reads recipes from the game's Recipe sheet.</summary>
public sealed class LuminaRecipeData : IRecipeData
{
    private readonly ExcelSheet<Recipe> sheet;

    public LuminaRecipeData(IDataManager data) => sheet = data.GetExcelSheet<Recipe>();

    public RecipeInfo? GetRecipe(uint recipeId)
    {
        if (!sheet.TryGetRow(recipeId, out var row))
            return null;

        var ingredients = new List<Ingredient>();
        for (var i = 0; i < row.Ingredient.Count && i < row.AmountIngredient.Count; i++)
        {
            var itemId = row.Ingredient[i].RowId;
            int amount = row.AmountIngredient[i];
            if (itemId != 0 && amount > 0)
                ingredients.Add(new Ingredient(itemId, amount));
        }

        return new RecipeInfo(recipeId, row.ItemResult.RowId, row.AmountResult, ingredients);
    }
}
