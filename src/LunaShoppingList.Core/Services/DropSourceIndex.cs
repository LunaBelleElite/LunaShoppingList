namespace LunaShoppingList.Core;

/// <summary>
/// The set of monster-drop items, loaded once in the background through a fetch delegate (the plugin supplies an HTTP download).
/// A failed fetch or parse is retried up to <see cref="MaxAttempts"/> times, at least the retry delay apart, then given up for the session.
/// </summary>
public sealed class DropSourceIndex : IDropSources, IDisposable
{
    /// <summary>Loads tried before giving up for the session.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Default wait between the start of one attempt and the next.</summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(10);

    private readonly Func<CancellationToken, Task<string>> fetch;
    private readonly Action<string>? log;
    private readonly long retryDelayMs;
    private readonly CancellationTokenSource cancel = new();

    // Guards the fields below; the log is only written while holding it and never after Dispose.
    private readonly object gate = new();
    private volatile HashSet<uint>? drops;
    private volatile bool failed;
    private bool disposed;
    private Task? warmUp;
    private long lastStartedAt;
    private int attempts;

    /// <param name="fetch">Returns the drop list's JSON text; may throw. Must honour the token.</param>
    /// <param name="log">Receives info, warning and error lines; optional.</param>
    /// <param name="retryDelay">Least time between attempts; <see cref="DefaultRetryDelay"/> when null.</param>
    public DropSourceIndex(Func<CancellationToken, Task<string>> fetch, Action<string>? log = null, TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        this.fetch = fetch;
        this.log = log;
        retryDelayMs = (long)(retryDelay ?? DefaultRetryDelay).TotalMilliseconds;
    }

    /// <summary>
    /// True once the list is loaded. While it is not, this starts (or, after a failure, retries up to
    /// <see cref="MaxAttempts"/> times) loading it in the background, so callers never wait for it.
    /// </summary>
    public bool IsReady
    {
        get
        {
            if (drops != null)
                return true;
            WarmUp();
            return false;
        }
    }

    /// <inheritdoc />
    public bool HasFailed => failed;

    /// <inheritdoc />
    public bool IsDrop(uint itemId)
    {
        var loaded = drops;
        if (loaded == null)
        {
            WarmUp();
            return false;
        }
        return loaded.Contains(itemId);
    }

    /// <summary>
    /// Loads the list on the thread pool unless it is loaded, being loaded, has failed for good, was disposed,
    /// or the last attempt started only moments ago.
    /// </summary>
    public void WarmUp()
    {
        lock (gate)
        {
            if (drops != null || failed || disposed || warmUp is { IsCompleted: false })
                return;
            if (attempts > 0 && Environment.TickCount64 - lastStartedAt < retryDelayMs)
                return;
            attempts++;
            lastStartedAt = Environment.TickCount64;
            var attempt = attempts;
            var token = cancel.Token;
            warmUp = Task.Run(() => RunAsync(attempt, token));
        }
    }

    private async Task RunAsync(int attempt, CancellationToken token)
    {
        try
        {
            var text = await fetch(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var parsed = DropSourceParser.Parse(text);
            lock (gate)
            {
                if (disposed)
                    return;
                drops = parsed;
                log?.Invoke($"Drop list ready: {parsed.Count} items");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Unloading: nothing to report.
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                if (disposed)
                    return;
                if (attempt >= MaxAttempts)
                {
                    failed = true;
                    log?.Invoke($"Loading the drop list failed {attempt} times, monster drops are not shown this session: {ex.Message}");
                }
                else
                {
                    log?.Invoke($"Loading the drop list failed (attempt {attempt} of {MaxAttempts}), it will be tried again: {ex.Message}");
                }
            }
        }
    }

    /// <summary>Stops a running load and makes sure nothing more is logged.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            cancel.Cancel();
        }
    }
}
