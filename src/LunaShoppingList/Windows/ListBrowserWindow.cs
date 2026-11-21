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

    private readonly Configuration config;
    private readonly IArtisanListSource artisan;
    private readonly IItemInfo items;
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

    internal ListBrowserWindow(Configuration config, IArtisanListSource artisan, IItemInfo items, WindowManager manager, IPluginLog log)
        : base("Luna Shopping List##LunaShoppingListBrowser")
    {
        this.config = config;
        this.artisan = artisan;
        this.items = items;
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

    public override void Draw()
    {
        try
        {
            // Every Begin is paired with its End in a finally, so an exception never leaves the ImGui stack unbalanced.
            if (ImGui.BeginTabBar("##lslTabs"))
            {
                try
                {
                    if (ImGui.BeginTabItem("My Lists"))
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
            message = "Something went wrong: " + ex.Message;
        }

        if (message.Length > 0)
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f), message);
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
            message = string.Empty;
            lists = artisan.GetLists();
            hiddenCount = artisan is ArtisanListSource source ? source.HiddenListCount : 0;
            config.PruneExcludedLists(lists.Select(l => l.Id));
        }
        catch (ArtisanConfigUnreadableException ex)
        {
            log.Warning(ex, "Reading Artisan's lists failed");
            message = UnreadableConfigText;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Reading Artisan's lists failed");
            message = "Could not read the lists: " + ex.Message;
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
            message = string.Empty;
            var result = PasteParser.Parse(pasteText, items);
            parsed = result.Requirements;
            unmatchedLines = result.UnmatchedLines;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Parsing pasted ingredients failed");
            message = "Could not parse the text: " + ex.Message;
        }
    }

    private void OpenParsed()
    {
        try
        {
            message = string.Empty;
            manager.OpenPasted(parsed);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Opening the pasted list failed");
            message = "Could not open the list: " + ex.Message;
        }
    }
}
