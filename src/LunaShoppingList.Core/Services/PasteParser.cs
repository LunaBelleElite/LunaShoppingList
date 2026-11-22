using System.Text.RegularExpressions;

namespace LunaShoppingList.Core;

/// <summary>Parses pasted Artisan "Export Remaining Ingredients" text ("Nx Item Name" lines); Teamcraft section headers ending in " :" are skipped silently.</summary>
public static class PasteParser
{
    // Matched against the TRIMMED line: a trailing \s* after a lazy name backtracked quadratically on long whitespace runs.
    private static readonly Regex LinePattern = new(@"^([0-9]+)\s*[xX\u00D7]\s*(\S.*)$", RegexOptions.CultureInvariant);
    // Section headers from Teamcraft's "Copy as Text" ("Crystals :", "Pre_crafts :"): text, a space, a colon.
    private static readonly Regex HeaderPattern = new(@"^\s*\S.* :\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>Parses pasted text into requirements.</summary>
    /// <param name="text">Pasted text.</param>
    /// <param name="items">Item lookup used to resolve names to ids.</param>
    /// <returns>Recognised requirements (summed per item, ordered by item id) and the lines that could not
    /// be matched, verbatim.</returns>
    public static PasteResult Parse(string text, IItemInfo items)
    {
        var totals = new SortedDictionary<uint, long>();
        var unmatched = new List<string>();

        foreach (var line in (text ?? string.Empty).Split('\n', '\r'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var m = LinePattern.Match(line.Trim());
            if (!m.Success && HeaderPattern.IsMatch(line))
                continue;

            if (!m.Success
                || !int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var qty)
                || qty <= 0)
            {
                unmatched.Add(line);
                continue;
            }

            var name = m.Groups[2].Value;
            var id = items.FindByName(name) ?? items.FindByName(Whitespace.Replace(name, " "));
            if (id is null)
            {
                unmatched.Add(line);
                continue;
            }

            totals[id.Value] = totals.GetValueOrDefault(id.Value) + qty;
        }

        // Summed quantity that no longer fits an int is not a valid requirement: report it rather than wrap.
        var requirements = new List<Requirement>();
        foreach (var (id, total) in totals)
        {
            if (total > int.MaxValue)
                unmatched.Add($"(total for item {id} exceeds {int.MaxValue})");
            else
                requirements.Add(new Requirement(id, (int)total));
        }

        return new PasteResult(requirements, unmatched);
    }
}
