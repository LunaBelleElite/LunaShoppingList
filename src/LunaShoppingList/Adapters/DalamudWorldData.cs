using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using LunaShoppingList.Core;

namespace LunaShoppingList.Adapters;

/// <summary>World data from IPlayerState and the World sheet.</summary>
public sealed class DalamudWorldData : IWorldData
{
    private readonly IPlayerState player;
    private readonly ExcelSheet<World> sheet;
    private IReadOnlyList<WorldInfo>? all;

    public DalamudWorldData(IPlayerState player, IDataManager data)
    {
        this.player = player;
        sheet = data.GetExcelSheet<World>();
    }

    public WorldInfo? CurrentWorld
    {
        get
        {
            if (!player.IsLoaded)
                return null;
            var id = player.CurrentWorld.RowId;
            return id == 0 || !sheet.TryGetRow(id, out var row) ? null : ToInfo(row);
        }
    }

    public IReadOnlyList<WorldInfo> AllWorlds
        => all ??= sheet.Where(w => w.RowId != 0 && !string.IsNullOrEmpty(w.Name.ToString())).Select(ToInfo).ToList();

    /// <summary>
    /// THE ONE PLACE deciding which World sheet field fills which WorldInfo field.
    /// The game's World.DataCenter row is its physical data centre group, and the group's name (Aether, Chaos,
    /// Elemental, Materia, ...) is what separates the regions: every world in a North America group shares one
    /// name, a Europe group another, and so on. So that name fills the data centre, the region and the physical
    /// data centre alike, and "same region" means "same group name". The names come from the game data, none are
    /// hard-coded here. Change the mapping here only.
    /// </summary>
    internal static WorldInfo ToInfo(World w)
    {
        var groupName = w.DataCenter.ValueNullable?.Name.ToString() ?? "";
        return new WorldInfo(w.RowId, w.Name.ToString(), groupName, groupName, groupName, w.IsPublic);
    }
}
