using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using LunaShoppingList.Adapters;
using LunaShoppingList.Core;

namespace LunaShoppingList.Windows;

/// <summary>The entry window: pick one of Artisan's saved lists, or paste ingredients, and open it as its own shopping-list window.</summary>
public sealed class ListBrowserWindow : Window
{
    private const string NoListsText = "Artisan not found or no lists";
    private const string UnreadableConfigText = "Couldn't read Artisan's config, try again in a moment.";
    private const string LoadingNamesText = "Loading item names...";
    private const string PasteHelpText = "In Artisan, open the Ingredients tab and choose 'Export Required Ingredients as Plain Text' "
        + "(hold Shift for the full 'Required' amounts rather than 'Remaining'), then paste the result here.";
    private const int PasteMaxLength = 50000;
    private const int SearchMaxLength = 100;
    private const int SearchResultCount = 10;
    private const string AddWithSubcraftsTooltip = "Adds everything needed to craft this, down to the materials you gather or buy. The item itself is not added. Where several recipes make an item, the first recipe found is used.";
    private const string NotCraftableText = "This item cannot be crafted";
    // The text boxes count UTF-8 bytes; the 60-character name limit itself is checked when the name is used.
    private const int NameBoxMaxBytes = CustomListOps.MaxNameLength * 4;

    private readonly Configuration config;
    private readonly IArtisanListSource artisan;
    private readonly IItemInfo items;
    private readonly IItemCatalog catalog;
    private readonly IRecipeLookup recipes;
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

    internal ListBrowserWindow(Configuration config, IArtisanListSource artisan, IItemInfo items, IItemCatalog catalog,
        IRecipeLookup recipes, WindowManager manager, IPluginLog log)
        : base("Luna Shopping List##LunaShoppingListBrowser")
    {
        this.config = config;
        this.artisan = artisan;
        this.items = items;
        this.catalog = catalog;
        this.recipes = recipes;
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
                    if (ImGui.BeginTabItem("Paste"))
                    {
                        try
                        {
                            DrawPaste();
                        }
                        finally
                        {
                            ImGui.EndTabItem();
                        }
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
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f), message);
    }

    private void SetMessage(string text)
    {
        message = text;
        notice = string.Empty;
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
        if (hiddenCount > 0 && lists.Count > 0)
            ImGui.TextDisabled($"{hiddenCount} lists are hidden: lists that are not in Artisan's saved config (premade lists, or lists not saved yet). To use one, paste its ingredients.");
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
            ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.5f, 1f), notice);
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
                        if (SelectableText("##row", $"{l.Name}  ({count} {(count == 1 ? "item" : "items")})", l.Id == selectedListId))
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
        ImGui.BeginDisabled(list.Items.Count == 0);
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
        if (list.Items.Count == 0)
        {
            ImGui.TextDisabled("This list is empty: search for an item above and press Add.");
            return;
        }

        uint removeId = 0;
        var commitQuantity = false;
        var tableOpen = ImGui.BeginTable("##customItems", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable);
        try
        {
            if (tableOpen)
            {
                var style = ImGui.GetStyle();
                var padding = style.CellPadding.X * 2;
                ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed,
                    ImGui.CalcTextSize("9,999,999").X + style.FramePadding.X * 2 + (ImGui.GetFrameHeight() + style.ItemInnerSpacing.X) * 2 + padding);
                ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed, ButtonWidth("Remove") + padding);
                ImGui.TableHeadersRow();

                // Rows are only read here; a removal is applied after the loop.
                for (var i = 0; i < list.Items.Count; i++)
                {
                    var row = list.Items[i];
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
        }

        if (commitQuantity)
            Commit(list);
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
        deletePending = false;
        var result = CustomListOps.Create(config.CustomLists, newListName);
        if (result.List is not { } created)
        {
            SetMessage(result.Error ?? "Could not create the list");
            return;
        }
        SetMessage(string.Empty);
        config.CustomLists.Add(created);
        config.Save();
        newListName = string.Empty;
        SelectList(created.Id);
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
        CustomListOps.PruneExclusions(list);
        Commit(list);
    }

    private void AddPickedWithSubcrafts(CustomList list)
    {
        deletePending = false;
        SetMessage(string.Empty);
        // The recipe expansion runs here, on the click, never per frame.
        var result = RecipeExpander.ExpandToMaterials(recipes, pickedItemId, addQuantity);
        if (!result.Ok)
        {
            SetMessage(result.Error ?? "Could not add the materials");
            return;
        }
        var added = CustomListOps.AddMaterials(list, result.Items!);
        CustomListOps.PruneExclusions(list);
        Commit(list);
        var count = result.Items!.Count;
        notice = added == 0
            ? "Nothing added: every material is already at the maximum."
            : $"Added {count} {(count == 1 ? "material" : "materials")} for {ItemName(pickedItemId)} x{addQuantity}";
        ClearPicked();
        searchText = string.Empty;
        addQuantity = 1;
    }

    /// <summary>Saves the settings and makes an open window for the list read it again.</summary>
    private void Commit(CustomList list)
    {
        if (unsavedListId == list.Id)
            unsavedListId = null;
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

    private void DrawPaste()
    {
        ImGui.TextWrapped(PasteHelpText);
        ImGui.InputTextMultiline("##paste", ref pasteText, PasteMaxLength, new Vector2(-1, 160));

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

        if (unmatchedLines.Count > 0 && ImGui.CollapsingHeader($"Unmatched lines ({unmatchedLines.Count})###unmatched"))
        {
            foreach (var line in unmatchedLines)
                ImGui.BulletText(line);
        }

        ImGui.BeginDisabled(parsed.Count == 0);
        try
        {
            if (ImGui.Button("Open as shopping list")) OpenParsed();
        }
        finally
        {
            ImGui.EndDisabled();
        }
    }

    private void ParsePaste()
    {
        try
        {
            SetMessage(string.Empty);
            var result = PasteParser.Parse(pasteText, items);
            parsed = result.Requirements;
            unmatchedLines = result.UnmatchedLines;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Parsing pasted ingredients failed");
            SetMessage("Could not parse the text: " + ex.Message);
        }
    }

    private void OpenParsed()
    {
        try
        {
            SetMessage(string.Empty);
            manager.OpenPasted(parsed);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Opening the pasted list failed");
            SetMessage("Could not open the list: " + ex.Message);
        }
    }
}
