using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using LunaShoppingList.Core;

namespace LunaShoppingList.Windows;

/// <summary>The results of a Build for one list: per item the total needed and a choice of worlds, plus the route and the trade-off between worlds and gil.</summary>
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

    private const int VisibleOptionRows = 8;

    private OfferBook book;
    private AllocationState state = null!;
    private RouteSummary? summary;
    private Dictionary<uint, Row[]> rows = new();
    private bool refreshNotice;
    private bool dirty = true;
    private bool closedReported;

    // The trade-off control: the options for the current book, the remembered budget (gil), the slider's value in the active unit,
    // and the option currently applied (null once the user edits a quantity by hand).
    private IReadOnlyList<PlanOption> planOptions = Array.Empty<PlanOption>();
    private long budget;
    private int sliderValue;
    private bool percentUnit = true;
    private PlanOption? applied;
    private bool planOpen = true;

    /// <summary>
    /// Creates the window. <paramref name="key"/> is the list's key (also used for the window id), <paramref name="onClosed"/> is called
    /// with it when the user closes the window.
    /// </summary>
    internal ResultsWindow(string key, string title, OfferBook book, IReadOnlyList<PlanOption> options, IItemInfo items, Action<string> onClosed)
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
        Commit(Compute(book, options));
    }

    /// <summary>
    /// Replaces the offers shown (a new Build for the same list). The user's manual changes are reset and the plan within the
    /// remembered budget is applied again to the new options; a notice says so until they edit a quantity or use the trade-off control.
    /// </summary>
    internal void Update(OfferBook book, IReadOnlyList<PlanOption> options)
    {
        // Everything is computed first; if that throws, the previous book, rows and state stay as they were.
        Commit(Compute(book, options));
        refreshNotice = true;
    }

    /// <summary>Everything derived from one book, computed without touching the window's fields.</summary>
    private sealed record Computed(
        OfferBook Book,
        IReadOnlyList<PlanOption> Options,
        PlanOption? Applied,
        AllocationState State,
        Dictionary<uint, Row[]> Rows,
        Dictionary<uint, bool> OpenItems,
        HashSet<uint> Present);

    private Computed Compute(OfferBook b, IReadOnlyList<PlanOption> options)
    {
        var s = new AllocationState(b);
        PlanOption? pick = null;
        if (options.Count > 0)
        {
            pick = PlanFrontier.Pick(options, budget);
            s.Apply(pick!.Lines);
        }
        else
        {
            s.Apply(Suggester.Suggest(b, SuggestionMode.Cheapest));
        }
        var (map, open, present) = PrecomputeRows(b);
        return new Computed(b, options, pick, s, map, open, present);
    }

    private void Commit(Computed c)
    {
        book = c.Book;
        planOptions = c.Options;
        applied = c.Applied;
        state = c.State;
        rows = c.Rows;
        if (planOptions.Count > 0)
            budget = Math.Min(budget, planOptions.Max(o => o.ExtraGil));
        sliderValue = Math.Min(BudgetToSlider(budget), SliderMax());
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

    private void ApplyOption(PlanOption option)
    {
        refreshNotice = false;
        applied = option;
        pinnedRows.Clear();
        state.Apply(option.Lines);
        dirty = true;
    }

    private long CheapestTotal => planOptions.Count > 0 ? planOptions[0].Total : 0;

    private int SliderMax()
    {
        if (planOptions.Count == 0)
            return 1;
        if (percentUnit)
            return (int)Math.Clamp(Math.Ceiling(planOptions.Max(o => o.ExtraPercent)), 1, 100000);
        return (int)Math.Clamp(planOptions.Max(o => o.ExtraGil), 1, int.MaxValue);
    }

    private long SliderToBudget(int value)
        => percentUnit ? (long)Math.Floor(CheapestTotal * (double)value / 100.0) : value;

    private int BudgetToSlider(long gil)
    {
        if (!percentUnit)
            return (int)Math.Clamp(gil, 0, int.MaxValue);
        return CheapestTotal <= 0 ? 0 : (int)Math.Clamp(Math.Round(gil * 100.0 / CheapestTotal), 0, 100000);
    }

    private void SetBudgetFromOption(PlanOption option)
    {
        budget = option.ExtraGil;
        sliderValue = Math.Min(BudgetToSlider(budget), SliderMax());
        ApplyOption(option);
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
        if (refreshNotice)
            ImGui.TextDisabled($"Prices were refreshed; the plan within +{budget:N0} gil was re-applied. Manual changes were reset.");

        if (planOptions.Count == 0)
            ImGui.TextDisabled("Trade-off options are not available for this list.");
        else
            DrawTradeOff();

        var s = CurrentSummary();
        ImGui.TextUnformatted($"Your choices: {s.WorldCount} worlds to visit, {s.GrandTotal:N0} gil in total");
        if (s.Unmet.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Red, $"{s.Unmet.Count} items not fully covered");
        }
    }

    private void DrawTradeOff()
    {
        var best = PlanFrontier.Pick(planOptions, budget);
        var label = applied != null
            ? $"Plan options: {applied.WorldCount:N0} worlds, +{applied.ExtraGil:N0} gil ({applied.Total:N0} total)###planOptions"
            : "Plan options: edited by hand###planOptions";
        ImGui.SetNextItemOpen(planOpen);
        planOpen = ImGui.CollapsingHeader(label);
        if (!planOpen)
            return;

        var style = ImGui.GetStyle();
        var spacing = style.ItemSpacing.X;

        // Row 1: label, slider filling the width, value text, unit switch.
        const string Caption = "Willing to pay extra";
        var valueText = percentUnit ? $"+{sliderValue}% (up to +{budget:N0} gil)" : $"+{budget:N0} gil";
        var valueW = ImGui.CalcTextSize(valueText).X;
        var radioW = ImGui.GetFrameHeight() + style.ItemInnerSpacing.X;
        var unitW = radioW * 2 + ImGui.CalcTextSize("%").X + ImGui.CalcTextSize("gil").X + spacing;
        ImGui.TextDisabled(Caption);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Max(80f, ImGui.GetContentRegionAvail().X - valueW - unitW - spacing * 2));
        var value = sliderValue;
        if (ImGui.SliderInt("##budget", ref value, 0, SliderMax(), "", ImGuiSliderFlags.AlwaysClamp))
        {
            value = Math.Clamp(value, 0, SliderMax());
            sliderValue = value;
            budget = SliderToBudget(value);
            var pick = PlanFrontier.Pick(planOptions, budget);
            if (pick != null && !ReferenceEquals(pick, applied))
                ApplyOption(pick);
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(valueText);
        ImGui.SameLine();
        if (ImGui.RadioButton("%###unitPercent", percentUnit) && !percentUnit)
        {
            percentUnit = true;
            sliderValue = Math.Min(BudgetToSlider(budget), SliderMax());
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("gil###unitGil", !percentUnit) && percentUnit)
        {
            percentUnit = false;
            sliderValue = Math.Min(BudgetToSlider(budget), SliderMax());
        }

        // Row 2: the readout in a green box.
        if (best != null)
            DrawGreenBox($"Fewest worlds within that: {best.WorldCount:N0} worlds, +{best.ExtraGil:N0} gil ({best.Total:N0} total)");

        DrawOptionList();

        if (ImGui.Button("Reset to cheapest###resetCheapest"))
            SetBudgetFromOption(planOptions[0]);
        ImGui.TextDisabled("These are good plans found by a fast search, not proven to be the lowest possible.");
        ImGui.TextDisabled("Your own quantity changes below still override the plan.");
    }

    private static void DrawGreenBox(string text)
    {
        const float Pad = 6f;
        var pos = ImGui.GetCursorScreenPos();
        var local = ImGui.GetCursorPos();
        var width = ImGui.GetContentRegionAvail().X;
        var textH = ImGui.CalcTextSize(text, false, width - Pad * 2).Y;
        var size = new Vector2(width, textH + Pad * 2);
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + size, ImGui.GetColorU32(new Vector4(0.2f, 0.6f, 0.3f, 0.28f)), 4f);
        dl.AddRect(pos, pos + size, ImGui.GetColorU32(new Vector4(0.3f, 0.75f, 0.4f, 0.9f)), 4f, ImDrawFlags.None, 1f);
        ImGui.SetCursorPos(new Vector2(local.X + Pad, local.Y + Pad));
        ImGui.PushTextWrapPos(local.X + width - Pad);
        try
        {
            ImGui.TextUnformatted(text);
        }
        finally
        {
            ImGui.PopTextWrapPos();
        }
        ImGui.SetCursorPos(local);
        ImGui.Dummy(size);
    }

    private void DrawOptionList()
    {
        var style = ImGui.GetStyle();
        var rowH = ImGui.GetTextLineHeight() + 6f;
        var height = Math.Min(planOptions.Count, VisibleOptionRows) * (rowH + style.ItemSpacing.Y) + style.WindowPadding.Y * 2;
        var maxExtra = planOptions.Max(o => o.ExtraGil);
        const float Pad = 8f;
        var leftW = ImGui.CalcTextSize("000 worlds").X;
        var rightW = ImGui.CalcTextSize("+99,999,999 gil").X;
        var accent = ImGui.GetColorU32(ImGuiCol.ButtonActive);
        var selectedBg = ImGui.GetColorU32(ImGuiCol.Header);
        var hoverBg = ImGui.GetColorU32(ImGuiCol.HeaderHovered, 0.5f);
        var textStrong = ImGui.GetColorU32(ImGuiCol.Text);
        var textNormal = ImGui.GetColorU32(ImGuiCol.Text, 0.8f);
        var track = ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 0.35f));
        var fill = ImGui.GetColorU32(ImGuiCol.PlotHistogram);
        try
        {
            if (!ImGui.BeginChild("##options", new Vector2(-1, height), true))
                return;
            for (var i = 0; i < planOptions.Count; i++)
            {
                var option = planOptions[i];
                ImGui.PushID(i);
                try
                {
                    var width = ImGui.GetContentRegionAvail().X;
                    var pos = ImGui.GetCursorScreenPos();
                    if (ImGui.InvisibleButton("##row", new Vector2(width, rowH)))
                        SetBudgetFromOption(option);
                    var selected = ReferenceEquals(option, applied);
                    var hovered = ImGui.IsItemHovered();
                    var dl = ImGui.GetWindowDrawList();
                    var max = pos + new Vector2(width, rowH);
                    if (selected)
                    {
                        dl.AddRectFilled(pos, max, selectedBg, 3f);
                        dl.AddRect(pos, max, accent, 3f, ImDrawFlags.None, 1f);
                    }
                    else if (hovered)
                    {
                        dl.AddRectFilled(pos, max, hoverBg, 3f);
                    }

                    var textY = pos.Y + (rowH - ImGui.GetTextLineHeight()) * 0.5f;
                    var textColor = selected ? textStrong : textNormal;
                    dl.AddText(new Vector2(pos.X + Pad, textY), textColor, $"{option.WorldCount:N0} worlds");
                    var rightText = i == 0 ? "cheapest" : $"+{option.ExtraGil:N0} gil";
                    dl.AddText(new Vector2(pos.X + width - Pad - ImGui.CalcTextSize(rightText).X, textY), textColor, rightText);

                    var barX = pos.X + Pad + leftW + Pad;
                    var barW = Math.Max(20f, width - Pad * 4 - leftW - rightW);
                    var barY = pos.Y + rowH * 0.5f - 3f;
                    dl.AddRectFilled(new Vector2(barX, barY), new Vector2(barX + barW, barY + 6f), track, 3f);
                    if (option.ExtraGil > 0 && maxExtra > 0)
                    {
                        var fillW = Math.Max(2f, barW * (float)option.ExtraGil / maxExtra);
                        dl.AddRectFilled(new Vector2(barX, barY), new Vector2(barX + fillW, barY + 6f), fill, 3f);
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
            ImGui.EndChild();
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
                        applied = null;
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
                    var itemName = Name(l.ItemId);
                    // Clicking the name copies it, so it can be pasted into the market board search.
                    if (ImGui.Selectable($"{itemName}##copy{w.WorldId}_{l.ItemId}"))
                        ImGui.SetClipboardText(itemName);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Click to copy the item name");
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
