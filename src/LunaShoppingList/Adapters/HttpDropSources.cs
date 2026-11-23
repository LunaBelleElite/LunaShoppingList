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

    /// <summary>Creates the index; call <see cref="DropSourceIndex.WarmUp"/> to start loading it.</summary>
    public static DropSourceIndex Create(IPluginLog log)
        => new(Fetch, line => Write(log, line));

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
