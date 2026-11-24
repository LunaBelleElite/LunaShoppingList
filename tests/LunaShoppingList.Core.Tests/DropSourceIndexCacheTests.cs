using System.Collections.Concurrent;

namespace LunaShoppingList.Core.Tests;

// The optional saved copy of the drop list: written only after a download parsed, read only after the first failed download.
public class DropSourceIndexCacheTests
{
    private const string Golden = "{\"4\":[115],\"44072\":[13101]}";
    private const string Cached = "{\"7\":[1]}";
    private static readonly TimeSpan NoDelay = TimeSpan.Zero;

    private static void Settle(DropSourceIndex index)
    {
        var until = Environment.TickCount64 + 5000;
        while (!(index.IsReady || index.HasFailed))
        {
            Assert.True(Environment.TickCount64 < until, "timed out");
            Thread.Sleep(5);
        }
    }

    // The index turns ready before the saved copy is written, so anything written after that is polled for, not assumed.
    private static void WaitFor(Func<bool> condition, string what)
    {
        var until = Environment.TickCount64 + 5000;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < until, "timed out waiting for " + what);
            Thread.Sleep(5);
        }
    }

    private static Func<CancellationToken, Task<string>> Failing(Action onCall) => _ => { onCall(); throw new HttpRequestException("offline"); };

    [Fact]
    public void Success_WritesTheDownloadedTextOnce_AndNeverReads()
    {
        var writes = new ConcurrentQueue<string>();
        var reads = 0;
        using var index = new DropSourceIndex(_ => Task.FromResult(Golden), null, NoDelay,
            () => { Interlocked.Increment(ref reads); return Cached; }, writes.Enqueue);
        Settle(index);
        WaitFor(() => !writes.IsEmpty, "the saved copy write");
        Thread.Sleep(100);
        Assert.Equal(new[] { Golden }, writes.ToArray());
        Assert.Equal(0, reads);
        Assert.True(index.IsDrop(4));
        Assert.False(index.IsDrop(7));
    }

    [Fact]
    public void DownloadThatDoesNotParse_IsNeverWritten()
    {
        var writes = new ConcurrentQueue<string>();
        using var index = new DropSourceIndex(_ => Task.FromResult("<html>not json</html>"), null, NoDelay, () => null, writes.Enqueue);
        Settle(index);
        Assert.True(index.HasFailed);
        Assert.Empty(writes);
    }

    [Fact]
    public void FirstFailure_WithAValidSavedCopy_BecomesReadyFromIt_AndStopsRetrying()
    {
        var calls = 0;
        var writes = new ConcurrentQueue<string>();
        var log = new ConcurrentQueue<string>();
        using var index = new DropSourceIndex(Failing(() => Interlocked.Increment(ref calls)), log.Enqueue, NoDelay, () => Cached, writes.Enqueue);
        Settle(index);
        Assert.True(index.IsReady);
        Assert.False(index.HasFailed);
        Assert.True(index.IsDrop(7));
        Assert.False(index.IsDrop(4));
        for (var i = 0; i < 20; i++)
        {
            index.WarmUp();
            _ = index.IsReady;
        }
        Thread.Sleep(100);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Empty(writes);
        Assert.Single(log, l => l.Contains("from saved copy", StringComparison.Ordinal));
        Assert.DoesNotContain(log, l => l.Contains("will be tried again", StringComparison.Ordinal));
    }

    [Fact]
    public void FirstAttemptThatDownloadsGarbage_AlsoFallsBackToTheSavedCopy()
    {
        using var index = new DropSourceIndex(_ => Task.FromResult("garbage"), null, NoDelay, () => Cached, _ => { });
        Settle(index);
        Assert.True(index.IsReady);
        Assert.True(index.IsDrop(7));
    }

    [Fact]
    public void CorruptSavedCopy_IsIgnored_AndTheNormalThreeAttemptsProceed()
    {
        var calls = 0;
        var log = new ConcurrentQueue<string>();
        using var index = new DropSourceIndex(Failing(() => Interlocked.Increment(ref calls)), log.Enqueue, NoDelay, () => "{ not json", _ => { });
        Settle(index);
        Assert.Contains(log, l => l.Contains("saved drop list could not be used", StringComparison.Ordinal));
        Assert.True(index.HasFailed);
        Assert.False(index.IsReady);
        Thread.Sleep(100);
        Assert.Equal(3, Volatile.Read(ref calls));
    }

    [Fact]
    public void CorruptSavedCopy_ThenALaterDownloadSucceeds_UsesTheDownload_AndWritesIt()
    {
        var calls = 0;
        var writes = new ConcurrentQueue<string>();
        using var index = new DropSourceIndex(_ =>
        {
            if (Interlocked.Increment(ref calls) < 3)
                throw new HttpRequestException("offline");
            return Task.FromResult(Golden);
        }, null, NoDelay, () => "[]", writes.Enqueue);
        Settle(index);
        Assert.True(index.IsReady);
        Assert.True(index.IsDrop(4));
        WaitFor(() => !writes.IsEmpty, "the saved copy write");
        Assert.Equal(new[] { Golden }, writes.ToArray());
    }

    [Fact]
    public void SavedCopy_IsReadOnlyAfterTheFirstFailure_NotLater()
    {
        var calls = 0;
        var reads = 0;
        using var index = new DropSourceIndex(_ =>
        {
            if (Interlocked.Increment(ref calls) < 3)
                throw new HttpRequestException("offline");
            return Task.FromResult(Golden);
        }, null, NoDelay, () => { Interlocked.Increment(ref reads); return null; }, _ => { });
        Settle(index);
        Assert.True(index.IsReady);
        Assert.Equal(1, Volatile.Read(ref reads));
    }

    [Fact]
    public void ThrowingRead_NeverBreaksTheIndex()
    {
        var calls = 0;
        var log = new ConcurrentQueue<string>();
        using var index = new DropSourceIndex(Failing(() => Interlocked.Increment(ref calls)), log.Enqueue, NoDelay,
            () => throw new IOException("disk gone"), _ => { });
        Settle(index);
        Assert.True(index.HasFailed);
        Thread.Sleep(100);
        Assert.Equal(3, Volatile.Read(ref calls));
        Assert.Contains(log, l => l.Contains("disk gone", StringComparison.Ordinal));
    }

    [Fact]
    public void ThrowingWrite_StillBecomesReady_AndLogsIt()
    {
        var log = new ConcurrentQueue<string>();
        using var index = new DropSourceIndex(_ => Task.FromResult(Golden), log.Enqueue, NoDelay, () => null,
            _ => throw new IOException("disk full"));
        Settle(index);
        Assert.True(index.IsReady);
        Assert.False(index.HasFailed);
        Assert.True(index.IsDrop(4));
        WaitFor(() => log.Any(l => l.Contains("disk full", StringComparison.Ordinal)), "the write failure log line");
        Assert.Contains(log, l => l.Contains("disk full", StringComparison.Ordinal));
        Assert.DoesNotContain(log, l => l.Contains("tried again", StringComparison.Ordinal) || l.Contains("failed", StringComparison.Ordinal));
    }

    [Fact]
    public void NoCacheDelegates_BehavesAsBefore()
    {
        var calls = 0;
        using var index = new DropSourceIndex(Failing(() => Interlocked.Increment(ref calls)), null, NoDelay);
        Settle(index);
        Assert.True(index.HasFailed);
        Thread.Sleep(100);
        Assert.Equal(3, Volatile.Read(ref calls));
    }
}
