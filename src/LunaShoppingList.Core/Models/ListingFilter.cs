namespace LunaShoppingList.Core;

/// <summary>
/// Drops stale listings when the offer book is built. A listing is dropped when its review time is strictly older than
/// <c>Now - MaxAge</c>; a listing exactly <c>MaxAge</c> old is kept. A review time at or before the Unix epoch is the
/// "unknown" sentinel the Universalis parser uses, and such a listing is always kept.
/// </summary>
/// <param name="MaxAge">The oldest review a listing may have, or <c>null</c> for no filtering.</param>
/// <param name="Now">The current time, passed in so the build never reads a clock.</param>
public record ListingFilter(TimeSpan? MaxAge, DateTimeOffset Now);
