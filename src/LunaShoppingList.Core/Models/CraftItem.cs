namespace LunaShoppingList.Core;

/// <summary>One entry of an Artisan crafting list.</summary>
/// <param name="RecipeId">Row id of the Recipe sheet (a recipe, not an item id).</param>
/// <param name="CraftCount">Number of crafts queued (not the number of items produced).</param>
/// <param name="Skipping">True if Artisan is set to skip this entry; it contributes no materials.</param>
/// <param name="NqOnly">True if the entry has Artisan's "Quick Synthesis this item" checkbox ticked. It controls how that one item is crafted (quick synthesis always gives normal-quality output) and does not restrict which ingredient qualities may be bought. Informational.</param>
public record CraftItem(uint RecipeId, int CraftCount, bool Skipping, bool NqOnly);
