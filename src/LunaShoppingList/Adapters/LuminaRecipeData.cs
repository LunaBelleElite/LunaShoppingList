using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using LunaShoppingList.Core;

namespace LunaShoppingList.Adapters;

/// <summary>Reads recipes from the game's Recipe sheet.</summary>
public sealed class LuminaRecipeData : IRecipeData, IRecipeLookup
{
    private readonly ExcelSheet<Recipe> sheet;
    private readonly Lazy<Dictionary<uint, uint>> recipeByResult;

    public LuminaRecipeData(IDataManager data)
    {
        sheet = data.GetExcelSheet<Recipe>();
        recipeByResult = new Lazy<Dictionary<uint, uint>>(BuildResultIndex, LazyThreadSafetyMode.PublicationOnly);
    }

    /// <summary>True once the item-to-recipe index exists, so <see cref="FindRecipeForItem"/> is a dictionary lookup.</summary>
    internal bool IsResultIndexReady => recipeByResult.IsValueCreated;

    private readonly object warmUpGate = new();
    private Task? warmUp;

    /// <summary>
    /// Builds the item-to-recipe index on the thread pool unless it exists or is already being built, so no game
    /// thread waits for it. A failed build is not remembered; a later call starts it again.
    /// </summary>
    internal void WarmUp()
    {
        lock (warmUpGate)
        {
            if (recipeByResult.IsValueCreated || warmUp is { IsCompleted: false })
                return;
            warmUp = Task.Run(() =>
            {
                try
                {
                    _ = recipeByResult.Value;
                }
                catch
                {
                    // The index stays unbuilt; the caller's note stays up and a later WarmUp tries again.
                }
            });
        }
    }

    /// <summary>
    /// Builds an item-to-recipe index on first use (one pass over the sheet), so call it on a click, never per frame.
    /// A build that failed is not remembered: the next call tries again.
    /// </summary>
    public RecipeInfo? FindRecipeForItem(uint itemId)
        => recipeByResult.Value.TryGetValue(itemId, out var recipeId) ? GetRecipe(recipeId) : null;

    private Dictionary<uint, uint> BuildResultIndex()
    {
        var map = new Dictionary<uint, uint>();
        foreach (var row in sheet)
        {
            var result = row.ItemResult.RowId;
            if (result != 0 && (!map.TryGetValue(result, out var existing) || row.RowId < existing))
                map[result] = row.RowId;
        }
        return map;
    }

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
