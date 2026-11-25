using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace LunaShoppingList.Adapters;

/// <summary>Writes the diagnostic dump (`/lsl dump`) to the Dalamud log and chat.</summary>
public sealed class DiagnosticDump
{
    private readonly IDataManager data;
    private readonly IPlayerState player;
    private readonly IPluginLog log;
    private readonly IChatGui chat;
    private readonly ArtisanListSource artisan;
    private readonly LuminaItemInfo items;

    public DiagnosticDump(IDataManager data, IPlayerState player, IPluginLog log, IChatGui chat, ArtisanListSource artisan, LuminaItemInfo items)
    {
        this.data = data;
        this.player = player;
        this.log = log;
        this.chat = chat;
        this.artisan = artisan;
        this.items = items;
    }

    public void Run()
    {
        try
        {
            Emit("LSL dump begin");
            var worlds = data.GetExcelSheet<World>();

            if (player.IsLoaded && worlds.TryGetRow(player.CurrentWorld.RowId, out var cur))
                Emit("current: " + Describe(cur));
            else
                Emit("current: not logged in");

            // One line per data centre group row, then its worlds grouped on one line.
            var byDc = new SortedDictionary<uint, List<string>>();
            foreach (var w in worlds)
            {
                if (!w.IsPublic || string.IsNullOrEmpty(w.Name.ToString()))
                    continue;
                if (!byDc.TryGetValue(w.DataCenter.RowId, out var names))
                    byDc[w.DataCenter.RowId] = names = [];
                names.Add(w.Name.ToString());
            }
            var dcSheet = data.GetExcelSheet<WorldDCGroupType>();
            foreach (var (dcId, names) in byDc)
            {
                if (dcSheet.TryGetRow(dcId, out var dc))
                    Emit($"DC row {dcId} '{dc.Name}': Region {RefText(dc.Region.RowId, dc.Region.ValueNullable?.Name)}, PvPRegion {RefText(dc.PvPRegion.RowId, dc.PvPRegion.ValueNullable?.Name)}");
                else
                    Emit($"DC row {dcId}: not in the DC sheet");
                names.Sort(StringComparer.Ordinal);
                Emit($"  {names.Count} worlds: {string.Join(", ", names)}");
            }

            // Only for the IPC count; the window keeps its own copy of what it displays.
            artisan.GetLists();
            Emit($"artisan IPC lists: {artisan.IpcListCount}");
            var configNames = artisan.ConfigListNames();
            if (configNames == null)
            {
                Emit("config-file lists: file missing or unreadable");
            }
            else
            {
                Emit($"config-file lists: {configNames.Count}");
                foreach (var listName in configNames.Take(30))
                    Emit("  " + listName);
                if (configNames.Count > 30)
                    Emit($"  ... and {configNames.Count - 30} more");
            }

            foreach (var name in new[] { "Fire Shard", "Fire Crystal", "Fire Cluster" })
            {
                var id = items.FindByName(name);
                var cat = id is { } i ? items.GetUiCategory(i) : null;
                Emit(cat is { } c ? $"{name}: item {id}, UICategory {c.Id} '{c.Name}'" : $"{name}: not found");
            }
            Emit($"crystal category constant: {LuminaItemInfo.CrystalUiCategoryId}");
            Emit("LSL dump end");
        }
        catch (Exception ex)
        {
            log.Error(ex, "LSL dump failed");
            chat.Print("LSL dump failed. See /xllog.");
        }
    }

    private static string RefText(uint rowId, object? name) => $"{rowId} '{name}'";

    private static string Describe(World w)
    {
        var dc = w.DataCenter.ValueNullable;
        return $"{w.RowId} {w.Name} DC row {w.DataCenter.RowId} '{dc?.Name}' World.Region {w.Region}";
    }

    private void Emit(string line)
    {
        log.Information("[LSL] {Line}", line);
        chat.Print(line, "LSL");
    }
}
