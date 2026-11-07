namespace GemStone.Gci.Tests;

/// <summary>Several logged-in sessions in one process, switched via GciSetSessionId.</summary>
[Collection("Stone")]
public class GciMultiSessionIntegrationTests
{
    private const string GemSessionId = "System session";

    [StoneFact]
    public void SessionsAreIndependentGems()
    {
        using var a = StoneEnv.Login();
        using var b = StoneEnv.Login();

        Assert.NotEqual(a.SessionId, b.SessionId);
        Assert.NotEqual(a.Evaluate<long>(GemSessionId), b.Evaluate<long>(GemSessionId));

        // Interleaved calls hit the right gem every time.
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(a.Evaluate<long>(GemSessionId), a.Evaluate<long>(GemSessionId));
            Assert.NotEqual(a.Evaluate<long>(GemSessionId), b.Evaluate<long>(GemSessionId));
        }
    }

    [StoneFact]
    public void SessionStateIsSeparate()
    {
        using var a = StoneEnv.Login();
        using var b = StoneEnv.Login();

        a.TransactionMode = GciTransactionMode.ManualBegin;
        Assert.Equal(GciTransactionMode.ManualBegin, a.TransactionMode);
        Assert.Equal(GciTransactionMode.AutoBegin, b.TransactionMode);

        // Session-local temporaries (SessionTemps) do not leak between sessions.
        a.Execute("SessionTemps current at: #GciProbe put: 'a'");
        b.Execute("SessionTemps current at: #GciProbe put: 'b'");
        Assert.Equal("a", a.Evaluate("SessionTemps current at: #GciProbe"));
        Assert.Equal("b", b.Evaluate("SessionTemps current at: #GciProbe"));
    }

    [StoneFact]
    public void ObjectsAndReleaseStayInTheirSession()
    {
        using var a = StoneEnv.Login();
        using var b = StoneEnv.Login();

        var strA = a.NewString("from a");
        var strB = b.NewString("from b");
        // Each call switches to the owning session before touching the OOP.
        Assert.Equal("from a", a.ToStringValue(strA));
        Assert.Equal("from b", b.ToStringValue(strB));
        Assert.Equal("from a!", a.Send(strA, ",", "!"));
        a.Release(strA);
        b.Release(strB);
    }

    [StoneFact]
    public void DisposingOneSessionKeepsTheOthersWorking()
    {
        using var a = StoneEnv.Login();
        var b = StoneEnv.Login();
        var c = StoneEnv.Login();

        b.Dispose();
        Assert.False(b.IsLoggedIn);
        Assert.Throws<ObjectDisposedException>(() => b.Execute("1"));

        Assert.Equal(7L, a.Evaluate("3 + 4"));
        Assert.Equal(8L, c.Evaluate("4 + 4"));
        c.Dispose();
        Assert.Equal(9L, a.Evaluate("4 + 5"));
    }

    [StoneFact]
    public void ErrorInOneSessionDoesNotAffectTheOther()
    {
        using var a = StoneEnv.Login();
        using var b = StoneEnv.Login();

        Assert.Throws<GciException>(() => a.Execute("1/0"));
        Assert.Equal(2L, b.Evaluate("1 + 1")); // no stale error report from a
        Assert.Equal(3L, a.Evaluate("1 + 2"));
    }

    [StoneFact]
    public void SessionsCanBeUsedFromDifferentThreads()
    {
        using var a = StoneEnv.Login();
        using var b = StoneEnv.Login();
        var gemA = a.Evaluate<long>(GemSessionId);
        var gemB = b.Evaluate<long>(GemSessionId);

        // Calls are serialized by the GCI lock; each must still reach its own gem.
        Parallel.For(0, 40, i =>
        {
            var (s, gem) = i % 2 == 0 ? (a, gemA) : (b, gemB);
            Assert.Equal(gem, s.Evaluate<long>(GemSessionId));
            Assert.Equal((long)i * i, s.Send(Oop.FromSmallInteger(i), "*", i));
        });
    }

    [StoneFact]
    public void SoftBreakOnlyInterruptsTheTargetSession()
    {
        using var a = StoneEnv.Login();
        using var idle = StoneEnv.Login();

        idle.SoftBreak(); // not executing: no effect, nothing pending afterwards
        Assert.Equal(1L, idle.Evaluate("1"));

        var breaker = Task.Run(async () =>
        {
            await Task.Delay(500);
            a.SoftBreak();
        });
        Assert.Throws<GciException>(() => a.Execute("[true] whileTrue"));
        breaker.Wait();
        Assert.Equal(2L, idle.Evaluate("2"));
        Assert.Equal(3L, a.Evaluate("3"));
    }
}
