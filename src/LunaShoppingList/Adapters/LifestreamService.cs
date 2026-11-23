using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using LunaShoppingList.Core;

namespace LunaShoppingList.Adapters;

/// <summary>
/// Asks the optional Lifestream plugin to travel to a world, over its IPC. Only ever called from a click. Lifestream's own in-game
/// messages cover most failures, so a "requested" result is not a promise of arrival. Every call is guarded and never throws.
/// </summary>
internal sealed class LifestreamService
{
    private const string InternalName = "Lifestream";

    private readonly IDalamudPluginInterface pi;
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<string, bool> changeWorld;
    private readonly ICallGateSubscriber<bool> isBusy;

    public LifestreamService(IDalamudPluginInterface pi, IPluginLog log)
    {
        this.pi = pi;
        this.log = log;
        changeWorld = pi.GetIpcSubscriber<string, bool>("Lifestream.ChangeWorld");
        isBusy = pi.GetIpcSubscriber<bool>("Lifestream.IsBusy");
    }

    /// <summary>Whether Lifestream is installed and loaded. Reads the plugin list, no IPC; call it on demand, not every frame.</summary>
    public bool IsAvailable()
    {
        try
        {
            return pi.InstalledPlugins.Any(p => p.IsLoaded && string.Equals(p.InternalName, InternalName, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Checking for Lifestream failed");
            return false;
        }
    }

    /// <summary>Asks Lifestream to go to <paramref name="worldName"/> and returns the line to show the user.</summary>
    public string GoTo(string worldName)
    {
        try
        {
            if (isBusy.InvokeFunc())
                return LifestreamNote.Line(LifestreamOutcome.Busy, worldName);
            var queued = changeWorld.InvokeFunc(worldName);
            return LifestreamNote.Line(queued ? LifestreamOutcome.Requested : LifestreamOutcome.Refused, worldName);
        }
        catch (Exception ex)
        {
            // Not installed, not loaded or not ready: the IPC throws.
            log.Debug(ex, "Lifestream IPC call failed");
            return LifestreamNote.Line(LifestreamOutcome.NotAvailable, worldName);
        }
    }
}
