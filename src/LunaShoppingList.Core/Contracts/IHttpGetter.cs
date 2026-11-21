namespace LunaShoppingList.Core;

/// <summary>Minimal HTTP GET abstraction so the Universalis client is testable offline.</summary>
public interface IHttpGetter
{
    /// <summary>Performs a GET and returns the body text; throws on transport failure or non-success status.</summary>
    /// <param name="url">Absolute URL.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string> GetStringAsync(string url, CancellationToken ct);
}
