namespace LunaShoppingList.Core;

/// <summary>Name search over the items a player can add to a list.</summary>
public static class ItemSearch
{
    /// <summary>
    /// The best matches for <paramref name="query"/>, at most <paramref name="max"/>: exact name, then names starting
    /// with the query, then names containing it (at a word start before mid-word); then, when the words are not
    /// contiguous, names containing every typed word in any order (first word a prefix, then every word at a word
    /// start, then the rest). Ties: shorter name, alphabetical, id.
    /// </summary>
    public static IReadOnlyList<ItemSearchEntry> Rank(string? query, IReadOnlyList<ItemSearchEntry> entries, int max)
    {
        var q = (query ?? string.Empty).Trim();
        if (q.Length == 0 || max <= 0) return Array.Empty<ItemSearchEntry>();

        // Words are split once per call, never per entry; plainWords is the same list with apostrophes removed.
        var words = q.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var plainWords = new string[words.Length];
        var plainCount = 0;
        for (var i = 0; i < words.Length; i++)
        {
            var w = StripApostrophes(words[i]);
            if (w.Length > 0) plainWords[plainCount++] = w;
        }
        if (plainCount != plainWords.Length) Array.Resize(ref plainWords, plainCount);
        var plainQuery = string.Join(' ', plainWords); // built once per call, reused for every apostrophe name

        // Sorted best-first buffer of at most max candidates; a candidate worse than the last is dropped.
        var best = new List<(int Tier, ItemSearchEntry Entry)>(Math.Min(max, 64) + 1);
        for (var n = 0; n < entries.Count; n++)
        {
            var e = entries[n];
            var tier = TierOf(e.Name, q, words, plainQuery, plainWords);
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

    // Tiers, best first. Whole query as one phrase: 0 exact, 1 prefix, 2 contained at a word start, 3 contained mid-word.
    // Otherwise every word contained, in any order: 4 first word is a prefix of the name, 5 every word starts a word,
    // 6 the rest. -1 no match. A name holding an apostrophe is retried with apostrophes removed from both sides.
    private static int TierOf(string name, string q, string[] words, string plainQuery, string[] plainWords)
    {
        var t = PlainTier(name, q, words);
        if (t >= 0 || (name.IndexOf('\'') < 0 && name.IndexOf('\u2019') < 0)) return t;
        if (plainWords.Length == 0) return -1;
        return PlainTier(StripApostrophes(name), plainQuery, plainWords);
    }

    private static int PlainTier(string name, string q, string[] words)
    {
        var phrase = PhraseTier(name, q);
        if (phrase >= 0 || words.Length < 2) return phrase;

        var allStartWords = true;
        for (var i = 0; i < words.Length; i++)
        {
            var w = WordPlacement(name, words[i]);
            if (w < 0) return -1;
            if (w == 2) allStartWords = false;
        }
        if (name.StartsWith(words[0], StringComparison.OrdinalIgnoreCase)) return 4;
        return allStartWords ? 5 : 6;
    }

    // 1 the word occurs at a word start (index 0 counts), 2 only mid-word, -1 not contained.
    private static int WordPlacement(string name, string word)
    {
        var from = 0;
        var found = false;
        while (from <= name.Length - word.Length)
        {
            var at = name.IndexOf(word, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0) break;
            found = true;
            if (at == 0 || !char.IsLetterOrDigit(name[at - 1])) return 1;
            from = at + 1;
        }
        return found ? 2 : -1;
    }

    private static string StripApostrophes(string s)
        => s.Replace("'", string.Empty).Replace("\u2019", string.Empty);

    private static int PhraseTier(string name, string q)
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
