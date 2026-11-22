using System.Diagnostics;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using LunaShoppingList.Core;

namespace LunaShoppingList.Adapters;

/// <summary>
/// Vendor sources of items (gil shops, currency shops, Grand Company seal shops), read from the game's sheets.
/// Only shops some NPC is found to sell from are listed; most NPCs have no Level row, so many sources have a name but no zone.
/// </summary>
public sealed class LuminaItemSources : IItemSources, IDisposable
{
    /// <summary>Builds tried before giving up for the session.</summary>
    internal const int MaxAttempts = 3;

    private const long RetryDelayMs = 10_000;

    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly CancellationTokenSource cancel = new();

    // Guards the fields below; the log is only written while holding it and never after Dispose.
    private readonly object gate = new();
    private volatile Dictionary<uint, ItemSource[]>? index;
    private volatile bool failed;
    private bool disposed;
    private Task? warmUp;
    private long lastStartedAt;
    private int attempts;

    public LuminaItemSources(IDataManager data, IPluginLog log)
    {
        this.data = data;
        this.log = log;
    }

    /// <summary>
    /// True once the vendor index is built. While it is not, this starts (or, after a failure, retries up to
    /// <see cref="MaxAttempts"/> times) building it in the background, so callers on the game's threads never wait for it.
    /// </summary>
    public bool IsReady
    {
        get
        {
            if (index != null)
                return true;
            WarmUp();
            return false;
        }
    }

    /// <inheritdoc />
    public bool HasFailed => failed;

    public IReadOnlyList<ItemSource> For(uint itemId)
    {
        var built = index;
        if (built == null)
        {
            WarmUp();
            return Array.Empty<ItemSource>();
        }
        return built.TryGetValue(itemId, out var sources) ? sources : Array.Empty<ItemSource>();
    }

    /// <summary>
    /// Builds the index on the thread pool unless it is built, being built, has failed for good, was disposed,
    /// or the last attempt started only moments ago.
    /// </summary>
    internal void WarmUp()
    {
        lock (gate)
        {
            if (index != null || failed || disposed || warmUp is { IsCompleted: false })
                return;
            if (attempts > 0 && Environment.TickCount64 - lastStartedAt < RetryDelayMs)
                return;
            attempts++;
            lastStartedAt = Environment.TickCount64;
            var attempt = attempts;
            var token = cancel.Token;
            warmUp = Task.Run(() => Run(attempt, token));
        }
    }

    private void Run(int attempt, CancellationToken token)
    {
        try
        {
            var built = ItemSourceIndexBuilder.Build(data.Excel, token, out var c);
            lock (gate)
            {
                if (disposed)
                    return;
                index = built;
                log.Information(
                    "Vendor index ready in {Ms} ms: {Items} items; {Gil} gil, {Currency} currency and {Seals} seal sources; " +
                    "{DroppedOffers} offers in {ShopsWithoutNpc} of {Shops} shops dropped (no NPC found selling them); " +
                    "{NpcsWithoutPlace} of {Npcs} NPCs have no zone; " +
                    "{FreeGil} gil shop entries with no price and {NoCost} special shop entries with no cost skipped",
                    c.ElapsedMs, c.Items, c.GilSources, c.CurrencySources, c.SealSources,
                    c.DroppedOffers, c.ShopsWithoutNpc, c.Shops, c.NpcsWithoutPlace, c.Npcs, c.FreeGilEntries, c.NoCostEntries);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Unloading: nothing to report.
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                if (disposed)
                    return;
                if (attempt >= MaxAttempts)
                {
                    failed = true;
                    log.Error(ex, "Building the vendor index failed {Attempts} times; vendor sources are not shown this session", attempt);
                }
                else
                {
                    log.Warning(ex, "Building the vendor index failed (attempt {Attempt} of {Max}); it will be tried again", attempt, MaxAttempts);
                }
            }
        }
    }

    /// <summary>Stops a running build at its next step and makes sure nothing more is logged.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            cancel.Cancel();
        }
    }
}


/// <summary>Builds the item id to vendor sources index from the game's sheets. Touches only Lumina and Core.</summary>
internal static class ItemSourceIndexBuilder
{
    /// <summary>What the build found, for the log line.</summary>
    internal sealed record Counts(
        long ElapsedMs, int Items, int GilSources, int CurrencySources, int SealSources,
        int Shops, int ShopsWithoutNpc, int DroppedOffers, int Npcs, int NpcsWithoutPlace, int FreeGilEntries, int NoCostEntries);

    private readonly record struct Offer(uint ItemId, ItemSourceKind Kind, uint CurrencyId, string CurrencyName, long Cost);

    private readonly record struct Place(string Zone, float X, float Y);

    private const uint GilItemId = 1;

    // SpecialShop cost types: 2 = the cost id is a tomestone slot (TomestonesItem.Tomestones), 3 = a scrip slot.
    private const byte TomestoneCostType = 2;
    private const byte ScripCostType = 3;

    // Scrip slots have no sheet mapping them to items; these four are the only slots shop costs use, and the shops
    // using them are named for crafters' (2, 6) and gatherers' (4, 7) scrips. Purple and orange respectively.
    private static readonly Dictionary<uint, uint> ScripSlotItems = new() { [2] = 33913, [4] = 33914, [6] = 41784, [7] = 41785 };

    // Grand Company seals are items 20, 21 and 22 for companies 1, 2 and 3.
    private const uint SealItemBase = 19;

    // Level rows of this type place an event NPC (Object is an ENpcBase row).
    private const byte LevelTypeEventNpc = 8;

    // Shop links can pass through TopicSelect, PreHandler, CustomTalk (and its nest handlers) and InclusionShop; nesting deeper than this is not followed.
    private const int MaxLinkDepth = 4;

    /// <summary>
    /// Builds the index. Offers in shops no NPC is found to sell from (test, retired, festival or otherwise unreachable shops)
    /// are dropped; NPCs with no Level row are kept with no zone. Stops with <see cref="OperationCanceledException"/> between steps
    /// once <paramref name="cancel"/> is set.
    /// </summary>
    public static Dictionary<uint, ItemSource[]> Build(ExcelModule excel, CancellationToken cancel, out Counts counts)
    {
        var clock = Stopwatch.StartNew();
        var items = excel.GetSheet<Item>();
        string ItemName(uint id) => items.TryGetRow(id, out var row) ? row.Name.ToString() : "";

        var offers = new Dictionary<uint, List<Offer>>();
        void AddOffer(uint shopId, Offer offer)
        {
            if (!offers.TryGetValue(shopId, out var list))
                offers[shopId] = list = new List<Offer>();
            list.Add(offer);
        }

        // Gil shops: the GilShopItem row id is the GilShop id; the price is the item's PriceMid.
        var freeGil = 0;
        foreach (var row in excel.GetSubrowSheet<GilShopItem>())
        {
            foreach (var sub in row)
            {
                var itemId = sub.Item.RowId;
                if (itemId == 0)
                    continue;
                var price = items.TryGetRow(itemId, out var item) ? item.PriceMid : 0;
                if (price == 0)
                {
                    freeGil++;
                    continue;
                }
                AddOffer(sub.RowId, new Offer(itemId, ItemSourceKind.GilVendor, GilItemId, "gil", price));
            }
        }

        cancel.ThrowIfCancellationRequested();
        // Special (currency) shops.
        var tomestoneItems = new Dictionary<uint, uint>();
        foreach (var t in excel.GetSheet<TomestonesItem>())
            if (t.Tomestones.RowId != 0 && t.Item.RowId != 0)
                tomestoneItems.TryAdd(t.Tomestones.RowId, t.Item.RowId);

        var noCost = 0;
        foreach (var shop in excel.GetSheet<SpecialShop>())
        {
            foreach (var entry in shop.Item)
            {
                var costs = new List<(uint Id, string Name, long Cost)>();
                foreach (var cost in entry.ItemCosts)
                {
                    if (cost.ItemCost.RowId == 0 || cost.CurrencyCost == 0)
                        continue;
                    var id = CurrencyItem(cost.ItemCost.RowId, cost.CostType, tomestoneItems);
                    var name = id == 0 ? "" : ItemName(id);
                    costs.Add((id, name.Length > 0 ? name : $"currency #{cost.ItemCost.RowId}", cost.CurrencyCost));
                }

                var receivesAny = false;
                foreach (var receive in entry.ReceiveItems)
                {
                    if (receive.Item.RowId == 0 || receive.ReceiveCount == 0)
                        continue;
                    receivesAny = true;
                    if (costs.Count == 0)
                        continue;
                    var (currencyId, currencyName, amount) = costs[0];
                    var gil = currencyId == GilItemId;
                    if (gil)
                        currencyName = "gil";
                    for (var i = 1; i < costs.Count; i++)
                        currencyName += $" + {ItemSourceSummary.Thousands(costs[i].Cost)} {costs[i].Name}";
                    if (receive.ReceiveCount > 1)
                        currencyName += $" (for {receive.ReceiveCount})";
                    var kind = gil && costs.Count == 1 ? ItemSourceKind.GilVendor : ItemSourceKind.CurrencyVendor;
                    AddOffer(shop.RowId, new Offer(receive.Item.RowId, kind, currencyId, currencyName, amount));
                }
                if (receivesAny && costs.Count == 0)
                    noCost++;
            }
        }

        cancel.ThrowIfCancellationRequested();
        // Grand Company seal shops: items are grouped by category, each category belongs to a company, and every
        // GCShop row of that company sells all of them.
        var categoryCompany = new Dictionary<uint, uint>();
        foreach (var category in excel.GetSheet<GCScripShopCategory>())
            categoryCompany[category.RowId] = category.GrandCompany.RowId;
        var sealOffers = new Dictionary<uint, List<Offer>>();
        foreach (var row in excel.GetSubrowSheet<GCScripShopItem>())
        {
            foreach (var sub in row)
            {
                if (sub.Item.RowId == 0 || sub.CostGCSeals == 0 || !categoryCompany.TryGetValue(sub.RowId, out var company) || company == 0)
                    continue;
                var sealId = SealItemBase + company;
                var sealName = ItemName(sealId);
                if (!sealOffers.TryGetValue(company, out var list))
                    sealOffers[company] = list = new List<Offer>();
                list.Add(new Offer(sub.Item.RowId, ItemSourceKind.CompanySeals, sealId, sealName.Length > 0 ? sealName : "company seals", sub.CostGCSeals));
            }
        }
        foreach (var gcShop in excel.GetSheet<GCShop>())
            if (sealOffers.TryGetValue(gcShop.GrandCompany.RowId, out var list))
                foreach (var offer in list)
                    AddOffer(gcShop.RowId, offer);

        cancel.ThrowIfCancellationRequested();
        // Shop -> NPCs, from each NPC's event data, following menu and pre-handler links.
        var topicSelect = excel.GetSheet<TopicSelect>();
        var preHandler = excel.GetSheet<PreHandler>();
        var customTalk = excel.GetSheet<CustomTalk>();
        var inclusionShop = excel.GetSheet<InclusionShop>();
        var inclusionSeries = excel.GetSubrowSheet<InclusionShopSeries>();
        var nestHandlers = excel.GetSubrowSheet<CustomTalkNestHandlers>();
        var resolved = new Dictionary<uint, List<uint>>();

        void Resolve(uint id, int depth, List<uint> into)
        {
            if (id == 0 || depth > MaxLinkDepth)
                return;
            if (offers.ContainsKey(id))
            {
                if (!into.Contains(id))
                    into.Add(id);
            }
            else if (topicSelect.TryGetRow(id, out var topic))
            {
                foreach (var shop in topic.Shop)
                    Resolve(shop.RowId, depth + 1, into);
            }
            else if (preHandler.TryGetRow(id, out var pre))
            {
                Resolve(pre.Target.RowId, depth + 1, into);
            }
            else if (customTalk.TryGetRow(id, out var talk))
            {
                if (nestHandlers.TryGetRow(talk.SpecialLinks.RowId, out var nest))
                    foreach (var handler in nest)
                        Resolve(handler.NestHandler.RowId, depth + 1, into);
                foreach (var script in talk.Script)
                    if (offers.ContainsKey(script.ScriptArg) || preHandler.HasRow(script.ScriptArg) || topicSelect.HasRow(script.ScriptArg))
                        Resolve(script.ScriptArg, depth + 1, into);
            }
            else if (inclusionShop.TryGetRow(id, out var inclusion))
            {
                foreach (var category in inclusion.Category)
                {
                    if (category.RowId == 0 || !category.IsValid)
                        continue;
                    if (inclusionSeries.TryGetRow(category.Value.InclusionShopSeries.RowId, out var series))
                        foreach (var entry in series)
                            Resolve(entry.SpecialShop.RowId, depth + 1, into);
                }
            }
        }

        var npcsByShop = new Dictionary<uint, List<uint>>();
        void AddNpc(uint shop, uint npcId)
        {
            if (!npcsByShop.TryGetValue(shop, out var npcs))
                npcsByShop[shop] = npcs = new List<uint>();
            if (!npcs.Contains(npcId))
                npcs.Add(npcId);
        }

        foreach (var npc in excel.GetSheet<ENpcBase>())
        {
            foreach (var link in npc.ENpcData)
            {
                var id = link.RowId;
                if (id == 0)
                    continue;
                if (!resolved.TryGetValue(id, out var shops))
                {
                    shops = new List<uint>();
                    Resolve(id, 0, shops);
                    resolved[id] = shops;
                }
                foreach (var shop in shops)
                    AddNpc(shop, npc.RowId);
            }
        }

        // FATE gem traders (Bicolor Gemstone vendors and the like): the FateShop row id is the ENpcBase id.
        foreach (var fate in excel.GetSheet<FateShop>())
        {
            if (fate.RowId == 0)
                continue;
            foreach (var shop in fate.SpecialShop)
                if (shop.RowId != 0 && offers.ContainsKey(shop.RowId))
                    AddNpc(shop.RowId, fate.RowId);
        }

        cancel.ThrowIfCancellationRequested();

        // NPC names and places (the first Level row placing each NPC). Most NPCs are placed only in the zone layout files,
        // which are not read here, so they have no Level row and are listed with a name but no zone.
        var neededNpcs = new HashSet<uint>(npcsByShop.Values.SelectMany(n => n));
        var residents = excel.GetSheet<ENpcResident>();
        var npcNames = new Dictionary<uint, string>();
        foreach (var id in neededNpcs)
            npcNames[id] = residents.TryGetRow(id, out var resident) ? DisplayName(resident.Singular.ToString()) : "";

        var places = new Dictionary<uint, Place>();
        foreach (var level in excel.GetSheet<Level>())
        {
            if (level.Type != LevelTypeEventNpc)
                continue;
            var npcId = level.Object.RowId;
            if (!neededNpcs.Contains(npcId) || places.ContainsKey(npcId) || !level.Map.IsValid)
                continue;
            var map = level.Map.Value;
            var zone = map.PlaceName.ValueNullable?.Name.ToString() ?? "";
            if (zone.Length == 0)
                zone = level.Territory.ValueNullable?.PlaceName.ValueNullable?.Name.ToString() ?? "";
            if (zone.Length == 0)
                continue;
            places[npcId] = new Place(zone,
                MapCoordinates.ToMap(level.X, map.SizeFactor, map.OffsetX),
                MapCoordinates.ToMap(level.Z, map.SizeFactor, map.OffsetY));
        }

        cancel.ThrowIfCancellationRequested();

        // Every offer once per NPC selling it; offers in shops with no NPC are dropped and counted.
        var byItem = new Dictionary<uint, List<ItemSource>>();
        int shopsWithoutNpc = 0, droppedOffers = 0;
        foreach (var (shopId, shopOffers) in offers)
        {
            var npcs = npcsByShop.GetValueOrDefault(shopId);
            if (npcs == null || npcs.Count == 0)
            {
                shopsWithoutNpc++;
                droppedOffers += shopOffers.Count;
                continue;
            }
            foreach (var offer in shopOffers)
            {
                if (!byItem.TryGetValue(offer.ItemId, out var list))
                    byItem[offer.ItemId] = list = new List<ItemSource>();
                foreach (var npcId in npcs)
                {
                    var place = places.GetValueOrDefault(npcId, new Place("", 0, 0));
                    list.Add(new ItemSource(offer.Kind, offer.CurrencyName, offer.CurrencyId, offer.Cost,
                        npcNames.GetValueOrDefault(npcId, ""), place.Zone, place.X, place.Y));
                }
            }
        }

        var result = new Dictionary<uint, ItemSource[]>(byItem.Count);
        int gilSources = 0, currencySources = 0, sealSources = 0;
        foreach (var (itemId, list) in byItem)
        {
            var ordered = ItemSourceSummary.Ordered(list).ToArray();
            result[itemId] = ordered;
            foreach (var s in ordered)
            {
                switch (s.Kind)
                {
                    case ItemSourceKind.GilVendor: gilSources++; break;
                    case ItemSourceKind.CurrencyVendor: currencySources++; break;
                    case ItemSourceKind.CompanySeals: sealSources++; break;
                }
            }
        }

        counts = new Counts(clock.ElapsedMilliseconds, result.Count, gilSources, currencySources, sealSources,
            offers.Count, shopsWithoutNpc, droppedOffers, neededNpcs.Count, neededNpcs.Count(n => !places.ContainsKey(n)), freeGil, noCost);
        return result;
    }

    /// <summary>Item id paid for a special shop cost: the id itself, or the tomestone/scrip behind a slot number (0 if unknown).</summary>
    private static uint CurrencyItem(uint costId, byte costType, Dictionary<uint, uint> tomestoneItems) => costType switch
    {
        TomestoneCostType => tomestoneItems.GetValueOrDefault(costId),
        ScripCostType => ScripSlotItems.GetValueOrDefault(costId),
        _ => costId,
    };

    /// <summary>Generic NPC names are stored in lower case ("material supplier"); the game shows them capitalised.</summary>
    private static string DisplayName(string name)
    {
        if (name.Length == 0 || !char.IsLower(name[0]))
            return name;
        var words = name.Split(' ');
        for (var i = 0; i < words.Length; i++)
            if (words[i].Length > 0)
                words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..];
        return string.Join(' ', words);
    }
}
