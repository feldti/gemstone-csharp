namespace GemStone.Gci.Tests;

/// <summary>
/// SmallIntegers and Booleans are immediate OOPs, so results can be compared without
/// fetch/convert functions: Execute("3 + 4") must yield the same OOP as Execute("7").
/// </summary>
[Collection("Stone")]
public class GciExecutionIntegrationTests
{
    [StoneFact]
    public void ExecuteReturnsResultOop()
    {
        using var s = StoneEnv.Login();
        Assert.Equal(s.Execute("7"), s.Execute("3 + 4"));
        Assert.Equal(Oop.True, s.Execute("3 < 4"));
    }

    [StoneFact]
    public void ExecuteReturningNilIsNotAnError()
    {
        using var s = StoneEnv.Login();
        Assert.Equal(Oop.Nil, s.Execute("nil"));
    }

    [StoneFact]
    public void ExecuteErrorThrowsGciException()
    {
        using var s = StoneEnv.Login();
        var ex = Assert.Throws<GciException>(() => s.Execute("1/0"));
        Assert.NotEqual(0, ex.Number);
        Assert.NotEmpty(ex.Message);

        // Session stays usable after a non-fatal error.
        Assert.Equal(s.Execute("2"), s.Execute("1 + 1"));
    }

    [StoneFact]
    public void PerformSendsUnaryBinaryAndKeywordMessages()
    {
        using var s = StoneEnv.Login();
        var three = s.Execute("3");

        Assert.Equal(s.Execute("-3"), s.Perform(three, "negated"));
        Assert.Equal(s.Execute("7"), s.Perform(three, "+", s.Execute("4")));
        Assert.Equal(s.Execute("5"), s.Perform(three, "max:", s.Execute("5")));
        Assert.Equal(Oop.True, s.Perform(three, "between:and:", s.Execute("1"), s.Execute("5")));
    }

    [StoneFact]
    public void PerformUnknownSelectorThrows()
    {
        using var s = StoneEnv.Login();
        Assert.Throws<GciException>(() => s.Perform(s.Execute("3"), "noSuchSelectorXyz"));
    }

    [StoneFact]
    public void CallsAfterDisposeThrow()
    {
        var s = StoneEnv.Login();
        s.Dispose();
        Assert.Throws<ObjectDisposedException>(() => s.Execute("1"));
    }
}
