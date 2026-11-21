using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using LunaShoppingList.Core;

namespace LunaShoppingList.Windows;

/// <summary>The results of a Build for one list: per item the total needed and a choice of worlds, plus the route and the two suggestions.</summary>
public sealed class ResultsWindow : Window
{
    /// <summary>A listing last reviewed longer ago than this is marked stale.</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    private const string Footnote = "Prices are from Universalis, exclude market tax, and may be out of date. This plugin does not travel for you.";
    private const int TopWorlds = 8;
    private const int OpenFirstItems = 5;
    private static readonly Vector4 Red = new(1f, 0.35f, 0.35f, 1f);
    private static readonly Vector4 Amber = new(1f, 0.75f, 0.2f, 1f);

    /// <summary>The display numbers of one world's offer, computed once per book.</summary>
    private readonly record struct Row(string Units, string Price, string Cost, bool Partial, bool Stale);

    private readonly string key;
    private readonly IItemInfo items;
    private readonly Action<string> onClosed;
    private readonly Dictionary<uint, string> names = new();
    private readonly Dictionary<uint, bool> openItems = new();
    private readonly HashSet<uint> showAll = new();
    private readonly HashSet<(uint, uint)> pinnedRows = new();

    private OfferBook book;
    private AllocationState state = null!;
    private IReadOnlyList<AllocationLine> cheapestLines = Array.Empty<AllocationLine>();
    private IReadOnlyList<AllocationLine> fewestLines = Array.Empty<AllocationLine>();
    private SuggestionComparison comparison = null!;
    private RouteSummary? summary;
    private Dictionary<uint, Row[]> rows = new();
    private SuggestionMode lastMode = SuggestionMode.Cheapest;
    private bool refreshNotice;
    private bool dirty = true;
    private bool closedReported;

    /// <summary>
    /// Creates the window. <paramref name="key"/> is the list's key (also used for the window id), <paramref name="onClosed"/> is called
    /// with it when the user closes the window.
    /// </summary>
    internal ResultsWindow(string key, string title, OfferBook book, IItemInfo items, Action<string> onClosed)
        : base(title + "###lsl-results-" + key)
    {
        this.key = key;
        this.items = items;
        this.onClosed = onClosed;
        this.book = book;
        Size = new Vector2(900, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
        // Escape must not close the window while the user is typing in a quantity box.
        RespectCloseHotkey = false;
        Commit(Compute(book));
    }

    /// <summary>
    /// Replaces the offers shown (a new Build for the same list). The user's manual changes are reset and the plan they last applied
    /// (the cheapest plan until they press a plan button) is applied again to the new offers; a notice says so until they edit a
    /// quantity or press a plan button.
    /// </summary>
    internal void Update(OfferBook book)
    {
        // Everything is computed first; if that throws, the previous book, rows and state stay as they were.
        Commit(Compute(book));
        refreshNotice = true;
    }

    /// <summary>Everything derived from one book, computed without touching the window's fields.</summary>
    private sealed record Computed(
        OfferBook Book,
        AllocationState State,
        SuggestionComparison Comparison,
        IReadOnlyList<AllocationLine> Cheapest,
        IReadOnlyList<AllocationLine> Fewest,
        Dictionary<uint, Row[]> Rows,
        Dictionary<uint, bool> OpenItems,
        HashSet<uint> Present);

    private Computed Compute(OfferBook b)
    {
        var cheapest = Suggester.Suggest(b, SuggestionMode.Cheapest);
        var fewest = Suggester.Suggest(b, SuggestionMode.FewestWorlds);
        var cmp = RouteSummarizer.Compare(b, cheapest, fewest);
        var s = new AllocationState(b);
        s.Apply(lastMode == SuggestionMode.FewestWorlds ? fewest : cheapest);
        var (map, open, present) = PrecomputeRows(b);
        return new Computed(b, s, cmp, cheapest, fewest, map, open, present);
    }

    private void Commit(Computed c)
    {
        book = c.Book;
        state = c.State;
        comparison = c.Comparison;
        cheapestLines = c.Cheapest;
        fewestLines = c.Fewest;
        rows = c.Rows;
        foreach (var id in openItems.Keys.Where(id => !c.Present.Contains(id)).ToList())
            openItems.Remove(id);
        foreach (var (id, open) in c.OpenItems)
            openItems.TryAdd(id, open);
        showAll.RemoveWhere(id => !c.Present.Contains(id));
        pinnedRows.Clear();
        summary = null;
        dirty = true;
    }

    private (Dictionary<uint, Row[]> Rows, Dictionary<uint, bool> NewOpen, HashSet<uint> Present) PrecomputeRows(OfferBook b)
    {
        var now = DateTimeOffset.UtcNow;
        var map = new Dictionary<uint, Row[]>();
        var newOpen = new Dictionary<uint, bool>();
        var present = new HashSet<uint>();
        for (var i = 0; i < b.Items.Count; i++)
        {
            var item = b.Items[i];
            present.Add(item.ItemId);
            var arr = new Row[item.Worlds.Count];
            long total = 0;
            for (var j = 0; j < arr.Length; j++)
            {
                var w = item.Worlds[j];
                var units = w.UnitsForSale;
                total += units;
                var lo = w.MinUnitPrice;
                var hi = w.MaxUnitPriceFor(item.Needed);
                var cost = w.CostFor(item.Needed);
                var partial = units < item.Needed;
                arr[j] = new Row(
                    units.ToString("N0"),
                    lo == hi || hi == 0 ? lo.ToString("N0") : $"{lo:N0}-{hi:N0}",
                    partial ? $"{cost:N0} (for {units:N0} of {item.Needed:N0})" : cost.ToString("N0"),
                    partial,
                    now - w.OldestReviewFor(item.Needed) > StaleAfter);
            }
            map[item.ItemId] = arr;
            if (!openItems.ContainsKey(item.ItemId))
                newOpen[item.ItemId] = i < OpenFirstItems || arr.Length == 0 || total < item.Needed;
        }
        return (map, newOpen, present);
    }

    /// <inheritdoc />
    public override void OnClose()
    {
        if (closedReported)
            return;
        closedReported = true;
        onClosed(key);
    }

    private string Name(uint itemId)
    {
        if (!names.TryGetValue(itemId, out var n))
            names[itemId] = n = items.GetName(itemId);
        return n;
    }

    private RouteSummary CurrentSummary()
    {
        if (dirty || summary == null)
        {
            summary = state.Summarize();
            dirty = false;
        }
        return summary;
    }

    private void Apply(SuggestionMode mode)
    {
        lastMode = mode;
        refreshNotice = false;
        state.Apply(mode == SuggestionMode.FewestWorlds ? fewestLines : cheapestLines);
        dirty = true;
    }

    /// <inheritdoc />
    public override void Draw()
    {
        DrawHeader();
        ImGui.Separator();

        var avail = ImGui.GetContentRegionAvail();
        var height = Math.Max(avail.Y, ImGui.GetTextLineHeightWithSpacing() * 8);
        var leftWidth = Math.Max(avail.X * 0.62f, 200f);

        try
        {
            if (ImGui.BeginChild("##items", new Vector2(leftWidth, height), true))
                DrawItems();
        }
        finally
        {
            ImGui.EndChild();
        }

        ImGui.SameLine();

        try
        {
            if (ImGui.BeginChild("##route", new Vector2(-1, height), true))
                DrawRoute();
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private void DrawHeader()
    {
        var c = comparison.Cheapest;
        var f = comparison.FewestWorlds;

        if (refreshNotice)
        {
            var modeText = lastMode == SuggestionMode.FewestWorlds ? "fewest-worlds" : "cheapest";
            ImGui.TextDisabled($"Prices were refreshed; the {modeText} plan was re-applied. Manual changes were reset.");
        }

        if (ImGui.Button($"Use cheapest plan ({c.WorldCount} worlds, {c.GrandTotal:N0} gil)###useCheapest"))
            Apply(SuggestionMode.Cheapest);

        ImGui.SameLine();

        var extra = comparison.ExtraGilForFewestWorlds;
        var saved = comparison.WorldsSaved;
        var extraText = extra > 0 ? $"+{extra:N0} gil" : extra == 0 ? "no extra cost" : $"{-extra:N0} gil cheaper";
        var savedText = saved > 0 ? $"saves {saved:N0} worlds" : saved == 0 ? "same number of worlds" : $"{-saved:N0} more worlds";
        if (ImGui.Button($"Use fewest-worlds plan ({f.WorldCount} worlds, {extraText}, {savedText})###useFewest"))
            Apply(SuggestionMode.FewestWorlds);

        var s = CurrentSummary();
        ImGui.TextUnformatted($"Your choices: {s.WorldCount} worlds to visit, {s.GrandTotal:N0} gil in total");
        if (s.Unmet.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Red, $"{s.Unmet.Count} items not fully covered");
        }
    }

    private void DrawItems()
    {
        foreach (var item in book.Items)
        {
            ImGui.PushID((int)item.ItemId);
            try
            {
                DrawItem(item);
            }
            finally
            {
                ImGui.PopID();
            }
            ImGui.Spacing();
        }

        if (book.Skipped.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("Skipped (you excluded these)");
            foreach (var s in book.Skipped)
                ImGui.BulletText($"{Name(s.ItemId)} x{s.Quantity:N0}");
        }

        ImGui.Separator();
        ImGui.TextWrapped(Footnote);
    }

    private void DrawItem(ItemOffers item)
    {
        var allocated = state.Allocated(item.ItemId);
        var unmet = state.Unmet(item.ItemId);
        var status = unmet > 0
            ? $" - bought {allocated:N0} of {item.Needed:N0}, short by {unmet:N0}"
            : allocated > item.Needed
                ? $" - bought {allocated:N0} of {item.Needed:N0}, over by {allocated - item.Needed:N0}"
                : $" - bought {allocated:N0} of {item.Needed:N0}";

        ImGui.SetNextItemOpen(openItems.TryGetValue(item.ItemId, out var wasOpen) && wasOpen);
        var open = ImGui.CollapsingHeader($"{Name(item.ItemId)} - need {item.Needed:N0}{status}###item{item.ItemId}");
        openItems[item.ItemId] = open;
        if (!open)
            return;

        if (item.Worlds.Count == 0)
        {
            ImGui.TextDisabled("No listings in your scope");
            return;
        }

        var all = showAll.Contains(item.ItemId);
        if (item.Worlds.Count > TopWorlds
            && ImGui.SmallButton(all ? $"Show top {TopWorlds} worlds###toggle" : $"Show all {item.Worlds.Count} worlds###toggle"))
        {
            if (all)
                showAll.Remove(item.ItemId);
            else
                showAll.Add(item.ItemId);
            all = !all;
        }

        var itemRows = rows[item.ItemId];
        var tableOpen = ImGui.BeginTable("##offers", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        try
        {
            if (!tableOpen)
                return;

            ImGui.TableSetupColumn("World");
            ImGui.TableSetupColumn("For sale", ImGuiTableColumnFlags.WidthFixed, 65);
            ImGui.TableSetupColumn("Unit price", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthFixed, 170);
            ImGui.TableSetupColumn("Seen", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Buy", ImGuiTableColumnFlags.WidthFixed, 130);
            ImGui.TableHeadersRow();

            for (var i = 0; i < item.Worlds.Count; i++)
            {
                var w = item.Worlds[i];
                var qty = state.GetQuantity(item.ItemId, w.WorldId);
                if (qty > 0)
                    pinnedRows.Add((item.ItemId, w.WorldId));
                if (!all && i >= TopWorlds && !pinnedRows.Contains((item.ItemId, w.WorldId)))
                    continue;

                var r = itemRows[i];
                ImGui.PushID((int)w.WorldId);
                try
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(w.WorldName);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(r.Units);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(r.Price);
                    ImGui.TableNextColumn();
                    if (r.Partial)
                        ImGui.TextColored(Amber, r.Cost);
                    else
                        ImGui.TextUnformatted(r.Cost);
                    ImGui.TableNextColumn();
                    if (r.Stale)
                        ImGui.TextColored(Amber, "stale");
                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.InputInt("##buy", ref qty, 1, 10))
                    {
                        state.SetQuantity(item.ItemId, w.WorldId, qty);
                        pinnedRows.Add((item.ItemId, w.WorldId));
                        refreshNotice = false;
                        dirty = true;
                    }
                }
                finally
                {
                    ImGui.PopID();
                }
            }
        }
        finally
        {
            if (tableOpen)
                ImGui.EndTable();
        }
    }

    private void DrawRoute()
    {
        var s = CurrentSummary();
        ImGui.TextUnformatted("Route");
        ImGui.Separator();

        foreach (var w in s.Worlds)
        {
            if (!ImGui.CollapsingHeader($"{w.WorldName} - {w.Lines.Count} items - {w.Total:N0} gil###route{w.WorldId}", ImGuiTreeNodeFlags.DefaultOpen))
                continue;
            var routeOpen = ImGui.BeginTable($"##route{w.WorldId}", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
            try
            {
                if (!routeOpen)
                    continue;
                ImGui.TableSetupColumn("Item");
                ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 45);
                ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthFixed, 85);
                ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthFixed, 80);
                ImGui.TableHeadersRow();
                foreach (var l in w.Lines)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(Name(l.ItemId));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(l.Quantity.ToString("N0"));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(l.MinUnitPrice == l.MaxUnitPrice ? l.MinUnitPrice.ToString("N0") : $"{l.MinUnitPrice:N0}-{l.MaxUnitPrice:N0}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(l.Cost.ToString("N0"));
                }
            }
            finally
            {
                if (routeOpen)
                    ImGui.EndTable();
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted($"Grand total: {s.GrandTotal:N0} gil");
        ImGui.TextUnformatted($"Worlds to visit: {s.WorldCount}");
    }
}
