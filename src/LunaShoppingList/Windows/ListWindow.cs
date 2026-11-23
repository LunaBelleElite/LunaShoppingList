using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using LunaShoppingList.Adapters;
using LunaShoppingList.Core;

namespace LunaShoppingList.Windows;

/// <summary>One window per shopping list: ingredients, scope and Build. Pressing Build opens that list's results window.</summary>
public sealed class ListWindow : Window, IDisposable
{
    private const string UnreadableConfigText = "Couldn't read Artisan's config, try again in a moment.";
    private const string NotStoredText = "This list is not stored in Artisan's config (premade/relic lists cannot be read). Use the Import / Export tab instead.";
    private const string CustomListGoneText = "This shopping list no longer exists.";
    private const string CraftedPendingText = "Crafted items will be unticked by themselves once recipe data has loaded.";
    private const string GoalsPendingText = "Materials for the goals are not loaded yet. They will appear by themselves once recipe data has loaded.";
    private const string RecipesFailedText = "Recipe data could not be loaded. Check /xllog, then try again.";
    private const string GoalsPendingBuildTooltip = "Waiting for recipe data. The goals' materials are not loaded yet.";
    private const string FootnoteText = "Prices are from Universalis, exclude market tax, and may be out of date. This plugin never travels on its own. A click on a world name only asks Lifestream to go there.";
    private const string OpenedText = "Opened the results in their own window.";
    private const string TicksChangedText = "Ticks changed while prices were loading. Press Find prices again.";
    private const string NotOnMarketSuffix = " (not sold on the market board)";
    private const string NoRetainerDataText = "Open each retainer once to count what they hold.";
    private const string ListChangedCancelledText = "The list changed, so the running build was cancelled.";
    private const string ListChangedDiscardedText = "The list changed, so the finished build was discarded.";

    private static readonly Vector4 WarningColor = new(1f, 0.6f, 0.3f, 1f);
    private static readonly Vector4 DimColor = new(0.6f, 0.6f, 0.6f, 1f);
    private static readonly Vector4 ErrorColor = new(1f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 EnoughColor = new(0.45f, 0.85f, 0.45f, 1f);

    private readonly string key;
    private string title;
    private readonly int? artisanListId;
    private readonly IReadOnlyList<Requirement>? pastedRequirements;
    private readonly string? customListId;
    private readonly Configuration config;
    private readonly ShoppingSession session;
    private readonly IArtisanListSource artisan;
    private readonly IItemInfo items;
    private readonly IRecipeLookup recipes;
    private readonly IWorldData worlds;
    private readonly ShoppingListService service;
    private readonly OwnedItemsService owned;
    private readonly IItemSources sources;
    private readonly IDropSources drops;
    private readonly IWindowHost host;
    private readonly IPluginLog log;

    // UI-thread state.
    private string sourceMessage = string.Empty;

    // True when the Untick crafted items option was on but the recipe index was not built yet, so nothing was unticked.
    private bool craftedPending;
    // Shown under the ingredient buttons after the Untick crafted items button could not run yet.
    private string craftedButtonNote = string.Empty;
    // True when a custom list has goals but the recipe index was not built yet, so only its plain rows were loaded.
    private bool goalsPending;
    // True once a tick was changed in this window since the list was last loaded; the automatic reload then leaves the rows alone.
    private bool userEdited;
    // Goals whose materials could not be worked out (custom lists only); shown as warning lines.
    private IReadOnlyList<GoalProblem> goalProblems = [];
    private List<Requirement> rows = [];
    private readonly Dictionary<uint, string> names = new();

    // Vendor column text per item, kept once the vendor index is ready so the rows are not rebuilt every frame.
    private readonly Dictionary<uint, VendorCell> vendorText = new();

    /// <summary>One Vendor column cell: its text and the facts the draw code needs. Cached once <see cref="VendorInfoReady"/>.</summary>
    private readonly record struct VendorCell(string Text, bool HasSources, bool Craftable, bool IsDrop);

    // The Vendor column is sized to its widest text so nothing is cut off. The wanted width is measured once per set of
    // rows, vendor index state and font (not every frame).
    private float vendorWantedWidth = -1f;
    private bool vendorCacheHasRecipes;
    private float vendorWantedFontKey;
    private bool vendorWantedReady;

    // Click-to-sort state. Per window only: not saved, gone when the window closes. Sorting changes display order only;
    // session.Requirements and rows are never reordered. Column -1 means no sort, which shows the natural order of rows.
    private const int BuyColumn = 0, ItemColumn = 1, QuantityColumn = 2, OnYouColumn = 3, RetainersColumn = 4, HouseColumn = 5, VendorColumn = 6;
    private const int IngredientColumns = 7;
    private const long OwnedRefreshMs = 1000;
    private int sortColumn = -1;
    private bool sortAscending = true;
    // The rows in display order; null when it must be rebuilt. Rebuilt only when the sort, the rows or the vendor index changed
    // (and about once a second while sorted by a count column, whose numbers move with the inventory).
    private IReadOnlyList<Requirement>? displayRows;
    private bool displayVendorsReady;
    private long displayBuiltAtMs;

    // Shared with the background build task. Writes happen under buildGate; a build publishes its
    // result only while its generation is still current, so a superseded build can never show it.
    private readonly object buildGate = new();
    private int generation;
    private OfferBook? pendingBook;
    private IReadOnlyList<PlanOption>? pendingOptions;
    // UI thread only: the excluded ids the current build was started with.
    private HashSet<uint>? buildExclusions;
    private volatile bool building;
    private volatile string progressText = string.Empty;
    private volatile string errorText = string.Empty;

    // UI thread only. A source is never disposed while its task can still use it: a build that is replaced or
    // reloaded away while still running is retired, and its source is disposed by a later sweep once the task ended.
    private CancellationTokenSource? cts;
    private Task? buildTask;
    private readonly List<(CancellationTokenSource Source, Task Task)> retired = [];

    /// <summary>
    /// Creates the window. Exactly one of <paramref name="artisanListId"/>, <paramref name="pastedRequirements"/> and <paramref name="customListId"/> is set:
    /// an Artisan list is loaded through <paramref name="artisan"/> and its saved exclusions applied; a custom list is read from
    /// the saved settings and its exclusions are written back to them; pasted requirements are
    /// used as given and their choices are not remembered. <paramref name="key"/> is stable and unique per list and is used for the window id.
    /// </summary>
    internal ListWindow(
        string key,
        string title,
        int? artisanListId,
        IReadOnlyList<Requirement>? pastedRequirements,
        string? customListId,
        Configuration config,
        ShoppingSession session,
        IArtisanListSource artisan,
        IItemInfo items,
        IRecipeLookup recipes,
        IWorldData worlds,
        ShoppingListService service,
        OwnedItemsService owned,
        IItemSources sources,
        IDropSources drops,
        IWindowHost host,
        IPluginLog log)
        : base(title + "###lsl-list-" + key)
    {
        if ((artisanListId.HasValue ? 1 : 0) + (pastedRequirements != null ? 1 : 0) + (customListId != null ? 1 : 0) != 1)
            throw new ArgumentException("Exactly one of artisanListId, pastedRequirements and customListId must be set.");

        this.key = key;
        this.title = title;
        this.artisanListId = artisanListId;
        this.pastedRequirements = pastedRequirements;
        this.customListId = customListId;
        this.config = config;
        this.session = session;
        this.artisan = artisan;
        this.items = items;
        this.recipes = recipes;
        this.worlds = worlds;
        this.service = service;
        this.owned = owned;
        this.sources = sources;
        this.drops = drops;
        this.host = host;
        this.log = log;
        Size = new Vector2(520, 560);
        SizeCondition = ImGuiCond.FirstUseEver;

        LoadList();
    }

    /// <summary>Every build task that has not ended yet, the current one and any retired ones. UI thread only.</summary>
    internal Task[] RunningBuilds
    {
        get
        {
            var list = new List<Task>();
            if (buildTask is { IsCompleted: false } current)
                list.Add(current);
            foreach (var (_, task) in retired)
                if (!task.IsCompleted)
                    list.Add(task);
            return list.ToArray();
        }
    }

    /// <summary>
    /// Cancels a running build and makes sure its outcome is never shown. Does not wait for the build to end.
    /// Safe to call more than once: a build still running keeps its source, and a later call after it has ended releases it.
    /// </summary>
    public void Dispose()
    {
        CancelBuild();
        RetireCurrent();
        foreach (var (source, _) in retired)
            CancelSource(source);
        lock (buildGate)
        {
            generation++;
            pendingBook = null;
            pendingOptions = null;
        }
        SweepRetired();
    }

    /// <inheritdoc />
    public override void OnClose()
    {
        // The host queues the removal; the window system is never touched from inside a draw.
        host.CloseList(key);
    }

    /// <inheritdoc />
    public override void Update()
    {
        // Runs every frame the window is open, collapsed or not, so a finished build opens its results
        // even while this window is collapsed.
        PublishPendingResults();

        // Escape closes an Artisan list window only when nothing would be lost: its choices are saved and
        // reopening reloads them. It never closes a window with a build running or a pasted list's choices.
        RespectCloseHotkey = (artisanListId.HasValue || customListId != null) && !building;
    }

    /// <inheritdoc />
    public override void Draw()
    {
        ApplyPendingRecipeData();
        DrawHeader();
        ImGui.Separator();
        DrawScope();
        ImGui.Separator();
        DrawIngredients();
        ImGui.Separator();
        DrawBuild();
        ImGui.Separator();
        ImGui.TextWrapped(FootnoteText);
    }

    // ---- Loading ----

    /// <summary>
    /// When the list was loaded before the recipe index existed, loads it again once the index is ready: one bool read per
    /// frame, and the reload happens once (LoadList clears both pending flags). Skipped while a build runs or after the
    /// user changed a tick, so nothing they did is thrown away; Reload list still does it on request.
    /// </summary>
    private void ApplyPendingRecipeData()
    {
        if (!vendorCacheHasRecipes && recipes is LuminaRecipeData { IsResultIndexReady: true })
        {
            // Cells cached while the index had failed for good have no craftable hint: build them again once it exists.
            vendorCacheHasRecipes = true;
            vendorText.Clear();
            vendorWantedWidth = -1f;
        }
        if (craftedButtonNote.Length > 0 && recipes is LuminaRecipeData { IsResultIndexReady: true })
            craftedButtonNote = string.Empty;
        if ((goalsPending || craftedPending) && !userEdited && !building
            && recipes is LuminaRecipeData { IsResultIndexReady: true })
            LoadList();
    }

    private void LoadList()
    {
        craftedButtonNote = string.Empty;
        userEdited = false;
        sourceMessage = string.Empty;
        craftedPending = false;
        goalsPending = false;
        goalProblems = [];
        try
        {
            if (artisanListId is int id)
            {
                var list = artisan.LoadList(id);
                if (list == null)
                {
                    sourceMessage = NotStoredText;
                }
                else
                {
                    session.LoadFromList(list, config.ExcludedItemsByList.TryGetValue(id, out var saved) ? saved : []);
                    // Order matters: the ticks the player set by hand are known before Untick crafted items runs.
                    session.ImportIncludes(config.IncludedItemsByList.TryGetValue(id, out var included) ? included : []);
                    if (config.ExcludeCraftedItems)
                        ApplyExcludeCrafted();
                }
            }
            else if (customListId != null)
            {
                var saved = config.FindCustomList(customListId);
                if (saved == null)
                {
                    session.LoadFromPaste(new PasteResult([], []));
                    sourceMessage = CustomListGoneText;
                }
                else
                {
                    if (saved.Name != title)
                    {
                        title = saved.Name;
                        WindowName = title + "###lsl-list-" + key;
                    }
                    session.LoadFromPaste(new PasteResult(LoadCustomRequirements(saved), []));
                    session.ImportExclusions(saved.ExcludedItemIds);
                }
            }
            else
            {
                session.LoadFromPaste(new PasteResult(pastedRequirements!, []));
            }
        }
        catch (ArtisanConfigUnreadableException ex)
        {
            log.Warning(ex, "Loading list {Key} failed", key);
            sourceMessage = UnreadableConfigText;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Loading list {Key} failed", key);
            sourceMessage = "Could not load the list: " + ex.Message;
        }
        RefreshRows();
    }

    /// <summary>
    /// The saved list's plain rows plus the materials of its goals. The recipe index is never built here (that would stall
    /// the UI thread): with goals and no index yet this starts the build in the background, loads the plain rows only and
    /// leaves a note; <see cref="ApplyPendingRecipeData"/> loads the list again once the index is ready.
    /// </summary>
    private IReadOnlyList<Requirement> LoadCustomRequirements(CustomList saved)
    {
        if (saved.Goals.Count == 0)
            return CustomListOps.ToRequirements(saved);
        if (recipes is LuminaRecipeData { IsResultIndexReady: false } data)
        {
            data.WarmUp();
            goalsPending = true;
            return CustomListOps.ToRequirements(saved);
        }
        var effective = CustomListOps.EffectiveRequirements(saved, recipes);
        goalProblems = effective.Problems;
        return effective.Requirements;
    }

    /// <summary>
    /// Unticks the ingredients that have a recipe. The recipe index is never built here (that would stall the UI thread):
    /// while it is not ready this only starts building it in the background and leaves a note; <see cref="ApplyPendingRecipeData"/> applies it later.
    /// </summary>
    private void ApplyExcludeCrafted()
    {
        if (recipes is LuminaRecipeData { IsResultIndexReady: false } data)
        {
            data.WarmUp();
            craftedPending = true;
            return;
        }
        session.ExcludeCrafted(recipes);
    }

    /// <summary>
    /// Reads the saved custom list again (name, items and exclusions); a running build is cancelled and its outcome
    /// discarded, and the window says so.
    /// </summary>
    internal void ReloadCustomList()
    {
        var wasBuilding = building;
        bool hadPending;
        lock (buildGate)
            hadPending = pendingBook != null;
        LoadList();
        if (wasBuilding || hadPending)
        {
            lock (buildGate)
                progressText = wasBuilding ? ListChangedCancelledText : ListChangedDiscardedText;
        }
    }

    /// <summary>Takes the saved custom list's new name for the title only; rows and a running build are left alone.</summary>
    internal void RenameCustomList()
    {
        if (customListId == null || config.FindCustomList(customListId) is not { } saved || saved.Name == title)
            return;
        title = saved.Name;
        WindowName = title + "###lsl-list-" + key;
    }

    private void RefreshRows()
    {
        // Crystals, shards and clusters sort to the end; this is purely presentation.
        rows = session.Requirements.OrderBy(r => items.IsCrystalLike(r.ItemId)).ThenBy(r => r.ItemId).ToList();
        names.Clear();
        vendorText.Clear();
        vendorWantedWidth = -1f;
        displayRows = null;
        CancelBuild();
        RetireCurrent();
        lock (buildGate)
        {
            generation++;
            pendingBook = null;
            pendingOptions = null;
            building = false;
            progressText = string.Empty;
            errorText = string.Empty;
        }
    }

    private string Name(uint itemId)
    {
        if (!names.TryGetValue(itemId, out var n))
            names[itemId] = n = items.GetName(itemId);
        return n;
    }

    // ---- Header ----

    private void DrawHeader()
    {
        ImGui.TextUnformatted(title);
        ImGui.TextDisabled(artisanListId.HasValue ? "Source: Artisan list" : customListId != null ? "Source: My shopping list" : "Source: pasted ingredients");
        if (artisanListId.HasValue || customListId != null)
        {
            // Reads the list and its saved choices again; a running build is cancelled and its outcome discarded.
            ImGui.SameLine();
            if (ImGui.SmallButton("Reload list"))
            {
                // An explicit request: also lets a recipe index that failed for good try again.
                if (recipes is LuminaRecipeData data)
                    data.RetryNow();
                LoadList();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Reads this list and your saved ticks again. A running build is cancelled.");
        }
        if (sourceMessage.Length > 0)
            WrappedColored(WarningColor, sourceMessage);
        if (craftedPending || goalsPending)
        {
            if (recipes is LuminaRecipeData { IsResultIndexFailed: true })
                WrappedColored(WarningColor, RecipesFailedText);
            else
            {
                if (craftedPending)
                    WrappedColored(DimColor, CraftedPendingText);
                if (goalsPending)
                    WrappedColored(DimColor, GoalsPendingText);
            }
        }
        foreach (var problem in goalProblems)
            WrappedColored(WarningColor, $"{Name(problem.ItemId)}: {problem.Error}");
    }

    /// <summary>Coloured text that wraps at the window edge instead of being cut off.</summary>
    private static void WrappedColored(Vector4 color, string text)
    {
        ImGui.PushTextWrapPos(0f);
        try
        {
            ImGui.TextColored(color, text);
        }
        finally
        {
            ImGui.PopTextWrapPos();
        }
    }

    // ---- Scope ----

    private void DrawScope()
    {
        ImGui.TextUnformatted("Where to shop");
        var scope = config.LastScope;
        if (ImGui.RadioButton("My region (cross-world)", scope == TravelScopeKind.CrossWorld))
            SetScope(TravelScopeKind.CrossWorld);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Worlds you can reach from a main-city aetheryte.");
        if (ImGui.RadioButton("All data centers", scope == TravelScopeKind.CrossDataCenter))
            SetScope(TravelScopeKind.CrossDataCenter);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Includes other regions. Needs Data Center Travel for those.");
    }

    private void SetScope(TravelScopeKind scope)
    {
        if (config.LastScope == scope) return;
        config.LastScope = scope;
        config.Save();
    }

    // ---- Ingredients ----

    private void DrawIngredients()
    {
        ImGui.TextUnformatted("Ingredients");
        if (rows.Count == 0)
        {
            ImGui.TextDisabled("No ingredients loaded.");
            return;
        }

        var allIncluded = session.AreAllIncluded;
        var tickAllLabel = allIncluded ? "Untick all" : "Tick all";
        if (ImGui.Button(tickAllLabel + "###tickAll"))
        {
            session.ApplyBulk(tickAllLabel, s => s.SetAllIncluded(!allIncluded));
            PersistExclusions();
        }
        SameLineIfFits("Untick items I have enough of");
        if (ImGui.Button("Untick items I have enough of"))
        {
            session.ApplyBulk("Untick items I have enough of", s => s.ExcludeOwned(OwnedEverywhere));
            PersistExclusions();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Unticks every ingredient where what you hold (bags, saddlebag, retainers) already covers the quantity needed, including items you ticked by hand.\nRetainer counts are as of your last visit to each retainer. Items you have not got enough of stay as they are.");
        SameLineIfFits("Untick crafted items");
        if (ImGui.Button("Untick crafted items"))
            ExcludeCraftedNow();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Unticks every ingredient that can be crafted, so only the raw materials are left to buy, including items you ticked by hand.\nCrystals, shards and clusters are not touched.");
        SameLineIfFits("Untick items in my house");
        if (ImGui.Button("Untick items in my house"))
        {
            // Unknown (no house counted yet) leaves the item ticked.
            session.ApplyBulk("Untick items in my house", s => s.ExcludeOwned(id => owned.Summarize(id).House));
            PersistExclusions();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Unticks every ingredient that is already in your house (placed or in the storeroom) in the quantity needed, including items you ticked by hand.\nOpen the Furnishings window once in each house first.\nHouse items are not counted by Untick items I have enough of.");
        SameLineIfFits("Undo");
        ImGui.BeginDisabled(!session.CanUndo);
        try
        {
            if (ImGui.Button("Undo"))
            {
                session.Undo();
                PersistExclusions();
            }
        }
        finally
        {
            ImGui.EndDisabled();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(session.CanUndo
                ? $"Undo \"{session.UndoLabel}\": puts the ticks back as they were."
                : "Nothing to undo. This only undoes the last Tick/Untick button.");
        if (craftedButtonNote.Length > 0)
            WrappedColored(DimColor, craftedButtonNote);

        // The table follows the window: it takes what is left after keeping room for the Build row,
        // its status and error lines, the login warning and the footnote.
        var rowHeight = ImGui.GetTextLineHeightWithSpacing();
        var showRetainerNote = !owned.HasAnyRetainerData && !owned.ExternalAvailable;
        var reserved = ImGui.GetFrameHeightWithSpacing() + rowHeight * (showRetainerNote ? 6 : 5) + ImGui.GetStyle().ItemSpacing.Y * 4;
        var free = ImGui.GetContentRegionAvail().Y - reserved;
        var tableHeight = Math.Max(rowHeight * 7, free);
        var padding = ImGui.GetStyle().CellPadding.X * 2;
        var wantedWidth = VendorWantedWidth(padding);
        var vendorWidth = Math.Min(wantedWidth, Math.Max(VendorColumnWidth(padding), ImGui.GetContentRegionAvail().X * 0.6f));
        // A resizable table keeps the widths from its first frame and no public call sets one later, so the wanted Vendor
        // width is part of the table id: when it changes the table starts over at the new width (and without a saved sort).
        // The id uses the uncapped wanted width (rows, vendor state, font only), so resizing the window never changes it;
        // the capped value only sets the initial column width. The column count is part of it too, so a table saved with fewer columns is never reused.
        if (ImGui.BeginTable($"##ingredients{IngredientColumns}c{(int)wantedWidth}", IngredientColumns, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate, new Vector2(-1, tableHeight)))
        {
            try
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                // Item takes the leftover width; the fixed columns can be dragged wider or narrower by their borders.
                // Their starting widths come from the current font, so they follow the font and UI scale.
                ImGui.TableSetupColumn("Buy", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize,
                    Math.Max(ImGui.CalcTextSize("Buy").X, ImGui.GetFrameHeight()) + padding);
                ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, CountColumnWidth("Qty", padding));
                ImGui.TableSetupColumn("On you", ImGuiTableColumnFlags.WidthFixed, CountColumnWidth("On you", padding));
                ImGui.TableSetupColumn("Retainers", ImGuiTableColumnFlags.WidthFixed, CountColumnWidth("Retainers", padding));
                ImGui.TableSetupColumn("House", ImGuiTableColumnFlags.WidthFixed, CountColumnWidth("House", padding));
                ImGui.TableSetupColumn("Vendor", ImGuiTableColumnFlags.WidthFixed, vendorWidth);
                ImGui.TableHeadersRow();
                // Every widget below is keyed by r.ItemId, never by position, so display order does not matter to it.
                foreach (var r in SortedRows())
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    var included = session.IsIncluded(r.ItemId);
                    if (ImGui.Checkbox($"##inc{r.ItemId}", ref included))
                    {
                        session.SetIncluded(r.ItemId, included);
                        PersistExclusions();
                    }
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(Name(r.ItemId));
                    // A set lookup once the item index is built (no sheet read); unknown items are treated as marketable, so no suffix.
                    if (!service.IsMarketable(r.ItemId))
                    {
                        ImGui.SameLine(0, 0);
                        ImGui.TextDisabled(NotOnMarketSuffix);
                    }
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(r.Quantity.ToString("N0"));

                    // Counts come from a snapshot published by the framework thread; nothing native is read here.
                    var summary = owned.Summarize(r.ItemId);
                    ImGui.TableNextColumn();
                    DrawOnYou(summary, r.Quantity);
                    ImGui.TableNextColumn();
                    DrawRetainers(summary, r.ItemId, r.Quantity);
                    ImGui.TableNextColumn();
                    DrawHouse(summary, r.ItemId, r.Quantity);
                    ImGui.TableNextColumn();
                    DrawVendor(r.ItemId);
                }
            }
            finally
            {
                ImGui.EndTable();
            }
        }
        if (showRetainerNote)
            ImGui.TextDisabled(NoRetainerDataText);
    }

    /// <summary>
    /// The rows in the order the table shows them. Reads the table's sort spec (call between TableHeadersRow and the first row);
    /// the ordered list is cached and rebuilt only when the spec, the rows or the vendor index changed.
    /// </summary>
    private IReadOnlyList<Requirement> SortedRows()
    {
        var specs = ImGui.TableGetSortSpecs();
        if (specs.SpecsDirty)
        {
            if (specs.SpecsCount > 0)
            {
                var spec = specs.Specs[0];
                sortColumn = spec.ColumnIndex;
                sortAscending = spec.SortDirection != ImGuiSortDirection.Descending;
            }
            else
            {
                sortColumn = -1;
            }
            displayRows = null;
            specs.SpecsDirty = false;
        }

        var now = Environment.TickCount64;
        var countColumn = sortColumn is OnYouColumn or RetainersColumn or HouseColumn;
        if (displayRows != null && sortColumn == VendorColumn && sources.IsReady != displayVendorsReady)
            displayRows = null;
        if (displayRows != null && countColumn && now - displayBuiltAtMs >= OwnedRefreshMs)
            displayRows = null;
        if (displayRows != null)
            return displayRows;

        displayBuiltAtMs = now;
        displayVendorsReady = sources.IsReady;
        displayRows = TableSort.Order(rows, BuildComparison(), sortAscending);
        return displayRows;
    }

    /// <summary>Sort keys by item id. A repeated id keeps its first key instead of throwing on the draw thread.</summary>
    private static Dictionary<uint, T> KeysById<T>(IEnumerable<Requirement> source, Func<Requirement, T> key)
    {
        var map = new Dictionary<uint, T>();
        foreach (var r in source)
            map.TryAdd(r.ItemId, key(r));
        return map;
    }

    /// <summary>The comparison for the sorted column, with its sort keys read once per sort rather than once per comparison; null for no sort.</summary>
    private Comparison<Requirement>? BuildComparison()
    {
        switch (sortColumn)
        {
            case BuyColumn:
                // Ticked rows first when ascending. Taken when the sort is made: ticking a row afterwards does not move it.
                var ticked = KeysById(rows, r => session.IsIncluded(r.ItemId) ? 0 : 1);
                return (a, b) => ticked[a.ItemId].CompareTo(ticked[b.ItemId]);
            case ItemColumn:
                return (a, b) => TableSort.CompareText(Name(a.ItemId), Name(b.ItemId));
            case QuantityColumn:
                return (a, b) => a.Quantity.CompareTo(b.Quantity);
            case OnYouColumn:
            {
                // Unknown ('-') sorts lowest.
                var keys = KeysById(rows, r => OnYouTotal(owned.Summarize(r.ItemId)) ?? long.MinValue);
                return (a, b) => keys[a.ItemId].CompareTo(keys[b.ItemId]);
            }
            case RetainersColumn:
            {
                var keys = KeysById(rows, r => owned.Summarize(r.ItemId).Retainers ?? long.MinValue);
                return (a, b) => keys[a.ItemId].CompareTo(keys[b.ItemId]);
            }
            case HouseColumn:
            {
                // Unknown ('-', no house counted yet) sorts lowest.
                var keys = KeysById(rows, r => owned.Summarize(r.ItemId).House ?? long.MinValue);
                return (a, b) => keys[a.ItemId].CompareTo(keys[b.ItemId]);
            }
            case VendorColumn:
            {
                // While the vendor index is not ready every row has no sources, so the order stays as it was.
                var keys = KeysById(rows, r => sources.IsReady ? sources.For(r.ItemId) : []);
                return (a, b) => ItemSourceOrder.Compare(keys[a.ItemId], keys[b.ItemId]);
            }
            default:
                return null;
        }
    }

    /// <summary>A count column wide enough for its header and for a six-digit count, at the current font size.</summary>
    private static float CountColumnWidth(string header, float padding)
        => Math.Max(ImGui.CalcTextSize(header).X, ImGui.CalcTextSize("999,999").X) + padding;

    /// <summary>A vendor column wide enough for its header and for a typical cost such as "1,200 gil +2", and mostly for "120 Bicolor Gemstone".</summary>
    private static float VendorColumnWidth(float padding)
        => Math.Max(ImGui.CalcTextSize("Vendor").X, Math.Max(ImGui.CalcTextSize("1,200 gil +2").X, ImGui.CalcTextSize("120 Bicolor Gemstone").X * 0.9f)) + padding;

    /// <summary>
    /// The Vendor column width: the widest vendor text on the list plus padding, never less than <see cref="VendorColumnWidth"/>,
    /// (the caller caps it to about 60% of the table so Item keeps room). Measured only when the rows, the vendor index state or the
    /// font changed, so it does not depend on the window size.
    /// </summary>
    private float VendorWantedWidth(float padding)
    {
        var minimum = VendorColumnWidth(padding);
        var fontKey = ImGui.CalcTextSize("Vendor").X;
        if (vendorWantedWidth < 0f || vendorWantedReady != VendorInfoReady || Math.Abs(fontKey - vendorWantedFontKey) > 0.01f)
        {
            var widest = 0f;
            foreach (var r in rows)
                widest = Math.Max(widest, ImGui.CalcTextSize(VendorCellFor(r.ItemId).Text).X);
            vendorWantedWidth = Math.Max(minimum, widest + padding);
            vendorWantedReady = VendorInfoReady;
            vendorWantedFontKey = fontKey;
        }
        return vendorWantedWidth;
    }

    /// <summary>The vendors alone, cached once the vendor index is ready.</summary>
    private string RawVendorText(uint itemId) => ItemSourceSummary.ColumnText(sources, itemId);

    /// <summary>
    /// True once the vendor index, the monster-drop list and the recipe index are all built (or the drop list or recipe index
    /// failed, so it will not change).
    /// </summary>
    private bool VendorInfoReady => sources.IsReady && (drops.IsReady || drops.HasFailed)
        && (recipes is not LuminaRecipeData d || d.IsResultIndexReady || d.IsResultIndexFailed);

    private bool IsCraftable(uint itemId)
    {
        // Never starts the recipe index from here: it runs every frame, and a failing build would restart each time.
        // The plugin warms it at start, and the load and click paths retry on purpose.
        if (recipes is LuminaRecipeData { IsResultIndexReady: false })
            return false;
        return recipes.FindRecipeForItem(itemId) != null;
    }

    /// <summary>The Vendor column cell for an item (text and facts), cached once everything it reads is ready.</summary>
    private VendorCell VendorCellFor(uint itemId)
    {
        if (vendorText.TryGetValue(itemId, out var cell))
            return cell;
        var raw = RawVendorText(itemId);
        var craftable = IsCraftable(itemId);
        var isDrop = drops.IsDrop(itemId);
        cell = new VendorCell(
            ItemSourceSummary.WithDrop(ItemSourceSummary.WithCraftable(raw, craftable), isDrop),
            raw != ItemSourceSummary.NoneText && raw != ItemSourceSummary.NotReadyText,
            craftable,
            isDrop);
        if (VendorInfoReady)
            vendorText[itemId] = cell;
        return cell;
    }

    /// <summary>Bags, crystals and saddlebag; <c>null</c> until the bags have been read for this character.</summary>
    internal static long? OnYouTotal(OwnedSummary s) => s.OnYou is int now ? (long)now + (s.Saddlebag ?? 0) : null;

    /// <summary>Puts the next button of the given label on the same line when it fits in the window, otherwise leaves it to wrap onto a new line.</summary>
    private static void SameLineIfFits(string nextLabel)
    {
        var style = ImGui.GetStyle();
        var next = ImGui.CalcTextSize(nextLabel).X + style.FramePadding.X * 2f;
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        if (ImGui.GetItemRectMax().X + style.ItemSpacing.X + next > right)
            return;
        ImGui.SameLine();
    }

    /// <summary>The button: unticks crafted ingredients now. While the recipe index is still building it only starts that and says to press again.</summary>
    private void ExcludeCraftedNow()
    {
        if (recipes is LuminaRecipeData { IsResultIndexReady: false } data)
        {
            // Read the failure first: RetryNow clears it, and this press is the user's explicit retry.
            var failed = data.IsResultIndexFailed;
            data.RetryNow();
            craftedButtonNote = failed ? RecipesFailedText + " Trying again, press the button again in a moment." : "Recipe data is still loading. Press the button again in a moment.";
            return;
        }
        craftedButtonNote = string.Empty;
        session.ApplyBulk("Untick crafted items", s => s.ExcludeCrafted(recipes, overrideHandTicks: true));
        PersistExclusions();
    }

    /// <summary>Bags, saddlebag and retainers together; <c>null</c> when neither the bags nor any retainer has been read.</summary>
    private long? OwnedEverywhere(uint itemId)
    {
        var s = owned.Summarize(itemId);
        var onYou = OnYouTotal(s);
        return onYou is null && s.Retainers is null ? null : (onYou ?? 0) + (s.Retainers ?? 0);
    }

    private static void DrawOnYou(OwnedSummary s, int needed)
    {
        if (OnYouTotal(s) is not long total)
            ImGui.TextDisabled("-");
        else if (total >= needed && needed > 0)
            ImGui.TextColored(EnoughColor, total.ToString("N0"));
        else
            ImGui.TextUnformatted(total.ToString("N0"));
        if (!ImGui.IsItemHovered()) return;

        ImGui.BeginTooltip();
        try
        {
            if (s.OnYou is int now)
                ImGui.TextUnformatted($"Bags and crystals now: {now:N0}");
            else
                ImGui.TextDisabled("Bags and crystals: not read yet");
            if (s.Saddlebag is int bag)
                ImGui.TextUnformatted($"Saddlebag: {bag:N0} (as of {LocalTime(s.SaddlebagCapturedUtc)})");
            else
                ImGui.TextDisabled("Saddlebag: not seen yet; open it once to count it.");
            ImGui.TextUnformatted($"Needed: {needed:N0}");
        }
        finally
        {
            ImGui.EndTooltip();
        }
    }

    private void DrawRetainers(OwnedSummary cell, uint itemId, int needed)
    {
        if (cell.Retainers is long retainers)
        {
            var text = retainers.ToString("N0");
            // Until the bags are read only the retainers themselves count towards "enough".
            if (needed > 0 && (OnYouTotal(cell) ?? 0) + retainers >= needed)
                ImGui.TextColored(EnoughColor, text);
            else if (cell.RetainerSource == RetainerSource.Cache)
                // This plugin's own record is only as current as the last visit to each retainer.
                ImGui.TextDisabled(text);
            else
                ImGui.TextUnformatted(text);
        }
        else
        {
            ImGui.TextDisabled("-");
        }
        if (!ImGui.IsItemHovered()) return;

        // The tooltip reads one snapshot, so the totals and the list of retainers below them always agree.
        var (s, where) = owned.SummarizeWithLocations(itemId);
        ImGui.BeginTooltip();
        try
        {
            ImGui.TextUnformatted(s.RetainerSource switch
            {
                RetainerSource.External => "Shown: AllaganTools",
                RetainerSource.Cache => "Shown: this plugin's record of your last retainer visits",
                _ => "Unknown: no retainer has been opened yet",
            });
            if (s.RetainersCached is long cached)
                ImGui.TextUnformatted($"This plugin's record: {cached:N0}, as of {LocalTime(s.RetainersCapturedOldestUtc)} to {LocalTime(s.RetainersCapturedNewestUtc)}");
            else
                ImGui.TextDisabled("This plugin's record: none yet; open each retainer once.");
            if (s.RetainersExternal is long ext)
                ImGui.TextUnformatted($"AllaganTools: {ext:N0}");
            else
                ImGui.TextDisabled("AllaganTools: not available");
            ImGui.TextDisabled("Counts retainer inventory, crystals and market listings.");
            if (s.Retainers is long r && OnYouTotal(s) is long onYou)
                ImGui.TextUnformatted($"With what is on you: {onYou + r:N0} of {needed:N0} needed");
            DrawRetainerLocations(s, where);
        }
        finally
        {
            ImGui.EndTooltip();
        }
    }

    private void DrawHouse(OwnedSummary cell, uint itemId, int needed)
    {
        if (cell.House is long house)
        {
            var text = house.ToString("N0");
            if (needed > 0 && house >= needed)
                ImGui.TextColored(EnoughColor, text);
            else
                // Always this plugin's own record, so only as current as the last visit to each house.
                ImGui.TextDisabled(text);
        }
        else
        {
            ImGui.TextDisabled("-");
        }
        if (!ImGui.IsItemHovered()) return;

        // The tooltip reads one snapshot, so the total and the list of houses below it always agree.
        var (s, where) = owned.SummarizeWithHouses(itemId);
        ImGui.BeginTooltip();
        try
        {
            if (s.House is long total)
                ImGui.TextUnformatted($"In your houses: {total:N0} of {needed:N0} needed");
            else
                ImGui.TextDisabled("Unknown: no house has been counted yet");
            ImGui.TextDisabled("Counts furniture placed inside the house and the storeroom, as of your last visit.");
            if (where.Count > 0)
            {
                ImGui.Separator();
                foreach (var loc in where.Take(MaxLocationLines))
                {
                    var label = string.IsNullOrEmpty(loc.Label) ? "(unnamed house)" : loc.Label;
                    var parts = new List<string>(2);
                    if (loc.Placed > 0)
                        parts.Add($"Placed x{loc.Placed:N0}");
                    if (loc.Storeroom > 0)
                        parts.Add($"Storeroom x{loc.Storeroom:N0}");
                    ImGui.TextUnformatted($"{label}: {string.Join(", ", parts)} (as of {LocalTime(loc.CapturedUtc)})");
                }
                if (where.Count > MaxLocationLines)
                    ImGui.TextDisabled($"+{where.Count - MaxLocationLines} more");
            }
            if (s.House is null)
                ImGui.TextDisabled("Open the Furnishings window once in each house to count it.");
        }
        finally
        {
            ImGui.EndTooltip();
        }
    }

    private void DrawVendor(uint itemId)
    {
        var (text, hasSources, craftable, isDrop) = VendorCellFor(itemId);
        if (hasSources || craftable || isDrop)
        {
            // The normal text colour, softened, so real sources read as calm secondary information in light and dark themes.
            var calm = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
            calm.W *= 0.85f;
            ImGui.TextColored(calm, text);
        }
        else
        {
            ImGui.TextDisabled(text);
        }
        if (!ImGui.IsItemHovered()) return;

        if (!sources.IsReady && !craftable && !isDrop)
        {
            ImGui.BeginTooltip();
            try
            {
                ImGui.TextUnformatted(sources.HasFailed
                    ? "The vendor index could not be built. Details are in /xllog."
                    : "Vendor index is still loading.");
            }
            finally
            {
                ImGui.EndTooltip();
            }
            return;
        }
        if (!hasSources)
        {
            if (!isDrop)
            {
                if (craftable)
                    ImGui.SetTooltip($"{Name(itemId)} can be crafted.");
                return;
            }
            ImGui.BeginTooltip();
            try
            {
                if (craftable)
                    ImGui.TextUnformatted($"{Name(itemId)} can be crafted.");
                DrawDropNote(itemId);
            }
            finally
            {
                ImGui.EndTooltip();
            }
            return;
        }

        var (shown, more) = ItemSourceSummary.Capped(ItemSourceSummary.Ordered(sources.For(itemId)), ItemSourceSummary.DefaultTooltipLines);
        ImGui.BeginTooltip();
        try
        {
            ImGui.TextUnformatted($"{Name(itemId)} can also be had from vendors");
            foreach (var s in shown)
                ImGui.TextUnformatted(ItemSourceSummary.Line(s));
            if (more > 0)
                ImGui.TextDisabled(ItemSourceSummary.MoreText(more));
            ImGui.TextDisabled("Availability and quest unlocks are not checked.");
            if (isDrop)
                DrawDropNote(itemId);
        }
        finally
        {
            ImGui.EndTooltip();
        }
    }

    /// <summary>The monster-drop paragraph of the Vendor tooltip; call it inside an open tooltip.</summary>
    private void DrawDropNote(uint itemId)
    {
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30f);
        try
        {
            ImGui.TextWrapped($"{Name(itemId)} is a monster drop. This plugin does not know which monsters; the Monster Loot Hunter plugin can look it up.");
        }
        finally
        {
            ImGui.PopTextWrapPos();
        }
    }

    private const int MaxLocationLines = 8;

    /// <summary>The "Where" part of the retainers tooltip. Runs only while the tooltip is open.</summary>
    private static void DrawRetainerLocations(OwnedSummary s, IReadOnlyList<RetainerLocation> where)
    {
        if (where.Count == 0)
            return;

        ImGui.Separator();
        if (s.RetainerSource == RetainerSource.External)
            ImGui.TextDisabled("AllaganTools does not say where an item is; the list below is this plugin's own record.");
        ImGui.TextUnformatted("Where");
        foreach (var loc in where.Take(MaxLocationLines))
        {
            var name = string.IsNullOrEmpty(loc.Name) ? "(unnamed retainer)" : loc.Name;
            if (loc.LocationRecorded)
            {
                var parts = string.Join(", ", RetainerPlaceDisplay.Merge(loc.Places).Select(p => $"{p.Label} x{p.Count:N0}"));
                ImGui.TextUnformatted($"{name}: {parts} (as of {LocalTime(loc.CapturedUtc)})");
            }
            else
            {
                ImGui.TextUnformatted($"{name}: {loc.Total:N0}");
                ImGui.SameLine();
                ImGui.TextDisabled($"location not recorded yet, open this retainer once (as of {LocalTime(loc.CapturedUtc)})");
            }
        }
        if (where.Count > MaxLocationLines)
            ImGui.TextDisabled($"+{where.Count - MaxLocationLines} more");
    }

    private static string LocalTime(DateTimeOffset? utc) => utc is { } t ? t.ToLocalTime().ToString("g") : "unknown";

    private void PersistExclusions()
    {
        userEdited = true;
        // Artisan and custom lists are remembered; pasted text has no stable id.
        if (artisanListId is int id)
        {
            config.ExcludedItemsByList[id] = new HashSet<uint>(session.ExportExclusions());
            config.IncludedItemsByList[id] = new HashSet<uint>(session.ExportIncludes());
            config.Save();
        }
        else if (customListId != null && config.FindCustomList(customListId) is { } saved)
        {
            saved.ExcludedItemIds = new HashSet<uint>(session.ExportExclusions());
            config.Save();
        }
    }

    // ---- Build ----

    private void DrawBuild()
    {
        var loggedIn = worlds.CurrentWorld != null;
        if (!loggedIn)
            ImGui.TextColored(WarningColor, "Log in to a character first.");

        if (building)
        {
            ImGui.BeginDisabled();
            try
            {
                ImGui.Button("Find prices");
            }
            finally
            {
                ImGui.EndDisabled();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel build")) CancelBuild();
        }
        else
        {
            // Pricing only the plain rows while the goals' materials are missing would give a result with no sign of it.
            // A build with nothing ticked would price nothing (every row is skipped), so it is not offered.
            var nothingTicked = rows.Count > 0 && !rows.Any(r => session.IsIncluded(r.ItemId));
            var reason = !loggedIn ? "Log in to a character first."
                : goalsPending ? (recipes is LuminaRecipeData { IsResultIndexFailed: true } ? RecipesFailedText + " Press Reload list to try again." : GoalsPendingBuildTooltip)
                : rows.Count == 0 ? "This list has no ingredients."
                : nothingTicked ? "Tick at least one item."
                : null;
            ImGui.BeginDisabled(reason != null);
            try
            {
                if (ImGui.Button("Find prices")) StartBuild();
            }
            finally
            {
                ImGui.EndDisabled();
            }
            if (reason != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(reason);
        }

        if (progressText.Length > 0)
        {
            ImGui.SameLine();
            ImGui.PushTextWrapPos(0f);
            try
            {
                ImGui.TextUnformatted(progressText);
            }
            finally
            {
                ImGui.PopTextWrapPos();
            }
        }
        if (errorText.Length > 0)
            WrappedColored(ErrorColor, errorText);
    }

    private void StartBuild()
    {
        // Snapshots: the background task never touches UI-thread state.
        var requirements = session.Requirements.ToList();
        var exclusions = new ExclusionState();
        exclusions.ExcludeAll(session.ExportExclusions(), true);
        // Remembered so a build that finishes after the ticks changed is not published.
        buildExclusions = new HashSet<uint>(session.ExportExclusions());
        var scope = config.LastScope;

        int gen;
        lock (buildGate)
        {
            gen = ++generation;
            pendingBook = null;
            pendingOptions = null;
            building = true;
            errorText = string.Empty;
            progressText = "Starting...";
        }
        SweepRetired();
        RetireCurrent();
        var source = new CancellationTokenSource();
        var token = source.Token;
        cts = source;
        var progress = new BuildProgress(this, gen);

        buildTask = Task.Run(async () =>
        {
            try
            {
                var result = await service.BuildOfferBookAsync(requirements, scope, exclusions, progress, token).ConfigureAwait(false);
                // The trade-off options are computed here, on the build task, so the draw thread never runs the search.
                token.ThrowIfCancellationRequested();
                IReadOnlyList<PlanOption> options;
                try
                {
                    options = PlanFrontier.Build(result);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The offer book is fine without the options: the results window falls back to the cheapest plan.
                    log.Warning(ex, "Computing the plan options failed");
                    options = Array.Empty<PlanOption>();
                }
                // A cancel pressed while the options were computed must not be overridden by a published result.
                token.ThrowIfCancellationRequested();
                progress.Finish(() =>
                {
                    pendingBook = result;
                    pendingOptions = options;
                    progressText = "Done.";
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                progress.Finish(() => progressText = "Cancelled.");
            }
            catch (Exception ex)
            {
                log.Error(ex, "Building the shopping list failed");
                progress.Finish(() =>
                {
                    progressText = string.Empty;
                    errorText = ex is TaskCanceledException { InnerException: TimeoutException }
                        ? "Could not find prices: a request timed out. Try again."
                        : "Could not find prices: " + ex.Message;
                });
            }
        });
    }

    private void CancelBuild() => CancelSource(cts);

    private static void CancelSource(CancellationTokenSource? source)
    {
        try { source?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Takes the current source out of use: disposed now if its task ended, otherwise kept until a sweep finds it ended.</summary>
    private void RetireCurrent()
    {
        if (cts == null) return;
        if (buildTask == null || buildTask.IsCompleted)
            cts.Dispose();
        else
            retired.Add((cts, buildTask));
        cts = null;
        buildTask = null;
    }

    /// <summary>Disposes the sources of retired builds whose task has ended.</summary>
    private void SweepRetired()
    {
        retired.RemoveAll(entry =>
        {
            if (!entry.Task.IsCompleted) return false;
            entry.Source.Dispose();
            return true;
        });
    }

    /// <summary>
    /// On the UI thread: hands a finished build's offers to the host exactly once. The book is taken under the gate,
    /// so a build superseded after publishing (a reload, a new build, disposal) never reaches the host.
    /// </summary>
    private void PublishPendingResults()
    {
        OfferBook? book;
        IReadOnlyList<PlanOption>? options;
        lock (buildGate)
        {
            book = pendingBook;
            options = pendingOptions;
            pendingBook = null;
            pendingOptions = null;
        }
        if (book == null) return;

        // The ticks changed while prices were loading: the result no longer matches the list, so it is not shown.
        if (buildExclusions != null && !buildExclusions.SetEquals(session.ExportExclusions()))
        {
            progressText = TicksChangedText;
            return;
        }

        try
        {
            host.ShowResults(key, title, book, options ?? Array.Empty<PlanOption>());
            progressText = OpenedText;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Opening the results window for {Key} failed", key);
            progressText = string.Empty;
            errorText = "Could not open the results window: " + ex.Message;
        }
    }

    /// <summary>
    /// Writes progress straight into the window (no captured context, so nothing arrives late), and
    /// ignores reports once its build has finished or been superseded.
    /// </summary>
    private sealed class BuildProgress(ListWindow owner, int gen) : IProgress<string>
    {
        private bool finished;

        public void Report(string value)
        {
            lock (owner.buildGate)
            {
                if (!finished && owner.generation == gen)
                    owner.progressText = value;
            }
        }

        /// <summary>Ends the build; publishes its outcome only if it is still the current build.</summary>
        public void Finish(Action publish)
        {
            lock (owner.buildGate)
            {
                finished = true;
                if (owner.generation != gen)
                    return;
                publish();
                owner.building = false;
            }
        }
    }
}
