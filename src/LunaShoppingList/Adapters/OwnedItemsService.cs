using System.Collections.Concurrent;
using Dalamud.Game.Inventory;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using LunaShoppingList.Core;

namespace LunaShoppingList.Adapters;

/// <summary>
/// Counts what the logged-in character owns. Everything native is read on the framework thread inside
/// <see cref="IFramework.Update"/> and copied into managed dictionaries straight away; nothing native is kept across frames.
/// Bags and crystals are read live. The saddlebag and each retainer can only be read while open, so what they held is
/// remembered per character in <c>owned-&lt;content id&gt;.json</c> in the plugin's config directory. AllaganTools, when
/// installed and tracking the same character, is asked for its retainer counts too. Results reach the draw thread as one
/// immutable snapshot swapped atomically; the draw thread never takes a lock and never reads native memory through this class.
/// </summary>
internal sealed unsafe class OwnedItemsService : IDisposable
{
    private static readonly GameInventoryType[] BagTypes =
    [
        GameInventoryType.Inventory1, GameInventoryType.Inventory2, GameInventoryType.Inventory3, GameInventoryType.Inventory4,
        GameInventoryType.Crystals,
    ];

    private static readonly GameInventoryType[] SaddlebagTypes = [GameInventoryType.SaddleBag1, GameInventoryType.SaddleBag2];
    private static readonly GameInventoryType[] PremiumSaddlebagTypes = [GameInventoryType.PremiumSaddleBag1, GameInventoryType.PremiumSaddleBag2];

    // The saddlebag window; its containers are only trusted while it is on screen.
    private static readonly string[] SaddlebagAddons = ["InventoryBuddy", "InventoryBuddy2"];

    // Equipped retainer gear is deliberately not counted.
    private static readonly (RetainerPlace Place, GameInventoryType Type)[] RetainerContainers =
    [
        (RetainerPlace.Bag1, GameInventoryType.RetainerPage1), (RetainerPlace.Bag2, GameInventoryType.RetainerPage2),
        (RetainerPlace.Bag3, GameInventoryType.RetainerPage3), (RetainerPlace.Bag4, GameInventoryType.RetainerPage4),
        (RetainerPlace.Bag5, GameInventoryType.RetainerPage5), (RetainerPlace.Bag6, GameInventoryType.RetainerPage6),
        (RetainerPlace.Bag7, GameInventoryType.RetainerPage7), (RetainerPlace.Crystals, GameInventoryType.RetainerCrystals),
        (RetainerPlace.Market, GameInventoryType.RetainerMarket),
    ];

    private static readonly GameInventoryType[] RetainerTypes = RetainerContainers.Select(c => c.Type).ToArray();

    // AllaganTools inventory categories: retainer bags (4), retainer market (9) and crystals (13). Crystals also cover the
    // character's own, which are left out by skipping the character's own id in the per-owner answer.
    private static readonly uint[] AllaganRetainerCategories = [4, 9, 13];

    private const long LiveIntervalMs = 1_000;
    private const long ExternalIntervalMs = 10_000;
    private const int ExternalBatch = 25;
    private const long SaveDelayMs = 5_000;
    private const long SaveRetryMs = 60_000;
    private const long WantedTtlMs = 15_000;
    private const long ErrorLogIntervalMs = 60_000;
    private static readonly TimeSpan StampRefresh = TimeSpan.FromMinutes(1);

    private static readonly IReadOnlyDictionary<uint, int> NoCounts = new Dictionary<uint, int>();

    private readonly IDalamudPluginInterface pi;
    private readonly IFramework framework;
    private readonly IGameInventory inventory;
    private readonly IGameGui gameGui;
    private readonly IPlayerState player;
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<bool> allaganInitialized;
    private readonly ICallGateSubscriber<ulong> allaganCurrentCharacter;
    private readonly ICallGateSubscriber<uint, bool, uint[], bool, Dictionary<ulong, uint>> allaganCountsByOwner;

    // Items the windows asked about recently (item id -> Environment.TickCount64 of the last ask); written by the draw thread.
    private readonly ConcurrentDictionary<uint, long> wanted = new();
    // Items asked about for the first time (or again after expiring), so they are queried without waiting for the next round.
    private readonly ConcurrentQueue<uint> newlyWanted = new();

    // Published state: replaced as a whole, never mutated after it is published.
    private Snapshot snapshot = new(0, null, new OwnedCache(), null);

    // Framework-thread state, guarded by tickGate so Dispose can never run alongside a tick.
    private readonly object tickGate = new();
    private bool disposed;
    private ulong contentId;
    private OwnedCache working = new();
    private bool workingChanged;
    // Bags and crystals of the current character; null until they have been read once.
    private IReadOnlyDictionary<uint, int>? live;
    private bool publishNeeded;
    private long nextLiveMs;

    // AllaganTools answers, kept per item and queried a batch at a time.
    private readonly Dictionary<uint, int> externalCounts = new();
    private readonly Queue<uint> externalQueue = new();
    private readonly HashSet<uint> externalQueued = new();
    private IReadOnlyDictionary<uint, int>? external;
    private bool externalAvailable;
    private bool externalChanged;
    private long nextExternalRoundMs;
    private bool externalErrorLogged;

    private bool dirty;
    // Only capture times moved forward; written with the next real save or on unload, not on its own.
    private bool stampPending;
    private long saveDueMs;
    private bool saveBlocked;
    private bool saveErrorLogged;
    private long lastTickErrorMs = long.MinValue;

    // Records of characters switched away from whose save failed, retried until written (content id -> record).
    private readonly Dictionary<ulong, OwnedCache> pendingSaves = new();
    private long pendingRetryMs;

    // The retainer currently open, if any (0 = none).
    private RetainerVisitGate gate = new();
    private ulong visitId;
    private bool visitCaptured;
    private DateTimeOffset visitLastVerifiedUtc;

    public OwnedItemsService(IDalamudPluginInterface pi, IFramework framework, IGameInventory inventory, IGameGui gameGui, IPlayerState player, IPluginLog log)
    {
        this.pi = pi;
        this.framework = framework;
        this.inventory = inventory;
        this.gameGui = gameGui;
        this.player = player;
        this.log = log;
        allaganInitialized = pi.GetIpcSubscriber<bool>("AllaganTools.IsInitialized");
        allaganCurrentCharacter = pi.GetIpcSubscriber<ulong>("AllaganTools.CurrentCharacter");
        allaganCountsByOwner = pi.GetIpcSubscriber<uint, bool, uint[], bool, Dictionary<ulong, uint>>("AllaganTools.GetItemCountsByCharacter");
        framework.Update += OnUpdate;
    }

    /// <summary>Whether any retainer of the current character was ever captured.</summary>
    public bool HasAnyRetainerData => Volatile.Read(ref snapshot).Cache.RetainerCount > 0;

    /// <summary>Whether AllaganTools is answering for the current character.</summary>
    public bool ExternalAvailable => Volatile.Read(ref snapshot).External is not null;

    /// <summary>The oldest retainer capture of the current character; <c>null</c> when none.</summary>
    public DateTimeOffset? OldestRetainerCapture => Volatile.Read(ref snapshot).Cache.OldestRetainerCapture;

    /// <summary>
    /// <see cref="Summarize"/> plus where each retainer holds <paramref name="itemId"/>, both from ONE read of the published snapshot so
    /// the two always agree. Allocates, so call it only while a tooltip is open, never per row per frame.
    /// </summary>
    public (OwnedSummary Summary, IReadOnlyList<RetainerLocation> Locations) SummarizeWithLocations(uint itemId)
    {
        var s = Volatile.Read(ref snapshot);
        return (SummarizeFrom(s, itemId), OwnedLookup.Locations(itemId, s.Cache));
    }

    /// <summary>
    /// What the current character owns of <paramref name="itemId"/>. Cheap (dictionary lookups on the published snapshot);
    /// safe to call from the draw thread every frame. Asking also marks the item for the next AllaganTools query.
    /// </summary>
    public OwnedSummary Summarize(uint itemId) => SummarizeFrom(Volatile.Read(ref snapshot), itemId);

    private OwnedSummary SummarizeFrom(Snapshot s, uint itemId)
    {
        var now = Environment.TickCount64;
        if (wanted.TryAdd(itemId, now))
            newlyWanted.Enqueue(itemId);
        else
            wanted[itemId] = now;
        var ext = s.External;
        return OwnedLookup.Summarize(itemId, s.Live, s.Cache, ext is null ? null : id => ext.TryGetValue(id, out var n) ? n : null);
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        lock (tickGate)
        {
            if (disposed) return;
            disposed = true;
            try
            {
                EndVisit();
                if (dirty || stampPending)
                    Save(Environment.TickCount64);
                RetryPendingSaves(Environment.TickCount64);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Saving the owned-items record on unload failed");
            }
        }
    }

    private void OnUpdate(IFramework _)
    {
        lock (tickGate)
        {
            if (disposed) return;
            var ms = Environment.TickCount64;
            try
            {
                Tick(ms, DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                // Never once per frame: a fault that repeats every tick is logged at most once a minute.
                if (ms - lastTickErrorMs >= ErrorLogIntervalMs)
                {
                    lastTickErrorMs = ms;
                    log.Warning(ex, "Counting owned items failed");
                }
            }
            finally
            {
                if (publishNeeded)
                    Publish();
            }
        }
    }

    private void Tick(long ms, DateTimeOffset now)
    {
        var current = player.IsLoaded ? player.ContentId : 0UL;
        if (current != contentId)
            SwitchCharacter(current, ms);

        if (pendingSaves.Count > 0 && ms >= pendingRetryMs)
            RetryPendingSaves(ms);

        if (contentId == 0)
            return;

        TickRetainer(ms, now);

        if (ms >= nextLiveMs)
        {
            nextLiveMs = ms + LiveIntervalMs;
            ReadLive();
            CaptureSaddlebag(now);
        }

        TickExternal(ms);

        if (dirty && ms >= saveDueMs)
            Save(ms);
    }

    // ---- Character ----

    private void SwitchCharacter(ulong newId, long ms)
    {
        // Finish the old character completely before anything of the new one is read: the two are never mixed.
        EndVisit();
        if (contentId != 0 && (dirty || stampPending) && !saveBlocked && !TryWrite(contentId, working))
        {
            // Kept, not dropped: retried on the normal schedule and on unload, against that character's own file.
            pendingSaves[contentId] = working;
            pendingRetryMs = ms + SaveRetryMs;
        }

        // The new record is in place before the new id is, so the old record is never attached to the new character.
        // Switching back to a character whose save is still pending resumes that unsaved record instead of the older file.
        OwnedCache? held = null;
        var resumed = newId != 0 && pendingSaves.Remove(newId, out held);
        var blocked = false;
        working = resumed && held is not null ? held : newId == 0 ? new OwnedCache() : Load(newId, out blocked);
        workingChanged = true;
        contentId = newId;
        saveBlocked = blocked;

        live = null;
        dirty = resumed;
        saveDueMs = ms + SaveDelayMs;
        stampPending = false;
        saveErrorLogged = false;
        gate = new RetainerVisitGate();
        ResetVisit(0);
        nextLiveMs = 0;
        nextExternalRoundMs = 0;
        DropExternal();
        publishNeeded = true;
    }

    private string CachePath(ulong id) => Path.Combine(pi.GetPluginConfigDirectory(), $"owned-{id}.json");

    private OwnedCache Load(ulong id, out bool blocked)
    {
        blocked = false;
        var path = CachePath(id);
        try
        {
            if (!File.Exists(path))
                return new OwnedCache();
            if (OwnedCache.TryFromJson(File.ReadAllText(path), out var cache))
                return cache;
        }
        catch (Exception ex)
        {
            // A file that exists but cannot be read is never overwritten by what little this session sees.
            blocked = true;
            log.Warning(ex, "Reading the owned-items record failed; retainer and saddlebag counts will not be saved this session");
            return new OwnedCache();
        }

        // Corrupt or from an unknown version: set it aside so nothing in it is lost, then start a fresh record.
        try
        {
            var bad = UnusedBadPath(path);
            File.Move(path, bad);
            log.Warning($"The owned-items record could not be read and was kept as {Path.GetFileName(bad)}; starting a new one");
        }
        catch (Exception ex)
        {
            blocked = true;
            log.Warning(ex, "The owned-items record could not be read or set aside; retainer and saddlebag counts will not be saved this session");
        }
        return new OwnedCache();
    }

    // An earlier backup is never overwritten: the plain name if it is free, otherwise a timestamped one.
    private static string UnusedBadPath(string path)
    {
        var plain = path + ".bad";
        if (!File.Exists(plain))
            return plain;
        var stamped = $"{path}.{DateTime.UtcNow:yyyyMMddHHmmss}";
        var candidate = stamped + ".bad";
        for (var n = 2; File.Exists(candidate); n++)
            candidate = $"{stamped}-{n}.bad";
        return candidate;
    }

    private void Save(long ms)
    {
        if (saveBlocked || contentId == 0)
        {
            dirty = false;
            stampPending = false;
            return;
        }
        if (TryWrite(contentId, working))
        {
            dirty = false;
            stampPending = false;
        }
        else
        {
            // Any failure waits a minute before the next try, so a persistent fault is not retried every frame.
            dirty = true;
            saveDueMs = ms + SaveRetryMs;
        }
    }

    private void RetryPendingSaves(long ms)
    {
        foreach (var (id, cache) in pendingSaves.ToList())
        {
            if (TryWrite(id, cache))
                pendingSaves.Remove(id);
        }
        pendingRetryMs = ms + SaveRetryMs;
    }

    private bool TryWrite(ulong id, OwnedCache cache)
    {
        var path = CachePath(id);
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, cache.ToJson());
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            if (!saveErrorLogged)
            {
                saveErrorLogged = true;
                log.Warning(ex, "Saving the owned-items record failed; will retry");
            }
            return false;
        }
    }

    /// <summary>Publishes the changed cache; <paramref name="contentsChanged"/> also schedules a save a few seconds later.</summary>
    private void MarkChanged(long ms, bool contentsChanged = true)
    {
        workingChanged = true;
        publishNeeded = true;
        if (!contentsChanged)
        {
            stampPending = true;
            return;
        }
        if (!dirty)
        {
            dirty = true;
            saveDueMs = ms + SaveDelayMs;
        }
    }

    // ---- Live bags and crystals ----

    private void ReadLive()
    {
        if (!AllLoaded(BagTypes))
            return;
        var counts = Count(BagTypes);
        if (live is not null && OwnedCounting.SameCounts(counts, live))
            return;
        live = counts;
        publishNeeded = true;
    }

    // ---- Saddlebag ----

    private void CaptureSaddlebag(DateTimeOffset now)
    {
        // Read only while the saddlebag window is on screen and its containers are loaded; otherwise the record stays as it was.
        if (!SaddlebagOpen() || !AllLoaded(SaddlebagTypes))
            return;
        var counts = Count(SaddlebagTypes);
        foreach (var type in PremiumSaddlebagTypes)
        {
            if (IsLoaded(type))
                AddContainer(counts, type);
        }

        var existing = working.Saddlebag;
        var same = existing is not null && OwnedCounting.SameCounts(existing.Items, counts);
        if (same && now - existing!.CapturedUtc < StampRefresh)
            return;
        working.ReplaceSaddlebag(now, counts);
        MarkChanged(Environment.TickCount64, contentsChanged: !same);
    }

    private bool SaddlebagOpen()
    {
        foreach (var name in SaddlebagAddons)
        {
            if (gameGui.GetAddonByName(name).IsVisible)
                return true;
        }
        return false;
    }

    // ---- Retainers ----

    private void TickRetainer(long ms, DateTimeOffset now)
    {
        // The retainer whose window is open right now; 0 as soon as it is closed.
        var order = ItemOrderModule.Instance();
        var id = order == null ? 0UL : order->ActiveRetainerId;
        var manager = RetainerManager.Instance();

        if (id != visitId)
        {
            EndVisit();
            ResetVisit(id);
        }

        // Every container must be loaded; a retainer that is still loading keeps its previous record.
        var ready = id != 0 && manager != null && manager->IsReady
            && AllLoaded(RetainerTypes);
        // The gate waits after every change of retainer and wants the same contents twice before the first capture.
        if (!gate.ShouldRead(id, ready, ms))
            return;

        // Each container is counted on its own so the record says where an item is, not just that it is there.
        var places = new Dictionary<RetainerPlace, IReadOnlyDictionary<uint, int>>();
        foreach (var (place, type) in RetainerContainers)
            places[place] = Count([type]);
        if (!gate.Offer(places, ms))
            return;

        var existing = FindRetainer(id);
        var name = RetainerName(manager, id) ?? existing?.Name ?? string.Empty;
        var same = existing is not null
            && existing.Name == name
            && OwnedCounting.SamePlaces(existing.Places, places);

        if (!visitCaptured || !same || now - existing!.CapturedUtc >= StampRefresh)
        {
            working.ReplaceRetainer(id, name, now, places);
            // The first capture of a visit is saved even when nothing changed, so the record says this visit happened.
            MarkChanged(ms, contentsChanged: !visitCaptured || !same);
        }
        visitCaptured = true;
        visitLastVerifiedUtc = now;
    }

    private static string? RetainerName(RetainerManager* manager, ulong id)
    {
        if (manager == null)
            return null;
        foreach (ref var r in manager->Retainers)
        {
            if (r.RetainerId == id)
                return r.NameString;
        }
        return null;
    }

    /// <summary>When a retainer closes, its record is stamped with the last time its contents were confirmed. Reads no native memory.</summary>
    private void EndVisit()
    {
        if (visitId == 0 || !visitCaptured)
            return;
        var existing = FindRetainer(visitId);
        if (existing is not null && existing.CapturedUtc < visitLastVerifiedUtc)
        {
            OwnedCounting.CopyRetainer(working, existing, visitLastVerifiedUtc);
            MarkChanged(Environment.TickCount64);
        }
        visitCaptured = false;
    }

    private void ResetVisit(ulong id)
    {
        visitId = id;
        visitCaptured = false;
        visitLastVerifiedUtc = default;
    }

    private RetainerSnapshot? FindRetainer(ulong id)
    {
        foreach (var r in working.Retainers)
        {
            if (r.Id == id)
                return r;
        }
        return null;
    }

    // ---- AllaganTools ----

    /// <summary>
    /// Asks AllaganTools about the items the windows show: every item once per round (every few seconds), and an item asked about
    /// for the first time straight away. At most a batch of items per frame, so no single frame does all the work.
    /// </summary>
    private void TickExternal(long ms)
    {
        if (ms >= nextExternalRoundMs)
        {
            nextExternalRoundMs = ms + ExternalIntervalMs;
            foreach (var (item, askedMs) in wanted)
            {
                if (ms - askedMs > WantedTtlMs)
                    wanted.TryRemove(item, out _);
            }
            foreach (var item in externalCounts.Keys.ToList())
            {
                if (!wanted.ContainsKey(item) && externalCounts.Remove(item))
                    externalChanged = true;
            }
            foreach (var item in wanted.Keys)
                QueueExternal(item);
        }
        while (newlyWanted.TryDequeue(out var fresh))
            QueueExternal(fresh);

        if (externalQueue.Count > 0)
        {
            try
            {
                if (!ExternalReady())
                {
                    DropExternal();
                    return;
                }
                if (!externalAvailable)
                {
                    externalAvailable = true;
                    externalChanged = true;
                }
                for (var n = 0; n < ExternalBatch && externalQueue.TryDequeue(out var item); n++)
                {
                    externalQueued.Remove(item);
                    var count = RetainerCountFromAllagan(item);
                    if (!externalCounts.TryGetValue(item, out var had) || had != count)
                    {
                        externalCounts[item] = count;
                        externalChanged = true;
                    }
                }
                externalErrorLogged = false;
            }
            catch (IpcNotReadyError)
            {
                // Not installed or not loaded: a normal state, shown as no external source.
                DropExternal();
                return;
            }
            catch (Exception ex)
            {
                DropExternal();
                if (!externalErrorLogged)
                {
                    externalErrorLogged = true;
                    log.Debug(ex, "AllaganTools did not answer; using this plugin's own retainer record");
                }
                return;
            }
        }

        if (externalChanged)
        {
            externalChanged = false;
            external = externalAvailable ? new Dictionary<uint, int>(externalCounts) : null;
            publishNeeded = true;
        }
    }

    private void QueueExternal(uint item)
    {
        if (externalQueued.Add(item))
            externalQueue.Enqueue(item);
    }

    /// <summary>Whether AllaganTools is loaded, initialized and tracking this very character (it answers 0 for everything otherwise).</summary>
    private bool ExternalReady()
    {
        if (!allaganInitialized.HasFunction || !allaganCurrentCharacter.HasFunction || !allaganCountsByOwner.HasFunction)
            return false;
        return allaganInitialized.InvokeFunc() && allaganCurrentCharacter.InvokeFunc() == contentId;
    }

    /// <summary>The item's count over this character's retainers: every owner AllaganTools reports except the character itself.</summary>
    private int RetainerCountFromAllagan(uint item)
    {
        var byOwner = allaganCountsByOwner.InvokeFunc(item, true, AllaganRetainerCategories, false);
        long sum = 0;
        if (byOwner is not null)
        {
            foreach (var (owner, count) in byOwner)
            {
                if (owner != contentId)
                    sum += count;
            }
        }
        return (int)Math.Min(sum, int.MaxValue);
    }

    /// <summary>Forgets every AllaganTools answer and shows it as unavailable until it answers for this character again.</summary>
    private void DropExternal()
    {
        externalCounts.Clear();
        externalQueue.Clear();
        externalQueued.Clear();
        externalChanged = false;
        externalAvailable = false;
        if (external is not null)
        {
            external = null;
            publishNeeded = true;
        }
    }

    // ---- Publishing ----

    private void Publish()
    {
        publishNeeded = false;
        var cache = workingChanged ? OwnedCounting.Copy(working) : Volatile.Read(ref snapshot).Cache;
        workingChanged = false;
        Volatile.Write(ref snapshot, new Snapshot(contentId, live, cache, external));
    }

    // ---- Native reads (framework thread only; counts are copied out immediately) ----

    private Dictionary<uint, int> Count(GameInventoryType[] types)
    {
        var counts = new Dictionary<uint, int>();
        foreach (var type in types)
            AddContainer(counts, type);
        return counts;
    }

    private void AddContainer(Dictionary<uint, int> counts, GameInventoryType type)
    {
        // High-quality and normal copies are summed under the base item id.
        foreach (var item in inventory.GetInventoryItems(type))
            OwnedCounting.Add(counts, item.BaseItemId, (int)item.Quantity);
    }

    private static bool AllLoaded(GameInventoryType[] types)
    {
        foreach (var type in types)
        {
            if (!IsLoaded(type))
                return false;
        }
        return true;
    }

    private static bool IsLoaded(GameInventoryType type)
    {
        // IGameInventory does not say whether a container holds current data, so ask the game directly.
        var manager = InventoryManager.Instance();
        if (manager == null)
            return false;
        var container = manager->GetInventoryContainer((InventoryType)(int)type);
        return container != null && container->IsLoaded;
    }

    /// <summary><paramref name="Live"/> is <c>null</c> until the bags of <paramref name="ContentId"/> have been read once.</summary>
    private sealed record Snapshot(ulong ContentId, IReadOnlyDictionary<uint, int>? Live, OwnedCache Cache, IReadOnlyDictionary<uint, int>? External);
}
