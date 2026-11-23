using System.Globalization;
using System.Text.Json;

namespace LunaShoppingList.Core;

/// <summary>Reads the drop list: a JSON object of item id to an array of monster ids, e.g. {"4":[115],"44072":[13101]}.</summary>
public static class DropSourceParser
{
    /// <summary>
    /// The item ids that have at least one monster. Keys that are not whole numbers, values that are not arrays and empty arrays are skipped.
    /// </summary>
    /// <exception cref="FormatException">The text is not valid JSON, or its top level is not an object.</exception>
    public static HashSet<uint> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new FormatException("The drop list is not a JSON object.");
            var ids = new HashSet<uint>();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() == 0)
                    continue;
                if (uint.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                    ids.Add(id);
            }
            return ids;
        }
        catch (JsonException ex)
        {
            throw new FormatException("The drop list is not valid JSON.", ex);
        }
    }
}
