using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using LunaShoppingList.Core;

namespace LunaShoppingList.Windows;

/// <summary>
/// Owns the window system and the per-list windows. Window callbacks run inside the window system's draw,
/// so adding and removing windows is queued and applied by <see cref="ProcessPending"/> before the next draw.
/// </summary>
internal sealed class WindowManager : IWindowHost, IDisposable
{
    private readonly Configuration config;
    private readonly IArtisanListSource artisan;
    private readonly IItemInfo items;
    private readonly IWorldData worlds;
    private readonly ShoppingListService service;
    private readonly MaterialsCalculator calc;
    private readonly IPluginLog log;

    private readonly Dictionary<string, ListWindow> listWindows = new();
    private readonly Dictionary<string, ResultsWindow> resultsWindows = new();
    private readonly Queue<Action> pending = new();

    // Closed list windows whose cancelled build had not ended yet. Unload waits for them too, and disposing
    // them again once the build has ended releases their cancellation source.
    private readonly List<ListWindow> closingWindows = new();
    private int pasteCounter;

    public WindowManager(WindowSystem windowSystem, Configuration config, IArtisanListSource artisan, IItemInfo items,
        IWorldData worlds, ShoppingListService service, MaterialsCalculator calc, IPluginLog log)
    {
        WindowSystem = windowSystem;
        this.config = config;
        this.artisan = artisan;
        this.items = items;
        this.worlds = worlds;
        this.service = service;
        this.calc = calc;
        this.log = log;
    }

    public WindowSystem WindowSystem { get; }

    /// <summary>Opens the window for an Artisan list, or brings it to the front if it is already open.</summary>
    public void OpenArtisanList(int id, string name)
    {
        var key = "artisan-" + id;
        if (listWindows.TryGetValue(key, out var existing))
        {
            existing.IsOpen = true;
            existing.BringToFront();
            return;
        }
        AddList(key, name, id, null);
    }

    /// <summary>Opens a window for pasted ingredients; each call makes a new window.</summary>
    public void OpenPasted(IReadOnlyList<Requirement> requirements)
    {
        var n = ++pasteCounter;
        AddList("paste-" + n, $"Pasted ingredients ({n})", null, requirements);
    }

    private void AddList(string key, string title, int? artisanListId, IReadOnlyList<Requirement>? requirements)
    {
        var session = new ShoppingSession(calc, items);
        var window = new ListWindow(key, title, artisanListId, requirements, config, session, artisan, items, worlds, service, this, log);
        listWindows[key] = window;
        window.IsOpen = true;
        pending.Enqueue(() => WindowSystem.AddWindow(window));
    }

    /// <inheritdoc />
    public void ShowResults(string key, string title, OfferBook book)
    {
        if (resultsWindows.TryGetValue(key, out var existing))
        {
            existing.Update(book);
            existing.IsOpen = true;
            existing.BringToFront();
            return;
        }
        var window = new ResultsWindow(key, title + " - shopping list", book, items, OnResultsClosed);
        resultsWindows[key] = window;
        window.IsOpen = true;
        pending.Enqueue(() => WindowSystem.AddWindow(window));
    }

    private void OnResultsClosed(string key)
    {
        if (!resultsWindows.Remove(key, out var window)) return;
        pending.Enqueue(() => WindowSystem.RemoveWindow(window));
    }

    /// <inheritdoc />
    public void CloseList(string key)
    {
        if (!listWindows.Remove(key, out var list)) return;

        // Cancelling touches no window state, so it happens now and never waits; only the window system change is queued.
        // The results window stays open until the user closes it; building the same list again updates it.
        DisposeQuietly(list);
        closingWindows.RemoveAll(w =>
        {
            if (w.RunningBuilds.Length > 0) return false;
            DisposeQuietly(w);
            return true;
        });
        if (list.RunningBuilds.Length > 0)
            closingWindows.Add(list);
        pending.Enqueue(() => WindowSystem.RemoveWindow(list));
    }

    /// <summary>Applies queued window changes. Call at the start of every frame, before the window system draws.</summary>
    public void ProcessPending()
    {
        while (pending.Count > 0)
        {
            try
            {
                pending.Dequeue()();
            }
            catch (Exception ex)
            {
                log.Error(ex, "Applying a window change failed");
            }
        }
    }

    /// <summary>
    /// Cancels every build first, then waits once, about a second in total, for all of them (including builds of
    /// windows already closed) so none is still using the HTTP client when the plugin disposes it.
    /// </summary>
    public void Dispose()
    {
        // The queue only holds window system changes, which RemoveAllWindows below supersedes;
        // closed list windows were already disposed by CloseList.
        pending.Clear();

        var all = listWindows.Values.Concat(closingWindows).ToList();
        foreach (var w in all)
            DisposeQuietly(w);

        var running = all.SelectMany(w => w.RunningBuilds).ToArray();
        try
        {
            Task.WaitAll(running, TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
            // Shutting down: whatever a build ended with no longer matters.
        }

        // A second pass releases the sources of builds that have now ended.
        foreach (var w in all)
            DisposeQuietly(w);
        closingWindows.Clear();
        listWindows.Clear();
        resultsWindows.Clear();
        WindowSystem.RemoveAllWindows();
    }

    private void DisposeQuietly(ListWindow window)
    {
        try
        {
            window.Dispose();
        }
        catch (Exception ex)
        {
            log.Error(ex, "Disposing a list window failed");
        }
    }
}
