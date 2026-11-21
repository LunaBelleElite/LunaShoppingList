namespace LunaShoppingList.Core;

/// <summary>Turns a crafting list into total material requirements.</summary>
public class MaterialsCalculator
{
    private readonly IRecipeData _recipes;

    /// <summary>Creates the calculator.</summary>
    /// <param name="recipes">Recipe lookup.</param>
    public MaterialsCalculator(IRecipeData recipes) => _recipes = recipes ?? throw new ArgumentNullException(nameof(recipes));

    /// <summary>
    /// Ingredient amount x CraftCount, summed per item; Skipping entries are excluded.
    /// The quantity is the craft count, not the item count. Sub-crafts are not expanded.
    /// </summary>
    /// <param name="list">The Artisan list.</param>
    /// <returns>Total units needed per ingredient item.</returns>
    /// <exception cref="InvalidOperationException">A non-skipped entry references an unknown recipe.</exception>
    /// <exception cref="OverflowException">A quantity exceeds the int range.</exception>
    public IReadOnlyList<Requirement> Compute(CraftingList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        var totals = new SortedDictionary<uint, int>();
        foreach (var item in list.Items)
        {
            if (item.Skipping || item.CraftCount <= 0) continue;
            var recipe = _recipes.GetRecipe(item.RecipeId)
                ?? throw new InvalidOperationException($"Unknown recipe ID {item.RecipeId} in crafting list.");
            foreach (var ing in recipe.Ingredients)
            {
                int qty = checked(ing.Amount * item.CraftCount);
                totals[ing.ItemId] = checked(totals.GetValueOrDefault(ing.ItemId) + qty);
            }
        }
        return totals.Where(kv => kv.Value != 0).Select(kv => new Requirement(kv.Key, kv.Value)).ToList();
    }
}
