namespace LunaShoppingList.Core;

/// <summary>What kind of text the Import tab recognised.</summary>
public enum ImportKind
{
    /// <summary>Lines such as "3x Iron Ore".</summary>
    ItemList,
    /// <summary>A Teamcraft import link or its bare data.</summary>
    TeamcraftLink,
    /// <summary>The JSON of one Artisan list (Export List).</summary>
    ArtisanList,
}

/// <summary>Outcome of reading pasted import text.</summary>
/// <param name="Kind">What the text was taken to be.</param>
/// <param name="SuggestedName">A name to offer for the saved list.</param>
/// <param name="Requirements">The rows read; empty (never null) when nothing was read or on error.</param>
/// <param name="Unmatched">Lines or rows that could not be read, as readable text.</param>
/// <param name="Error">A message the player can read, or null on success.</param>
public sealed record ImportResult(ImportKind Kind, string SuggestedName, IReadOnlyList<Requirement> Requirements, IReadOnlyList<string> Unmatched, string? Error);

/// <summary>
/// Reads text pasted into the Import tab and decides what it is: Artisan's list JSON (starts with a brace), a
/// Teamcraft import link, or "Nx Item Name" lines.
/// </summary>
public static class ImportParser
{
    private const string BlankError = "Paste something first";
    private const string ArtisanDefaultName = "Imported Artisan list";
    private const string TeamcraftName = "Teamcraft import";
    private const string ListName = "Imported list";

    /// <summary>
    /// Detection order: trimmed text starting with <c>{</c> is an Artisan list; otherwise text containing <c>/import/</c>
    /// (any case), or one whitespace-free token that <see cref="TeamcraftLink.TryParse"/> reads with at least one row, is a
    /// Teamcraft link; everything else is an item list for <see cref="PasteParser"/>. Never throws; Requirements is
    /// never null.
    /// </summary>
    public static ImportResult Parse(string text, IItemInfo items, IRecipeData recipes)
    {
        try
        {
            var trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length == 0)
                return new ImportResult(ImportKind.ItemList, ListName, [], [], BlankError);

            if (trimmed[0] == '{')
            {
                var a = ArtisanListExport.TryParse(trimmed, recipes);
                var name = string.IsNullOrWhiteSpace(a.Name) ? ArtisanDefaultName : a.Name.Trim();
                return new ImportResult(ImportKind.ArtisanList, name, a.Requirements ?? [], a.Unmatched, a.Error);
            }

            var hasMarker = trimmed.Contains("/import/", StringComparison.OrdinalIgnoreCase);
            if (hasMarker || !trimmed.Any(char.IsWhiteSpace))
            {
                var t = TeamcraftLink.TryParse(trimmed);
                if (hasMarker || (t.Requirements is { Count: > 0 }))
                    return new ImportResult(ImportKind.TeamcraftLink, TeamcraftName, t.Requirements ?? [], t.Unmatched, t.Error);
            }

            var p = PasteParser.Parse(trimmed, items);
            return new ImportResult(ImportKind.ItemList, ListName, p.Requirements, p.UnmatchedLines, null);
        }
        catch (Exception)
        {
            return new ImportResult(ImportKind.ItemList, ListName, [], [], "That could not be read");
        }
    }
}
