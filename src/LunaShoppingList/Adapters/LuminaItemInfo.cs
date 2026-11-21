using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using LunaShoppingList.Core;

namespace LunaShoppingList.Adapters;

/// <summary>Reads item names and categories from the game's Item sheet.</summary>
public sealed class LuminaItemInfo : IItemInfo
{
    /// <summary>
    /// Row id of the ItemUICategory named "Crystal" (shards, crystals, clusters).
    /// From research only; the `/lsl dump` spike prints the category of Fire Shard/Crystal/Cluster to confirm it.
    /// </summary>
    internal const uint CrystalUiCategoryId = 59;

    private readonly ExcelSheet<Item> items;
    private readonly ExcelSheet<Recipe> recipes;
    private readonly IPluginLog log;

    // PublicationOnly: a failed build is not cached, so a later access tries again.
    private readonly Lazy<Dictionary<string, uint>> byName;
    private readonly object warmUpGate = new();
    private Task? warmUp;
    private long warmUpStartedAt;

    public LuminaItemInfo(IDataManager data, IPluginLog log)
    {
        items = data.GetExcelSheet<Item>();
        recipes = data.GetExcelSheet<Recipe>();
        this.log = log;
        byName = new Lazy<Dictionary<string, uint>>(BuildIndex, LazyThreadSafetyMode.PublicationOnly);
    }

    /// <summary>
    /// True once the name index used by <see cref="FindByName"/> is built. While it is not, this starts
    /// (or, after a failure, retries) building it in the background, so callers on the game's threads never wait for it.
    /// </summary>
    internal bool IsNameIndexReady
    {
        get
        {
            if (byName.IsValueCreated)
                return true;
            WarmUp();
            return false;
        }
    }

    /// <summary>Builds the name index on the thread pool unless it is built, being built, or failed only moments ago.</summary>
    internal void WarmUp()
    {
        lock (warmUpGate)
        {
            if (byName.IsValueCreated || warmUp is { IsCompleted: false })
                return;
            if (warmUp != null && Environment.TickCount64 - warmUpStartedAt < 10_000)
                return;
            warmUpStartedAt = Environment.TickCount64;
            warmUp = Task.Run(() =>
            {
                try
                {
                    _ = byName.Value;
                }
                catch (Exception ex)
                {
                    log.Error(ex, "Building the item name index failed");
                }
            });
        }
    }

    public string GetName(uint itemId)
    {
        if (items.TryGetRow(itemId, out var row))
        {
            var name = row.Name.ToString();
            if (!string.IsNullOrEmpty(name))
                return name;
        }
        return $"Item #{itemId}";
    }

    public bool IsCrystalLike(uint itemId)
        => items.TryGetRow(itemId, out var row) && row.ItemUICategory.RowId == CrystalUiCategoryId;

    public uint? FindByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        return byName.Value.TryGetValue(name.Trim(), out var id) ? id : null;
    }

    /// <summary>Raw UI category row id and name for an item (used by the diagnostic dump), or null if unknown.</summary>
    internal (uint Id, string Name)? GetUiCategory(uint itemId)
    {
        if (!items.TryGetRow(itemId, out var row))
            return null;
        return (row.ItemUICategory.RowId, row.ItemUICategory.ValueNullable?.Name.ToString() ?? "?");
    }

    private Dictionary<string, uint> BuildIndex()
    {
        var withRecipe = new HashSet<uint>();
        foreach (var r in recipes)
            if (r.ItemResult.RowId != 0)
                withRecipe.Add(r.ItemResult.RowId);

        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in items)
        {
            var name = row.Name.ToString();
            if (string.IsNullOrEmpty(name))
                continue;
            // First row wins, except a row that has a recipe replaces one that does not.
            if (!map.TryGetValue(name, out var existing) || (!withRecipe.Contains(existing) && withRecipe.Contains(row.RowId)))
                map[name] = row.RowId;
        }
        return map;
    }
}
