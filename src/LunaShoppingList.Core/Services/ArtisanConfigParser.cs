using System.Text.Json;

namespace LunaShoppingList.Core;

/// <summary>Reads crafting lists out of the saved configuration JSON of Artisan.</summary>
public static class ArtisanConfigParser
{
    /// <summary>Parses all lists found in the config JSON.</summary>
    /// <param name="json">Artisan config file text.</param>
    /// <returns>The crafting lists, in file order. Lists without a usable ID and items without a usable
    /// ID or a positive Quantity are skipped. A lone UTF-16 surrogate in the text never throws: a raw one is read as
    /// U+FFFD (so a list name keeps its other characters), and a name holding an escaped one (<c>\ud800</c>), which cannot be
    /// read as text at all, comes back as an empty name; the list's recipes are still read.</returns>
    /// <exception cref="InvalidDataException">The text is not valid JSON, is not an object, or has no usable
    /// <c>NewCraftingLists</c> collection.</exception>
    public static IReadOnlyList<CraftingList> ParseLists(string json)
    {
        if (json is null)
            throw new InvalidDataException("Artisan config text is null.");

        var options = new JsonDocumentOptions { MaxDepth = 256, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Artisan config is not valid JSON: " + ex.Message, ex);
        }
        catch (ArgumentException)
        {
            // Raw lone surrogate(s) in the text: not encodable as UTF-8. Read it again with each one replaced by U+FFFD.
            try
            {
                doc = JsonDocument.Parse(ReplaceLoneSurrogates(json), options);
            }
            catch (Exception e) when (e is JsonException or ArgumentException)
            {
                throw new InvalidDataException("Artisan config is not valid JSON: " + e.Message, e);
            }
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Artisan config root is not a JSON object.");

            // TryGetProperty is ordinal (case-sensitive): "ID" never matches "Id" or "id".
            if (!root.TryGetProperty("NewCraftingLists", out var listsElement) || !TryEnumerate(listsElement, out var listElements))
                throw new InvalidDataException("Artisan config has no 'NewCraftingLists' array.");

            var result = new List<CraftingList>();
            foreach (var listElement in listElements)
            {
                if (listElement.ValueKind != JsonValueKind.Object)
                    continue;
                if (!listElement.TryGetProperty("ID", out var idElement)
                    || idElement.ValueKind != JsonValueKind.Number
                    || !idElement.TryGetInt32(out var listId))
                    continue;

                var name = listElement.TryGetProperty("Name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? ReadName(nameElement)
                    : string.Empty;

                var items = new List<CraftItem>();
                if (listElement.TryGetProperty("Recipes", out var recipesElement) && TryEnumerate(recipesElement, out var recipeElements))
                {
                    foreach (var recipe in recipeElements)
                    {
                        var item = ParseItem(recipe);
                        if (item is not null)
                            items.Add(item);
                    }
                }

                result.Add(new CraftingList(listId, name, items));
            }

            return result;
        }
    }

    /// <summary>The name; empty when it holds an escaped lone surrogate and cannot be read as text.</summary>
    private static string ReadName(JsonElement element)
    {
        try
        {
            return element.GetString() ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
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

    private static CraftItem? ParseItem(JsonElement recipe)
    {
        if (recipe.ValueKind != JsonValueKind.Object)
            return null;
        if (!recipe.TryGetProperty("ID", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetUInt32(out var recipeId) || recipeId == 0)
            return null;
        if (!recipe.TryGetProperty("Quantity", out var qty) || qty.ValueKind != JsonValueKind.Number || !qty.TryGetInt32(out var crafts) || crafts <= 0)
            return null;

        var skipping = false;
        var nqOnly = false;
        if (recipe.TryGetProperty("ListItemOptions", out var opts) && opts.ValueKind == JsonValueKind.Object)
        {
            skipping = opts.TryGetProperty("Skipping", out var s) && s.ValueKind == JsonValueKind.True;
            nqOnly = opts.TryGetProperty("NQOnly", out var n) && n.ValueKind == JsonValueKind.True;
        }

        return new CraftItem(recipeId, crafts, skipping, nqOnly);
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
