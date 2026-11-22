namespace LunaShoppingList.Core;

/// <summary>Outcome of expanding an item: the new rows, or a message the player can read.</summary>
public sealed record ExpandResult(IReadOnlyList<CustomListItem>? Items, string? Error)
{
    /// <summary>True when the expansion worked and <see cref="Items"/> is set.</summary>
    public bool Ok => Items != null;
}

/// <summary>Replaces an item on a list by its recipe ingredients.</summary>
public static class RecipeExpander
{
    /// <summary>
    /// One level only. Returns the list's rows with the item's row replaced by its ingredients (merged into existing
    /// rows); <paramref name="list"/> is not changed.
    /// </summary>
    public static ExpandResult ExpandOnce(IRecipeLookup recipes, CustomList list, uint itemId)
    {
        var row = list.Items.FirstOrDefault(i => i.ItemId == itemId);
        if (row == null) return new ExpandResult(null, "Item is not on the list");
        var recipe = recipes.FindRecipeForItem(itemId);
        if (recipe == null) return new ExpandResult(null, "This item cannot be crafted");

        long yield = recipe.Yield < 1 ? 1 : recipe.Yield;
        long crafts = (Math.Max(row.Quantity, 1) + yield - 1) / yield;

        var rows = new List<CustomListItem>(list.Items.Count + recipe.Ingredients.Count);
        foreach (var i in list.Items)
            if (i.ItemId != itemId) rows.Add(new CustomListItem { ItemId = i.ItemId, Quantity = i.Quantity });

        var usable = 0;
        foreach (var ing in recipe.Ingredients)
        {
            if (ing.ItemId == 0 || ing.Amount < 1) continue;
            usable++;
            var add = CustomListOps.ClampQuantity(ing.Amount * crafts);
            var existing = rows.FirstOrDefault(r => r.ItemId == ing.ItemId);
            if (existing == null) rows.Add(new CustomListItem { ItemId = ing.ItemId, Quantity = add });
            else existing.Quantity = CustomListOps.ClampQuantity((long)existing.Quantity + add);
        }
        if (usable == 0) return new ExpandResult(null, "This recipe has no ingredients");
        return new ExpandResult(rows, null);
    }
}
