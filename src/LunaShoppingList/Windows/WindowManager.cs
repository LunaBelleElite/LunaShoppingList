using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using LunaShoppingList.Adapters;
using LunaShoppingList.Core;

namespace LunaShoppingList.Windows;

/// <summary>
/// Owns the window system and the per-list windows. Window callbacks run inside the window system's draw,
/// so adding and removing windows is queued and applied by <see cref="ProcessPending"/> before the next draw.
/// </summary>
internal sealed class WindowManager : IWindowHost, IDisposable
{
    private const string ResultsTitleSuffix = " - where to buy";

    private readonly Configuration config;
    private readonly IArtisanListSource artisan;
    private readonly IItemInfo items;
    private readonly IRecipeLookup recipes;
    private readonly IWorldData worlds;
    private readonly ShoppingListService service;
    private readonly MaterialsCalculator calc;
    private readonly OwnedItemsService owned;
    private readonly LifestreamService lifestream;
    private readonly IItemSources sources;
    private readonly IDropSources drops;
    private readonly IPluginLog log;

    private readonly Dictionary<string, ListWindow> listWindows = new();
    private readonly Dictionary<string, ResultsWindow> resultsWindows = new();
    private readonly Queue<Action> pending = new();

    // Closed list windows whose cancelled build had not ended yet. Unload waits for them too, and disposing
    // them again once the build has ended releases their cancellation source.
    private readonly List<ListWindow> closingWindows = new();
    private int pasteCounter;

    public WindowManager(WindowSystem windowSystem, Configuration config, IArtisanListSource artisan, IItemInfo items,
        IRecipeLookup recipes, IWorldData worlds, ShoppingListService service, MaterialsCalculator calc, OwnedItemsService owned, LifestreamService lifestream, IItemSources sources, IDropSources drops, IPluginLog log)
    {
        WindowSystem = windowSystem;
        this.config = config;
        this.artisan = artisan;
        this.items = items;
        this.recipes = recipes;
        this.worlds = worlds;
        this.service = service;
        this.calc = calc;
        this.owned = owned;
        this.lifestream = lifestream;
        this.sources = sources;
        this.drops = drops;
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
        AddList(key, name, id, null, null);
    }

    /// <summary>Opens the window for a saved custom list, or brings it to the front if it is already open.</summary>
    public void OpenCustomList(string id)
    {
        var list = config.FindCustomList(id);
        if (list == null) return;
        var key = CustomKey(id);
        if (listWindows.TryGetValue(key, out var existing))
        {
            existing.IsOpen = true;
            existing.BringToFront();
            return;
        }
        AddList(key, list.Name, null, null, id);
    }

    /// <summary>Closes the list window and the results window of a custom list; a running build is cancelled. Safe if neither is open.</summary>
    public void CloseCustomList(string id)
    {
        var key = CustomKey(id);
        CloseList(key);
        OnResultsClosed(key);
    }

    /// <summary>Makes an open window for a custom list read the saved list again. Safe if the window is not open.</summary>
    public void RefreshCustomList(string id)
    {
        if (listWindows.TryGetValue(CustomKey(id), out var window))
            window.ReloadCustomList();
    }

    /// <summary>
    /// Gives an open list window and results window of a custom list the list's saved name. Only the titles change:
    /// rows and a running build are left alone. Safe if neither is open.
    /// </summary>
    public void RenameCustomList(string id)
    {
        var key = CustomKey(id);
        if (listWindows.TryGetValue(key, out var window))
            window.RenameCustomList();
        if (resultsWindows.TryGetValue(key, out var results) && config.FindCustomList(id) is { } list)
            results.WindowName = list.Name + ResultsTitleSuffix + "###lsl-results-" + key;
    }

    private static string CustomKey(string id) => "custom-" + id;

    /// <summary>Opens a window for pasted ingredients; each call makes a new window.</summary>
    public void OpenPasted(IReadOnlyList<Requirement> requirements)
    {
        var n = ++pasteCounter;
        AddList("paste-" + n, $"Pasted ingredients ({n})", null, requirements, null);
    }

    private void AddList(string key, string title, int? artisanListId, IReadOnlyList<Requirement>? requirements, string? customListId)
    {
        var session = new ShoppingSession(calc, items);
        var window = new ListWindow(key, title, artisanListId, requirements, customListId, config, session, artisan, items, recipes, worlds, service, owned, sources, drops, this, log);
        listWindows[key] = window;
        window.IsOpen = true;
        pending.Enqueue(() => WindowSystem.AddWindow(window));
    }

    /// <inheritdoc />
    public void ShowResults(string key, string title, OfferBook book, IReadOnlyList<PlanOption> options)
    {
        if (resultsWindows.TryGetValue(key, out var existing))
        {
            existing.Update(book, options);
            existing.IsOpen = true;
            existing.BringToFront();
            return;
        }
        var window = new ResultsWindow(key, title + ResultsTitleSuffix, book, options, items, owned, lifestream, OnResultsClosed);
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
