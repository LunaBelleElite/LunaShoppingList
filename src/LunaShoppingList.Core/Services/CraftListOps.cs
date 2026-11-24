namespace LunaShoppingList.Core;

/// <summary>Pure helpers for exchanging craft lists with another plugin (Allagan Tools). Nothing here touches the game or IPC.</summary>
public static class CraftListOps
{
    /// <summary>Longest list name sent to the other plugin (same limit as our own list names).</summary>
    public const int MaxNameLength = CustomListOps.MaxNameLength;

    /// <summary>Name used when the list has none.</summary>
    public const string FallbackName = "Luna Shopping List";

    /// <summary>The message for a plugin that is not installed or not ready.</summary>
    public const string NotAvailableMessage = "Allagan Tools is not installed or has not finished loading.";

    /// <summary>The message for a list the other plugin did not accept (it gave no list key back).</summary>
    public const string NotAcceptedMessage = "Allagan Tools did not accept the list";

    /// <summary>The message for a list key the other plugin does not know.</summary>
    public const string UnknownListMessage = "Allagan Tools has no craft list with that key.";

    /// <summary>The message for an export with no items.</summary>
    public const string NothingToSendMessage = "There is nothing in this list to send to Allagan Tools.";

    /// <summary>The message when the new list was sent but reading it back failed.</summary>
    public const string NotConfirmedMessage = "Allagan Tools did not confirm the list. Check its craft lists before sending again.";

    /// <summary>The message when a key came back but the re-read list differs from what was sent: the list may exist.</summary>
    public const string NotMatchedMessage = "Allagan Tools may have created the list, but its contents did not match what was sent. Check its craft lists before sending again.";

    /// <summary>The message when the create call itself threw: the list may already exist.</summary>
    public const string SendThrewMessage = "Something went wrong while sending. Allagan Tools may have created the list, so check its craft lists before sending again.";

    /// <summary>
    /// Turns the other plugin's item-to-quantity answer into goals: ordered by item id, item id 0 and quantity 0 dropped,
    /// duplicates summed, quantities clamped to the int range.
    /// </summary>
    public static IReadOnlyList<Requirement> ToGoals(IEnumerable<KeyValuePair<uint, uint>>? items)
        => items == null ? [] : Build(items.Select(p => (p.Key, (long)p.Value)), int.MaxValue);

    /// <summary>
    /// The finished items to send: the list's goals, or its plain rows when it has no usable goal (item id 0 or quantity 0 or less
    /// does not count). Summed per item id and clamped to <see cref="CustomListOps.MaxQuantity"/>; rows of quantity 0 or less and item
    /// id 0 are skipped; ordered by item id. Raw materials are never added: the other plugin works out the ingredients itself.
    /// </summary>
    public static IReadOnlyList<Requirement> ForExport(CustomList? list)
    {
        if (list == null) return [];
        var goals = Usable(list.Goals);
        return goals.Count > 0 ? goals : Usable(list.Items);
    }

    private static IReadOnlyList<Requirement> Usable(IEnumerable<CustomListItem?> rows)
        => Build(rows.Where(r => r != null).Select(r => (r!.ItemId, (long)Math.Min(r.Quantity, CustomListOps.MaxQuantity))), CustomListOps.MaxQuantity);

    /// <summary>The goals as the item-to-quantity dictionary the other plugin takes (summed per item id, quantities of 0 or less skipped).</summary>
    public static Dictionary<uint, uint> ToIpcItems(IReadOnlyList<Requirement> goals)
    {
        var map = new Dictionary<uint, uint>();
        foreach (var g in Build(goals.Select(g => (g.ItemId, (long)g.Quantity)), int.MaxValue))
            map[g.ItemId] = (uint)g.Quantity;
        return map;
    }

    /// <summary>A safe list name: trimmed, cut to <see cref="MaxNameLength"/> characters (never inside a surrogate pair), <see cref="FallbackName"/> when empty.</summary>
    public static string ExportName(string? name)
    {
        var text = (name ?? string.Empty).Trim();
        if (text.Length > MaxNameLength)
        {
            var end = MaxNameLength;
            if (char.IsHighSurrogate(text[end - 1])) end--;
            text = text[..end].TrimEnd();
        }
        return text.Length == 0 ? FallbackName : text;
    }

    /// <summary>The lists from a key-to-name answer, ordered by name (ignoring case) then key; blank keys dropped, a blank name shown as its key.</summary>
    public static IReadOnlyList<CraftListInfo> ToLists(IEnumerable<KeyValuePair<string, string>>? answer)
    {
        if (answer == null) return [];
        return answer
            .Where(p => !string.IsNullOrWhiteSpace(p.Key))
            .Select(p => new CraftListInfo(p.Key, string.IsNullOrWhiteSpace(p.Value) ? p.Key : p.Value))
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Whether what the other plugin reports for the new list is what was sent (both summed per item id).</summary>
    public static bool SameItems(IReadOnlyList<Requirement> sent, IEnumerable<KeyValuePair<uint, uint>>? reread)
    {
        var a = Build(sent.Select(g => (g.ItemId, (long)g.Quantity)), int.MaxValue);
        var b = ToGoals(reread);
        return a.SequenceEqual(b);
    }

    /// <summary>The result for a creation: Ok only when a key came back and the re-read list matches what was sent.</summary>
    public static CraftListResult VerifyCreated(string name, string? key, IReadOnlyList<Requirement> sent, IEnumerable<KeyValuePair<uint, uint>>? reread)
    {
        // A blank key means the other plugin gave no list back, so nothing is known to exist: not accepted. A key with a different
        // re-read means the list is there but wrong: it may exist, so the message says so (a second send would duplicate it).
        if (string.IsNullOrWhiteSpace(key))
            return new CraftListResult(CraftListStatus.Failed, NotAcceptedMessage);
        if (!SameItems(sent, reread))
            return new CraftListResult(CraftListStatus.Failed, NotMatchedMessage);
        var count = Build(sent.Select(g => (g.ItemId, (long)g.Quantity)), int.MaxValue).Count;
        return new CraftListResult(CraftListStatus.Ok, $"Sent \"{name}\" to Allagan Tools ({Plural.Of(count, "item")}).", key);
    }

    /// <summary>The result of reading a list: Failed with <see cref="UnknownListMessage"/> when <paramref name="key"/> is not among <paramref name="lists"/>.</summary>
    public static CraftListGoals ReadResult(string key, IReadOnlyList<CraftListInfo> lists, IEnumerable<KeyValuePair<uint, uint>>? items)
    {
        if (!lists.Any(l => string.Equals(l.Key, key, StringComparison.Ordinal)))
            return new CraftListGoals(CraftListStatus.Failed, UnknownListMessage, []);
        return new CraftListGoals(CraftListStatus.Ok, string.Empty, ToGoals(items));
    }

    // Sums per item id in long, drops item id 0 and totals of 0 or less, clamps each total to max, orders by item id.
    private static List<Requirement> Build(IEnumerable<(uint Id, long Qty)> rows, int max)
    {
        var sums = new SortedDictionary<uint, long>();
        foreach (var (id, qty) in rows)
        {
            if (id == 0 || qty <= 0) continue;
            sums[id] = sums.GetValueOrDefault(id) + qty;
        }
        return sums.Select(p => new Requirement(p.Key, (int)Math.Min(p.Value, max))).ToList();
    }
}
