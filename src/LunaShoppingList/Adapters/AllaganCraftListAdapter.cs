using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using LunaShoppingList.Core;

namespace LunaShoppingList.Adapters;

/// <summary>
/// Reads and creates the player's Allagan Tools craft lists over its IPC. Thin on purpose: everything that can be decided
/// without the game (ordering, summing, the name, verifying a created list) is in <see cref="CraftListOps"/>. The subscribers are
/// built once. Allagan Tools registers its handlers without any thread marshalling, so every call here must come from the framework
/// thread (the draw thread is one). Nothing throws: not installed or not ready is a normal state and is silent, and any other
/// exception is logged once per distinct exception type at Warning, naming the gate. Compile-verified only.
/// </summary>
internal sealed class AllaganCraftListAdapter : ICraftListSource
{
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<bool> isInitialized;
    private readonly ICallGateSubscriber<Dictionary<string, string>> getCraftLists;
    private readonly ICallGateSubscriber<string, Dictionary<uint, uint>> getCraftItems;
    private readonly ICallGateSubscriber<string, Dictionary<uint, uint>, string> addNewCraftList;
    private readonly HashSet<string> loggedErrorTypes = [];

    public AllaganCraftListAdapter(IDalamudPluginInterface pi, IPluginLog log)
    {
        this.log = log;
        isInitialized = pi.GetIpcSubscriber<bool>("AllaganTools.IsInitialized");
        getCraftLists = pi.GetIpcSubscriber<Dictionary<string, string>>("AllaganTools.GetCraftLists");
        getCraftItems = pi.GetIpcSubscriber<string, Dictionary<uint, uint>>("AllaganTools.GetCraftItems");
        addNewCraftList = pi.GetIpcSubscriber<string, Dictionary<uint, uint>, string>("AllaganTools.AddNewCraftList");
    }

    /// <inheritdoc />
    public bool IsAvailable => Ready();

    /// <inheritdoc />
    public IReadOnlyList<CraftListInfo> GetLists()
        => Ready() && TryCall("GetCraftLists", () => getCraftLists.InvokeFunc(), out var answer) ? CraftListOps.ToLists(answer) : [];

    /// <inheritdoc />
    public CraftListGoals GetGoals(string key)
    {
        if (!Ready() || !TryCall("GetCraftLists", () => getCraftLists.InvokeFunc(), out var answer))
            return new CraftListGoals(CraftListStatus.NotAvailable, CraftListOps.NotAvailableMessage, []);
        var lists = CraftListOps.ToLists(answer);
        // An unknown list gives an empty dictionary, not an error, so the list set is what tells "unknown" from "empty".
        if (!lists.Any(l => string.Equals(l.Key, key, StringComparison.Ordinal)))
            return CraftListOps.ReadResult(key, lists, null);
        if (!TryCall("GetCraftItems", () => getCraftItems.InvokeFunc(key), out var items))
            return new CraftListGoals(CraftListStatus.NotAvailable, CraftListOps.NotAvailableMessage, []);
        return CraftListOps.ReadResult(key, lists, items);
    }

    /// <inheritdoc />
    public CraftListResult CreateList(string name, IReadOnlyList<Requirement> goals)
    {
        if (!Ready())
            return new CraftListResult(CraftListStatus.NotAvailable, CraftListOps.NotAvailableMessage);
        var items = CraftListOps.ToIpcItems(goals);
        if (items.Count == 0)
            return new CraftListResult(CraftListStatus.Failed, CraftListOps.NothingToSendMessage);
        var safeName = CraftListOps.ExportName(name);
        if (!TryCall("AddNewCraftList", () => addNewCraftList.InvokeFunc(safeName, items), out var key))
            return new CraftListResult(CraftListStatus.Failed, CraftListOps.SendThrewMessage);
        if (string.IsNullOrWhiteSpace(key))
            return CraftListOps.VerifyCreated(safeName, key, goals, null);
        // The write gates report nothing useful, so the only proof is to read the new list back and compare it.
        if (!TryCall("GetCraftItems", () => getCraftItems.InvokeFunc(key), out var reread))
            return new CraftListResult(CraftListStatus.Failed, CraftListOps.NotConfirmedMessage);
        return CraftListOps.VerifyCreated(safeName, key, goals, reread);
    }

    /// <summary>Allagan Tools has every gate we use registered and says it has finished initializing.</summary>
    private bool Ready()
    {
        return TryCall("IsInitialized", () => isInitialized.HasFunction && getCraftLists.HasFunction && getCraftItems.HasFunction
            && addNewCraftList.HasFunction && isInitialized.InvokeFunc(), out var ready) && ready;
    }

    private bool TryCall<T>(string gate, Func<T> call, out T? result)
    {
        try
        {
            result = call();
            return true;
        }
        catch (IpcNotReadyError)
        {
            // Not installed or not loaded: a normal state.
        }
        catch (Exception ex)
        {
            // Each distinct exception type is logged once (a signature change shows up as a new type); repeats stay quiet.
            if (loggedErrorTypes.Add(ex.GetType().FullName ?? ex.GetType().Name))
                log.Warning(ex, "Allagan Tools craft-list IPC call {Gate} failed", gate);
        }
        result = default;
        return false;
    }
}
