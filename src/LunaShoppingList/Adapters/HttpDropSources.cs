using Dalamud.Plugin.Services;
using LunaShoppingList.Core;

namespace LunaShoppingList.Adapters;

/// <summary>
/// Builds the monster-drop index, loaded from a public data file on the internet. The request is a plain GET:
/// no sign-in, no cookies, no custom headers and nothing about the user.
/// </summary>
public static class HttpDropSources
{
    internal const string Url = "https://raw.githubusercontent.com/ffxiv-teamcraft/ffxiv-teamcraft/master/libs/data/src/lib/json/drop-sources.json";

    private static readonly HttpClient Client = new(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(15) };

    internal const string CacheFileName = "drop-sources.cache.json";

    /// <summary>
    /// Creates the index; call <see cref="DropSourceIndex.WarmUp"/> to start loading it. A copy of the last good download is kept
    /// as <see cref="CacheFileName"/> in <paramref name="configDir"/> and used when the first download fails.
    /// </summary>
    public static DropSourceIndex Create(IPluginLog log, string configDir)
    {
        var path = Path.Combine(configDir, CacheFileName);
        return new(Fetch, line => Write(log, line), null, () => ReadCache(path), text => WriteCache(path, text));
    }

    // Returns the saved text, or null if there is none. A copy that does not parse is set aside as .bad (replacing an older one).
    internal static string? ReadCache(string path)
    {
        if (!File.Exists(path))
            return null;
        var text = File.ReadAllText(path);
        try
        {
            DropSourceParser.Parse(text);
            return text;
        }
        catch (FormatException)
        {
            File.Move(path, path + ".bad", overwrite: true);
            return null;
        }
    }

    // Writes beside the file first, then moves it over, so a crash mid-write never leaves a half-written copy.
    internal static void WriteCache(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }

    private static async Task<string> Fetch(CancellationToken ct)
    {
        using var response = await Client.GetAsync(Url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    // The index logs plain lines; the wording of each one says how serious it is.
    private static void Write(IPluginLog log, string line)
    {
        if (line.Contains("will be tried again", StringComparison.Ordinal))
            log.Warning("{Line}", line);
        else if (line.Contains("failed", StringComparison.Ordinal))
            log.Error("{Line}", line);
        else
            log.Information("{Line}", line);
    }
}
