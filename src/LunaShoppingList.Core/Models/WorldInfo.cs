namespace LunaShoppingList.Core;

/// <summary>A game world and where it sits in the data-centre hierarchy.</summary>
/// <param name="Id">World row id.</param>
/// <param name="Name">World name, as used by Universalis.</param>
/// <param name="DataCenter">Logical data centre name (the one shown in the travel UI).</param>
/// <param name="Region">Region name used for world travel grouping (CrossWorld reaches every public world in the same region; compared ordinal ignoring case; a null or blank region makes CrossWorld fall back to the same DataCenter only).</param>
/// <param name="PhysicalDataCenter">Physical data centre name (differs from DataCenter for some worlds); not yet confirmed against the live game.</param>
/// <param name="IsPublic">True if players can normally be on this world (excludes test or internal worlds).</param>
public record WorldInfo(uint Id, string Name, string DataCenter, string Region, string PhysicalDataCenter, bool IsPublic);
