using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using LunaShoppingList.Adapters;
using LunaShoppingList.Core;
using LunaShoppingList.Windows;

namespace LunaShoppingList;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameInventory GameInventory { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;

    private const string CommandName = "/lsl";

    public readonly WindowSystem WindowSystem = new("LunaShoppingList");
    private ListBrowserWindow Browser { get; init; }
    private WindowManager Windows { get; init; }

    internal Configuration Configuration { get; }
    internal LuminaRecipeData RecipeData { get; }
    internal LuminaItemInfo ItemInfo { get; }
    internal LuminaItemSources ItemSources { get; }
    internal DalamudWorldData WorldData { get; }
    internal ArtisanListSource ArtisanLists { get; }
    internal HttpClientGetter HttpGetter { get; }
    internal ShoppingListService ShoppingService { get; }
    internal OwnedItemsService OwnedItems { get; }
    internal LifestreamService Lifestream { get; }
    private readonly DiagnosticDump diagnosticDump;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        RecipeData = new LuminaRecipeData(DataManager, Log);
        RecipeData.WarmUp();
        ItemInfo = new LuminaItemInfo(DataManager, Log);
        ItemInfo.WarmUp();
        ItemSources = new LuminaItemSources(DataManager, Log);
        ItemSources.WarmUp();
        WorldData = new DalamudWorldData(PlayerState, DataManager);
        ArtisanLists = new ArtisanListSource(PluginInterface, Log);
        HttpGetter = new HttpClientGetter();
        var universalis = new UniversalisClient(HttpGetter, TimeSpan.FromMinutes(10), () => DateTimeOffset.UtcNow);
        // Items not sold on the market board are never sent to Universalis; until the item index is built every item counts as marketable.
        ShoppingService = new ShoppingListService(WorldData, universalis, ItemInfo.IsMarketable);
        OwnedItems = new OwnedItemsService(PluginInterface, Framework, GameInventory, GameGui, PlayerState, Condition, Log);
        Lifestream = new LifestreamService(PluginInterface, Log);
        diagnosticDump = new DiagnosticDump(DataManager, PlayerState, Log, ChatGui, ArtisanLists, ItemInfo);

        Windows = new WindowManager(WindowSystem, Configuration, ArtisanLists, ItemInfo, RecipeData, WorldData, ShoppingService,
            new MaterialsCalculator(RecipeData), OwnedItems, Lifestream, ItemSources, Log);
        Browser = new ListBrowserWindow(Configuration, ArtisanLists, ItemInfo, ItemInfo, RecipeData, RecipeData, Windows, Log);
        WindowSystem.AddWindow(Browser);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Luna Shopping List window; '/lsl dump' writes the diagnostic dump"
        });

        PluginInterface.UiBuilder.Draw += DrawUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
    }

    public void Dispose()
    {
        // Each step is independent: one that throws must not skip the ones after it (above all the final save).
        Step("remove UI callbacks", () =>
        {
            PluginInterface.UiBuilder.Draw -= DrawUi;
            PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
            PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        });
        // Stops a vendor index build still running and silences its log.
        Step("dispose item sources", ItemSources.Dispose);
        // Cancels every running build and waits briefly for it, so they are done with the HTTP client first.
        Step("dispose windows", Windows.Dispose);
        Step("remove windows", WindowSystem.RemoveAllWindows);
        // Stops counting and writes any retainer or saddlebag record not yet saved.
        Step("dispose owned items", OwnedItems.Dispose);
        Step("remove command", () => CommandManager.RemoveHandler(CommandName));
        Step("dispose HTTP getter", HttpGetter.Dispose);
    }

    private static void Step(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unloading failed at step: {Step}", what);
        }
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("dump", StringComparison.OrdinalIgnoreCase))
        {
            // The dump looks items up by name; refuse rather than build the name index on the game thread.
            if (ItemInfo.IsNameIndexReady)
                diagnosticDump.Run();
            else
                ChatGui.Print("Item names are still loading; try /lsl dump again in a moment.", "LSL");
        }
        else
            Browser.Toggle();
    }

    private void DrawUi()
    {
        // Window changes queued during the last frame are applied here, never inside the window system's draw.
        Windows.ProcessPending();
        WindowSystem.Draw();
    }

    public void ToggleMainUi() => Browser.Toggle();
}
