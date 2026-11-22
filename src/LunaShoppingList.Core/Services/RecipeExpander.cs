namespace LunaShoppingList.Core;

/// <summary>Outcome of expanding an item into materials: the rows, or a message the player can read.</summary>
public sealed record MaterialsResult(IReadOnlyList<Requirement>? Items, string? Error)
{
    /// <summary>True when the expansion worked and <see cref="Items"/> is set.</summary>
    public bool Ok => Error is null && Items is not null;
}

/// <summary>Expands a craftable item through every level of sub-crafts into the materials that must be gathered or bought.</summary>
public static class RecipeExpander
{
    /// <summary>Most distinct craftable items one expansion will look at before giving up.</summary>
    public const int MaxCraftableItems = 5_000;

    private static readonly Int128 DemandCap = 1_000_000_000_000_000_000;

    private sealed class Node
    {
        public RecipeInfo Recipe = null!;
        public readonly List<(uint ItemId, int Amount, bool IsNode)> Edges = new();
        public Int128 Demand;
    }

    /// <summary>
    /// Expands <paramref name="itemId"/> (quantity clamped to 1..9,999,999) through all levels of sub-crafts. Every
    /// craftable item is expanded, even one that is a plain ingredient elsewhere. A craftable item needed in several
    /// places is crafted once, with the combined demand rounded up once. An item reached again while it is still being
    /// expanded (a recipe loop) is treated as a raw material. More than <see cref="MaxCraftableItems"/> distinct
    /// craftable items is an error. The result holds only items with no recipe (plus the repeated item of a loop, and a craftable item whose recipe has no usable ingredient),
    /// with totals clamped to 9,999,999, ordered by item id ascending; the item itself and the crafted intermediates
    /// are not in it. Nothing is mutated.
    /// </summary>
    public static MaterialsResult ExpandToMaterials(IRecipeLookup recipes, uint itemId, int quantity)
    {
        if (itemId == 0) return new MaterialsResult(null, "Item is not valid");
        var rootRecipe = recipes.FindRecipeForItem(itemId);
        if (rootRecipe is null) return new MaterialsResult(null, "This item cannot be crafted");
        if (!rootRecipe.Ingredients.Any(i => i.ItemId != 0 && i.Amount >= 1))
            return new MaterialsResult(null, "This recipe has no ingredients");

        var memo = new Dictionary<uint, RecipeInfo?> { [itemId] = rootRecipe };
        var nodes = new Dictionary<uint, Node>();
        var onPath = new HashSet<uint>();
        var postorder = new List<uint>();
        var stack = new Stack<(uint Id, int Next)>();

        bool Enter(uint id, RecipeInfo recipe)
        {
            if (nodes.Count >= MaxCraftableItems) return false;
            var node = new Node { Recipe = recipe };
            nodes[id] = node;
            onPath.Add(id);
            stack.Push((id, 0));
            return true;
        }

        if (!Enter(itemId, rootRecipe)) return new MaterialsResult(null, "This recipe is too large to expand");
        while (stack.Count > 0)
        {
            var (id, next) = stack.Pop();
            var node = nodes[id];
            var ingredients = node.Recipe.Ingredients;
            if (next >= ingredients.Count)
            {
                onPath.Remove(id);
                postorder.Add(id);
                continue;
            }
            stack.Push((id, next + 1));
            var ing = ingredients[next];
            if (ing.ItemId == 0 || ing.Amount < 1) continue;

            if (onPath.Contains(ing.ItemId))
            {
                node.Edges.Add((ing.ItemId, ing.Amount, false));
                continue;
            }
            if (!memo.TryGetValue(ing.ItemId, out var child))
            {
                child = recipes.FindRecipeForItem(ing.ItemId);
                memo[ing.ItemId] = child;
            }
            if (child is null)
            {
                node.Edges.Add((ing.ItemId, ing.Amount, false));
                continue;
            }
            node.Edges.Add((ing.ItemId, ing.Amount, true));
            if (!nodes.ContainsKey(ing.ItemId) && !Enter(ing.ItemId, child))
                return new MaterialsResult(null, "This recipe is too large to expand");
        }

        var rootDemand = (Int128)CustomListOps.ClampQuantity(quantity);
        nodes[itemId].Demand = rootDemand;
        var leaves = new SortedDictionary<uint, Int128>();
        for (var k = postorder.Count - 1; k >= 0; k--)
        {
            var node = nodes[postorder[k]];
            if (node.Edges.Count == 0 && postorder[k] != itemId)
            {
                // A craftable item whose recipe has nothing usable is gathered like a raw material.
                leaves.TryGetValue(postorder[k], out var own);
                leaves[postorder[k]] = Saturate(own + node.Demand);
                continue;
            }
            Int128 yield = node.Recipe.Yield < 1 ? 1 : node.Recipe.Yield;
            var crafts = (node.Demand + yield - 1) / yield;
            foreach (var (ingId, amount, isNode) in node.Edges)
            {
                var add = (Int128)amount * crafts;
                if (isNode)
                {
                    var target = nodes[ingId];
                    target.Demand = Saturate(target.Demand + add);
                }
                else
                {
                    leaves.TryGetValue(ingId, out var current);
                    leaves[ingId] = Saturate(current + add);
                }
            }
        }

        var result = new List<Requirement>(leaves.Count);
        foreach (var (id, total) in leaves)
            result.Add(new Requirement(id, total > CustomListOps.MaxQuantity ? CustomListOps.MaxQuantity : (int)total));
        return new MaterialsResult(result, null);
    }

    private static Int128 Saturate(Int128 value) => value > DemandCap ? DemandCap : value;
}
