using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using LunaShoppingList.Adapters;
using LunaShoppingList.Core;

namespace LunaShoppingList.Windows;

/// <summary>The results of a Build for one list: per item the total needed and a choice of worlds, plus the route and the trade-off between worlds and gil.</summary>
public sealed class ResultsWindow : Window
{
    /// <summary>A listing last reviewed longer ago than this is marked stale.</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    private const string Footnote = "Prices are from Universalis, exclude market tax, and may be out of date. This plugin never travels on its own. A click on a world name only asks Lifestream to go there.";
    private const int TopWorlds = 8;
    private const string ReplacesTypedText = "Applies the cheapest plan. Quantities you typed are replaced.";
    private const string OptionReplacesTypedText = "Applies this plan. Quantities you typed are replaced.";
    private static readonly Vector4 Red = new(1f, 0.35f, 0.35f, 1f);
    private static readonly Vector4 Amber = new(1f, 0.75f, 0.2f, 1f);

    /// <summary>The display numbers of one world's offer, computed once per book.</summary>
    private readonly record struct Row(string Units, string Price, string Cost, bool Partial, bool Stale, int UnitsN, int PriceN, long CostN, DateTimeOffset Reviewed);

    /// <summary>
    /// A table's displayed row order for one sort choice: positions into the table's natural rows. The natural rows, the
    /// <see cref="AllocationState"/> and the <see cref="OfferBook"/> are never reordered; only this display list is.
    /// </summary>
    private sealed record SortedOrder(int Column, bool Ascending, RouteSummary? Summary, int[] Order);

    private readonly string key;
    private readonly IItemInfo items;
    private readonly OwnedItemsService owned;
    private readonly LifestreamService lifestream;
    private readonly Action<string> onClosed;

    // The left pane lists items in the order the route visits them (first world first, then down each world's lines), so it reads the
    // same as the route on the right. Items the plan buys nowhere come last, in the book's order. Rebuilt when the route changes, but
    // never while a box is being edited, so an item does not jump away from under the cursor.
    private IReadOnlyList<ItemOffers>? itemOrder;
    private RouteSummary? itemOrderSummary;
    private int itemOrderSignature;
    private readonly Dictionary<uint, string> names = new();
    private readonly Dictionary<uint, bool> openItems = new();
    private readonly HashSet<uint> showAll = new();
    private readonly HashSet<(uint, uint)> pinnedRows = new();

    // Click-to-sort state, per window only (never saved). ImGui keeps each table's chosen column and direction; these remember the
    // order built for it, one entry per item's offers table and one per world's route table. Each table sorts on its own.
    // An order is rebuilt only when the sort choice changes, a new book arrives, a plan option is applied, or (route) the summary
    // changes. A typed quantity does NOT rebuild it, so a row never jumps away from under the box being edited; the new order shows
    // the next time one of those happens.
    private readonly Dictionary<uint, SortedOrder> offerOrders = new();
    private readonly Dictionary<uint, SortedOrder> routeOrders = new();

    private const int VisibleOptionRows = 8;

    private OfferBook book;
    // The note for the items that are not sold on the market board, built once per book instead of every frame.
    private string notOnMarketText = string.Empty;
    private AllocationState state = null!;
    private RouteSummary? summary;
    private Dictionary<uint, Row[]> rows = new();
    private bool refreshNotice;
    private bool dirty = true;
    private bool closedReported;

    // The trade-off control: the options for the current book, the remembered budget (gil), the slider's value in the active unit,
    // and the option currently applied (null once the user edits a quantity by hand).
    private IReadOnlyList<PlanOption> planOptions = Array.Empty<PlanOption>();
    // requestedBudget is what the user asked for (set only by the slider and by choosing a plan option); budget is what is in effect
    // for the current book, Math.Min(requestedBudget, the book's largest extra). A narrower book lowers budget but never the request,
    // so a later wider book restores it.
    private long requestedBudget;
    private long budget;
    private int sliderValue;
    private bool percentUnit = true;
    private PlanOption? applied;
    private bool planOpen = true;
    // Per window, not saved: false hides the left item list so the route has the whole window.
    private bool showItems = true;
    // The last Lifestream result line, shown once near the Route title. Cleared when a new build replaces the results.
    private string lifestreamNote = string.Empty;
    private bool lifestreamAvailable;
    private long lifestreamCheckedMs = long.MinValue;

    /// <summary>
    /// Creates the window. <paramref name="key"/> is the list's key (also used for the window id), <paramref name="onClosed"/> is called
    /// with it when the user closes the window.
    /// </summary>
    internal ResultsWindow(string key, string title, OfferBook book, IReadOnlyList<PlanOption> options, IItemInfo items, OwnedItemsService owned, LifestreamService lifestream, Action<string> onClosed)
        : base(title + "###lsl-results-" + key)
    {
        this.key = key;
        this.items = items;
        this.owned = owned;
        this.lifestream = lifestream;
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
        lifestreamNote = string.Empty;
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
            pick = PlanFrontier.Pick(options, Math.Min(requestedBudget, options.Max(o => o.ExtraGil)));
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
        // First, so a failure here leaves the previous book and state untouched.
        notOnMarketText = c.Book.NotOnMarket.Count > 0 ? NotOnMarketNote.Text(c.Book.NotOnMarket, Name) : string.Empty;
        book = c.Book;
        planOptions = c.Options;
        applied = c.Applied;
        state = c.State;
        rows = c.Rows;
        offerOrders.Clear();
        routeOrders.Clear();
        itemOrder = null;
        if (planOptions.Count > 0)
            budget = Math.Min(requestedBudget, planOptions.Max(o => o.ExtraGil));
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
            for (var j = 0; j < arr.Length; j++)
            {
                var w = item.Worlds[j];
                var units = w.UnitsForSale;
                var lo = w.MinUnitPrice;
                var hi = w.MaxUnitPriceFor(item.Needed);
                var cost = w.CostFor(item.Needed);
                var partial = units < item.Needed;
                arr[j] = new Row(
                    units.ToString("N0"),
                    lo == hi || hi == 0 ? lo.ToString("N0") : $"{lo:N0}-{hi:N0}",
                    partial ? $"{cost:N0} (for {units:N0} of {item.Needed:N0})" : cost.ToString("N0"),
                    partial,
                    now - w.OldestReviewFor(item.Needed) > StaleAfter,
                    units,
                    lo,
                    cost,
                    w.OldestReviewFor(item.Needed));
            }
            map[item.ItemId] = arr;
            if (!openItems.ContainsKey(item.ItemId))
                newOpen[item.ItemId] = false; // every item starts collapsed; the header still says if the plan is short
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
        offerOrders.Clear();
        dirty = true;
    }

    /// <summary>The column and direction the user chose by clicking a header of the current table, or false while unsorted.</summary>
    private static bool TryReadSort(out int column, out bool ascending)
    {
        column = 0;
        ascending = true;
        var specs = ImGui.TableGetSortSpecs();
        if (specs.IsNull || specs.SpecsCount < 1)
            return false;
        var spec = specs.Specs;
        column = spec.ColumnIndex;
        ascending = spec.SortDirection == ImGuiSortDirection.Ascending;
        specs.SpecsDirty = false;
        return true;
    }

    /// <summary>Compares two worlds' rows of one item in the chosen offers column. Buy compares the quantity chosen right now.</summary>
    private Comparison<int>? OfferComparison(ItemOffers item, Row[] itemRows, int column) => column switch
    {
        0 => (a, b) => TableSort.CompareText(item.Worlds[a].WorldName, item.Worlds[b].WorldName),
        1 => (a, b) => itemRows[a].UnitsN.CompareTo(itemRows[b].UnitsN),
        2 => (a, b) => itemRows[a].PriceN.CompareTo(itemRows[b].PriceN),
        3 => (a, b) => itemRows[a].CostN.CompareTo(itemRows[b].CostN),
        // Age (formerly "Seen") sorts by age: ascending is the most recently reviewed first.
        4 => (a, b) => itemRows[b].Reviewed.CompareTo(itemRows[a].Reviewed),
        5 => (a, b) => state.GetQuantity(item.ItemId, item.Worlds[a].WorldId).CompareTo(state.GetQuantity(item.ItemId, item.Worlds[b].WorldId)),
        _ => null,
    };

    private static int[] BuildOrder(int count, Comparison<int>? comparison, bool ascending)
    {
        var natural = new int[count];
        for (var i = 0; i < count; i++)
            natural[i] = i;
        return TableSort.Order(natural, comparison, ascending).ToArray();
    }

    /// <summary>The display order of one item's worlds (positions into item.Worlds), or null for the natural order.</summary>
    private int[]? OfferDisplayOrder(ItemOffers item, Row[] itemRows)
    {
        if (!TryReadSort(out var column, out var ascending))
            return null;
        if (offerOrders.TryGetValue(item.ItemId, out var cached) && cached.Column == column && cached.Ascending == ascending
            && cached.Order.Length == item.Worlds.Count)
            return cached.Order;
        var order = BuildOrder(item.Worlds.Count, OfferComparison(item, itemRows, column), ascending);
        offerOrders[item.ItemId] = new SortedOrder(column, ascending, null, order);
        return order;
    }

    /// <summary>The display order of one world's route lines (positions into w.Lines), or null for the natural order.</summary>
    private int[]? RouteDisplayOrder(RouteSummary s, WorldRoute w)
    {
        if (!TryReadSort(out var column, out var ascending))
        {
            // Unsorted again: forget the old order so the left pane goes back to the natural one with it.
            routeOrders.Remove(w.WorldId);
            return null;
        }
        if (routeOrders.TryGetValue(w.WorldId, out var cached) && cached.Column == column && cached.Ascending == ascending
            && ReferenceEquals(cached.Summary, s) && cached.Order.Length == w.Lines.Count)
            return cached.Order;
        var lines = w.Lines;
        Comparison<int>? comparison = column switch
        {
            0 => (a, b) => TableSort.CompareText(Name(lines[a].ItemId), Name(lines[b].ItemId)),
            1 => (a, b) => lines[a].Quantity.CompareTo(lines[b].Quantity),
            2 => (a, b) => lines[a].MinUnitPrice.CompareTo(lines[b].MinUnitPrice),
            3 => (a, b) => lines[a].Cost.CompareTo(lines[b].Cost),
            _ => null,
        };
        var order = BuildOrder(lines.Count, comparison, ascending);
        routeOrders[w.WorldId] = new SortedOrder(column, ascending, s, order);
        return order;
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
        requestedBudget = option.ExtraGil;
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

        if (showItems)
        {
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
        }

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
        ImGui.TextUnformatted($"Your choices: {Plural.Of(s.WorldCount, "world")} to visit, {s.GrandTotal:N0} gil in total");
        if (s.Unmet.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Red, $"{Plural.Of(s.Unmet.Count, "item")} not fully covered");
        }
        // The checkbox wraps to its own line when the totals leave no room for it, instead of clipping at the window edge.
        SameLineIfFits(ImGui.CalcTextSize("Show item list").X + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.Checkbox("Show item list", ref showItems);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Untick to hide the per-item prices and show only the route.");
    }

    /// <summary>Puts the next widget, <paramref name="nextWidth"/> wide, on the same line when it fits in the window, otherwise leaves it to wrap onto a new line.</summary>
    private static void SameLineIfFits(float nextWidth)
    {
        var style = ImGui.GetStyle();
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        if (ImGui.GetItemRectMax().X + style.ItemSpacing.X + nextWidth > right)
            return;
        ImGui.SameLine();
    }

    private void DrawTradeOff()
    {
        var best = PlanFrontier.Pick(planOptions, budget);
        var label = applied != null
            ? $"Plan options: {Plural.Of(applied.WorldCount, "world")}, +{applied.ExtraGil:N0} gil ({applied.Total:N0} total)###planOptions"
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
            requestedBudget = SliderToBudget(value);
            budget = Math.Min(requestedBudget, planOptions.Max(o => o.ExtraGil));
            var pick = PlanFrontier.Pick(planOptions, budget);
            if (pick != null && !ReferenceEquals(pick, applied))
                ApplyOption(pick);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Moving this applies the best plan within that budget. Quantities you typed are replaced.");
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
            DrawGreenBox($"Fewest worlds within that: {Plural.Of(best.WorldCount, "world")}, +{best.ExtraGil:N0} gil ({best.Total:N0} total)");

        DrawOptionList();

        if (ImGui.Button("Reset to cheapest###resetCheapest"))
            SetBudgetFromOption(planOptions[0]);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(ReplacesTypedText);
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
                    if (hovered)
                        ImGui.SetTooltip(i == 0 ? ReplacesTypedText : OptionReplacesTypedText);
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
                    dl.AddText(new Vector2(pos.X + Pad, textY), textColor, Plural.Of(option.WorldCount, "world"));
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

    /// <summary>The items in route order; see <see cref="itemOrder"/>.</summary>
    private IReadOnlyList<ItemOffers> ItemsInRouteOrder()
    {
        var s = CurrentSummary();
        // The route tables may be sorted by the user; the left pane follows what the right shows. Each route table's chosen order is
        // remembered by RouteDisplayOrder when that table draws, so the signature of those orders tells when the right side changed.
        var signature = 0;
        foreach (var o in routeOrders.Values)
            signature = HashCode.Combine(signature, o.Column, o.Ascending, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o.Order));
        if (itemOrder != null && ((ReferenceEquals(itemOrderSummary, s) && signature == itemOrderSignature) || ImGui.IsAnyItemActive()))
            return itemOrder;

        var byId = new Dictionary<uint, ItemOffers>();
        foreach (var it in book.Items)
            byId.TryAdd(it.ItemId, it);

        var ordered = new List<ItemOffers>(book.Items.Count);
        var seen = new HashSet<uint>();
        foreach (var w in s.Worlds)
        {
            var shown = routeOrders.TryGetValue(w.WorldId, out var ro) && ReferenceEquals(ro.Summary, s) && ro.Order.Length == w.Lines.Count ? ro.Order : null;
            for (var k = 0; k < w.Lines.Count; k++)
            {
                var l = w.Lines[shown == null ? k : shown[k]];
                if (byId.TryGetValue(l.ItemId, out var it) && seen.Add(l.ItemId))
                    ordered.Add(it);
            }
        }
        foreach (var it in book.Items)
            if (seen.Add(it.ItemId))
                ordered.Add(it);

        itemOrder = ordered;
        itemOrderSummary = s;
        itemOrderSignature = signature;
        return ordered;
    }

    private void DrawItems()
    {
        foreach (var item in ItemsInRouteOrder())
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
            ImGui.TextUnformatted("Skipped (you unticked these)");
            foreach (var s in book.Skipped)
                ImGui.BulletText($"{Name(s.ItemId)} x{s.Quantity:N0}");
        }

        // Items that cannot be bought on the market board were not priced; they are not "short" and not in any count above.
        if (book.NotOnMarket.Count > 0)
        {
            ImGui.Separator();
            ImGui.PushTextWrapPos(0f);
            try
            {
                ImGui.TextDisabled(notOnMarketText);
            }
            finally
            {
                ImGui.PopTextWrapPos();
            }
        }

        DrawFootnote();
    }

    /// <summary>The prices disclaimer. Drawn under the item list, or under the route when the item list is hidden, so it is always on screen once.</summary>
    private static void DrawFootnote()
    {
        ImGui.Separator();
        ImGui.TextWrapped(Footnote);
    }

    private void DrawItem(ItemOffers item)
    {
        var allocated = state.Allocated(item.ItemId);
        var unmet = state.Unmet(item.ItemId);
        // "have" is live: what is on you now (bags, crystals, saddlebag), so it moves up as things are bought. "plan buys" is what
        // the current plan would purchase, which is not a purchase until it shows up as "have".
        var have = ListWindow.OnYouTotal(owned.Summarize(item.ItemId));
        var haveText = have is long h ? h.ToString("N0") : "-";
        var plan = unmet > 0
            ? $" - plan buys {allocated:N0}, short by {unmet:N0}"
            : allocated > item.Needed
                ? $" - plan buys {allocated:N0}, over by {allocated - item.Needed:N0}"
                : $" - plan buys {allocated:N0}";
        var status = $" - have {haveText} of {item.Needed:N0}{plan}";

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
            && ImGui.SmallButton(all ? $"Show top {Plural.Of(TopWorlds, "world")}###toggle" : $"Show all {Plural.Of(item.Worlds.Count, "world")}###toggle"))
        {
            if (all)
                showAll.Remove(item.ItemId);
            else
                showAll.Add(item.ItemId);
            all = !all;
        }

        var itemRows = rows[item.ItemId];
        var tableOpen = ImGui.BeginTable("##offers", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate);
        try
        {
            if (!tableOpen)
                return;

            // Fixed columns are as wide as their header and their widest text, so a price range such as
            // "15,150-17,000" is never cut off or run into the next column.
            ImGui.TableSetupColumn("World");
            ImGui.TableSetupColumn("For sale", ImGuiTableColumnFlags.WidthFixed, FitWidth("For sale", itemRows.Select(x => x.Units)));
            ImGui.TableSetupColumn("Unit price", ImGuiTableColumnFlags.WidthFixed, FitWidth("Unit price", itemRows.Select(x => x.Price)));
            ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthFixed, FitWidth("Cost", itemRows.Select(x => x.Cost)));
            ImGui.TableSetupColumn("Age", ImGuiTableColumnFlags.WidthFixed, FitWidth("Age", new[] { "stale" }));
            // The Buy column holds an InputInt, so its width follows the font (the box itself fills the cell), not a fixed pixel count.
            ImGui.TableSetupColumn("Buy here", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 9f);
            // The header row is drawn by hand (what TableHeadersRow does) so the Age header can carry a tooltip.
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            for (var c = 0; c < 6; c++)
            {
                ImGui.TableSetColumnIndex(c);
                ImGui.TableHeader(ImGui.TableGetColumnName(c));
                if (c == 4 && ImGui.IsItemHovered())
                    ImGui.SetTooltip("Marked stale when the oldest listing used is over 24 hours old.");
            }

            // Display order only. Everything below looks the world up by item id and world id, never by displayed position.
            var order = OfferDisplayOrder(item, itemRows);
            for (var k = 0; k < item.Worlds.Count; k++)
            {
                var i = order == null ? k : order[k];
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
                    if (DrawWorldLink(w.WorldName, "go"))
                        lifestreamNote = lifestream.GoTo(w.WorldName);
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
                    ImGui.SetNextItemWidth(-1);
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

    private static string RoutePriceText(RouteLine l)
        => l.MinUnitPrice == l.MaxUnitPrice ? l.MinUnitPrice.ToString("N0") : $"{l.MinUnitPrice:N0}-{l.MaxUnitPrice:N0}";

    /// <summary>
    /// A fixed column width that fits its header (with room for the sort arrow) and the widest of its texts, plus cell padding.
    /// These tables are not resizable, so the width is applied again every frame and follows the data.
    /// </summary>
    private static float FitWidth(string header, IEnumerable<string> texts)
    {
        var widest = ImGui.CalcTextSize(header).X + ImGui.GetFontSize();
        foreach (var t in texts)
            widest = Math.Max(widest, ImGui.CalcTextSize(t).X);
        return widest + ImGui.GetStyle().CellPadding.X * 2;
    }

    /// <summary>
    /// Draws a world name as a link-styled, frameless small button; true when clicked. The click is the only thing that ever asks
    /// Lifestream to travel. <paramref name="idPrefix"/> keeps the id apart between the item tables and the route panel.
    /// </summary>
    private bool DrawWorldLink(string world, string idPrefix)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 1f, 1f, 0.08f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 1f, 1f, 0.16f));
        ImGui.PushStyleColor(ImGuiCol.Text, LinkBlue);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0f, 0f));
        var clicked = ImGui.SmallButton($"{world}##{idPrefix}go");
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            // The plugin list is read at most every few seconds, never every frame.
            var now = Environment.TickCount64;
            if (now - lifestreamCheckedMs > 5000)
            {
                lifestreamAvailable = lifestream.IsAvailable();
                lifestreamCheckedMs = now;
            }
            ImGui.SetTooltip(lifestreamAvailable
                ? $"Ask Lifestream to take you to {world}"
                : $"Ask Lifestream to take you to {world}\nLifestream must be installed (and loaded) for this to work.");
        }
        return clicked;
    }

    private static readonly Vector4 LinkBlue = new(0.45f, 0.7f, 1f, 1f);

    private void DrawRoute()
    {
        var s = CurrentSummary();
        ImGui.TextUnformatted("Route");
        if (lifestreamNote.Length > 0)
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextDisabled(lifestreamNote);
            ImGui.PopTextWrapPos();
        }
        ImGui.Separator();

        foreach (var w in s.Worlds)
        {
            // The arrow and the empty-labelled header toggle the section; the world name is a separate link drawn over the header's row
            // (AllowOverlap lets that later widget take its own clicks), then the plain rest of the old header text.
            var rowX = ImGui.GetCursorPosX();
            var open = ImGui.CollapsingHeader($"##route{w.WorldId}", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.AllowItemOverlap);
            ImGui.SameLine();
            ImGui.SetCursorPosX(rowX + ImGui.GetTreeNodeToLabelSpacing());
            if (DrawWorldLink(w.WorldName, "route"))
                lifestreamNote = lifestream.GoTo(w.WorldName);
            ImGui.SameLine(0, 0);
            ImGui.TextUnformatted($" - {Plural.Of(w.Lines.Count, "item")} - {w.Total:N0} gil");
            if (!open)
                continue;
            // The columns can be dragged by their borders. They start as wide as their longest text, and a resizable table keeps
            // its first widths, so the widths are part of the table id: when the data needs different widths the table starts over.
            var qtyWidth = FitWidth("Qty", w.Lines.Select(l => l.Quantity.ToString("N0")));
            var priceWidth = FitWidth("Price", w.Lines.Select(RoutePriceText));
            var costWidth = FitWidth("Cost", w.Lines.Select(l => l.Cost.ToString("N0")));
            var routeOpen = ImGui.BeginTable($"##route{w.WorldId}_{(int)qtyWidth}_{(int)priceWidth}_{(int)costWidth}", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate);
            try
            {
                if (!routeOpen)
                    continue;
                ImGui.TableSetupColumn("Item");
                ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, qtyWidth);
                ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthFixed, priceWidth);
                ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthFixed, costWidth);
                ImGui.TableHeadersRow();
                var order = RouteDisplayOrder(s, w);
                for (var k = 0; k < w.Lines.Count; k++)
                {
                    var l = w.Lines[order == null ? k : order[k]];
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
                    ImGui.TextUnformatted(RoutePriceText(l));
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
        ImGui.TextUnformatted($"Worlds to visit: {s.WorldCount:N0}");
        if (!showItems)
            DrawFootnote();
    }
}
