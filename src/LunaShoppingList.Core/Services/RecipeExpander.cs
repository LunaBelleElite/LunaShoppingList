namespace LunaShoppingList.Core;

/// <summary>Outcome of expanding an item into materials: the rows, or a message the player can read.</summary>
public sealed record MaterialsResult(IReadOnlyList<Requirement>? Items, string? Error)
{
    /// <summary>True when the expansion worked and <see cref="Items"/> is set.</summary>
    public bool Ok => Error is null && Items is not null;
}

/// <summary>One recipe to run when crafting a set of items, with how many times.</summary>
/// <param name="ItemId">The item the recipe makes.</param>
/// <param name="RecipeId">The recipe (from <see cref="IRecipeLookup.FindRecipeForItem"/>).</param>
/// <param name="Crafts">How many times to run it, 1..9,999,999.</param>
public sealed record CraftEntry(uint ItemId, uint RecipeId, int Crafts);

/// <summary>Outcome of expanding items into the crafts that make them: the entries, or a message the player can read.</summary>
public sealed record CraftsResult(IReadOnlyList<CraftEntry>? Crafts, string? Error)
{
    /// <summary>True when the expansion worked and <see cref="Crafts"/> is set.</summary>
    public bool Ok => Error is null && Crafts is not null;
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
        public bool IsRoot;

        /// <summary>A craftable item whose recipe has nothing usable, reached only as an ingredient: gathered like a raw material.</summary>
        public bool IsGathered => Edges.Count == 0 && !IsRoot;
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
        var problem = RootProblem(recipes, itemId, out var rootRecipe);
        if (problem != null) return new MaterialsResult(null, problem);

        var memo = new Dictionary<uint, RecipeInfo?> { [itemId] = rootRecipe };
        var nodes = new Dictionary<uint, Node>();
        var postorder = new List<uint>();
        if (!Walk(recipes, memo, nodes, postorder, itemId, rootRecipe!))
            return new MaterialsResult(null, "This recipe is too large to expand");

        nodes[itemId].IsRoot = true;
        nodes[itemId].Demand = CustomListOps.ClampQuantity(quantity);
        var leaves = Propagate(nodes, postorder);

        var result = new List<Requirement>(leaves.Count);
        foreach (var (id, total) in leaves)
            result.Add(new Requirement(id, Clamp(total)));
        return new MaterialsResult(result, null);
    }

    /// <summary>
    /// The raw materials of all of <paramref name="roots"/> expanded together, so an intermediate shared by several
    /// roots, or also given as a root itself, is crafted once with the combined demand rounded up once. A root the
    /// single-item overload would reject (item id 0, no recipe, a recipe with no usable ingredient) or with a quantity
    /// below 1 is skipped; it can still be a material of another root. Quantities are clamped to 9,999,999 and a
    /// repeated root adds to the first. Materials, loops, totals, order and the <see cref="MaxCraftableItems"/> cap
    /// (counted over all roots together; error "This list is too large to expand") are as in
    /// <see cref="ExpandToMaterials(IRecipeLookup, uint, int)"/>. No usable root gives an empty list. Nothing is mutated.
    /// </summary>
    public static MaterialsResult ExpandToMaterials(IRecipeLookup recipes, IReadOnlyList<Requirement> roots)
    {
        var nodes = new Dictionary<uint, Node>();
        var postorder = new List<uint>();
        if (!WalkRoots(recipes, roots, true, nodes, postorder))
            return new MaterialsResult(null, TooLargeList);
        var leaves = Propagate(nodes, postorder);

        var result = new List<Requirement>(leaves.Count);
        foreach (var (id, total) in leaves)
            result.Add(new Requirement(id, Clamp(total)));
        return new MaterialsResult(result, null);
    }

    /// <summary>
    /// The crafts that make all of <paramref name="roots"/> together, through every level of sub-crafts. Roots with
    /// item id 0, a quantity below 1 or no recipe are ignored; quantities are clamped to 9,999,999 and a repeated root
    /// adds to the first. Every item is expanded once over the whole set, so an intermediate shared by several roots,
    /// or also given as a root itself, is crafted once with the combined demand rounded up once:
    /// crafts = ceil(demand / yield), yield below 1 counting as 1, clamped to 9,999,999. Recipe loops, the
    /// <see cref="MaxCraftableItems"/> cap (counted over all roots together) and an intermediate whose recipe has no
    /// usable ingredient are handled as in <see cref="ExpandToMaterials(IRecipeLookup, uint, int)"/>: such an intermediate is a material, not an
    /// entry, while a root like that is still an entry. Raw materials are not entries. Order is crafting order: each
    /// entry comes after every entry it uses, intermediates in the order they are first reached, roots in the given
    /// order. No roots gives an empty list. Nothing is mutated.
    /// </summary>
    public static CraftsResult ExpandToCrafts(IRecipeLookup recipes, IReadOnlyList<Requirement> roots)
    {
        var nodes = new Dictionary<uint, Node>();
        var postorder = new List<uint>();
        if (!WalkRoots(recipes, roots, false, nodes, postorder))
            return new CraftsResult(null, TooLargeList);
        Propagate(nodes, postorder);

        var result = new List<CraftEntry>(postorder.Count);
        foreach (var id in postorder)
        {
            var node = nodes[id];
            if (node.IsGathered) continue;
            result.Add(new CraftEntry(id, node.Recipe.RecipeId, Clamp(Crafts(node))));
        }
        return new CraftsResult(result, null);
    }

    private const string TooLargeList = "This list is too large to expand";

    /// <summary>
    /// Why <paramref name="itemId"/> cannot be expanded on its own (the single-item overload's errors, in its order),
    /// or null; <paramref name="recipe"/> is its recipe when it has one.
    /// </summary>
    internal static string? RootProblem(IRecipeLookup recipes, uint itemId, out RecipeInfo? recipe)
    {
        recipe = null;
        if (itemId == 0) return "Item is not valid";
        recipe = recipes.FindRecipeForItem(itemId);
        if (recipe is null) return "This item cannot be crafted";
        if (!recipe.Ingredients.Any(i => i.ItemId != 0 && i.Amount >= 1)) return "This recipe has no ingredients";
        return null;
    }

    /// <summary>
    /// Walks every usable root in turn into one graph (roots with item id 0, a quantity below 1 or no recipe are
    /// skipped; so is a root whose recipe has no usable ingredient when <paramref name="skipRootsWithoutIngredients"/>),
    /// marks the roots and sets their summed, clamped demand. False when the graph would pass <see cref="MaxCraftableItems"/>.
    /// </summary>
    private static bool WalkRoots(IRecipeLookup recipes, IReadOnlyList<Requirement> roots, bool skipRootsWithoutIngredients,
        Dictionary<uint, Node> nodes, List<uint> postorder)
    {
        var memo = new Dictionary<uint, RecipeInfo?>();
        var rootDemand = new Dictionary<uint, Int128>();

        foreach (var root in roots)
        {
            if (root is null || root.ItemId == 0 || root.Quantity < 1) continue;
            if (!memo.TryGetValue(root.ItemId, out var recipe))
            {
                recipe = recipes.FindRecipeForItem(root.ItemId);
                memo[root.ItemId] = recipe;
            }
            if (recipe is null) continue;
            if (skipRootsWithoutIngredients && !recipe.Ingredients.Any(i => i.ItemId != 0 && i.Amount >= 1)) continue;

            rootDemand.TryGetValue(root.ItemId, out var have);
            rootDemand[root.ItemId] = Saturate(have + CustomListOps.ClampQuantity(root.Quantity));
            if (!nodes.ContainsKey(root.ItemId) && !Walk(recipes, memo, nodes, postorder, root.ItemId, recipe))
                return false;
            nodes[root.ItemId].IsRoot = true;
        }

        foreach (var (id, demand) in rootDemand)
            nodes[id].Demand = demand;
        return true;
    }

    /// <summary>
    /// Depth-first walk from <paramref name="rootId"/>, adding every craftable item not yet in <paramref name="nodes"/>
    /// and appending each to <paramref name="postorder"/> once everything it uses is there. An ingredient still on the
    /// current path (a loop) is an edge to a raw material. False when more than <see cref="MaxCraftableItems"/> nodes would exist.
    /// </summary>
    private static bool Walk(IRecipeLookup recipes, Dictionary<uint, RecipeInfo?> memo, Dictionary<uint, Node> nodes,
        List<uint> postorder, uint rootId, RecipeInfo rootRecipe)
    {
        var onPath = new HashSet<uint>();
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

        if (!Enter(rootId, rootRecipe)) return false;
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
                return false;
        }
        return true;
    }

    /// <summary>
    /// Pushes each node's demand down to what it uses, parents before children (reverse postorder), so every node's
    /// demand is complete before it is rounded into crafts. Returns the raw-material totals by item id ascending.
    /// </summary>
    private static SortedDictionary<uint, Int128> Propagate(Dictionary<uint, Node> nodes, List<uint> postorder)
    {
        var leaves = new SortedDictionary<uint, Int128>();
        for (var k = postorder.Count - 1; k >= 0; k--)
        {
            var node = nodes[postorder[k]];
            if (node.IsGathered)
            {
                leaves.TryGetValue(postorder[k], out var own);
                leaves[postorder[k]] = Saturate(own + node.Demand);
                continue;
            }
            var crafts = Crafts(node);
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
        return leaves;
    }

    private static Int128 Crafts(Node node)
    {
        Int128 yield = node.Recipe.Yield < 1 ? 1 : node.Recipe.Yield;
        return (node.Demand + yield - 1) / yield;
    }

    private static int Clamp(Int128 value) => value > CustomListOps.MaxQuantity ? CustomListOps.MaxQuantity : (int)value;

    private static Int128 Saturate(Int128 value) => value > DemandCap ? DemandCap : value;
}
