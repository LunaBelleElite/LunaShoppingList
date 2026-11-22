namespace LunaShoppingList.Core;

/// <summary>Name search over the items a player can add to a list.</summary>
public static class ItemSearch
{
    /// <summary>
    /// The best matches for <paramref name="query"/>, at most <paramref name="max"/>: exact name, then names starting
    /// with the query, then names containing it (at a word start before mid-word). Ties: shorter name, alphabetical, id.
    /// </summary>
    public static IReadOnlyList<ItemSearchEntry> Rank(string? query, IReadOnlyList<ItemSearchEntry> entries, int max)
    {
        var q = (query ?? string.Empty).Trim();
        if (q.Length == 0 || max <= 0) return Array.Empty<ItemSearchEntry>();

        // Sorted best-first buffer of at most max candidates; a candidate worse than the last is dropped.
        var best = new List<(int Tier, ItemSearchEntry Entry)>(Math.Min(max, 64) + 1);
        for (var n = 0; n < entries.Count; n++)
        {
            var e = entries[n];
            var tier = TierOf(e.Name, q);
            if (tier < 0) continue;
            if (best.Count == max && Compare(tier, e, best[max - 1].Tier, best[max - 1].Entry) >= 0) continue;

            var pos = best.Count;
            while (pos > 0 && Compare(tier, e, best[pos - 1].Tier, best[pos - 1].Entry) < 0) pos--;
            best.Insert(pos, (tier, e));
            if (best.Count > max) best.RemoveAt(max);
        }

        var result = new ItemSearchEntry[best.Count];
        for (var i = 0; i < result.Length; i++) result[i] = best[i].Entry;
        return result;
    }

    private static int Compare(int tierA, ItemSearchEntry a, int tierB, ItemSearchEntry b)
    {
        if (tierA != tierB) return tierA.CompareTo(tierB);
        var byLength = a.Name.Length.CompareTo(b.Name.Length);
        if (byLength != 0) return byLength;
        var byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        if (byName != 0) return byName;
        return a.ItemId.CompareTo(b.ItemId);
    }

    // 0 exact, 1 prefix, 2 contained at a word start, 3 contained mid-word, -1 no match.
    private static int TierOf(string name, string q)
    {
        if (name.Length < q.Length) return -1;
        if (name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            return name.Length == q.Length ? 0 : 1;

        var from = 1;
        var found = false;
        while (from <= name.Length - q.Length)
        {
            var at = name.IndexOf(q, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0) break;
            found = true;
            if (!char.IsLetterOrDigit(name[at - 1])) return 2;
            from = at + 1;
        }
        return found ? 3 : -1;
    }
}
