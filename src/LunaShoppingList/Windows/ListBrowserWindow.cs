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
    private const string UnreadableConfigText = "Could not read Artisan's config. Try again in a moment.";
    private const string LoadingNamesText = "Loading item names...";
    private const string ImportHelpText = "Paste a Teamcraft import link, an Artisan list copied with Export List, or lines like 3x Iron Ore.";
    private const string BreakdownTooltip = "Replaces each craftable item with its raw materials. Items that cannot be crafted stay as they are.";
    private const string TypeNameFirstText = "Type a name first.";
    private const string PickItemFirstText = "Pick an item from the results first.";
    private const string AddItemFirstText = "Add an item first.";
    private const string ParseFirstText = "Parse some text first.";
    private const string OpenTeamcraftTooltip = "Opens Teamcraft in your browser with the craftable items.";
    private const string CopyForArtisanTooltip = "Copies the list for Artisan's import. Only craftable items.";
    private const string ConfirmDeleteText = "Confirm delete";
    private const string UntickLabel = "Untick crafted items when a list opens";
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
    private const string AddWithSubcraftsTooltip = "Keeps the item as a goal and adds the raw materials it needs, so the list can go to Artisan with its subcrafts. The item itself isn't a shopping row. Where several recipes make it, the first one is used.";
    private const string GoalsNoteText = "The materials for these goals are added to the shopping list automatically.";
    private const string RecipesLoadingText = "Recipe data is still loading...";
    private const string RecipesFailedText = "Recipe data could not be loaded. Check /xllog, then try again.";
    private const string TeamcraftLinkHintText = "Copy Teamcraft link is for sending to someone else. They paste it into a browser's address bar. It won't work in Teamcraft's import dialog.";
    private const string ShareTeamcraftTooltip = "Copies a link you can send to someone. They open it in a browser. It won't work in Teamcraft's own import dialog. To use it yourself, press Open in Teamcraft.";
    private const string ExcludeCraftedTooltip = "When a list opens, craftable ingredients start unticked. Your saved ticks still apply. Crystals, shards and clusters are never touched.";
    private const string NotCraftableText = "This item cannot be crafted";
    private const string AllaganMissingText = "Allagan Tools isn't loaded, or hasn't finished starting. Install it, or wait a moment and press Refresh lists.";
    private const string AllaganNoListsText = "Allagan Tools has no craft lists yet. Make one there, then press Refresh lists.";
    private const string AllaganPickFirstText = "Click a list first.";
    private const string AllaganSaveTooltip = "Keeps this Allagan Tools list as one of your shopping lists. Its finished items stay goals, so the materials follow the recipes.";
    private const string AllaganHelpText = "Click a list to open it. Only its finished items are read, and normal and high quality are counted together.";
    private const string SendToAllaganTooltip = "Creates a new craft list in Allagan Tools with this list's goals. Allagan Tools can't delete it again, so sending twice makes two lists. High quality isn't kept.";
    private const string SentRecentlyText = "Sent a moment ago. Allagan Tools can't delete a list, so sending again makes another one.";
    private const long SentGuardMs = 5000;
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
    private readonly ICraftListSource craftLists;

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

    // Allagan Tools lists tab. Availability and names are read from the other plugin only when the window or a tab opens, on
    // Refresh lists and after a send, never per frame.
    private bool craftAvailable;
    private IReadOnlyList<CraftListInfo> craftInfos = [];
    private string craftFilter = string.Empty;
    private string? craftSelectedKey;
    private string craftNotice = string.Empty;
    private string craftWarn = string.Empty;
    private bool allaganTabOpen;
    private bool exportTabOpen;
    // The export selection ("c" + list id, or "a" + Artisan list id) last sent to Allagan Tools and when, so one click never sends twice.
    private string? sentSelection;
    private long sentAtMs;

    // 'My shopping lists' tab state. Everything below belongs to the selected list and is reset when the selection changes.
    private string newListName = string.Empty;
    private string? selectedListId;
    private bool renaming;
    // Set when renaming starts, so the next frame puts the cursor in the name box.
    private bool focusRename;
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
        IRecipeLookup recipes, IRecipeData recipeData, WindowManager manager, ICraftListSource craftLists, IPluginLog log)
        : base("Luna Shopping List##LunaShoppingListBrowser")
    {
        this.config = config;
        this.artisan = artisan;
        this.items = items;
        this.catalog = catalog;
        this.recipes = recipes;
        this.recipeData = recipeData;
        this.manager = manager;
        this.craftLists = craftLists;
        this.log = log;
        Size = new Vector2(380, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnOpen()
    {
        // One IPC call per opening, never per frame.
        RefreshLists();
        RefreshCraftLists();
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
            DrawSharedSettings();
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
                    if (ImGui.BeginTabItem("Allagan Tools lists###lslAllagan"))
                    {
                        try
                        {
                            // One refresh when the tab opens, never per frame.
                            if (!allaganTabOpen)
                            {
                                allaganTabOpen = true;
                                RefreshCraftLists();
                            }
                            DrawAllaganLists();
                        }
                        finally
                        {
                            ImGui.EndTabItem();
                        }
                    }
                    else
                    {
                        allaganTabOpen = false;
                        craftNotice = string.Empty;
                        craftWarn = string.Empty;
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
                            // The export row needs to know whether Allagan Tools is ready: one refresh when the tab opens.
                            if (!exportTabOpen)
                            {
                                exportTabOpen = true;
                                RefreshCraftLists();
                            }
                            DrawImportExport();
                        }
                        finally
                        {
                            ImGui.EndTabItem();
                        }
                    }
                    else
                    {
                        exportTabOpen = false;
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
                ? $"Artisan has no saved lists of yours. {Plural.Of(hiddenCount, "list")} {(hiddenCount == 1 ? "is" : "are")} hidden because they aren't in Artisan's saved config. To use a premade list, paste its ingredients."
                : NoListsText);
        }
        else
        {
            // Room for the Refresh lists row (the two shared settings now sit above the tab bar) and the hidden-lists note.
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

        if (ImGui.Button("Refresh lists")) RefreshLists();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Reads Artisan's saved lists again.");
        if (hiddenCount > 0 && lists.Count > 0)
        {
            ImGui.PushTextWrapPos(0f);
            try
            {
                ImGui.TextDisabled($"{Plural.Of(hiddenCount, "list")} {(hiddenCount == 1 ? "is" : "are")} hidden because they're not in Artisan's saved config (premade lists, or lists not saved yet). To use {(hiddenCount == 1 ? "it" : "one")}, paste its ingredients.");
            }
            finally
            {
                ImGui.PopTextWrapPos();
            }
        }
    }

    private const int MaxListingAgeLimit = 720;

    /// <summary>The two settings that apply to every list, above the tab bar so they show on every tab.</summary>
    private void DrawSharedSettings()
    {
        var excludeCrafted = config.ExcludeCraftedItems;
        if (ImGui.Checkbox(UntickLabel, ref excludeCrafted))
        {
            config.ExcludeCraftedItems = excludeCrafted;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(ExcludeCraftedTooltip);
        DrawListingAge();
        ImGui.Spacing();
    }

    /// <summary>
    /// The listing age setting: an integer box on a local copy (an ImGui int box edits the variable it is given, so the saved value
    /// is only written when an edit happened). The value is kept inside 0 to 720 and saved once the box is left, not on every key.
    /// </summary>
    private void DrawListingAge()
    {
        var hours = Math.Clamp(config.MaxListingAgeHours, 0, MaxListingAgeLimit);
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 9f);
        if (ImGui.InputInt("Ignore listings older than (hours)", ref hours, 1, 24))
            config.MaxListingAgeHours = Math.Clamp(hours, 0, MaxListingAgeLimit);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Listings older than this are skipped when pricing. Set 0 to turn this off.");
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

    // ---- Allagan Tools lists tab ----

    /// <summary>
    /// Asks Allagan Tools whether it is ready and for its list names. Called when the window opens, when the Allagan Tools lists or
    /// Import / Export tab opens, after a send, and by Refresh lists: never per frame, because the readiness check costs several IPC calls.
    /// </summary>
    private void RefreshCraftLists()
    {
        try
        {
            craftAvailable = craftLists.IsAvailable;
            craftInfos = craftAvailable ? craftLists.GetLists() : [];
        }
        catch (Exception ex)
        {
            log.Error(ex, "Reading Allagan Tools' craft lists failed");
            craftAvailable = false;
            craftInfos = [];
        }
        if (craftSelectedKey != null && !craftInfos.Any(l => l.Key == craftSelectedKey))
            craftSelectedKey = null;
    }

    private void DrawAllaganLists()
    {
        if (!craftAvailable)
        {
            ImGui.TextWrapped(AllaganMissingText);
            if (ImGui.Button("Refresh lists##craftRefresh"))
                PressCraftRefresh();
            return;
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##craftFilter", "Filter...", ref craftFilter, 100);

        if (craftInfos.Count == 0)
        {
            ImGui.TextWrapped(AllaganNoListsText);
        }
        else
        {
            var messageLines = craftNotice.Length > 0 || craftWarn.Length > 0 ? 3 : 0;
            var footer = ImGui.GetFrameHeightWithSpacing() * 2 + ImGui.GetTextLineHeightWithSpacing() * (3 + messageLines);
            var height = Math.Max(ImGui.GetTextLineHeightWithSpacing() * 4, ImGui.GetContentRegionAvail().Y - footer);
            CraftListInfo? clicked = null;
            // EndChild is called whatever BeginChild returned.
            try
            {
                if (ImGui.BeginChild("##craftScroll", new Vector2(-1, height), true))
                {
                    foreach (var l in craftInfos)
                    {
                        if (craftFilter.Length > 0 && !l.Name.Contains(craftFilter, StringComparison.OrdinalIgnoreCase))
                            continue;
                        ImGui.PushID("k" + l.Key);
                        try
                        {
                            if (SelectableText("##row", l.Name, l.Key == craftSelectedKey))
                                clicked = l;
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
            {
                craftSelectedKey = clicked.Key;
                OpenCraftList(clicked);
            }
        }

        var avail = ImGui.GetContentRegionAvail().X;
        if (ImGui.Button("Refresh lists##craftRefresh"))
            PressCraftRefresh();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Reads Allagan Tools' craft lists again.");
        var used = ButtonWidth("Refresh lists");
        NextInRow(ref used, avail, "Save as shopping list");
        var noSelection = craftSelectedKey == null;
        ImGui.BeginDisabled(noSelection);
        try
        {
            if (ImGui.Button("Save as shopping list##craftSave"))
                SaveCraftList();
        }
        finally
        {
            ImGui.EndDisabled();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(noSelection ? AllaganPickFirstText : AllaganSaveTooltip);

        ImGui.PushTextWrapPos(0f);
        try
        {
            ImGui.TextDisabled(AllaganHelpText);
        }
        finally
        {
            ImGui.PopTextWrapPos();
        }
        if (craftNotice.Length > 0)
            WrappedColored(new Vector4(0.5f, 0.9f, 0.5f, 1f), craftNotice);
        if (craftWarn.Length > 0)
            WrappedColored(new Vector4(1f, 0.6f, 0.3f, 1f), craftWarn);
    }

    private void PressCraftRefresh()
    {
        craftNotice = string.Empty;
        craftWarn = string.Empty;
        RefreshCraftLists();
    }

    /// <summary>
    /// Reads one Allagan Tools list (on the click only) as a temporary list: finished items that have a recipe become goals, the
    /// others plain rows, so nothing is dropped. Null, with the reason in <see cref="craftWarn"/>, when it cannot be read.
    /// </summary>
    private CustomList? ReadCraftList(CraftListInfo info, bool forSave = false)
    {
        SetMessage(string.Empty);
        craftNotice = string.Empty;
        craftWarn = string.Empty;
        // Working out recipes would build the recipe index on the draw thread: start it in the background and ask for another click.
        if (recipes is LuminaRecipeData { IsResultIndexReady: false } data)
        {
            if (data.IsResultIndexFailed)
            {
                data.RetryNow();
                craftWarn = RecipesFailedText;
            }
            else
            {
                data.WarmUp();
                craftWarn = RecipesLoadingText + (forSave ? " Press the button again in a moment." : " Click the list again in a moment.");
            }
            return null;
        }
        var read = craftLists.GetGoals(info.Key);
        if (!read.Ok)
        {
            // An unknown key means the list was removed or renamed since the list of lists was read.
            craftWarn = read.Message == CraftListOps.UnknownListMessage ? read.Message + " Press Refresh lists." : read.Message;
            return null;
        }
        if (read.Goals.Count == 0)
        {
            craftWarn = $"\"{info.Name}\" has no finished items to shop for.";
            return null;
        }
        var list = new CustomList { Name = info.Name };
        foreach (var g in read.Goals)
        {
            var recipe = recipes.FindRecipeForItem(g.ItemId);
            if (recipe != null && recipe.Ingredients.Any(i => i.ItemId != 0 && i.Amount >= 1))
                CustomListOps.AddGoal(list, g.ItemId, g.Quantity);
            else
                CustomListOps.AddItem(list, g.ItemId, g.Quantity);
        }
        return list;
    }

    /// <summary>Opens an Allagan Tools list in its own list window, its materials worked out from its goals.</summary>
    private void OpenCraftList(CraftListInfo info)
    {
        try
        {
            if (ReadCraftList(info) is not { } list)
                return;
            var effective = CustomListOps.EffectiveRequirements(list, recipes);
            if (effective.Requirements.Count == 0)
            {
                craftWarn = effective.Problems.Count > 0
                    ? "Could not work out the materials: " + effective.Problems[0].Error + "."
                    : "There is nothing to shop for in that list.";
                return;
            }
            manager.OpenFromCraftList(info.Name, effective.Requirements);
            if (effective.Problems.Count > 0)
            {
                var left = string.Join(", ", effective.Problems.Take(MaxSkippedNames).Select(p => $"{ItemName(p.ItemId)} ({p.Error})"));
                var more = effective.Problems.Count > MaxSkippedNames ? $", +{effective.Problems.Count - MaxSkippedNames} more" : string.Empty;
                craftWarn = "Left out: " + left + more;
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Opening an Allagan Tools list failed");
            craftWarn = "Could not open the list: " + ex.Message;
        }
    }

    /// <summary>Saves the selected Allagan Tools list as a shopping list of its own: goals stay goals, so the materials follow the recipes.</summary>
    private void SaveCraftList()
    {
        try
        {
            var info = craftInfos.FirstOrDefault(l => l.Key == craftSelectedKey);
            if (info == null)
            {
                craftWarn = AllaganPickFirstText;
                return;
            }
            if (ReadCraftList(info, forSave: true) is not { } list)
                return;
            var saved = CreateNamedList(TextLimit.CutToChars(info.Name, CustomListOps.MaxNameLength), null);
            if (saved == null)
                return;
            saved.Goals.AddRange(list.Goals);
            saved.Items.AddRange(list.Items);
            InvalidateMaterials();
            config.Save();
            var count = saved.Goals.Count + saved.Items.Count;
            craftNotice = $"Saved {Plural.Of(count, "item")} as {saved.Name} in My shopping lists.";
        }
        catch (Exception ex)
        {
            log.Error(ex, "Saving an Allagan Tools list failed");
            craftWarn = "Could not save the list: " + ex.Message;
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
        // Enter creates the list, like pressing Create.
        var enter = ImGui.InputTextWithHint("##newListName", "Name your list...", ref newListName, NameBoxMaxBytes, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        var blank = string.IsNullOrWhiteSpace(newListName);
        ImGui.BeginDisabled(blank);
        var create = false;
        try
        {
            create = ImGui.Button("Create");
        }
        finally
        {
            ImGui.EndDisabled();
        }
        if (blank && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(TypeNameFirstText);
        if ((create || enter) && !blank)
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
        var emptyList = list.Items.Count == 0 && list.Goals.Count == 0;
        ImGui.BeginDisabled(emptyList);
        try
        {
            if (ImGui.Button("Shop for this list"))
                ShopFor(list);
        }
        finally
        {
            ImGui.EndDisabled();
        }
        if (emptyList && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(AddItemFirstText);
        var used = ButtonWidth("Shop for this list");

        if (!renaming)
        {
            NextInRow(ref used, avail, "Rename");
            if (ImGui.Button("Rename"))
            {
                deletePending = false;
                renaming = true;
                focusRename = true;
                renameText = list.Name;
            }
        }

        // Two-step delete: the first click arms it, a second click on the same button deletes, any other click cancels.
        // Measured with the longer label in both states, so the button never moves to a new line when it is armed.
        NextInRow(ref used, avail, ConfirmDeleteText);
        var deleteClicked = ImGui.Button(deletePending ? ConfirmDeleteText + "###delete" : "Delete###delete");
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
        if (focusRename)
        {
            ImGui.SetKeyboardFocusHere();
            focusRename = false;
        }
        // Enter saves, like pressing Save.
        var enter = ImGui.InputText("##rename", ref renameText, NameBoxMaxBytes, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        var blank = string.IsNullOrWhiteSpace(renameText);
        ImGui.BeginDisabled(blank);
        var save = false;
        try
        {
            save = ImGui.Button("Save");
        }
        finally
        {
            ImGui.EndDisabled();
        }
        if (blank && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(TypeNameFirstText);
        if ((save || enter) && !blank)
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
        var quantityWidth = ImGui.CalcTextSize("Qty").X;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Qty");
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
        if (pickedItemId == 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(PickItemFirstText);

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
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(pickedItemId == 0 ? PickItemFirstText : craftable == false ? NotCraftableText : AddWithSubcraftsTooltip);
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
                {
                    // The retry guard gives up after three attempts, so an explicit click restarts it.
                    if (recipes is LuminaRecipeData retryData)
                        retryData.RetryNow();
                    InvalidateMaterials();
                }
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

        var ioAvail = ImGui.GetContentRegionAvail().X;
        if (ImGui.Button("Paste from clipboard"))
            PasteFromClipboard();
        var ioUsed = ButtonWidth("Paste from clipboard");
        NextInRow(ref ioUsed, ioAvail, "Parse");

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
        if (!namesReady && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(LoadingNamesText);
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

        var nothingParsed = parsed.Count == 0;
        var saveAvail = ImGui.GetContentRegionAvail().X;
        ImGui.BeginDisabled(nothingParsed);
        try
        {
            if (ImGui.Button("Save as shopping list")) SaveImported();
        }
        finally
        {
            ImGui.EndDisabled();
        }
        if (nothingParsed && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(ParseFirstText);
        var saveUsed = ButtonWidth("Save as shopping list");
        NextInRow(ref saveUsed, saveAvail, "Open without saving");
        ImGui.BeginDisabled(nothingParsed);
        try
        {
            if (ImGui.Button("Open without saving")) OpenParsed();
        }
        finally
        {
            ImGui.EndDisabled();
        }
        if (nothingParsed && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(ParseFirstText);
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

        // Buttons wrap to a new line instead of being clipped at the default width.
        var exportAvail = ImGui.GetContentRegionAvail().X;
        ImGui.BeginDisabled(!selected);
        try
        {
            if (ImGui.Button("Open in Teamcraft")) OpenTeamcraftLink();
        }
        finally
        {
            ImGui.EndDisabled();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(selected ? OpenTeamcraftTooltip : ChooseListFirstText);
        var exportUsed = ButtonWidth("Open in Teamcraft");

        NextInRow(ref exportUsed, exportAvail, "Copy Teamcraft link");
        ImGui.BeginDisabled(!selected);
        try
        {
            if (ImGui.Button("Copy Teamcraft link")) CopyTeamcraftLink();
        }
        finally
        {
            ImGui.EndDisabled();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(selected ? ShareTeamcraftTooltip : ChooseListFirstText);

        NextInRow(ref exportUsed, exportAvail, "Copy for Artisan");
        ImGui.BeginDisabled(!selected || isArtisan);
        try
        {
            if (ImGui.Button("Copy for Artisan")) CopyArtisanList();
        }
        finally
        {
            ImGui.EndDisabled();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(isArtisan ? ArtisanAlreadyText : !selected ? ChooseListFirstText : CopyForArtisanTooltip);

        NextInRow(ref exportUsed, exportAvail, "Send to Allagan Tools");
        var sendBlock = SendBlockReason(selected);
        ImGui.BeginDisabled(sendBlock != null);
        try
        {
            if (ImGui.Button("Send to Allagan Tools")) SendToAllagan();
        }
        finally
        {
            ImGui.EndDisabled();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(sendBlock ?? SendToAllaganTooltip);

        // Names are compared from the cached list, so this costs no IPC.
        if (craftAvailable && selected && ExportDisplayName() is { } sendName
            && craftInfos.Any(l => string.Equals(l.Name, CraftListOps.ExportName(sendName), StringComparison.OrdinalIgnoreCase)))
        {
            WrappedColored(new Vector4(1f, 0.6f, 0.3f, 1f),
                $"Allagan Tools already has a craft list called \"{CraftListOps.ExportName(sendName)}\". Sending again makes a second one.");
        }

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

    private string CurrentSelectionKey() => exportCustomId != null ? "c" + exportCustomId : "a" + exportArtisanId;

    /// <summary>The name of the selected list for the Allagan Tools list it would make, or null with no selection.</summary>
    private string? ExportDisplayName()
        => exportCustomId != null
            ? config.FindCustomList(exportCustomId)?.Name
            : exportArtisanId is { } id ? ArtisanListName(id) : null;

    /// <summary>Why Send to Allagan Tools cannot be pressed right now, or null when it can. Cheap: no IPC and no file reads.</summary>
    private string? SendBlockReason(bool selected)
    {
        if (!craftAvailable)
            return AllaganMissingText;
        if (!selected)
            return ChooseListFirstText;
        if (exportCustomId != null && CraftListOps.ForExport(config.FindCustomList(exportCustomId)).Count == 0)
            return NothingToExportText;
        if (sentSelection == CurrentSelectionKey() && Environment.TickCount64 - sentAtMs < SentGuardMs)
            return SentRecentlyText;
        return null;
    }

    /// <summary>
    /// Sends the selected list to Allagan Tools as a new craft list (its goals, or its plain rows when it has none). An Artisan list
    /// sends the finished items it crafts. Runs on the click only.
    /// </summary>
    private void SendToAllagan()
    {
        try
        {
            SetMessage(string.Empty);
            if (!craftAvailable)
                return;
            var key = CurrentSelectionKey();
            IReadOnlyList<Requirement> goals;
            string name;
            if (exportCustomId != null)
            {
                if (config.FindCustomList(exportCustomId) is not { } list)
                {
                    SetMessage("That list no longer exists.");
                    return;
                }
                goals = CraftListOps.ForExport(list);
                name = list.Name;
            }
            else
            {
                if (ExportRows() is not { } source)
                    return;
                var temporary = new CustomList();
                foreach (var r in source.Rows)
                    CustomListOps.AddItem(temporary, r.ItemId, r.Quantity);
                goals = CraftListOps.ForExport(temporary);
                name = source.Name;
            }
            ioInImport = false;
            if (goals.Count == 0)
            {
                ioWarn = NothingToExportText;
                return;
            }
            var result = craftLists.CreateList(CraftListOps.ExportName(name), goals);
            if (result.Status != CraftListStatus.NotAvailable)
            {
                // A failed send may still have left a list behind, so a failure blocks a quick second click too.
                sentSelection = key;
                sentAtMs = Environment.TickCount64;
            }
            var text = result.Message.EndsWith('.') ? result.Message : result.Message + ".";
            if (result.Ok)
                ioNotice = text;
            else
                ioWarn = text;
            // The new list's name now shows in the duplicate warning.
            RefreshCraftLists();
        }
        catch (Exception ex)
        {
            log.Error(ex, "Sending a list to Allagan Tools failed");
            SetMessage("Could not send the list: " + ex.Message);
        }
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
