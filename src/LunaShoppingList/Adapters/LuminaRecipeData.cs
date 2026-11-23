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

    private readonly IPluginLog? log;

    public LuminaRecipeData(IDataManager data, IPluginLog? log = null)
    {
        this.log = log;
        sheet = data.GetExcelSheet<Recipe>();
        recipeByResult = new Lazy<Dictionary<uint, uint>>(BuildResultIndex, LazyThreadSafetyMode.PublicationOnly);
    }

    /// <summary>
    /// True once the item-to-recipe index exists, so <see cref="FindRecipeForItem"/> is a dictionary lookup. While it does not,
    /// reading this starts the background build or its next attempt (paced and capped, see <see cref="WarmUp"/>), so a window
    /// that asks every frame is what drives the retries; it never waits for the build.
    /// </summary>
    internal bool IsResultIndexReady
    {
        get
        {
            if (recipeByResult.IsValueCreated)
                return true;
            WarmUp();
            return false;
        }
    }

    private readonly object warmUpGate = new();
    private Task? warmUp;
    private volatile bool resultIndexFailed;

    internal const int MaxAttempts = 3;
    private const long RetryDelayMs = 10_000;
    private int attempts;
    private long lastStartedAt;

    /// <summary>True once the build failed <see cref="MaxAttempts"/> times in a row; stays set until <see cref="RetryNow"/>.</summary>
    internal bool IsResultIndexFailed => resultIndexFailed;

    /// <summary>
    /// Builds the item-to-recipe index on the thread pool unless it exists, is already being built, has failed for good, or the
    /// last attempt started less than ten seconds ago, so no game thread waits for it. At most <see cref="MaxAttempts"/> attempts.
    /// </summary>
    internal void WarmUp()
    {
        lock (warmUpGate)
        {
            if (recipeByResult.IsValueCreated || resultIndexFailed || warmUp is { IsCompleted: false })
                return;
            if (attempts > 0 && Environment.TickCount64 - lastStartedAt < RetryDelayMs)
                return;
            attempts++;
            lastStartedAt = Environment.TickCount64;
            var attempt = attempts;
            warmUp = Task.Run(() =>
            {
                try
                {
                    _ = recipeByResult.Value;
                }
                catch (Exception ex)
                {
                    if (attempt >= MaxAttempts)
                    {
                        resultIndexFailed = true;
                        log?.Error(ex, "Could not build the recipe index after {Attempts} attempts", attempt);
                    }
                    else
                    {
                        log?.Warning(ex, "Could not build the recipe index (attempt {Attempt} of {Max}); it will be tried again", attempt, MaxAttempts);
                    }
                }
            });
        }
    }

    /// <summary>An explicit user request (Reload list, the crafted button): forgets a final failure and starts the attempts over, then warms up.</summary>
    internal void RetryNow()
    {
        lock (warmUpGate)
        {
            if (resultIndexFailed)
            {
                resultIndexFailed = false;
                attempts = 0;
            }
        }
        WarmUp();
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
