using System.Text.Json;

namespace LunaShoppingList.Core;

/// <summary>Parses Universalis v2 responses into listings.</summary>
public static class UniversalisResponseParser
{
    /// <summary>Parses either the single-item or the multi-item response shape.</summary>
    /// <param name="json">Response body.</param>
    /// <param name="requestedItemIds">Items that were requested; each gets an entry even if absent from the response.</param>
    /// <returns>One ItemListings per requested item, in requested order.</returns>
    /// <exception cref="FormatException">The body is not valid JSON or its root is not an object.</exception>
    public static IReadOnlyList<ItemListings> Parse(string json, IReadOnlyCollection<uint> requestedItemIds)
    {
        ArgumentNullException.ThrowIfNull(requestedItemIds);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new FormatException("Universalis response body was empty.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException("Universalis response body was not valid JSON: " + ex.Message, ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("Universalis response root was not a JSON object.");
            }

            var byItem = new Dictionary<uint, IReadOnlyList<Listing>>();

            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
            {
                // Multi-item shape: items keyed by item id; worldID/worldName may be at the top level.
                foreach (var prop in items.EnumerateObject())
                {
                    if (uint.TryParse(prop.Name, out var id) && prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        byItem[id] = ParseListings(id, prop.Value, root);
                    }
                }
            }
            else
            {
                // Single-item shape: the item view is the root.
                uint? id = ReadUInt(root, "itemID");
                if (id is null && requestedItemIds.Count == 1)
                {
                    id = requestedItemIds.First();
                }
                if (id is not null)
                {
                    byItem[id.Value] = ParseListings(id.Value, root, root);
                }
            }

            var result = new List<ItemListings>(requestedItemIds.Count);
            foreach (var requested in requestedItemIds)
            {
                result.Add(new ItemListings(requested, byItem.TryGetValue(requested, out var l) ? l : Array.Empty<Listing>()));
            }
            return result;
        }
    }

    /// <summary>Parses the data-center list: a JSON array of objects with name, region and worlds.</summary>
    /// <param name="json">Response body.</param>
    /// <returns>The data centers; entries without a name are skipped and non-integer world ids are dropped.</returns>
    /// <exception cref="FormatException">The body is not valid JSON or its root is not an array.</exception>
    public static IReadOnlyList<UniversalisDataCenter> ParseDataCenters(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new FormatException("Universalis data-center response body was empty.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException("Universalis data-center response body was not valid JSON: " + ex.Message, ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("Universalis data-center response root was not a JSON array.");
            }

            var result = new List<UniversalisDataCenter>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var name = ReadString(e, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }
                var worlds = new List<uint>();
                if (e.TryGetProperty("worlds", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var w in arr.EnumerateArray())
                    {
                        if (w.ValueKind == JsonValueKind.Number && w.TryGetUInt32(out var id))
                        {
                            worlds.Add(id);
                        }
                    }
                }
                result.Add(new UniversalisDataCenter(name, ReadString(e, "region") ?? string.Empty, worlds));
            }
            return result;
        }
    }

    private static IReadOnlyList<Listing> ParseListings(uint itemId, JsonElement view, JsonElement root)
    {
        if (!view.TryGetProperty("listings", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Listing>();
        }

        // Single-world queries carry the world on the view/root rather than on each listing.
        uint fallbackWorldId = ReadUInt(view, "worldID") ?? ReadUInt(root, "worldID") ?? 0;
        string fallbackWorldName = ReadString(view, "worldName") ?? ReadString(root, "worldName") ?? string.Empty;

        var list = new List<Listing>();
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            // A listing without a usable price or quantity, or priced at zero or below, cannot be planned against; skip it.
            if (!TryReadInt(e, "pricePerUnit", out var price) || !TryReadInt(e, "quantity", out var qty) || price <= 0)
            {
                continue;
            }
            bool hq = e.TryGetProperty("hq", out var hqEl) && hqEl.ValueKind == JsonValueKind.True;
            var review = e.TryGetProperty("lastReviewTime", out var rv) && rv.ValueKind == JsonValueKind.Number && rv.TryGetInt64(out var secs)
                ? SafeFromUnix(secs)
                : DateTimeOffset.UnixEpoch;
            list.Add(new Listing(
                itemId,
                ReadUInt(e, "worldID") ?? fallbackWorldId,
                ReadString(e, "worldName") ?? fallbackWorldName,
                price, qty, hq, review));
        }
        return list;
    }

    private static DateTimeOffset SafeFromUnix(long secs)
    {
        try { return DateTimeOffset.FromUnixTimeSeconds(secs); }
        catch (ArgumentOutOfRangeException) { return DateTimeOffset.UnixEpoch; }
    }

    private static bool TryReadInt(JsonElement e, string name, out int value)
    {
        value = 0;
        return e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out value);
    }

    private static uint? ReadUInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetUInt32(out var v) ? v : null;

    private static string? ReadString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
