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
    private const string NotStoredText = "This list is not stored in Artisan's config (premade/relic lists cannot be read). Use Paste ingredients instead.";
    private const string FootnoteText = "Prices are from Universalis, exclude market tax, and may be out of date. This plugin does not travel for you.";
    private const string OpenedText = "Shopping list opened in its own window.";

    private static readonly Vector4 WarningColor = new(1f, 0.6f, 0.3f, 1f);
    private static readonly Vector4 ErrorColor = new(1f, 0.4f, 0.4f, 1f);

    private readonly string key;
    private readonly string title;
    private readonly int? artisanListId;
    private readonly IReadOnlyList<Requirement>? pastedRequirements;
    private readonly Configuration config;
    private readonly ShoppingSession session;
    private readonly IArtisanListSource artisan;
    private readonly IItemInfo items;
    private readonly IWorldData worlds;
    private readonly ShoppingListService service;
    private readonly IWindowHost host;
    private readonly IPluginLog log;

    // UI-thread state.
    private string sourceMessage = string.Empty;
    private List<Requirement> rows = [];
    private readonly Dictionary<uint, string> names = new();

    // Shared with the background build task. Writes happen under buildGate; a build publishes its
    // result only while its generation is still current, so a superseded build can never show it.
    private readonly object buildGate = new();
    private int generation;
    private OfferBook? pendingBook;
    private volatile bool building;
    private volatile string progressText = string.Empty;
    private volatile string errorText = string.Empty;

    // UI thread only. A source is never disposed while its task can still use it: a build that is replaced or
    // reloaded away while still running is retired, and its source is disposed by a later sweep once the task ended.
    private CancellationTokenSource? cts;
    private Task? buildTask;
    private readonly List<(CancellationTokenSource Source, Task Task)> retired = [];

    /// <summary>
    /// Creates the window. Exactly one of <paramref name="artisanListId"/> and <paramref name="pastedRequirements"/> is set:
    /// an Artisan list is loaded through <paramref name="artisan"/> and its saved exclusions applied; pasted requirements are
    /// used as given and their choices are not remembered. <paramref name="key"/> is stable and unique per list and is used for the window id.
    /// </summary>
    internal ListWindow(
        string key,
        string title,
        int? artisanListId,
        IReadOnlyList<Requirement>? pastedRequirements,
        Configuration config,
        ShoppingSession session,
        IArtisanListSource artisan,
        IItemInfo items,
        IWorldData worlds,
        ShoppingListService service,
        IWindowHost host,
        IPluginLog log)
        : base(title + "###lsl-list-" + key)
    {
        if (artisanListId.HasValue == (pastedRequirements != null))
            throw new ArgumentException("Exactly one of artisanListId and pastedRequirements must be set.");

        this.key = key;
        this.title = title;
        this.artisanListId = artisanListId;
        this.pastedRequirements = pastedRequirements;
        this.config = config;
        this.session = session;
        this.artisan = artisan;
        this.items = items;
        this.worlds = worlds;
        this.service = service;
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
        RespectCloseHotkey = artisanListId.HasValue && !building;
    }

    /// <inheritdoc />
    public override void Draw()
    {
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

    private void LoadList()
    {
        sourceMessage = string.Empty;
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

    private void RefreshRows()
    {
        // Crystals, shards and clusters sort to the end; this is purely presentation.
        rows = session.Requirements.OrderBy(r => items.IsCrystalLike(r.ItemId)).ThenBy(r => r.ItemId).ToList();
        names.Clear();
        CancelBuild();
        RetireCurrent();
        lock (buildGate)
        {
            generation++;
            pendingBook = null;
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
        ImGui.TextDisabled(artisanListId.HasValue ? "Source: Artisan list" : "Source: pasted ingredients");
        if (artisanListId.HasValue)
        {
            // Reads the list and its saved choices again; a running build is cancelled and its outcome discarded.
            ImGui.SameLine();
            if (ImGui.SmallButton("Reload list"))
                LoadList();
        }
        if (sourceMessage.Length > 0)
            ImGui.TextColored(WarningColor, sourceMessage);
    }

    // ---- Scope ----

    private void DrawScope()
    {
        ImGui.TextUnformatted("Where to shop");
        var scope = config.LastScope;
        if (ImGui.RadioButton("Cross-world (worlds reachable from a main-city aetheryte: your region)", scope == TravelScopeKind.CrossWorld))
            SetScope(TravelScopeKind.CrossWorld);
        if (ImGui.RadioButton("Cross data center (everywhere; needs Data Center Travel for other regions)", scope == TravelScopeKind.CrossDataCenter))
            SetScope(TravelScopeKind.CrossDataCenter);
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

        if (ImGui.Button("Toggle all crystals/shards/clusters"))
        {
            session.SetCrystalsExcluded(!session.AreAllCrystalsExcluded);
            PersistExclusions();
        }
        ImGui.SameLine();
        ImGui.TextDisabled(session.AreAllCrystalsExcluded ? "(all excluded)" : "(not all excluded)");

        // The table follows the window: it takes what is left after keeping room for the Build row,
        // its status and error lines, the login warning and the footnote.
        var rowHeight = ImGui.GetTextLineHeightWithSpacing();
        var reserved = ImGui.GetFrameHeightWithSpacing() + rowHeight * 5 + ImGui.GetStyle().ItemSpacing.Y * 4;
        var free = ImGui.GetContentRegionAvail().Y - reserved;
        var tableHeight = Math.Max(rowHeight * 7, free);
        if (ImGui.BeginTable("##ingredients", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(-1, tableHeight)))
        {
            try
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Buy", ImGuiTableColumnFlags.WidthFixed, 40);
                ImGui.TableSetupColumn("Item");
                ImGui.TableSetupColumn("Quantity", ImGuiTableColumnFlags.WidthFixed, 70);
                ImGui.TableHeadersRow();
                foreach (var r in rows)
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
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(r.Quantity.ToString("N0"));
                }
            }
            finally
            {
                ImGui.EndTable();
            }
        }
    }

    private void PersistExclusions()
    {
        // Only Artisan lists are remembered; pasted text has no stable id.
        if (artisanListId is not int id) return;
        config.ExcludedItemsByList[id] = new HashSet<uint>(session.ExportExclusions());
        config.Save();
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
                ImGui.Button("Build shopping list");
            }
            finally
            {
                ImGui.EndDisabled();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) CancelBuild();
        }
        else
        {
            ImGui.BeginDisabled(!loggedIn || rows.Count == 0);
            try
            {
                if (ImGui.Button("Build shopping list")) StartBuild();
            }
            finally
            {
                ImGui.EndDisabled();
            }
        }

        if (progressText.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted(progressText);
        }
        if (errorText.Length > 0)
            ImGui.TextColored(ErrorColor, errorText);
    }

    private void StartBuild()
    {
        // Snapshots: the background task never touches UI-thread state.
        var requirements = session.Requirements.ToList();
        var exclusions = new ExclusionState();
        exclusions.ExcludeAll(session.ExportExclusions(), true);
        var scope = config.LastScope;

        int gen;
        lock (buildGate)
        {
            gen = ++generation;
            pendingBook = null;
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
                progress.Finish(() =>
                {
                    pendingBook = result;
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
                        ? "Could not build the shopping list: a request timed out. Try again."
                        : "Could not build the shopping list: " + ex.Message;
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
        lock (buildGate)
        {
            book = pendingBook;
            pendingBook = null;
        }
        if (book == null) return;

        try
        {
            host.ShowResults(key, title, book);
            progressText = OpenedText;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Opening the results window for {Key} failed", key);
            progressText = string.Empty;
            errorText = "Could not open the shopping list window: " + ex.Message;
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
