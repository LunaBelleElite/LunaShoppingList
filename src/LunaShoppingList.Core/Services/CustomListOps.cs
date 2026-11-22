namespace LunaShoppingList.Core;

/// <summary>Outcome of creating or renaming a list: the list, or a message the player can read.</summary>
public sealed record CustomListResult(CustomList? List, string? Error)
{
    /// <summary>True when the operation succeeded and <see cref="List"/> is set.</summary>
    public bool Ok => List != null;
}

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

    /// <summary>Removes excluded item ids that are not on the list. Returns how many were removed.</summary>
    public static int PruneExclusions(CustomList list)
    {
        var present = new HashSet<uint>();
        foreach (var i in list.Items) present.Add(i.ItemId);
        return list.ExcludedItemIds.RemoveWhere(id => !present.Contains(id));
    }

    /// <summary>
    /// Repairs lists read from a hand-edited or older settings file: drops missing lists and missing rows, and gives a
    /// list with a blank id, or an id already used by an earlier list, a fresh id. Returns how many repairs were made.
    /// </summary>
    public static int Normalize(List<CustomList> lists, Func<string>? idFactory = null)
    {
        var changes = lists.RemoveAll(l => l is null);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var list in lists)
        {
            changes += list.Items.RemoveAll(i => i is null);
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

    /// <summary>Clamps a quantity to 1..<see cref="MaxQuantity"/>.</summary>
    public static int ClampQuantity(long quantity)
        => quantity < 1 ? 1 : quantity > MaxQuantity ? MaxQuantity : (int)quantity;

    private static CustomListItem? Find(CustomList list, uint itemId)
    {
        foreach (var i in list.Items) if (i.ItemId == itemId) return i;
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
