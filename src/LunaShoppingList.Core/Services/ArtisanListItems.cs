namespace LunaShoppingList.Core;

/// <summary>Reads what an Artisan list makes, for exporting it or shopping for it.</summary>
public static class ArtisanListItems
{
    /// <summary>
    /// The items an Artisan list crafts: for each entry not set to Skip, the recipe's result item times
    /// CraftCount times yield (yield below 1 counting as 1), merged by item in first-seen order and capped at
    /// <see cref="CustomListOps.MaxQuantity"/>. Sub-crafts are left out (<see cref="SubCraftRecipeIds"/>). Entries with an unknown recipe, result item 0 or fewer than 1 craft are left out.
    /// </summary>
    public static IReadOnlyList<Requirement> CraftedItems(CraftingList list, IRecipeData recipes)
    {
        var counted = list.Items.Where(i => i is not null && !i.Skipping && i.CraftCount >= 1
            && recipes.GetRecipe(i.RecipeId) is { ResultItemId: not 0 }).Select(i => i.RecipeId).ToList();
        var subCrafts = SubCraftRecipeIds(counted, recipes);

        var order = new List<uint>();
        var totals = new Dictionary<uint, long>();
        foreach (var item in list.Items)
        {
            if (item is null || item.Skipping || item.CraftCount < 1 || subCrafts.Contains(item.RecipeId))
                continue;
            var recipe = recipes.GetRecipe(item.RecipeId);
            if (recipe is null || recipe.ResultItemId == 0)
                continue;

            var made = Math.Min(CustomListOps.MaxQuantity, (long)item.CraftCount * Math.Max(1, recipe.Yield));
            if (totals.TryGetValue(recipe.ResultItemId, out var sum))
                totals[recipe.ResultItemId] = Math.Min(CustomListOps.MaxQuantity, sum + made);
            else
            {
                order.Add(recipe.ResultItemId);
                totals[recipe.ResultItemId] = made;
            }
        }

        var result = new List<Requirement>(order.Count);
        foreach (var id in order)
            result.Add(new Requirement(id, (int)totals[id]));
        return result;
    }

    /// <summary>
    /// Which of the given recipes are sub-crafts of the others, like Artisan's own Teamcraft export: a recipe whose result
    /// item is a direct ingredient of a different recipe in the set. Pass only the entries that count (not set to Skip, with
    /// a known recipe). A recipe that needs its own result is not dropped by itself; if dropping would remove every
    /// recipe (a cycle), nothing is dropped.
    /// </summary>
    public static HashSet<uint> SubCraftRecipeIds(IReadOnlyCollection<uint> recipeIds, IRecipeData recipes)
    {
        var infos = new List<RecipeInfo>();
        foreach (var id in recipeIds.Distinct())
            if (recipes.GetRecipe(id) is { } info)
                infos.Add(info);

        var dropped = new HashSet<uint>();
        foreach (var candidate in infos)
        {
            foreach (var other in infos)
            {
                if (other.RecipeId != candidate.RecipeId && other.Ingredients.Any(i => i.ItemId == candidate.ResultItemId))
                {
                    dropped.Add(candidate.RecipeId);
                    break;
                }
            }
        }
        return dropped.Count >= infos.Count ? new HashSet<uint>() : dropped;
    }
}
