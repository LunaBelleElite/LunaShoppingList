using System.Collections.Concurrent;

namespace LunaShoppingList.Core.Tests;

public class DropSourceIndexTests
{
    private const string Golden = "{\"4\":[115],\"44072\":[13101]}";
    private static readonly TimeSpan NoDelay = TimeSpan.Zero;

    private static void WaitFor(Func<bool> condition)
    {
        var until = Environment.TickCount64 + 5000;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < until, "timed out waiting for the condition");
            Thread.Sleep(5);
        }
    }

    // Polls IsReady (which retries) until the index is ready or has failed.
    private static void Settle(DropSourceIndex index) => WaitFor(() => index.IsReady || index.HasFailed);

    [Fact]
    public void Constructing_DoesNotFetch()
    {
        var calls = 0;
        using var index = new DropSourceIndex(_ => { Interlocked.Increment(ref calls); return Task.FromResult(Golden); }, null, NoDelay);
        Thread.Sleep(100);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Success_BecomesReady_AndAnswersIsDrop()
    {
        using var index = new DropSourceIndex(_ => Task.FromResult(Golden), null, NoDelay);
        Settle(index);
        Assert.True(index.IsReady);
        Assert.False(index.HasFailed);
        Assert.True(index.IsDrop(4));
        Assert.True(index.IsDrop(44072));
        Assert.False(index.IsDrop(5));
        Assert.False(index.IsDrop(0));
    }

    [Fact]
    public void WhileLoading_IsNotReady_IsDropFalse_AndNeverBlocks()
    {
        var release = new TaskCompletionSource<string>();
        using var index = new DropSourceIndex(_ => release.Task, null, NoDelay);
        index.WarmUp();
        Assert.False(index.IsReady);
        Assert.False(index.IsDrop(4));
        Assert.False(index.HasFailed);
        release.SetResult(Golden);
        Settle(index);
        Assert.True(index.IsDrop(4));
    }

    [Fact]
    public void ManyWarmUpsAndReads_StartOneLoad()
    {
        var calls = 0;
        var release = new TaskCompletionSource<string>();
        using var index = new DropSourceIndex(_ => { Interlocked.Increment(ref calls); return release.Task; }, null, NoDelay);
        for (var i = 0; i < 20; i++)
        {
            index.WarmUp();
            _ = index.IsReady;
            _ = index.IsDrop(4);
        }
        WaitFor(() => Volatile.Read(ref calls) >= 1);
        Thread.Sleep(100);
        Assert.Equal(1, Volatile.Read(ref calls));
        release.SetResult(Golden);
        Settle(index);
        for (var i = 0; i < 20; i++)
            index.WarmUp();
        Thread.Sleep(100);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public void ReadingIsDropAlone_StartsTheLoad()
    {
        using var index = new DropSourceIndex(_ => Task.FromResult(Golden), null, NoDelay);
        Assert.False(index.IsDrop(4));
        WaitFor(() => index.IsDrop(4));
    }

    [Fact]
    public void ThreeFetchFailures_SetHasFailed_AndNeverTryAgain()
    {
        var calls = 0;
        using var index = new DropSourceIndex(_ => { Interlocked.Increment(ref calls); throw new HttpRequestException("offline"); }, null, NoDelay);
        Settle(index);
        Assert.True(index.HasFailed);
        Assert.False(index.IsReady);
        Assert.False(index.IsDrop(4));
        for (var i = 0; i < 20; i++)
        {
            index.WarmUp();
            _ = index.IsReady;
        }
        Thread.Sleep(100);
        Assert.Equal(3, Volatile.Read(ref calls));
    }

    [Fact]
    public void AsyncFetchFailure_CountsLikeAThrow()
    {
        var calls = 0;
        using var index = new DropSourceIndex(_ => { Interlocked.Increment(ref calls); return Task.FromException<string>(new HttpRequestException("offline")); }, null, NoDelay);
        Settle(index);
        Assert.True(index.HasFailed);
        Assert.Equal(3, Volatile.Read(ref calls));
    }

    [Fact]
    public void ParseFailure_CountsAsAFailedAttempt()
    {
        var calls = 0;
        using var index = new DropSourceIndex(_ => { Interlocked.Increment(ref calls); return Task.FromResult("<html>not json</html>"); }, null, NoDelay);
        Settle(index);
        Assert.True(index.HasFailed);
        Assert.False(index.IsReady);
        Assert.Equal(3, Volatile.Read(ref calls));
    }

    [Fact]
    public void NotFailedUntilTheThirdAttempt()
    {
        var calls = 0;
        var third = new TaskCompletionSource<string>();
        using var index = new DropSourceIndex(_ =>
        {
            var n = Interlocked.Increment(ref calls);
            return n < 3 ? Task.FromException<string>(new IOException("x")) : third.Task;
        }, null, NoDelay);
        WaitFor(() => { _ = index.IsReady; return Volatile.Read(ref calls) == 3; });
        Assert.False(index.HasFailed);
        third.SetException(new IOException("x"));
        Settle(index);
        Assert.True(index.HasFailed);
    }

    [Fact]
    public void TwoFailuresThenSuccess_BecomesReady()
    {
        var calls = 0;
        using var index = new DropSourceIndex(_ =>
        {
            var n = Interlocked.Increment(ref calls);
            return n < 3 ? Task.FromException<string>(new IOException("x")) : Task.FromResult(Golden);
        }, null, NoDelay);
        Settle(index);
        Assert.True(index.IsReady);
        Assert.False(index.HasFailed);
        Assert.True(index.IsDrop(44072));
        Assert.Equal(3, Volatile.Read(ref calls));
    }

    [Fact]
    public void RetryDelay_BlocksAnEarlySecondAttempt()
    {
        var calls = 0;
        using var index = new DropSourceIndex(_ => { Interlocked.Increment(ref calls); throw new IOException("x"); }, null, TimeSpan.FromHours(1));
        index.WarmUp();
        WaitFor(() => Volatile.Read(ref calls) == 1);
        Thread.Sleep(100);
        for (var i = 0; i < 20; i++)
        {
            index.WarmUp();
            _ = index.IsReady;
        }
        Thread.Sleep(100);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.False(index.HasFailed);
    }

    [Fact]
    public void DefaultRetryDelay_IsTenSeconds_AndMaxAttemptsIsThree()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), DropSourceIndex.DefaultRetryDelay);
        Assert.Equal(3, DropSourceIndex.MaxAttempts);
        var calls = 0;
        using var index = new DropSourceIndex(_ => { Interlocked.Increment(ref calls); throw new IOException("x"); });
        index.WarmUp();
        WaitFor(() => Volatile.Read(ref calls) == 1);
        Thread.Sleep(100);
        _ = index.IsReady;
        Thread.Sleep(100);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public void Dispose_CancelsTheFetchToken()
    {
        CancellationToken seen = default;
        var started = new TaskCompletionSource();
        var index = new DropSourceIndex(async token =>
        {
            seen = token;
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Golden;
        }, null, NoDelay);
        index.WarmUp();
        WaitFor(() => started.Task.IsCompleted);
        Assert.False(seen.IsCancellationRequested);
        index.Dispose();
        Assert.True(seen.IsCancellationRequested);
    }

    [Fact]
    public void Dispose_WhileLoading_NeverBecomesReadyAndLogsNothing()
    {
        var log = new ConcurrentQueue<string>();
        var release = new TaskCompletionSource<string>();
        var started = new TaskCompletionSource();
        var index = new DropSourceIndex(_ => { started.TrySetResult(); return release.Task; }, log.Enqueue, NoDelay);
        index.WarmUp();
        WaitFor(() => started.Task.IsCompleted);
        index.Dispose();
        release.SetResult(Golden);
        Thread.Sleep(150);
        Assert.False(index.IsReady);
        Assert.False(index.IsDrop(4));
        Assert.Empty(log);
    }

    [Fact]
    public void Dispose_WhileLoading_ALateFailureLogsNothingAndDoesNotFail()
    {
        var log = new ConcurrentQueue<string>();
        var release = new TaskCompletionSource<string>();
        var started = new TaskCompletionSource();
        var index = new DropSourceIndex(_ => { started.TrySetResult(); return release.Task; }, log.Enqueue, NoDelay);
        index.WarmUp();
        WaitFor(() => started.Task.IsCompleted);
        index.Dispose();
        release.SetException(new IOException("late"));
        Thread.Sleep(150);
        Assert.False(index.HasFailed);
        Assert.Empty(log);
    }

    [Fact]
    public void AfterDispose_NothingIsFetched()
    {
        var calls = 0;
        var index = new DropSourceIndex(_ => { Interlocked.Increment(ref calls); return Task.FromResult(Golden); }, null, NoDelay);
        index.Dispose();
        index.WarmUp();
        _ = index.IsReady;
        _ = index.IsDrop(4);
        Thread.Sleep(100);
        Assert.Equal(0, calls);
        index.Dispose();
    }

    [Fact]
    public void Success_LogsOneLineWithTheCount()
    {
        var log = new ConcurrentQueue<string>();
        using var index = new DropSourceIndex(_ => Task.FromResult(Golden), log.Enqueue, NoDelay);
        Settle(index);
        Assert.Equal(new[] { "Drop list ready: 2 items" }, log.ToArray());
    }

    [Fact]
    public void ThreeFailures_LogTwoRetriesAndOneGiveUp()
    {
        var log = new ConcurrentQueue<string>();
        using var index = new DropSourceIndex(_ => throw new IOException("boom"), log.Enqueue, NoDelay);
        Settle(index);
        var lines = log.ToArray();
        Assert.Equal(3, lines.Length);
        Assert.Contains("attempt 1 of 3", lines[0]);
        Assert.Contains("attempt 2 of 3", lines[1]);
        Assert.Contains("failed 3 times", lines[2]);
        Assert.All(lines, l => Assert.Contains("boom", l));
    }

    [Fact]
    public void NullFetch_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DropSourceIndex(null!));
    }
}
