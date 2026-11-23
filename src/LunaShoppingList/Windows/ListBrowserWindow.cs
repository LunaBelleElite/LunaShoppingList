using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using LunaShoppingList.Adapters;
using LunaShoppingList.Core;

namespace LunaShoppingList.Windows;

/// <summary>The entry window: pick one of Artisan's saved lists, build your own, or import and export lists, and open them as their own shopping-list windows.</summary>
public sealed class ListBrowserWindow : Window
{
    private const string NoListsText = "Artisan not found or no lists";
    private const string UnreadableConfigText = "Couldn't read Artisan's config, try again in a moment.";
    private const string LoadingNamesText = "Loading item names...";
    private const string ImportHelpText = "Paste a Teamcraft import link, an Artisan list copied with Export List, or lines like 3x Iron Ore.";
    private const string BreakdownTooltip = "Replaces each craftable item with the materials needed to make it, like Add with subcrafts. Items that can't be crafted stay as they are.";
    private const string ChooseListFirstText = "Choose a list first.";
    private const string ArtisanAlreadyText = "This list is already in Artisan.";
    private const string NothingToExportText = "Nothing to export: the list is empty.";
    private const string AllSkippedText = "Every entry in this list is set to Skip or has an unknown recipe, so there is nothing to export.";
    private const int PasteMaxLength = 50000;
    private const int MaxSkippedNames = 8;
    private const string CutOffText = "The text was cut off at the size limit, so the last items may be missing or wrong.";
    // The box buffer holds PasteMaxLength bytes including the closing NUL, so the text itself may use one less.
    private const int PasteMaxTextBytes = PasteMaxLength - 1;
    private const int NearCapBytes = 8;
    private const int SearchMaxLength = 100;
    private const int SearchResultCount = 10;
    private const string AddWithSubcraftsTooltip = "Keeps the finished item as a goal on this list and works out the materials down to raw materials, so the list can be exported to Artisan with its subcrafts. The item itself is not a shopping row. Where several recipes make an item, the first recipe found is used.";
    private const string GoalsNoteText = "The materials for these goals are added to the shopping list automatically.";
    private const string RecipesLoadingText = "Recipe data is still loading...";
    private const string RecipesFailedText = "Recipe data could not be loaded. Check /xllog, then try again.";
    private const string TeamcraftLinkHintText = "Share Teamcraft list copies a link to send to someone else. They should open it as a website (paste it into the browser's address bar), not try to import it in Teamcraft's import dialog.";
    private const string ShareTeamcraftTooltip = "Copies the Teamcraft link so you can send it to another person. They open it as a website; it does not work in Teamcraft's own import dialog. To use it yourself, press Open in Teamcraft instead.";
    private const string ExcludeCraftedTooltip = "When on, Artisan lists open with crafted ingredients (anything that has a recipe) unticked, so you only buy the raw materials. Your own ticks still win.";
    private const string NotCraftableText = "This item cannot be crafted";
    // The text boxes count UTF-8 bytes; the 60-character name limit itself is checked when the name is used.
    private const int NameBoxMaxBytes = CustomListOps.MaxNameLength * 4;

    private readonly Configuration config;
    private readonly IArtisanListSource artisan;
    private readonly IItemInfo items;
    private readonly IItemCatalog catalog;
    private readonly IRecipeLookup recipes;
    private readonly IRecipeData recipeData;
    private readonly WindowManager manager;
    private readonly IPluginLog log;

    // UI-thread state.
    private IReadOnlyList<ArtisanListInfo> lists = [];
    private int hiddenCount;
    private string filter = string.Empty;
    private string message = string.Empty;
    private string pasteText = string.Empty;
    private IReadOnlyList<string> unmatchedLines = [];
    private IReadOnlyList<Requirement> parsed = [];
    // Import / Export tab. The parse state resets when the box text no longer matches what was parsed.
    private bool importParsed;
    private string parsedFromText = string.Empty;
    private ImportKind importKind;
    private string? importError;
    private string importName = string.Empty;
    private bool importCut;
    // The clipboard text as cut by Paste from clipboard, to recognise it still in the box when Parse is pressed.
    private string? clipboardCutText;
    // True while 'message' came from Parse, so a changed box can clear it.
    private bool messageFromParse;
    private bool importBreakdown;
    // The export selection: a My shopping list by id, or an Artisan list by id (never both).
    private string? exportCustomId;
    private int? exportArtisanId;
    // Green note and orange side note of the Import / Export tab; cleared by every SetMessage.
    private string ioNotice = string.Empty;
    private string ioWarn = string.Empty;
    private bool ioInImport;

    // 'My shopping lists' tab state. Everything below belongs to the selected list and is reset when the selection changes.
    private string newListName = string.Empty;
    private string? selectedListId;
    private bool renaming;
    private string renameText = string.Empty;
    private bool deletePending;
    private string searchText = string.Empty;
    private string? lastQuery;
    private IReadOnlyList<ItemSearchEntry>? lastEntries;
    private IReadOnlyList<ItemSearchEntry> searchResults = [];
    private uint pickedItemId;
    private string pickedLabel = string.Empty;
    private int addQuantity = 1;
    // A row quantity changed in memory but not saved yet (the edit is still in progress).
    private string? unsavedListId;
    private readonly Dictionary<uint, string> itemNames = new();
    // Whether the picked item has a recipe: null while unknown, looked up once per pick.
    private bool? pickedCraftable;
    // A success note shown in the message area until the next action.
    private string notice = string.Empty;
    // The materials of the selected list's goals, worked out only when the goals, the selection or the recipe index
    // change (never per frame: working them out looks up recipes). Null while there are no goals or the index is loading.
    private EffectiveRequirementsResult? materials;
    private bool materialsDirty = true;
    private bool materialsWarmRequested;
    // Display order of the two tables (click-to-sort headers). Window-only, never saved; the stored order never changes.
    private readonly SortedRows goalSort = new();
    private readonly SortedRows itemSort = new();

    /// <summary>
    /// The display order of one table, cached: the rows are ordered again only when the sort column or direction, the
    /// list, or the rows (count or item ids in stored order) changed, or after a quantity edit was committed.
    /// </summary>
    private sealed class SortedRows
    {
        private string? listId;
        private int column = -1;
        private bool ascending = true;
        private bool dirty = true;
        private uint[] storedIds = [];
        private IReadOnlyList<CustomListItem> view = [];

        /// <summary>The next call orders the rows again (a finished quantity edit may have moved a row).</summary>
        public void Touch() => dirty = true;

        public IReadOnlyList<CustomListItem> Get(string id, List<CustomListItem> rows, int sortColumn, bool sortAscending,
            Func<uint, string> name)
        {
            if (!dirty && listId == id && column == sortColumn && ascending == sortAscending && storedIds.Length == rows.Count)
            {
                var same = true;
                for (var i = 0; i < rows.Count && same; i++)
                    same = storedIds[i] == rows[i].ItemId;
                if (same)
                    return view;
            }

            Comparison<CustomListItem>? comparison = sortColumn switch
            {
                0 => (a, b) => TableSort.CompareText(name(a.ItemId), name(b.ItemId)),
                1 => (a, b) => a.Quantity.CompareTo(b.Quantity),
                _ => null,
            };
            view = TableSort.Order(rows, comparison, sortAscending);
            storedIds = new uint[rows.Count];
            for (var i = 0; i < rows.Count; i++)
                storedIds[i] = rows[i].ItemId;
            listId = id;
            column = sortColumn;
            ascending = sortAscending;
            dirty = false;
            return view;
        }
    }

    /// <summary>The sort the open table's header clicks chose: no column (stored order), or a column and a direction.</summary>
    private static (int Column, bool Ascending) ReadSortSpecs()
    {
        var specs = ImGui.TableGetSortSpecs();
        if (specs.IsNull || specs.SpecsCount < 1)
            return (-1, true);
        var spec = specs.Specs;
        specs.SpecsDirty = false;
        return (spec.ColumnIndex, spec.SortDirection != ImGuiSortDirection.Descending);
    }

    internal ListBrowserWindow(Configuration config, IArtisanListSource artisan, IItemInfo items, IItemCatalog catalog,
        IRecipeLookup recipes, IRecipeData recipeData, WindowManager manager, IPluginLog log)
        : base("Luna Shopping List##LunaShoppingListBrowser")
    {
        this.config = config;
        this.artisan = artisan;
        this.items = items;
        this.catalog = catalog;
        this.recipes = recipes;
        this.recipeData = recipeData;
        this.manager = manager;
        this.log = log;
        Size = new Vector2(380, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnOpen()
    {
        // One IPC call per opening, never per frame.
        RefreshLists();
    }

    public override void OnClose()
    {
        deletePending = false;
        renaming = false;
        FlushUnsaved();
    }

    public override void Draw()
    {
        try
        {
            // Every Begin is paired with its End in a finally, so an exception never leaves the ImGui stack unbalanced.
            if (ImGui.BeginTabBar("##lslTabs"))
            {
                try
                {
                    if (ImGui.BeginTabItem("Artisan crafting lists"))
                    {
                        try
                        {
                            DrawMyLists();
                        }
                        finally
                        {
                            ImGui.EndTabItem();
                        }
                    }
                    if (ImGui.BeginTabItem("My shopping lists"))
                    {
                        try
                        {
                            DrawCustomLists();
                        }
                        finally
                        {
                            ImGui.EndTabItem();
                        }
                    }
                    else
                    {
                        // Leaving the tab cancels a pending delete and saves an unfinished quantity edit.
                        deletePending = false;
                        notice = string.Empty;
                        FlushUnsaved();
                    }
                    if (ImGui.BeginTabItem("Import / Export###lslImportExport"))
                    {
                        try
                        {
                            DrawImportExport();
                        }
                        finally
                        {
                            ImGui.EndTabItem();
                        }
                    }
                    else
                    {
                        ioNotice = string.Empty;
                        ioWarn = string.Empty;
                    }
                }
                finally
                {
                    ImGui.EndTabBar();
                }
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Drawing the list browser failed");
            SetMessage("Something went wrong: " + ex.Message);
        }

        if (message.Length > 0)
            WrappedColored(new Vector4(1f, 0.6f, 0.3f, 1f), message);
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

    private void SetMessage(string text)
    {
        message = text;
        messageFromParse = false;
        notice = string.Empty;
        ioNotice = string.Empty;
        ioWarn = string.Empty;
    }

    private void DrawMyLists()
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", "Filter...", ref filter, 100);

        if (lists.Count == 0)
        {
            ImGui.TextWrapped(hiddenCount > 0
                ? $"Artisan has no saved lists of yours. {hiddenCount} lists are hidden (not in Artisan's saved config); to use a premade list, paste its ingredients."
                : NoListsText);
        }
        else
        {
            var footer = ImGui.GetFrameHeightWithSpacing() + (hiddenCount > 0 ? ImGui.GetTextLineHeightWithSpacing() * 3 : 0);
            var height = Math.Max(ImGui.GetTextLineHeightWithSpacing() * 4, ImGui.GetContentRegionAvail().Y - footer);
            // EndChild is called whatever BeginChild returned.
            try
            {
                if (ImGui.BeginChild("##listScroll", new Vector2(-1, height), true))
                {
                    foreach (var l in lists)
                    {
                        if (filter.Length > 0 && !l.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (ImGui.Selectable($"{l.Name}##list{l.Id}"))
                            manager.OpenArtisanList(l.Id, l.Name);
                        ImGui.SameLine();
                        ImGui.TextDisabled($"(ID: {l.Id})");
                    }
                }
            }
            finally
            {
                ImGui.EndChild();
            }
        }

        if (ImGui.Button("Refresh")) RefreshLists();
        ImGui.SameLine();
        var excludeCrafted = config.ExcludeCraftedItems;
        if (ImGui.Checkbox("Exclude crafted items", ref excludeCrafted))
        {
            config.ExcludeCraftedItems = excludeCrafted;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(ExcludeCraftedTooltip);
        if (hiddenCount > 0 && lists.Count > 0)
        {
            ImGui.PushTextWrapPos(0f);
            try
            {
                ImGui.TextDisabled($"{hiddenCount} lists are hidden: lists that are not in Artisan's saved config (premade lists, or lists not saved yet). To use one, paste its ingredients.");
            }
            finally
            {
                ImGui.PopTextWrapPos();
            }
        }
    }

    private void RefreshLists()
    {
        try
        {
            SetMessage(string.Empty);
            lists = artisan.GetLists();
            hiddenCount = artisan is ArtisanListSource source ? source.HiddenListCount : 0;
            config.PruneExcludedLists(lists.Select(l => l.Id));
        }
        catch (ArtisanConfigUnreadableException ex)
        {
            log.Warning(ex, "Reading Artisan's lists failed");
            SetMessage(UnreadableConfigText);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Reading Artisan's lists failed");
            SetMessage("Could not read the lists: " + ex.Message);
        }
    }

    private void DrawCustomLists()
    {
        DrawCustomListsBody();
        if (notice.Length > 0)
            WrappedColored(new Vector4(0.5f, 0.9f, 0.5f, 1f), notice);
    }

    private void DrawCustomListsBody()
    {
        DrawCreate();
        DrawSavedLists();

        if (SelectedList() is not { } list)
        {
            ImGui.TextDisabled("Create or select a list to add items to it.");
            return;
        }

        ImGui.Separator();
        DrawListHeader(list);
        // The header can delete the list.
        if (SelectedList() is not { } current)
            return;
        DrawItemSearch(current);
        DrawListItems(current);
    }

    private void DrawCreate()
    {
        var style = ImGui.GetStyle();
        ImGui.SetNextItemWidth(Math.Max(60f, ImGui.GetContentRegionAvail().X - ButtonWidth("Create") - style.ItemSpacing.X));
        ImGui.InputTextWithHint("##newListName", "Name your list...", ref newListName, NameBoxMaxBytes);
        ImGui.SameLine();
        if (ImGui.Button("Create"))
            CreateList();
    }

    private void DrawSavedLists()
    {
        var lists = config.CustomLists;
        if (lists.Count == 0)
        {
            ImGui.TextDisabled("No lists yet: name one above and press Create.");
            return;
        }

        var shown = Math.Clamp(lists.Count, 2, 6);
        var height = shown * ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().WindowPadding.Y * 2;
        string? clicked = null;
        // EndChild is called whatever BeginChild returned.
        try
        {
            if (ImGui.BeginChild("##customListScroll", new Vector2(-1, height), true))
            {
                for (var i = 0; i < lists.Count; i++)
                {
                    var l = lists[i];
                    if (l is null)
                        continue;
                    ImGui.PushID(l.Id);
                    try
                    {
                        var count = l.Items.Count;
                        var goalCount = l.Goals.Count;
                        var goalText = goalCount > 0 ? $", {goalCount} {(goalCount == 1 ? "goal" : "goals")}" : string.Empty;
                        if (SelectableText("##row", $"{l.Name}  ({count} {(count == 1 ? "item" : "items")}{goalText})", l.Id == selectedListId))
                            clicked = l.Id;
                    }
                    finally
                    {
                        ImGui.PopID();
                    }
                }
            }
        }
        finally
        {
            ImGui.EndChild();
        }

        if (clicked != null)
            SelectList(clicked);
    }

    private void DrawListHeader(CustomList list)
    {
        if (renaming)
            DrawRename(list);
        else
            ImGui.TextUnformatted(list.Name);

        var avail = ImGui.GetContentRegionAvail().X;
        ImGui.BeginDisabled(list.Items.Count == 0 && list.Goals.Count == 0);
        try
        {
            if (ImGui.Button("Shop for this list"))
                ShopFor(list);
        }
        finally
        {
            ImGui.EndDisabled();
        }
        var used = ButtonWidth("Shop for this list");

        if (!renaming)
        {
            NextInRow(ref used, avail, "Rename");
            if (ImGui.Button("Rename"))
            {
                deletePending = false;
                renaming = true;
                renameText = list.Name;
            }
        }

        // Two-step delete: the first click arms it, a second click on the same button deletes, any other click cancels.
        var deleteText = deletePending ? "Click again to delete" : "Delete";
        NextInRow(ref used, avail, deleteText);
        var deleteClicked = ImGui.Button(deletePending ? "Click again to delete###delete" : "Delete###delete");
        var deleteHovered = ImGui.IsItemHovered();
        if (deleteClicked)
        {
            if (deletePending)
                DeleteList(list);
            else
                deletePending = true;
        }
        else if (deletePending && !deleteHovered
                 && (ImGui.IsMouseClicked(ImGuiMouseButton.Left) || ImGui.IsMouseClicked(ImGuiMouseButton.Right)))
        {
            deletePending = false;
        }
    }

    private void DrawRename(CustomList list)
    {
        var style = ImGui.GetStyle();
        var buttons = ButtonWidth("Save") + ButtonWidth("Cancel") + style.ItemSpacing.X * 2;
        ImGui.SetNextItemWidth(Math.Max(60f, ImGui.GetContentRegionAvail().X - buttons));
        ImGui.InputText("##rename", ref renameText, NameBoxMaxBytes);
        ImGui.SameLine();
        if (ImGui.Button("Save"))
            SaveRename(list);
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            renaming = false;
    }

    private void DrawItemSearch(CustomList list)
    {
        ImGui.Spacing();
        // The item index is built in the background; never build it on the draw thread.
        var ready = catalog.IsReady;
        var enterPressed = false;
        ImGui.BeginDisabled(!ready);
        try
        {
            ImGui.SetNextItemWidth(-1);
            // A new search drops the picked item, so Add never adds something no longer shown.
            if (ImGui.InputTextWithHint("##itemSearch", "Type an item name...", ref searchText, SearchMaxLength))
            {
                notice = string.Empty;
                ClearPicked();
            }
            enterPressed = ImGui.IsItemFocused()
                && (ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false));
        }
        finally
        {
            ImGui.EndDisabled();
        }
        if (!ready)
            ImGui.TextDisabled(LoadingNamesText);

        var results = SearchResults();
        if (enterPressed && results.Count == 1)
            Pick(results[0]);

        for (var i = 0; i < results.Count; i++)
        {
            var e = results[i];
            ImGui.PushID((int)e.ItemId);
            try
            {
                if (SelectableText("##result", e.Name, e.ItemId == pickedItemId))
                    Pick(e);
            }
            finally
            {
                ImGui.PopID();
            }
        }
        if (ready && results.Count == 0 && !string.IsNullOrWhiteSpace(searchText))
            ImGui.TextDisabled("No market-board item matches that name.");

        if (pickedItemId != 0)
            ImGui.TextUnformatted(pickedLabel);

        var style = ImGui.GetStyle();
        var subLabel = "Add with subcrafts";
        // Measured before the row is drawn: once a widget is drawn the cursor has moved on.
        var lineAvail = ImGui.GetContentRegionAvail().X;
        var quantityWidth = ImGui.CalcTextSize("Quantity").X;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Quantity");
        ImGui.SameLine();
        var rest = lineAvail - quantityWidth - ButtonWidth("Add") - ButtonWidth(subLabel) - style.ItemSpacing.X * 3;
        var boxWidth = Math.Clamp(rest, 60f, 160f);
        ImGui.SetNextItemWidth(boxWidth);
        if (ImGui.InputInt("##addQuantity", ref addQuantity, 1, 100))
        {
            addQuantity = CustomListOps.ClampQuantity(addQuantity);
            notice = string.Empty;
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(pickedItemId == 0);
        try
        {
            if (ImGui.Button("Add"))
                AddPicked(list);
        }
        finally
        {
            ImGui.EndDisabled();
        }

        // On a narrow window the second button wraps to the next line instead of being clipped.
        var used = quantityWidth + style.ItemSpacing.X + boxWidth + style.ItemSpacing.X + ButtonWidth("Add");
        NextInRow(ref used, lineAvail, subLabel);
        var craftable = pickedItemId == 0 ? null : PickedCraftable();
        ImGui.BeginDisabled(pickedItemId == 0 || craftable == false);
        try
        {
            if (ImGui.Button(subLabel + "##addSub"))
                AddPickedWithSubcrafts(list);
        }
        finally
        {
            ImGui.EndDisabled();
        }
        if (pickedItemId != 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(craftable == false ? NotCraftableText : AddWithSubcraftsTooltip);
    }

    private void DrawListItems(CustomList list)
    {
        ImGui.Spacing();
        if (list.Items.Count == 0 && list.Goals.Count == 0)
        {
            ImGui.TextDisabled("This list is empty: search for an item above and press Add.");
            return;
        }

        if (list.Goals.Count > 0)
            DrawGoals(list);
        if (list.Items.Count > 0)
        {
            if (list.Goals.Count > 0)
                ImGui.TextUnformatted("Items");
            DrawItemRows(list);
        }
        if (list.Goals.Count > 0)
            DrawMaterials(list);
    }

    private void DrawGoals(CustomList list)
    {
        ImGui.TextUnformatted("Goals");
        uint removeId = 0;
        var commitQuantity = false;
        // The list id scopes every widget id below, so a box on another list with the same item id is never the same widget.
        ImGui.PushID(list.Id);
        var tableOpen = false;
        try
        {
            tableOpen = ImGui.BeginTable("##customGoals", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate);
            if (tableOpen)
            {
                var style = ImGui.GetStyle();
                var padding = style.CellPadding.X * 2;
                ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed,
                    ImGui.CalcTextSize("9,999,999").X + style.FramePadding.X * 2 + (ImGui.GetFrameHeight() + style.ItemInnerSpacing.X) * 2 + padding);
                ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, ButtonWidth("Remove") + padding);
                ImGui.TableHeadersRow();

                // Goals are only read here; a removal is applied after the loop. Only the display order is sorted: every id
                // and every edit below uses the goal's item id, never its position.
                var (sortColumn, sortAscending) = ReadSortSpecs();
                var rows = goalSort.Get(list.Id, list.Goals, sortColumn, sortAscending, ItemName);
                for (var i = 0; i < rows.Count; i++)
                {
                    var goal = rows[i];
                    ImGui.PushID((int)goal.ItemId);
                    try
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(ItemName(goal.ItemId));

                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(-1);
                        var qty = goal.Quantity;
                        var changed = ImGui.InputInt("##goalQty", ref qty, 1, 100);
                        if (changed)
                        {
                            CustomListOps.SetGoalQuantity(list, goal.ItemId, qty);
                            unsavedListId = list.Id;
                            notice = string.Empty;
                            InvalidateMaterials();
                        }
                        // Typed edits are saved when the box is left; a step-button click is saved at once.
                        if (ImGui.IsItemDeactivatedAfterEdit() || (changed && !ImGui.IsItemActive()))
                            commitQuantity = true;

                        ImGui.TableNextColumn();
                        if (ImGui.SmallButton("Remove"))
                            removeId = goal.ItemId;
                    }
                    finally
                    {
                        ImGui.PopID();
                    }
                }
            }
        }
        finally
        {
            if (tableOpen)
                ImGui.EndTable();
            ImGui.PopID();
        }
        PushWrappedDim(GoalsNoteText);

        if (commitQuantity)
        {
            goalSort.Touch();
            Commit(list);
        }
        if (removeId != 0)
            RemoveGoal(list, removeId);
    }

    /// <summary>The goals' materials, read-only, in a collapsible section. Never worked out here: see <see cref="GetMaterials"/>.</summary>
    private void DrawMaterials(CustomList list)
    {
        var result = GetMaterials(list);
        if (result == null)
        {
            if (recipes is LuminaRecipeData { IsResultIndexFailed: true })
            {
                PushWrappedDim(RecipesFailedText);
                // The failed build is not retried every frame: a click here, or the next goal edit, starts it again.
                if (ImGui.SmallButton("Try again##recipesRetry"))
                    InvalidateMaterials();
            }
            else
            {
                PushWrappedDim(RecipesLoadingText);
            }
            return;
        }
        foreach (var problem in result.Problems)
            WrappedColored(new Vector4(1f, 0.6f, 0.3f, 1f), $"{ItemName(problem.ItemId)}: {problem.Error}");
        if (ImGui.CollapsingHeader($"Materials from goals ({result.Requirements.Count})###goalMaterials"))
        {
            foreach (var r in result.Requirements)
                ImGui.BulletText($"{ItemName(r.ItemId)} x{r.Quantity}");
        }
    }

    /// <summary>The goals or the selection changed: the materials are worked out again the next time they are drawn.</summary>
    private void InvalidateMaterials()
    {
        materials = null;
        materialsDirty = true;
        materialsWarmRequested = false;
    }

    private static void PushWrappedDim(string text)
    {
        ImGui.PushTextWrapPos(0f);
        try
        {
            ImGui.TextDisabled(text);
        }
        finally
        {
            ImGui.PopTextWrapPos();
        }
    }

    /// <summary>
    /// The goals' materials, or null while there are no goals or the recipe index is still loading (a background build is
    /// started, and the result is worked out as soon as it is ready). The work is repeated only after a goal, the
    /// selection or the index changed, so a drawn frame is a field read.
    /// </summary>
    private EffectiveRequirementsResult? GetMaterials(CustomList list)
    {
        if (list.Goals.Count == 0)
            return null;
        if (recipes is LuminaRecipeData { IsResultIndexReady: false } data)
        {
            if (!materialsWarmRequested)
            {
                data.WarmUp();
                materialsWarmRequested = true;
            }
            materials = null;
            materialsDirty = true;
            return null;
        }
        if (materialsDirty || materials == null)
        {
            // Only the goals matter here: the plain rows are shown as they are, so they are left out of the sum.
            materials = CustomListOps.EffectiveRequirements(new CustomList { Id = list.Id, Goals = list.Goals }, recipes);
            materialsDirty = false;
        }
        return materials;
    }

    private void DrawItemRows(CustomList list)
    {
        uint removeId = 0;
        var commitQuantity = false;
        // The list id scopes every widget id below, so a box on another list with the same item id is never the same widget.
        ImGui.PushID(list.Id);
        var tableOpen = false;
        try
        {
            tableOpen = ImGui.BeginTable("##customItems", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate);
            if (tableOpen)
            {
                var style = ImGui.GetStyle();
                var padding = style.CellPadding.X * 2;
                ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed,
                    ImGui.CalcTextSize("9,999,999").X + style.FramePadding.X * 2 + (ImGui.GetFrameHeight() + style.ItemInnerSpacing.X) * 2 + padding);
                ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, ButtonWidth("Remove") + padding);
                ImGui.TableHeadersRow();

                // Rows are only read here; a removal is applied after the loop. Only the display order is sorted: every id
                // and every edit below uses the row's item id, never its position.
                var (sortColumn, sortAscending) = ReadSortSpecs();
                var rows = itemSort.Get(list.Id, list.Items, sortColumn, sortAscending, ItemName);
                for (var i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    ImGui.PushID((int)row.ItemId);
                    try
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(ItemName(row.ItemId));

                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(-1);
                        var qty = row.Quantity;
                        var changed = ImGui.InputInt("##qty", ref qty, 1, 100);
                        if (changed)
                        {
                            CustomListOps.SetQuantity(list, row.ItemId, qty);
                            unsavedListId = list.Id;
                            notice = string.Empty;
                        }
                        // Typed edits are saved when the box is left; a step-button click is saved at once.
                        if (ImGui.IsItemDeactivatedAfterEdit() || (changed && !ImGui.IsItemActive()))
                            commitQuantity = true;

                        ImGui.TableNextColumn();
                        if (ImGui.SmallButton("Remove"))
                            removeId = row.ItemId;
                    }
                    finally
                    {
                        ImGui.PopID();
                    }
                }
            }
        }
        finally
        {
            if (tableOpen)
                ImGui.EndTable();
            ImGui.PopID();
        }

        if (commitQuantity)
        {
            itemSort.Touch();
            Commit(list);
        }
        if (removeId != 0)
            RemoveRow(list, removeId);
    }

    /// <summary>The selected list, or null. A selected list that no longer exists (deleted, or settings reloaded) clears the selection.</summary>
    private CustomList? SelectedList()
    {
        if (selectedListId == null)
            return null;
        var list = config.FindCustomList(selectedListId);
        if (list == null)
        {
            unsavedListId = null;
            SelectList(null);
        }
        return list;
    }

    private void SelectList(string? id)
    {
        if (id == selectedListId)
            return;
        FlushUnsaved();
        notice = string.Empty;
        selectedListId = id;
        InvalidateMaterials();
        renaming = false;
        renameText = string.Empty;
        deletePending = false;
        searchText = string.Empty;
        ClearPicked();
    }

    private void ClearPicked()
    {
        pickedItemId = 0;
        pickedLabel = string.Empty;
        pickedCraftable = null;
    }

    private void Pick(ItemSearchEntry entry)
    {
        deletePending = false;
        notice = string.Empty;
        pickedItemId = entry.ItemId;
        pickedCraftable = null;
        pickedLabel = "Selected: " + entry.Name;
    }

    /// <summary>Ranks only when the query or the item index changed; never per frame.</summary>
    private IReadOnlyList<ItemSearchEntry> SearchResults()
    {
        var entries = catalog.MarketableItems;
        if (!ReferenceEquals(entries, lastEntries) || !string.Equals(searchText, lastQuery, StringComparison.Ordinal))
        {
            lastEntries = entries;
            lastQuery = searchText;
            searchResults = ItemSearch.Rank(searchText, entries, SearchResultCount);
        }
        return searchResults;
    }

    private void CreateList()
    {
        if (CreateNamedList(newListName, null) != null)
            newListName = string.Empty;
    }

    /// <summary>
    /// Creates, fills (with <paramref name="rows"/>, when given), adds, saves and selects a new list. On a name error the
    /// message is shown and nothing changes; returns null.
    /// </summary>
    private CustomList? CreateNamedList(string name, IReadOnlyList<Requirement>? rows)
    {
        deletePending = false;
        var result = CustomListOps.Create(config.CustomLists, name);
        if (result.List is not { } created)
        {
            SetMessage(result.Error ?? "Could not create the list");
            return null;
        }
        SetMessage(string.Empty);
        if (rows != null)
            CustomListOps.AddMaterials(created, rows);
        config.CustomLists.Add(created);
        config.Save();
        SelectList(created.Id);
        return created;
    }

    private void SaveRename(CustomList list)
    {
        deletePending = false;
        FlushUnsaved();
        var result = CustomListOps.Rename(config.CustomLists, list, renameText);
        if (!result.Ok)
        {
            SetMessage(result.Error ?? "Could not rename the list");
            return;
        }
        SetMessage(string.Empty);
        renaming = false;
        // Only the titles change: a running build in the list's window keeps going.
        config.Save();
        manager.RenameCustomList(list.Id);
    }

    private void ShopFor(CustomList list)
    {
        deletePending = false;
        SetMessage(string.Empty);
        FlushUnsaved();
        manager.OpenCustomList(list.Id);
    }

    private void DeleteList(CustomList list)
    {
        deletePending = false;
        SetMessage(string.Empty);
        var id = list.Id;
        if (unsavedListId == id)
            unsavedListId = null;
        manager.CloseCustomList(id);
        CustomListOps.Delete(config.CustomLists, id);
        config.Save();
        SelectList(null);
    }

    private void AddPicked(CustomList list)
    {
        deletePending = false;
        SetMessage(string.Empty);
        CustomListOps.AddItem(list, pickedItemId, addQuantity);
        Commit(list);
        ClearPicked();
        searchText = string.Empty;
        addQuantity = 1;
    }

    private void RemoveRow(CustomList list, uint itemId)
    {
        deletePending = false;
        SetMessage(string.Empty);
        CustomListOps.RemoveItem(list, itemId);
        PruneExclusions(list);
        Commit(list);
    }

    private void RemoveGoal(CustomList list, uint itemId)
    {
        deletePending = false;
        SetMessage(string.Empty);
        CustomListOps.RemoveGoal(list, itemId);
        InvalidateMaterials();
        PruneExclusions(list);
        Commit(list);
    }

    /// <summary>
    /// Drops unticked ids that are no longer rows. With goals the rows depend on the recipes, so while the recipe index is
    /// still loading this only starts the background build and skips (a stale id is harmless and goes at the next prune);
    /// otherwise a click would build the index on the UI thread.
    /// </summary>
    private void PruneExclusions(CustomList list)
    {
        if (list.Goals.Count > 0 && recipes is LuminaRecipeData { IsResultIndexReady: false } data)
        {
            data.WarmUp();
            return;
        }
        CustomListOps.PruneExclusions(list, recipes);
    }

    private void AddPickedWithSubcrafts(CustomList list)
    {
        deletePending = false;
        SetMessage(string.Empty);
        // The recipe expansion runs here, on the click, never per frame. It checks that the goal can be added (alone and
        // together with the goals already in the list) before it is stored; a refusal changes nothing.
        var problem = CustomListOps.CheckAddGoal(list, pickedItemId, addQuantity, recipes);
        if (problem != null)
        {
            SetMessage(problem);
            return;
        }
        var result = RecipeExpander.ExpandToMaterials(recipes, pickedItemId, addQuantity);
        if (!result.Ok)
        {
            SetMessage(result.Error ?? "Could not add the materials");
            return;
        }
        CustomListOps.AddGoal(list, pickedItemId, addQuantity);
        InvalidateMaterials();
        PruneExclusions(list);
        Commit(list);
        var count = result.Items!.Count;
        notice = $"Added {ItemName(pickedItemId)} x{addQuantity} as a goal ({count} {(count == 1 ? "material" : "materials")})";
        ClearPicked();
        searchText = string.Empty;
        addQuantity = 1;
    }

    /// <summary>Saves the settings and makes an open window for the list read it again.</summary>
    private void Commit(CustomList list)
    {
        if (unsavedListId == list.Id)
            unsavedListId = null;
        InvalidateMaterials();
        config.Save();
        manager.RefreshCustomList(list.Id);
    }

    /// <summary>Saves a quantity edit that was never finished (the tab, the window or the selection changed mid-edit).</summary>
    private void FlushUnsaved()
    {
        if (unsavedListId == null)
            return;
        var id = unsavedListId;
        unsavedListId = null;
        try
        {
            if (config.FindCustomList(id) == null)
                return;
            config.Save();
            manager.RefreshCustomList(id);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Saving a list quantity failed");
            SetMessage("Could not save the list: " + ex.Message);
        }
    }

    /// <summary>
    /// True or false once known for the picked item; null while unknown. Looked up only when the recipe index already
    /// exists (a dictionary lookup, once per pick); before that the click builds the index.
    /// </summary>
    private bool? PickedCraftable()
    {
        if (pickedCraftable != null)
            return pickedCraftable;
        if (recipes is not LuminaRecipeData { IsResultIndexReady: true })
            return null;
        pickedCraftable = recipes.FindRecipeForItem(pickedItemId) != null;
        return pickedCraftable;
    }

    private string ItemName(uint itemId)
    {
        if (!itemNames.TryGetValue(itemId, out var name))
        {
            name = items.GetName(itemId);
            itemNames[itemId] = name;
        }
        return name;
    }

    /// <summary>A full-width selectable row whose text is drawn unformatted, so names containing '#' or '%' show as typed.</summary>
    private static bool SelectableText(string id, string text, bool selected)
    {
        var start = ImGui.GetCursorPos();
        var clicked = ImGui.Selectable(id, selected, ImGuiSelectableFlags.None, new Vector2(0, ImGui.GetTextLineHeight()));
        ImGui.SetCursorPos(start);
        ImGui.TextUnformatted(text);
        return clicked;
    }

    private static float ButtonWidth(string label)
        => ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2;

    /// <summary>Puts the next button on the same line when it fits in <paramref name="avail"/>, otherwise on a new line.</summary>
    private static void NextInRow(ref float used, float avail, string label)
    {
        var width = ButtonWidth(label);
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        if (used + spacing + width <= avail)
        {
            ImGui.SameLine();
            used += spacing + width;
        }
        else
        {
            used = width;
        }
    }

    private void DrawImportExport()
    {
        DrawImport();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawExport();
    }

    private void DrawIoMessages(bool forImport)
    {
        if (ioInImport != forImport)
            return;
        if (ioNotice.Length > 0)
            WrappedColored(new Vector4(0.5f, 0.9f, 0.5f, 1f), ioNotice);
        if (ioWarn.Length > 0)
            WrappedColored(new Vector4(1f, 0.6f, 0.3f, 1f), ioWarn);
    }

    private void DrawImport()
    {
        ImGui.TextUnformatted("Import");
        ImGui.PushTextWrapPos(0f);
        try
        {
            ImGui.TextDisabled(ImportHelpText);
        }
        finally
        {
            ImGui.PopTextWrapPos();
        }
        ImGui.InputTextMultiline("##paste", ref pasteText, PasteMaxLength, new Vector2(-1, 110));
        if ((importParsed || messageFromParse) && !string.Equals(pasteText, parsedFromText, StringComparison.Ordinal))
            ResetParsed();

        if (ImGui.Button("Paste from clipboard"))
            PasteFromClipboard();
        ImGui.SameLine();

        // The name index takes a while to build; never build it on the draw thread.
        var namesReady = items is not LuminaItemInfo lumina || lumina.IsNameIndexReady;
        ImGui.BeginDisabled(!namesReady);
        try
        {
            if (ImGui.Button("Parse")) ParsePaste();
        }
        finally
        {
            ImGui.EndDisabled();
        }
        if (!namesReady)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(LoadingNamesText);
        }

        if (importParsed)
        {
            ImGui.TextDisabled($"Detected: {KindLabel(importKind)} - {parsed.Count} {(parsed.Count == 1 ? "item" : "items")}");
            if (importError != null)
                ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f), importError);
            if (importCut)
            {
                ImGui.PushTextWrapPos(0f);
                try
                {
                    ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f), CutOffText);
                }
                finally
                {
                    ImGui.PopTextWrapPos();
                }
            }
        }

        if (unmatchedLines.Count > 0 && ImGui.CollapsingHeader($"Unmatched lines ({unmatchedLines.Count})###unmatched"))
        {
            foreach (var line in unmatchedLines)
                ImGui.BulletText(line);
        }

        if (parsed.Count > 0)
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##importName", "Name for the saved list...", ref importName, NameBoxMaxBytes);
            ImGui.Checkbox("Break down into raw materials", ref importBreakdown);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(BreakdownTooltip);
        }

        ImGui.BeginDisabled(parsed.Count == 0);
        try
        {
            if (ImGui.Button("Save as shopping list")) SaveImported();
            ImGui.SameLine();
            if (ImGui.Button("Open without saving")) OpenParsed();
        }
        finally
        {
            ImGui.EndDisabled();
        }
        DrawIoMessages(true);
    }

    private static string KindLabel(ImportKind kind) => kind switch
    {
        ImportKind.TeamcraftLink => "Teamcraft link",
        ImportKind.ArtisanList => "Artisan list",
        _ => "Item list",
    };

    private void ResetParsed()
    {
        importParsed = false;
        parsed = [];
        unmatchedLines = [];
        importError = null;
        importCut = false;
        if (messageFromParse)
            SetMessage(string.Empty);
        ioNotice = string.Empty;
        ioWarn = string.Empty;
    }

    private void PasteFromClipboard()
    {
        try
        {
            var clip = TextLimit.CutToBytes(ImGui.GetClipboardText() ?? string.Empty, PasteMaxTextBytes, out var wasCut);
            clipboardCutText = wasCut ? clip : null;
            pasteText = clip;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Reading the clipboard failed");
            SetMessage("Could not read the clipboard: " + ex.Message);
        }
    }

    private void ParsePaste()
    {
        parsedFromText = pasteText;
        try
        {
            SetMessage(string.Empty);
            var result = ImportParser.Parse(pasteText, items, recipeData);
            parsed = result.Requirements;
            unmatchedLines = result.Unmatched;
            importKind = result.Kind;
            importError = result.Error;
            importName = TextLimit.CutToChars(result.SuggestedName, CustomListOps.MaxNameLength);
            importCut = (clipboardCutText != null && string.Equals(pasteText, clipboardCutText, StringComparison.Ordinal))
                || System.Text.Encoding.UTF8.GetByteCount(pasteText) >= PasteMaxTextBytes - NearCapBytes;
            parsedFromText = pasteText;
            importParsed = true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Parsing the pasted text failed");
            SetMessage("Could not parse the text: " + ex.Message);
            messageFromParse = true;
        }
    }

    /// <summary>The parsed rows, or their raw materials when the box is ticked (computed on the click, never per frame).</summary>
    private IReadOnlyList<Requirement> ImportRows()
        => importBreakdown ? CustomListOps.Breakdown(recipes, parsed) : parsed;

    private void SaveImported()
    {
        try
        {
            SetMessage(string.Empty);
            var rows = ImportRows();
            if (rows.Count == 0)
            {
                SetMessage("Nothing to save.");
                return;
            }
            var list = CreateNamedList(importName, rows);
            if (list == null)
                return;
            var count = list.Items.Count;
            ioInImport = true;
            ioNotice = $"Saved {count} {(count == 1 ? "item" : "items")} as {list.Name}";
        }
        catch (Exception ex)
        {
            log.Error(ex, "Saving the imported list failed");
            SetMessage("Could not save the list: " + ex.Message);
        }
    }

    private void OpenParsed()
    {
        try
        {
            SetMessage(string.Empty);
            manager.OpenPasted(ImportRows());
        }
        catch (Exception ex)
        {
            log.Error(ex, "Opening the pasted list failed");
            SetMessage("Could not open the list: " + ex.Message);
        }
    }

    private void DrawExport()
    {
        ImGui.TextUnformatted("Export");

        // A selection whose list is gone (deleted, or no longer in Artisan) falls back to none.
        if (exportCustomId != null && config.FindCustomList(exportCustomId) == null)
            exportCustomId = null;
        var artisanName = exportArtisanId is { } aid ? ArtisanListName(aid) : null;
        if (exportArtisanId != null && artisanName == null)
            exportArtisanId = null;
        var customName = exportCustomId != null ? config.FindCustomList(exportCustomId)?.Name : null;

        ImGui.SetNextItemWidth(-1);
        var preview = customName ?? (artisanName != null ? "Artisan: " + artisanName : "Export which list");
        if (ImGui.BeginCombo("##exportList", preview))
        {
            try
            {
                DrawExportChoices();
            }
            finally
            {
                ImGui.EndCombo();
            }
        }
        if (lists.Count == 0)
            ImGui.TextDisabled("No Artisan lists are shown (Artisan not found, or its config could not be read).");

        var selected = exportCustomId != null || exportArtisanId != null;
        var isArtisan = exportArtisanId != null;

        ImGui.BeginDisabled(!selected);
        try
        {
            if (ImGui.Button("Open in Teamcraft")) OpenTeamcraftLink();
        }
        finally
        {
            ImGui.EndDisabled();
        }
        if (!selected && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(ChooseListFirstText);

        ImGui.SameLine();
        ImGui.BeginDisabled(!selected || isArtisan);
        try
        {
            if (ImGui.Button("Copy Artisan list")) CopyArtisanList();
        }
        finally
        {
            ImGui.EndDisabled();
        }
        if ((!selected || isArtisan) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(isArtisan ? ArtisanAlreadyText : ChooseListFirstText);

        ImGui.SameLine();
        ImGui.BeginDisabled(!selected);
        try
        {
            if (ImGui.Button("Share Teamcraft list")) CopyTeamcraftLink();
        }
        finally
        {
            ImGui.EndDisabled();
        }
        if (!selected && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(ChooseListFirstText);
        else if (ImGui.IsItemHovered())
            ImGui.SetTooltip(ShareTeamcraftTooltip);

        ImGui.PushTextWrapPos(0f);
        try
        {
            ImGui.TextDisabled(TeamcraftLinkHintText);
        }
        finally
        {
            ImGui.PopTextWrapPos();
        }

        DrawIoMessages(false);
    }

    private void DrawExportChoices()
    {
        var custom = config.CustomLists;
        if (custom.Count > 0)
        {
            ImGui.TextDisabled("My shopping lists");
            for (var i = 0; i < custom.Count; i++)
            {
                var l = custom[i];
                if (l is null)
                    continue;
                ImGui.PushID("c" + l.Id);
                try
                {
                    if (SelectableText("##row", l.Name, exportCustomId == l.Id))
                        SelectExport(l.Id, null);
                }
                finally
                {
                    ImGui.PopID();
                }
            }
        }
        if (lists.Count > 0)
        {
            ImGui.TextDisabled("Artisan lists");
            foreach (var l in lists)
            {
                ImGui.PushID("a" + l.Id);
                try
                {
                    if (SelectableText("##row", l.Name, exportArtisanId == l.Id))
                        SelectExport(null, l.Id);
                }
                finally
                {
                    ImGui.PopID();
                }
            }
        }
        if (custom.Count == 0 && lists.Count == 0)
            ImGui.TextDisabled("No lists to export.");
    }

    private void SelectExport(string? customId, int? artisanId)
    {
        exportCustomId = customId;
        exportArtisanId = artisanId;
        SetMessage(string.Empty);
    }

    private string? ArtisanListName(int id)
    {
        foreach (var l in lists)
            if (l.Id == id)
                return l.Name;
        return null;
    }

    /// <summary>The rows and name of the selected list, or null (with a message shown) when they cannot be read. Runs on the click only.</summary>
    private (IReadOnlyList<Requirement> Rows, string Name)? ExportRows()
    {
        if (exportCustomId != null)
        {
            var list = config.FindCustomList(exportCustomId);
            if (list == null)
            {
                SetMessage("That list no longer exists.");
                return null;
            }
            // The goals and the plain rows, not the goals' materials: Teamcraft gets the finished items, Artisan the craft tree.
            return (CustomListOps.ExportRows(list), list.Name);
        }
        if (exportArtisanId is { } id)
        {
            try
            {
                var list = artisan.LoadList(id);
                if (list == null)
                {
                    SetMessage("That Artisan list was not found.");
                    return null;
                }
                var crafted = ArtisanListItems.CraftedItems(list, recipeData);
                if (crafted.Count == 0 && list.Items.Count > 0)
                {
                    // CraftedItems also leaves out entries with fewer than 1 craft; the wording keeps to the two common causes.
                    SetMessage(AllSkippedText);
                    return null;
                }
                return (crafted, list.Name);
            }
            catch (ArtisanConfigUnreadableException ex)
            {
                log.Warning(ex, "Reading an Artisan list failed");
                SetMessage(UnreadableConfigText);
                return null;
            }
        }
        SetMessage(ChooseListFirstText);
        return null;
    }

    /// <summary>
    /// The Teamcraft link for the craftable rows only (<see cref="ExportFilter.CraftableOnly"/>, the rule the Artisan export uses),
    /// or null with a message shown when there is nothing to send. Recipe lookups run on the click only.
    /// </summary>
    private (string Link, CraftableRows Craftable)? BuildCraftableTeamcraftLink(IReadOnlyList<Requirement> rows)
    {
        var craftable = ExportFilter.CraftableOnly(rows, recipes);
        var link = craftable.Rows.Count == 0 ? null : TeamcraftLink.Build(craftable.Rows);
        if (link == null)
        {
            SetMessage(craftable.SkippedItemIds.Count > 0
                ? "None of these items can be crafted, so there is nothing to give Teamcraft."
                : NothingToExportText);
            return null;
        }
        return (link, craftable);
    }

    /// <summary>The orange note under a Teamcraft export: the items left out, and the long-link warning; empty when neither applies.</summary>
    private string TeamcraftWarning(CraftableRows craftable, string link)
    {
        var parts = new List<string>();
        if (craftable.SkippedItemIds.Count > 0)
            parts.Add(SkippedText(craftable.SkippedItemIds));
        if (link.Length > TeamcraftLink.LongLinkWarningLength)
            parts.Add("This link is very long. Some browsers may refuse it.");
        return string.Join(" ", parts);
    }

    private void CopyTeamcraftLink()
    {
        try
        {
            SetMessage(string.Empty);
            if (ExportRows() is not { } source)
                return;
            if (BuildCraftableTeamcraftLink(source.Rows) is not { } built)
                return;
            var (link, craftable) = built;
            ImGui.SetClipboardText(link);
            ioInImport = false;
            ioNotice = $"Copied a Teamcraft link for {craftable.Rows.Count} {(craftable.Rows.Count == 1 ? "item" : "items")}.";
            ioWarn = TeamcraftWarning(craftable, link);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Copying a Teamcraft link failed");
            SetMessage("Could not copy the link: " + ex.Message);
        }
    }

    private void OpenTeamcraftLink()
    {
        try
        {
            SetMessage(string.Empty);
            if (ExportRows() is not { } source)
                return;
            if (BuildCraftableTeamcraftLink(source.Rows) is not { } built)
                return;
            var (link, craftable) = built;
            if (!TeamcraftLink.IsSafeWebLink(link))
            {
                SetMessage("Could not open the link: it is not a web address.");
                return;
            }
            Dalamud.Utility.Util.OpenLink(link);
            ioInImport = false;
            ioNotice = $"Opened Teamcraft with {craftable.Rows.Count} {(craftable.Rows.Count == 1 ? "item" : "items")}.";
            ioWarn = TeamcraftWarning(craftable, link);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Opening a Teamcraft link failed");
            SetMessage("Could not open the link: " + ex.Message);
        }
    }

    private void CopyArtisanList()
    {
        try
        {
            SetMessage(string.Empty);
            if (ExportRows() is not { } source)
                return;
            if (source.Rows.Count == 0)
            {
                SetMessage(NothingToExportText);
                return;
            }
            var result = ArtisanListExport.Build(source.Name, source.Rows, recipes);
            if (result.Error != null)
            {
                SetMessage(result.Error);
                return;
            }
            if (result.Json.Length == 0)
            {
                SetMessage("None of these items can be crafted, so there is nothing to give Artisan.");
                return;
            }
            ImGui.SetClipboardText(result.Json);
            ioInImport = false;
            ioNotice = $"Copied an Artisan list with {result.CraftedItems} {(result.CraftedItems == 1 ? "item" : "items")}.";
            if (result.SkippedItemIds.Count > 0)
                ioWarn = SkippedText(result.SkippedItemIds);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Copying an Artisan list failed");
            SetMessage("Could not copy the list: " + ex.Message);
        }
    }

    private string SkippedText(IReadOnlyList<uint> ids)
    {
        var names = string.Join(", ", ids.Take(MaxSkippedNames).Select(ItemName));
        var more = ids.Count > MaxSkippedNames ? $", +{ids.Count - MaxSkippedNames} more" : string.Empty;
        return $"{ids.Count} {(ids.Count == 1 ? "item" : "items")} can't be crafted and {(ids.Count == 1 ? "was" : "were")} left out: {names}{more}";
    }

}
