namespace LunaShoppingList.Core;

/// <summary>What happened when the plugin asked Lifestream to change world.</summary>
public enum LifestreamOutcome
{
    /// <summary>Lifestream is not installed or not loaded (or its IPC was not ready).</summary>
    NotAvailable,
    /// <summary>Lifestream reported that it is busy.</summary>
    Busy,
    /// <summary>Lifestream refused the request (busy, or the world is not reachable from here).</summary>
    Refused,
    /// <summary>Lifestream queued the request. It is not a promise of arrival.</summary>
    Requested,
}

/// <summary>The one-line result text shown after a click on a world name.</summary>
public static class LifestreamNote
{
    /// <summary>The line for <paramref name="outcome"/>; <paramref name="world"/> is named where the line has room for it.</summary>
    public static string Line(LifestreamOutcome outcome, string world) => outcome switch
    {
        LifestreamOutcome.NotAvailable => "Lifestream is not installed or not loaded.",
        LifestreamOutcome.Busy => "Lifestream is busy.",
        LifestreamOutcome.Refused => $"Lifestream can't go to {world} right now (busy, or the world is not reachable from here).",
        LifestreamOutcome.Requested => $"Asked Lifestream to go to {world}. Lifestream shows its own messages if it can't.",
        _ => string.Empty,
    };
}
