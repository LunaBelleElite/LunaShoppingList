using System.Text.Encodings.Web;
using System.Text.Json;

namespace LunaShoppingList.Core;

/// <summary>Outcome of building Artisan's list JSON.</summary>
/// <param name="Json">The JSON to put on the clipboard; empty when no item could be exported.</param>
/// <param name="CraftedItems">
/// How many of the given rows Artisan will craft (rows counted as given, a repeated row each time); not the number of
/// recipe entries, which also holds the sub-crafts.
/// </param>
/// <param name="SkippedItemIds">Rows, and sub-crafts, left out because Artisan cannot craft them.</param>
/// <param name="Error">A message the player can read when the crafts could not be worked out (the JSON is then empty); otherwise null.</param>
public sealed record ArtisanExportResult(string Json, int CraftedItems, IReadOnlyList<uint> SkippedItemIds, string? Error = null);

/// <summary>Outcome of reading Artisan's list JSON.</summary>
/// <param name="Name">The list name in the JSON, or null.</param>
/// <param name="Requirements">The items the list makes (result item times crafts times yield), or null on error.</param>
/// <param name="Unmatched">Entries that could not be read, as readable text.</param>
/// <param name="Error">A message the player can read, or null on success.</param>
public sealed record ArtisanImportResult(string? Name, IReadOnlyList<Requirement>? Requirements, IReadOnlyList<string> Unmatched, string? Error);

/// <summary>Builds and reads the JSON of one Artisan crafting list (Artisan's "Export List" / "Import List From Clipboard").</summary>
/// <remarks>
/// Artisan reads the clipboard with Json.NET and <c>MissingMemberHandling.Error</c>: any member its
/// <c>NewCraftingList</c> / <c>ListItem</c> / <c>ListItemOptions</c> classes do not have (including a
/// <c>$type</c> that is not first in its object) makes the whole import fail. <see cref="Build"/> therefore
/// writes only members those classes have, and no <c>$type</c> or <c>$values</c>.
/// </remarks>
public static class ArtisanListExport
{
    private const int MaxQuantity = 9_999_999;
    private const string DefaultName = "Shopping list";

    /// <summary>Builds the JSON for a list that crafts the given items, with every sub-craft they need.</summary>
    /// <remarks>
    /// Rows with item id 0 or quantity below 1 are ignored. A row whose item has no recipe
    /// (<see cref="IRecipeLookup.FindRecipeForItem"/>), or a recipe with id 0, is listed in
    /// <see cref="ArtisanExportResult.SkippedItemIds"/> (each item once, first-seen order). The other rows are expanded
    /// together by <see cref="RecipeExpander.ExpandToCrafts"/>: one entry per craft of the whole tree, intermediates
    /// before the items that use them, a shared intermediate (or a row that is also an ingredient of another row, or a
    /// repeated row) once with the combined demand rounded up once. Entries for the same recipe id are summed at the
    /// first one's position; crafts are capped at 9,999,999. A sub-craft whose recipe id is 0 is not written and its
    /// item is listed in SkippedItemIds; its own sub-crafts are still written. If the tree is too large the JSON is
    /// empty and <see cref="ArtisanExportResult.Error"/> says why. A blank name becomes "Shopping list".
    /// </remarks>
    public static ArtisanExportResult Build(string name, IReadOnlyList<Requirement> rows, IRecipeLookup recipes)
    {
        // The same craftable rule as the Teamcraft export (ExportFilter), so the two cannot disagree.
        var filtered = ExportFilter.CraftableOnly(rows, recipes);
        var roots = filtered.Rows;
        var skipped = filtered.SkippedItemIds.ToList();

        if (roots.Count == 0)
            return new ArtisanExportResult(string.Empty, 0, skipped);

        var tree = RecipeExpander.ExpandToCrafts(recipes, roots);
        if (!tree.Ok)
            return new ArtisanExportResult(string.Empty, 0, skipped, tree.Error ?? "The list could not be expanded");

        var order = new List<uint>();
        var crafts = new Dictionary<uint, long>();
        foreach (var entry in tree.Crafts!)
        {
            if (entry.RecipeId == 0)
            {
                if (!skipped.Contains(entry.ItemId))
                    skipped.Add(entry.ItemId);
                continue;
            }
            if (crafts.TryGetValue(entry.RecipeId, out var sum))
                crafts[entry.RecipeId] = Math.Min(MaxQuantity, sum + entry.Crafts);
            else
            {
                order.Add(entry.RecipeId);
                crafts[entry.RecipeId] = Math.Min(MaxQuantity, entry.Crafts);
            }
        }
        var crafted = roots.Count;

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false }))
        {
            w.WriteStartObject();
            w.WriteString("Name", string.IsNullOrWhiteSpace(name) ? DefaultName : name);
            w.WriteStartArray("Recipes");
            foreach (var id in order)
            {
                w.WriteStartObject();
                w.WriteNumber("ID", id);
                w.WriteNumber("Quantity", (int)crafts[id]);
                w.WriteStartObject("ListItemOptions");
                w.WriteBoolean("NQOnly", false);
                w.WriteBoolean("Skipping", false);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("ExpandedList");
            w.WriteEndArray();
            w.WriteBoolean("SkipLiteral", false);
            w.WriteBoolean("OnlyRestockNonCrafted", false);
            w.WriteEndObject();
        }

        return new ArtisanExportResult(System.Text.Encoding.UTF8.GetString(stream.ToArray()), crafted, skipped);
    }

    /// <summary>Reads the JSON of one Artisan list into the items it makes.</summary>
    /// <remarks>
    /// Accepts Artisan's "Export List" object (unknown members, <c>$type</c> and <c>$values</c> ignored; names
    /// are case-sensitive). Each <c>Recipes</c> entry becomes its recipe's result item, Quantity x yield (yield
    /// below 1 counting as 1), merged by item in first-seen order and capped at 9,999,999. Entries that cannot be
    /// used, or are set to Skip in Artisan, are listed in <see cref="ArtisanImportResult.Unmatched"/>. Never throws.
    /// </remarks>
    public static ArtisanImportResult TryParse(string json, IRecipeData recipes)
    {
        var unmatched = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
            return Fail(null, unmatched, "The clipboard is empty.");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException)
        {
            return Fail(null, unmatched, "That is not an Artisan list (the text is not valid JSON).");
        }
        catch (ArgumentException)
        {
            // Raw lone surrogate(s) in the text: not encodable as UTF-8. Read it again with each one replaced by U+FFFD.
            try
            {
                doc = JsonDocument.Parse(ReplaceLoneSurrogates(json), new JsonDocumentOptions { MaxDepth = 64 });
            }
            catch (Exception e) when (e is JsonException or ArgumentException)
            {
                return Fail(null, unmatched, "That is not an Artisan list (the text is not valid JSON).");
            }
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Fail(null, unmatched, "That is not an Artisan list (expected one JSON object).");

            string? name = root.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String ? ReadName(n) : null;

            if (!root.TryGetProperty("Recipes", out var recipesElement) || !TryEnumerate(recipesElement, out var entries))
                return Fail(name, unmatched, "That is not an Artisan list (it has no Recipes).");

            var read = new List<(uint RecipeId, uint ItemId, long Items)>();
            var index = 0;
            foreach (var entry in entries)
            {
                index++;
                var problem = ReadEntry(entry, recipes, out var recipeId, out var itemId, out var items);
                if (problem is not null)
                {
                    unmatched.Add($"Entry {index}: {problem}");
                    continue;
                }
                read.Add((recipeId, itemId, items));
            }

            // Sub-crafts (an entry whose result is an ingredient of another entry) are dropped silently, as Artisan's own Teamcraft export does.
            var subCrafts = ArtisanListItems.SubCraftRecipeIds(read.Select(r => r.RecipeId).ToList(), recipes);
            var order = new List<uint>();
            var totals = new Dictionary<uint, long>();
            foreach (var (recipeId, itemId, items) in read)
            {
                if (subCrafts.Contains(recipeId))
                    continue;
                if (totals.TryGetValue(itemId, out var sum))
                    totals[itemId] = Math.Min(MaxQuantity, sum + items);
                else
                {
                    order.Add(itemId);
                    totals[itemId] = Math.Min(MaxQuantity, items);
                }
            }

            if (order.Count == 0)
                return Fail(name, unmatched, index == 0 ? "The Artisan list has no recipes." : "No recipe in the Artisan list could be read.");

            return new ArtisanImportResult(name, order.Select(id => new Requirement(id, (int)totals[id])).ToList(), unmatched, null);
        }
    }

    /// <summary>The name, or null when it holds an escaped lone surrogate and cannot be read as text.</summary>
    private static string? ReadName(JsonElement element)
    {
        try
        {
            return element.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string ReplaceLoneSurrogates(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsHighSurrogate(chars[i]) && i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1])) i++;
            else if (char.IsSurrogate(chars[i])) chars[i] = (char)0xFFFD;
        }
        return new string(chars);
    }

    private static ArtisanImportResult Fail(string? name, List<string> unmatched, string error)
        => new(name, null, unmatched, error);

    /// <summary>Reads one Recipes entry; returns null on success, otherwise the reason it was not used.</summary>
    private static string? ReadEntry(JsonElement entry, IRecipeData recipes, out uint recipeId, out uint itemId, out long items)
    {
        recipeId = 0;
        itemId = 0;
        items = 0;
        if (entry.ValueKind != JsonValueKind.Object)
            return "not a recipe";
        if (!entry.TryGetProperty("ID", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetUInt32(out var rid) || rid == 0)
            return "no recipe ID";
        recipeId = rid;

        var recipe = recipes.GetRecipe(recipeId);
        if (recipe is null || recipe.ResultItemId == 0)
            return $"recipe {recipeId} is not a known recipe";
        if (!entry.TryGetProperty("Quantity", out var q) || q.ValueKind != JsonValueKind.Number || !q.TryGetInt32(out var crafts) || crafts < 1)
            return $"recipe {recipeId} has no quantity of 1 or more";
        if (entry.TryGetProperty("ListItemOptions", out var opts) && opts.ValueKind == JsonValueKind.Object
            && opts.TryGetProperty("Skipping", out var s) && s.ValueKind == JsonValueKind.True)
            return $"recipe {recipeId} is set to Skip in Artisan";

        itemId = recipe.ResultItemId;
        items = (long)crafts * Math.Max(1, recipe.Yield);
        return null;
    }

    /// <summary>Accepts a bare array or a Newtonsoft-style <c>{"$type": ..., "$values": [...]}</c> wrapper.</summary>
    private static bool TryEnumerate(JsonElement element, out JsonElement.ArrayEnumerator items)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            items = element.EnumerateArray();
            return true;
        }

        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("$values", out var values)
            && values.ValueKind == JsonValueKind.Array)
        {
            items = values.EnumerateArray();
            return true;
        }

        items = default;
        return false;
    }
}
