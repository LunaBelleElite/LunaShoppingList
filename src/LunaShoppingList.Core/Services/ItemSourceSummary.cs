using System.Globalization;

namespace LunaShoppingList.Core;

/// <summary>Text for the vendor column and its tooltip.</summary>
public static class ItemSourceSummary
{
    /// <summary>Column text while the vendor index is still being built.</summary>
    public const string NotReadyText = "...";

    /// <summary>Column text for an item no vendor sells.</summary>
    public const string NoneText = "-";

    /// <summary>Column text for an item that can be crafted and that no vendor sells.</summary>
    public const string CraftableText = "Craftable";

    /// <summary>
    /// Adds the crafting hint to a vendor column text: a craftable item nothing sells (or whose vendors are not known yet)
    /// reads <see cref="CraftableText"/>, one with vendors reads e.g. "1,200 gil (craftable)"; anything else is unchanged.
    /// </summary>
    /// <param name="vendorText">The text from <see cref="ColumnText(IItemSources, uint)"/>.</param>
    /// <param name="craftable">True when the item has a recipe.</param>
    public static string WithCraftable(string vendorText, bool craftable)
    {
        if (!craftable) return vendorText;
        return vendorText == NoneText || vendorText == NotReadyText ? CraftableText : vendorText + " (craftable)";
    }

    /// <summary>Column text for an item that is a monster drop and that no vendor sells.</summary>
    public const string DropText = "Monster drop";

    /// <summary>
    /// Adds the monster-drop hint to a vendor column text: a drop nothing sells reads <see cref="DropText"/>, one with
    /// vendors reads e.g. "1,200 gil, monster drop"; a drop whose vendors are still loading and any non-drop are unchanged.
    /// </summary>
    /// <param name="text">The text from <see cref="ColumnText(IItemSources, uint)"/>, possibly through <see cref="WithCraftable"/>.</param>
    /// <param name="isDrop">True when the item is on the drop list.</param>
    public static string WithDrop(string text, bool isDrop)
    {
        if (!isDrop || text == NotReadyText) return text;
        return text == NoneText ? DropText : text + ", monster drop";
    }

    /// <summary>Tooltip lines shown before the rest is summarised as '+N more'.</summary>
    public const int DefaultTooltipLines = 10;

    /// <summary>Whole number with thousands separators, e.g. 1,200 (the same in every culture).</summary>
    public static string Thousands(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Removes duplicates (same kind, NPC name, zone, currency id, currency text and cost; coordinates are ignored) and orders the rest:
    /// gil vendors first, cheapest first; then every other currency by currency name (ignoring case), then by cost.
    /// Ties go to sources with a known NPC and zone first, then a known NPC only, then by NPC name and zone. Sources with no NPC or zone are kept.
    /// </summary>
    public static IReadOnlyList<ItemSource> Ordered(IEnumerable<ItemSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var seen = new HashSet<(ItemSourceKind Kind, string Npc, string Zone, uint Currency, string CurrencyName, long Cost)>();
        return sources
            .OrderBy(s => s.Kind == ItemSourceKind.GilVendor ? 0 : 1)
            .ThenBy(s => s.CurrencyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.CurrencyItemId)
            .ThenBy(s => s.Cost)
            .ThenBy(s => s.NpcName.Length == 0 ? 2 : s.Zone.Length == 0 ? 1 : 0)
            .ThenBy(s => s.NpcName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Zone, StringComparer.OrdinalIgnoreCase)
            .Where(s => seen.Add((s.Kind, s.NpcName, s.Zone, s.CurrencyItemId, s.CurrencyName, s.Cost)))
            .ToList();
    }

    /// <summary>
    /// Short column text for an item: <see cref="NotReadyText"/> while the index is being built and
    /// <see cref="NoneText"/> if building it failed (in both cases without asking it for sources), <see cref="NoneText"/> when nothing sells it, otherwise as <see cref="ColumnText(IReadOnlyList{ItemSource})"/>.
    /// </summary>
    public static string ColumnText(IItemSources sources, uint itemId)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.IsReady)
            return ColumnText(sources.For(itemId));
        return sources.HasFailed ? NoneText : NotReadyText;
    }

    /// <summary>
    /// Short column text for a list of sources: <see cref="NoneText"/> for none; otherwise the first source in
    /// <see cref="Ordered"/> order, e.g. "1,200 gil", followed by " +N" when N more distinct sources exist.
    /// </summary>
    public static string ColumnText(IReadOnlyList<ItemSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var ordered = Ordered(sources);
        if (ordered.Count == 0)
            return NoneText;
        var first = CostText(ordered[0]);
        return ordered.Count == 1 ? first : $"{first} +{ordered.Count - 1}";
    }

    /// <summary>Cost and currency, e.g. "1,200 gil" or "120 Bicolor Gemstone".</summary>
    public static string CostText(ItemSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return $"{Thousands(source.Cost)} {source.CurrencyName}";
    }

    /// <summary>
    /// One tooltip line: "cost currency - NPC, Zone (x, y)". The zone is left out when unknown, the coordinates when both
    /// are 0 or the zone is unknown; a source with no NPC reads "vendor unknown".
    /// </summary>
    public static string Line(ItemSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var who = source.NpcName.Length > 0 ? source.NpcName : "vendor unknown";
        var where = "";
        if (source.Zone.Length > 0)
        {
            where = ", " + source.Zone;
            if (source.X != 0 || source.Y != 0)
                where += string.Create(CultureInfo.InvariantCulture, $" ({source.X:0.0}, {source.Y:0.0})");
        }
        return $"{CostText(source)} - {who}{where}";
    }

    /// <summary>The ordered sources cut to at most <paramref name="max"/> lines, with how many were left out.</summary>
    public static (IReadOnlyList<ItemSource> Shown, int More) Capped(IReadOnlyList<ItemSource> ordered, int max)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        if (max < 0)
            max = 0;
        return ordered.Count <= max ? (ordered, 0) : (ordered.Take(max).ToList(), ordered.Count - max);
    }

    /// <summary>"+N more" for a positive count, otherwise empty.</summary>
    public static string MoreText(int more) => more > 0 ? $"+{more} more" : "";
}
