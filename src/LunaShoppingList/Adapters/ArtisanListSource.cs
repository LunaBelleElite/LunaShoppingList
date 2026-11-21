using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using LunaShoppingList.Core;

namespace LunaShoppingList.Adapters;

/// <summary>Artisan lists: names via IPC, contents from Artisan's saved config file.</summary>
public sealed class ArtisanListSource : IArtisanListSource
{
    private readonly IDalamudPluginInterface pi;
    private readonly IPluginLog log;
    private DateTime cachedStamp;
    private IReadOnlyList<CraftingList>? cached;

    public ArtisanListSource(IDalamudPluginInterface pi, IPluginLog log)
    {
        this.pi = pi;
        this.log = log;
    }

    private string ConfigPath => Path.Combine(pi.ConfigDirectory.Parent!.FullName, "Artisan.json");

    /// <summary>How many lists Artisan's IPC reported on the last <see cref="GetLists"/> call.</summary>
    internal int IpcListCount { get; private set; }

    /// <summary>How many IPC lists the last <see cref="GetLists"/> call left out because their contents cannot be read.</summary>
    internal int HiddenListCount { get; private set; }

    /// <summary>
    /// Lists Artisan reports over IPC. When the config file is readable, only the lists stored in it are returned
    /// (premade lists exist only inside Artisan, so their contents can never be loaded here); when it is not
    /// readable, every IPC list is returned so the user is not left with an empty picker.
    /// </summary>
    public IReadOnlyList<ArtisanListInfo> GetLists()
    {
        IReadOnlyList<ArtisanListInfo> all;
        try
        {
            var sub = pi.GetIpcSubscriber<Dictionary<int, string>>("Artisan.GetLists");
            var lists = sub.InvokeFunc();
            all = lists.Select(kv => new ArtisanListInfo(kv.Key, kv.Value)).ToList();
        }
        catch (IpcNotReadyError)
        {
            all = [];
        }
        catch (IpcError ex)
        {
            log.Warning(ex, "Artisan IPC error");
            all = [];
        }
        catch (Exception ex)
        {
            log.Error(ex, "Artisan.GetLists failed");
            all = [];
        }

        IpcListCount = all.Count;
        HiddenListCount = 0;
        if (all.Count == 0)
            return all;

        IReadOnlyList<CraftingList> stored;
        try
        {
            stored = ReadAll();
        }
        catch (ArtisanConfigUnreadableException ex)
        {
            log.Warning(ex, "Could not read Artisan config; showing every list");
            return all;
        }
        var storedIds = stored.Select(l => l.Id).ToHashSet();
        var readable = all.Where(l => storedIds.Contains(l.Id)).ToList();
        HiddenListCount = all.Count - readable.Count;
        return readable;
    }

    /// <inheritdoc />
    /// <exception cref="ArtisanConfigUnreadableException">Artisan's config file is missing or could not be read.</exception>
    public CraftingList? LoadList(int id) => ReadAll().FirstOrDefault(l => l.Id == id);

    /// <summary>Names of the lists in the config file, or null if the file is missing or unreadable.</summary>
    internal IReadOnlyList<string>? ConfigListNames()
    {
        try
        {
            return ReadAll().Select(l => l.Name).ToList();
        }
        catch (ArtisanConfigUnreadableException)
        {
            return null;
        }
    }

    private IReadOnlyList<CraftingList> ReadAll()
    {
        var path = ConfigPath;
        try
        {
            if (!File.Exists(path))
                throw new ArtisanConfigUnreadableException($"Artisan config not found at {path}.");
            var stamp = File.GetLastWriteTimeUtc(path);
            if (cached != null && stamp == cachedStamp)
                return cached;

            string text;
            // Delete sharing too: Artisan saves by replacing the file, which our open handle must not block.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs))
                text = sr.ReadToEnd();

            cached = ArtisanConfigParser.ParseLists(text);
            cachedStamp = stamp;
            return cached;
        }
        // InvalidDataException is what ArtisanConfigParser throws for malformed JSON or a missing list key,
        // which is exactly what a half-written file looks like while Artisan is saving.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            throw new ArtisanConfigUnreadableException($"Could not read Artisan config at {path}.", ex);
        }
    }
}

/// <summary>Artisan's config file is missing, locked, half-written or not valid; unlike a list id that is simply not in it.</summary>
public sealed class ArtisanConfigUnreadableException : Exception
{
    public ArtisanConfigUnreadableException(string message) : base(message) { }

    public ArtisanConfigUnreadableException(string message, Exception inner) : base(message, inner) { }
}
