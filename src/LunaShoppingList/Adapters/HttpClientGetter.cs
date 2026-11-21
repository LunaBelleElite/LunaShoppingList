using System.Net.Http.Headers;
using System.Reflection;
using LunaShoppingList.Core;

namespace LunaShoppingList.Adapters;

/// <summary>IHttpGetter over one shared HttpClient.</summary>
public sealed class HttpClientGetter : IHttpGetter, IDisposable
{
    private readonly HttpClient client;

    public HttpClientGetter()
    {
        client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LunaShoppingList", version));
    }

    public async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var path = response.RequestMessage?.RequestUri?.AbsolutePath ?? "?";
            if (path.Length > 200)
                path = path[..200] + "...";
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} for {path}", null, response.StatusCode);
        }
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    public void Dispose() => client.Dispose();
}
