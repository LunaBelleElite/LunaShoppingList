namespace LunaShoppingList.Core;

/// <summary>What kind of vendor sells an item.</summary>
public enum ItemSourceKind
{
    /// <summary>An NPC that sells for gil.</summary>
    GilVendor,

    /// <summary>An NPC that sells for another item used as currency (gemstones, tomestones, scrips, MGP and the like).</summary>
    CurrencyVendor,

    /// <summary>A Grand Company quartermaster that sells for company seals.</summary>
    CompanySeals,
}

/// <summary>One vendor that sells an item.</summary>
/// <param name="Kind">Kind of vendor.</param>
/// <param name="CurrencyName">Display name of what is paid ("gil" for gil vendors).</param>
/// <param name="CurrencyItemId">Item row id of what is paid (1, the Gil item, for gil vendors).</param>
/// <param name="Cost">Amount of the currency paid per purchase.</param>
/// <param name="NpcName">Vendor name; empty if the shop could not be tied to an NPC (the game-data adapter leaves such shops out).</param>
/// <param name="Zone">Zone name; empty if the NPC's place is unknown, which is the case for most vendor NPCs.</param>
/// <param name="X">Map X coordinate; 0 if unknown.</param>
/// <param name="Y">Map Y coordinate; 0 if unknown.</param>
public sealed record ItemSource(ItemSourceKind Kind, string CurrencyName, uint CurrencyItemId, long Cost, string NpcName, string Zone, float X, float Y);
