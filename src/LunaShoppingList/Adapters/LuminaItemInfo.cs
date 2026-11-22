using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using LunaShoppingList.Core;

namespace LunaShoppingList.Adapters;

/// <summary>Reads item names and categories from the game's Item sheet.</summary>
public sealed class LuminaItemInfo : IItemInfo, IItemCatalog
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
    private readonly Lazy<Index> index;
    private static readonly IReadOnlyList<ItemSearchEntry> NoEntries = Array.Empty<ItemSearchEntry>();
    private readonly object warmUpGate = new();
    private Task? warmUp;
    private long warmUpStartedAt;
    private int attempts;
    private bool failed;

    /// <summary>Most times the name index build is tried before it is given up for the session.</summary>
    internal const int MaxAttempts = 3;

    public LuminaItemInfo(IDataManager data, IPluginLog log)
    {
        items = data.GetExcelSheet<Item>();
        recipes = data.GetExcelSheet<Recipe>();
        this.log = log;
        index = new Lazy<Index>(BuildIndex, LazyThreadSafetyMode.PublicationOnly);
    }

    /// <summary>
    /// True once the name index used by <see cref="FindByName"/> is built. While it is not, this starts
    /// (or, after a failure, retries) building it in the background, so callers on the game's threads never wait for it.
    /// </summary>
    internal bool IsNameIndexReady
    {
        get
        {
            if (index.IsValueCreated)
                return true;
            if (failed)
                return false;
            WarmUp();
            return false;
        }
    }

    /// <summary>True once <see cref="MarketableItems"/> is filled; while it is not, this starts the same background build as <see cref="IsNameIndexReady"/>.</summary>
    public bool IsReady => IsNameIndexReady;

    /// <summary>Market-board items, one per distinct name, sorted by name then id. Empty until <see cref="IsReady"/>; never blocks.</summary>
    public IReadOnlyList<ItemSearchEntry> MarketableItems => index.IsValueCreated ? index.Value.Marketable : NoEntries;

    /// <summary>
    /// Whether an item can be sold on the market board (a market search category and not untradable), by id, from the same
    /// background index as <see cref="MarketableItems"/>. Answers <c>true</c> while that index is not built yet (or failed for
    /// good): an unknown item is treated as marketable, so a build never skips a price lookup on a guess. Never blocks and reads
    /// no sheet: a set lookup once the index is ready.
    /// </summary>
    public bool IsMarketable(uint itemId) => !index.IsValueCreated || index.Value.MarketableIds.Contains(itemId);

    /// <summary>Builds the name index on the thread pool unless it is built, being built, has failed for good, or the last attempt started only moments ago.</summary>
    internal void WarmUp()
    {
        lock (warmUpGate)
        {
            if (index.IsValueCreated || failed || warmUp is { IsCompleted: false })
                return;
            if (attempts > 0 && Environment.TickCount64 - warmUpStartedAt < 10_000)
                return;
            warmUpStartedAt = Environment.TickCount64;
            attempts++;
            var attempt = attempts;
            warmUp = Task.Run(() =>
            {
                try
                {
                    _ = index.Value;
                }
                catch (Exception ex)
                {
                    if (attempt >= MaxAttempts)
                    {
                        lock (warmUpGate)
                            failed = true;
                        log.Error(ex, "Building the item name index failed {Attempts} times; item names stay unavailable this session", attempt);
                    }
                    else
                    {
                        log.Warning(ex, "Building the item name index failed (attempt {Attempt} of {Max}); it will be tried again", attempt, MaxAttempts);
                    }
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
        return index.Value.ByName.TryGetValue(name.Trim(), out var id) ? id : null;
    }

    /// <summary>Raw UI category row id and name for an item (used by the diagnostic dump), or null if unknown.</summary>
    internal (uint Id, string Name)? GetUiCategory(uint itemId)
    {
        if (!items.TryGetRow(itemId, out var row))
            return null;
        return (row.ItemUICategory.RowId, row.ItemUICategory.ValueNullable?.Name.ToString() ?? "?");
    }

    // MarketableIds holds every marketable row id (Marketable is deduplicated by name, so it is no per-id test).
    private sealed record Index(Dictionary<string, uint> ByName, IReadOnlyList<ItemSearchEntry> Marketable, HashSet<uint> MarketableIds);

    private Index BuildIndex()
    {
        var withRecipe = new HashSet<uint>();
        foreach (var r in recipes)
            if (r.ItemResult.RowId != 0)
                withRecipe.Add(r.ItemResult.RowId);

        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var market = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var marketableIds = new HashSet<uint>();
        foreach (var row in items)
        {
            if (row.ItemSearchCategory.RowId > 0 && !row.IsUntradable)
                marketableIds.Add(row.RowId);
            // Trimmed, because FindByName trims what it is asked for; a few rows end in a space.
            var name = row.Name.ToString().Trim();
            if (string.IsNullOrEmpty(name))
                continue;
            // First row wins, except a row that has a recipe replaces one that does not.
            if (!map.TryGetValue(name, out var existing) || (!withRecipe.Contains(existing) && withRecipe.Contains(row.RowId)))
                map[name] = row.RowId;
            if (row.ItemSearchCategory.RowId > 0 && !row.IsUntradable
                && (!market.TryGetValue(name, out var marketExisting) || (!withRecipe.Contains(marketExisting) && withRecipe.Contains(row.RowId))))
                market[name] = row.RowId;
        }

        var list = market.Select(kv => new ItemSearchEntry(kv.Value, kv.Key)).ToList();
        list.Sort((a, b) =>
        {
            var c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.ItemId.CompareTo(b.ItemId);
        });
        return new Index(map, list.AsReadOnly(), marketableIds);
    }
}
