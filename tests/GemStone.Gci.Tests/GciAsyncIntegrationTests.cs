using System.Diagnostics;

namespace GemStone.Gci.Tests;

[Collection("Stone")]
public class GciAsyncIntegrationTests
{
    private static string Sleep(int ms) => $"(Delay forMilliseconds: {ms}) wait. {ms}";

    [StoneFact]
    public async Task AsyncCallsReturnResultsAndErrors()
    {
        using var s = StoneEnv.Login();
        Assert.Equal(7L, await s.EvaluateAsync("3 + 4"));
        Assert.Equal(5, await s.EvaluateAsync<int>("'Grüße' size")); // non-ASCII source path
        Assert.Null(await s.EvaluateAsync("nil"));                    // nil is not an error
        Assert.Equal(s.Execute("7"), await s.PerformAsync(Oop.FromSmallInteger(3), "+", Oop.FromSmallInteger(4)));
        Assert.Equal("ab!", await s.SendAsync(s.Execute("'ab'"), ",", ["!"]));

        var ex = await Assert.ThrowsAsync<GciException>(() => s.ExecuteAsync("1/0"));
        Assert.Equal(2026, ex.Number); // ZeroDivide
        Assert.Equal(2L, s.Evaluate("1 + 1")); // usable afterwards, sync and async
        Assert.Equal(3L, await s.EvaluateAsync("1 + 2"));
    }

    [StoneFact]
    public async Task SessionsExecuteInParallel()
    {
        using var a = StoneEnv.Login();
        using var b = StoneEnv.Login();
        await Task.WhenAll(a.EvaluateAsync("1"), b.EvaluateAsync("1")); // warm up

        var watch = Stopwatch.StartNew();
        var results = await Task.WhenAll(a.EvaluateAsync<long>(Sleep(1000)), b.EvaluateAsync<long>(Sleep(1000)));
        watch.Stop();

        Assert.Equal([1000L, 1000L], results);
        Assert.InRange(watch.ElapsedMilliseconds, 950, 1700); // sequential would take ≥ 2000 ms
    }

    [StoneFact]
    public async Task OtherSessionsStayUsableWhileOneWaits()
    {
        using var slow = StoneEnv.Login();
        using var fast = StoneEnv.Login();

        var pending = slow.EvaluateAsync<long>(Sleep(1500));
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
            Assert.Equal((long)i, fast.Evaluate($"{i}"));
        watch.Stop();

        Assert.False(pending.IsCompleted);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"fast session was blocked for {watch.ElapsedMilliseconds} ms");
        Assert.Equal(1500L, await pending);
    }

    [StoneFact]
    public async Task SyncCallOnBusySessionIsRejected()
    {
        using var s = StoneEnv.Login();
        var pending = s.ExecuteAsync(Sleep(500));

        Assert.Throws<InvalidOperationException>(() => s.Execute("1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.ExecuteAsync("1"));

        await pending;
        Assert.Equal(1L, s.Evaluate("1"));
    }

    [StoneFact]
    public async Task CancellationSendsSoftBreak()
    {
        using var s = StoneEnv.Login();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var watch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => s.ExecuteAsync("[true] whileTrue", cancellationToken: cts.Token));
        Assert.IsType<GciException>(ex.InnerException);
        Assert.True(watch.ElapsedMilliseconds < 5000);

        Assert.True(s.IsLoggedIn);
        Assert.Equal(4L, await s.EvaluateAsync("2 + 2"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            s.ExecuteAsync("1", cancellationToken: new CancellationToken(canceled: true)));
    }

    [StoneFact]
    public async Task AsyncTransactions()
    {
        var key = "GemStoneGciTest_" + Guid.NewGuid().ToString("N")[..12];
        using var a = StoneEnv.Login();
        using var b = StoneEnv.Login();
        try
        {
            await a.AbortAsync();
            a.Execute($"UserGlobals at: #{key} put: 1");
            await a.CommitAsync();

            await b.AbortAsync();
            Assert.Equal(1L, b.Evaluate($"UserGlobals at: #{key}"));

            // Conflict: b's snapshot predates a's second commit.
            a.Execute($"UserGlobals at: #{key} put: 2");
            await a.CommitAsync();
            b.Execute($"UserGlobals at: #{key} put: 3");
            Assert.False(await b.TryCommitAsync());
            await Assert.ThrowsAsync<GciTransactionConflictException>(() => b.CommitAsync());
            await b.AbortAsync();
            Assert.Equal(2L, b.Evaluate($"UserGlobals at: #{key}"));

            a.TransactionMode = GciTransactionMode.ManualBegin;
            await a.BeginAsync();
            Assert.True(a.InTransaction);
        }
        finally
        {
            a.TransactionMode = GciTransactionMode.AutoBegin;
            a.Abort();
            a.Execute($"UserGlobals removeKey: #{key} ifAbsent: [nil]");
            a.Commit();
        }
    }

    [StoneFact]
    public async Task DisposeDuringPendingCallInterruptsIt()
    {
        using var other = StoneEnv.Login();
        var s = StoneEnv.Login();
        var pending = s.ExecuteAsync("[true] whileTrue");
        await Task.Delay(200);

        s.Dispose();
        Assert.False(s.IsLoggedIn);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
        Assert.Equal(1L, other.Evaluate("1"));
    }
}
