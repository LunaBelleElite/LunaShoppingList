namespace LunaShoppingList.Core;

/// <summary>Outcome of <see cref="ExportFilter.CraftableOnly"/>.</summary>
/// <param name="Rows">The rows whose item is crafted, as given (order and repeats kept).</param>
/// <param name="SkippedItemIds">Items of the other rows, each once, in first-seen order.</param>
public sealed record CraftableRows(IReadOnlyList<Requirement> Rows, IReadOnlyList<uint> SkippedItemIds);

/// <summary>The one rule for which items an export may send: only items something can craft.</summary>
/// <remarks>Both the Artisan and the Teamcraft export use it, so the two cannot disagree about what is craftable.</remarks>
public static class ExportFilter
{
    /// <summary>
    /// Splits the rows into those whose item is crafted and the items that are not. Rows with item id 0 or quantity
    /// below 1 are ignored (neither kept nor reported). A row is crafted when <see cref="IRecipeLookup.FindRecipeForItem"/>
    /// returns a recipe whose id is not 0; otherwise its item goes into <see cref="CraftableRows.SkippedItemIds"/>
    /// (each item once, first-seen order). Kept rows stay as given, repeats included.
    /// </summary>
    public static CraftableRows CraftableOnly(IReadOnlyList<Requirement> rows, IRecipeLookup recipes)
    {
        var kept = new List<Requirement>();
        var skipped = new List<uint>();
        foreach (var row in rows ?? Array.Empty<Requirement>())
        {
            if (row is null || row.ItemId == 0 || row.Quantity < 1)
                continue;

            var recipe = recipes.FindRecipeForItem(row.ItemId);
            if (recipe is null || recipe.RecipeId == 0)
            {
                if (!skipped.Contains(row.ItemId))
                    skipped.Add(row.ItemId);
                continue;
            }
            kept.Add(row);
        }
        return new CraftableRows(kept, skipped);
    }
}
