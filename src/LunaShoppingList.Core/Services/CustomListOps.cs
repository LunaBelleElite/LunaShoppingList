namespace LunaShoppingList.Core;

/// <summary>Outcome of creating or renaming a list: the list, or a message the player can read.</summary>
public sealed record CustomListResult(CustomList? List, string? Error)
{
    /// <summary>True when the operation succeeded and <see cref="List"/> is set.</summary>
    public bool Ok => List != null;
}

/// <summary>A goal whose materials could not be worked out, and why, in words the player can read.</summary>
/// <param name="ItemId">The goal's item id.</param>
/// <param name="Error">Why it contributes no materials.</param>
public sealed record GoalProblem(uint ItemId, string Error);

/// <summary>What a list needs bought: its plain rows merged with the materials of its goals.</summary>
/// <param name="Requirements">The rows to show and price.</param>
/// <param name="Problems">Goals that contributed nothing, in goal order.</param>
public sealed record EffectiveRequirementsResult(IReadOnlyList<Requirement> Requirements, IReadOnlyList<GoalProblem> Problems);

/// <summary>Pure operations on the player's own shopping lists.</summary>
public static class CustomListOps
{
    /// <summary>Longest list name accepted.</summary>
    public const int MaxNameLength = 60;

    /// <summary>Largest quantity of one item on a list.</summary>
    public const int MaxQuantity = 9_999_999;

    /// <summary>Validates <paramref name="name"/> and returns a new empty list; <paramref name="existing"/> is not changed.</summary>
    public static CustomListResult Create(IReadOnlyList<CustomList> existing, string? name, Func<string>? idFactory = null)
    {
        var error = ValidateName(existing, name, null, out var trimmed);
        if (error != null) return new CustomListResult(null, error);
        var id = idFactory != null ? idFactory() : Guid.NewGuid().ToString();
        return new CustomListResult(new CustomList { Id = id, Name = trimmed }, null);
    }

    /// <summary>Validates <paramref name="newName"/> and, when it is acceptable, renames <paramref name="list"/>.</summary>
    public static CustomListResult Rename(IReadOnlyList<CustomList> existing, CustomList list, string? newName)
    {
        var error = ValidateName(existing, newName, list, out var trimmed);
        if (error != null) return new CustomListResult(null, error);
        list.Name = trimmed;
        return new CustomListResult(list, null);
    }

    /// <summary>Adds a row or merges into the existing row for the item. False when the item id is 0.</summary>
    public static bool AddItem(CustomList list, uint itemId, int quantity)
    {
        if (itemId == 0) return false;
        var row = Find(list, itemId);
        if (row == null) list.Items.Add(new CustomListItem { ItemId = itemId, Quantity = ClampQuantity(quantity) });
        else row.Quantity = ClampQuantity((long)row.Quantity + ClampQuantity(quantity));
        return true;
    }

    /// <summary>Sets the quantity of an existing row. False when the item is not on the list.</summary>
    public static bool SetQuantity(CustomList list, uint itemId, int quantity)
    {
        var row = Find(list, itemId);
        if (row == null) return false;
        row.Quantity = ClampQuantity(quantity);
        return true;
    }

    /// <summary>Removes the row for the item. False when it is not on the list.</summary>
    public static bool RemoveItem(CustomList list, uint itemId)
        => list.Items.RemoveAll(i => i.ItemId == itemId) > 0;

    /// <summary>Removes the first list with the given id (only that one). False when there is none.</summary>
    public static bool Delete(List<CustomList> lists, string id)
    {
        var index = lists.FindIndex(l => l?.Id == id);
        if (index < 0) return false;
        lists.RemoveAt(index);
        return true;
    }

    /// <summary>The list's rows as requirements, in row order.</summary>
    public static IReadOnlyList<Requirement> ToRequirements(CustomList list)
    {
        var result = new List<Requirement>(list.Items.Count);
        foreach (var i in list.Items) result.Add(new Requirement(i.ItemId, i.Quantity));
        return result;
    }

    /// <summary>
    /// Adds a goal (a finished item to craft) or merges into the existing goal for the item, saturating; the quantity is
    /// clamped to 1..<see cref="MaxQuantity"/>. Plain rows are not touched. False when the item id is 0.
    /// </summary>
    public static bool AddGoal(CustomList list, uint itemId, int quantity)
    {
        if (itemId == 0) return false;
        var goal = FindGoal(list, itemId);
        if (goal == null) list.Goals.Add(new CustomListItem { ItemId = itemId, Quantity = ClampQuantity(quantity) });
        else goal.Quantity = ClampQuantity((long)goal.Quantity + ClampQuantity(quantity));
        return true;
    }

    /// <summary>Sets the quantity of an existing goal (clamped to 1..<see cref="MaxQuantity"/>). False when the item is not a goal.</summary>
    public static bool SetGoalQuantity(CustomList list, uint itemId, int quantity)
    {
        var goal = FindGoal(list, itemId);
        if (goal == null) return false;
        goal.Quantity = ClampQuantity(quantity);
        return true;
    }

    /// <summary>Removes the goal for the item; its materials go with it, since they are never stored. False when it is not a goal.</summary>
    public static bool RemoveGoal(CustomList list, uint itemId)
        => list.Goals.RemoveAll(g => g != null && g.ItemId == itemId) > 0;

    /// <summary>
    /// What the list needs bought, worked out afresh: the plain rows ("buy as is", never expanded), in their order, then
    /// the materials of all the goals expanded together
    /// (<see cref="RecipeExpander.ExpandToMaterials(IRecipeLookup, IReadOnlyList{Requirement})"/>), by item id ascending, so
    /// an intermediate shared by several goals, or itself a goal, is crafted once with the combined demand rounded up
    /// once, as in the Artisan export. A material that is also a plain row is added to that row (the first, if repeated),
    /// saturating at <see cref="MaxQuantity"/>. A goal that cannot be expanded on its own (item id 0, no recipe, a
    /// recipe with no usable ingredient) adds nothing and is listed in <see cref="EffectiveRequirementsResult.Problems"/>
    /// with that reason; the other goals still expand. If the goals together pass
    /// <see cref="RecipeExpander.MaxCraftableItems"/>, no goal adds anything and every goal that took part is listed
    /// with "This list is too large to expand". Problems are in goal order. Nothing throws. With no goals the rows are
    /// exactly <see cref="ToRequirements"/>. The list is not changed.
    /// </summary>
    public static EffectiveRequirementsResult EffectiveRequirements(CustomList list, IRecipeLookup recipes)
    {
        var plain = new List<(uint ItemId, int Quantity)>(list.Items.Count);
        var plainIndex = new Dictionary<uint, int>();
        foreach (var i in list.Items)
        {
            plainIndex.TryAdd(i.ItemId, plain.Count);
            plain.Add((i.ItemId, i.Quantity));
        }

        var goals = new List<(uint ItemId, string? Error)>();
        var usable = new List<Requirement>();
        foreach (var goal in list.Goals)
        {
            if (goal == null) continue;
            var error = RecipeExpander.RootProblem(recipes, goal.ItemId, out _);
            goals.Add((goal.ItemId, error));
            if (error == null) usable.Add(new Requirement(goal.ItemId, ClampQuantity(goal.Quantity)));
        }

        var derived = new List<Requirement>();
        if (usable.Count > 0)
        {
            var expanded = RecipeExpander.ExpandToMaterials(recipes, usable);
            if (expanded.Ok)
            {
                foreach (var m in expanded.Items!)
                {
                    if (plainIndex.TryGetValue(m.ItemId, out var at))
                        plain[at] = (m.ItemId, ClampQuantity((long)plain[at].Quantity + m.Quantity));
                    else
                        derived.Add(m);
                }
            }
            else
            {
                for (var k = 0; k < goals.Count; k++)
                    goals[k] = (goals[k].ItemId, goals[k].Error ?? expanded.Error ?? "This list is too large to expand");
            }
        }

        var problems = new List<GoalProblem>();
        foreach (var (id, error) in goals)
            if (error != null) problems.Add(new GoalProblem(id, error));

        var result = new List<Requirement>(plain.Count + derived.Count);
        foreach (var (id, quantity) in plain) result.Add(new Requirement(id, quantity));
        result.AddRange(derived);
        return new EffectiveRequirementsResult(result, problems);
    }

    /// <summary>
    /// The rows to export a list to Teamcraft or Artisan with: the plain rows, then the goals, merged by item id in
    /// first-seen order, saturating at <see cref="MaxQuantity"/>, quantities clamped to 1..<see cref="MaxQuantity"/>.
    /// Missing rows and item id 0 are left out. The goals are exported as themselves, not as their materials.
    /// </summary>
    public static IReadOnlyList<Requirement> ExportRows(CustomList list)
    {
        var order = new List<uint>();
        var totals = new Dictionary<uint, int>();
        foreach (var row in list.Items.Concat(list.Goals))
        {
            if (row == null || row.ItemId == 0) continue;
            if (totals.TryGetValue(row.ItemId, out var have)) totals[row.ItemId] = ClampQuantity((long)have + ClampQuantity(row.Quantity));
            else { order.Add(row.ItemId); totals[row.ItemId] = ClampQuantity(row.Quantity); }
        }
        var result = new List<Requirement>(order.Count);
        foreach (var id in order) result.Add(new Requirement(id, totals[id]));
        return result;
    }

    /// <summary>
    /// Removes excluded item ids that are not on the list. Returns how many were removed. A list with goals is left
    /// alone (returns 0): its material rows depend on the recipes, which this overload does not have; use
    /// <see cref="PruneExclusions(CustomList, IRecipeLookup)"/> for it.
    /// </summary>
    public static int PruneExclusions(CustomList list)
    {
        if (list.Goals.Count > 0) return 0;
        var present = new HashSet<uint>();
        foreach (var i in list.Items) present.Add(i.ItemId);
        return list.ExcludedItemIds.RemoveWhere(id => !present.Contains(id));
    }

    /// <summary>
    /// Removes excluded item ids that are not a row of <see cref="EffectiveRequirements"/> (plain rows and the goals'
    /// materials as they are now). Returns how many were removed. With no goals this is the same as
    /// <see cref="PruneExclusions(CustomList)"/>.
    /// </summary>
    public static int PruneExclusions(CustomList list, IRecipeLookup recipes)
    {
        var present = new HashSet<uint>();
        foreach (var r in EffectiveRequirements(list, recipes).Requirements) present.Add(r.ItemId);
        return list.ExcludedItemIds.RemoveWhere(id => !present.Contains(id));
    }

    /// <summary>
    /// Repairs lists read from a hand-edited or older settings file: drops missing lists, missing rows and missing goals, and gives a
    /// list with a blank id, or an id already used by an earlier list, a fresh id. Returns how many repairs were made.
    /// </summary>
    public static int Normalize(List<CustomList> lists, Func<string>? idFactory = null)
    {
        var changes = lists.RemoveAll(l => l is null);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var list in lists)
        {
            changes += list.Items.RemoveAll(i => i is null);
            changes += list.Goals.RemoveAll(g => g is null);
            if (string.IsNullOrWhiteSpace(list.Id) || !seen.Add(list.Id))
            {
                string id;
                do id = idFactory != null ? idFactory() : Guid.NewGuid().ToString();
                while (string.IsNullOrWhiteSpace(id) || seen.Contains(id) || lists.Exists(l => l.Id == id));
                list.Id = id;
                seen.Add(id);
                changes++;
            }
        }
        return changes;
    }

    /// <summary>
    /// Merges <paramref name="rows"/> into the list: an existing row keeps its position and gains the quantity (capped at
    /// <see cref="MaxQuantity"/>), a new row is appended in the given order; rows with item id 0 or a quantity below 1
    /// are skipped. A row that was new or whose quantity changed is no longer excluded, so the added amount is not
    /// skipped. Returns how many rows were new or changed.
    /// </summary>
    public static int AddMaterials(CustomList list, IReadOnlyList<Requirement> rows)
    {
        var changed = 0;
        foreach (var r in rows)
        {
            if (r.ItemId == 0 || r.Quantity < 1) continue;
            var row = Find(list, r.ItemId);
            if (row == null)
            {
                list.Items.Add(new CustomListItem { ItemId = r.ItemId, Quantity = ClampQuantity(r.Quantity) });
            }
            else
            {
                var sum = ClampQuantity((long)row.Quantity + r.Quantity);
                if (sum == row.Quantity) continue;
                row.Quantity = sum;
            }
            list.ExcludedItemIds.Remove(r.ItemId);
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// Replaces each row whose item can be crafted with its raw materials (<see cref="RecipeExpander.ExpandToMaterials(IRecipeLookup, uint, int)"/>),
    /// then merges everything by item id with saturating sums clamped to <see cref="MaxQuantity"/>. A row that cannot be
    /// crafted, or whose expansion fails (too large, no ingredients), is kept as given. Order: the rows' first-seen order,
    /// with a row's materials placed where that row was; a later row for an item already seen adds to the earlier entry.
    /// Rows with item id 0 or a quantity below 1 are dropped.
    /// </summary>
    public static IReadOnlyList<Requirement> Breakdown(IRecipeLookup recipes, IReadOnlyList<Requirement> rows)
    {
        var order = new List<uint>();
        var totals = new Dictionary<uint, int>();

        void Add(uint id, int quantity)
        {
            if (id == 0 || quantity < 1) return;
            if (totals.TryGetValue(id, out var have)) totals[id] = ClampQuantity((long)have + quantity);
            else { order.Add(id); totals[id] = ClampQuantity(quantity); }
        }

        foreach (var row in rows)
        {
            if (row.ItemId == 0 || row.Quantity < 1) continue;
            if (recipes.FindRecipeForItem(row.ItemId) != null)
            {
                var expanded = RecipeExpander.ExpandToMaterials(recipes, row.ItemId, row.Quantity);
                if (expanded.Ok)
                {
                    foreach (var m in expanded.Items!) Add(m.ItemId, m.Quantity);
                    continue;
                }
            }
            Add(row.ItemId, row.Quantity);
        }

        var result = new List<Requirement>(order.Count);
        foreach (var id in order) result.Add(new Requirement(id, totals[id]));
        return result;
    }

    /// <summary>Clamps a quantity to 1..<see cref="MaxQuantity"/>.</summary>
    public static int ClampQuantity(long quantity)
        => quantity < 1 ? 1 : quantity > MaxQuantity ? MaxQuantity : (int)quantity;

    private static CustomListItem? Find(CustomList list, uint itemId)
    {
        foreach (var i in list.Items) if (i.ItemId == itemId) return i;
        return null;
    }

    private static CustomListItem? FindGoal(CustomList list, uint itemId)
    {
        foreach (var g in list.Goals) if (g != null && g.ItemId == itemId) return g;
        return null;
    }

    private static string? ValidateName(IReadOnlyList<CustomList> existing, string? name, CustomList? self, out string trimmed)
    {
        trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return "Enter a name for the list";
        if (trimmed.Length > MaxNameLength) return "Names can be at most 60 characters";
        foreach (var other in existing)
        {
            if (ReferenceEquals(other, self)) continue;
            if (string.Equals(other.Name?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
                return "You already have a list with that name";
        }
        return null;
    }
}
