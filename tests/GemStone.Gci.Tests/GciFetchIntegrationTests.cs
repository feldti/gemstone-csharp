using System.Text;

namespace GemStone.Gci.Tests;

[Collection("Stone")]
public class GciFetchIntegrationTests
{
    [StoneFact]
    public void NonAsciiSourceIsCompiledAsUtf8()
    {
        using var s = StoneEnv.Login();
        // Via GciExecuteStr this would be 7 (UTF-8 bytes read as 8-bit chars).
        Assert.Equal(s.Execute("5"), s.Execute("'Grüße' size"));
        Assert.Equal(s.Execute("3"), s.Execute("'€ 😀' size"));
    }

    [StoneFact]
    public void PrintStringRoundTripsUnicode()
    {
        using var s = StoneEnv.Login();
        Assert.Equal("7", s.PrintString(s.Execute("3 + 4")));
        Assert.Equal("nil", s.PrintString(Oop.Nil));
        Assert.Equal("'日本ä'", s.PrintString(s.Execute("'日本' , 'ä'")));
    }

    [StoneFact]
    public void FetchBytesReturnsByteObjectContents()
    {
        using var s = StoneEnv.Login();
        var str = s.Execute("'hello' copy");
        try
        {
            Assert.Equal(5, s.FetchSize(str));
            Assert.Equal("hello", Encoding.ASCII.GetString(s.FetchBytes(str)));
            Assert.Empty(s.FetchBytes(s.Execute("String new")));
        }
        finally
        {
            s.Release(str);
        }
    }

    [StoneFact]
    public void SoftBreakInterruptsRunningCodeAndSessionStaysUsable()
    {
        using var s = StoneEnv.Login();
        var breaker = Task.Run(async () =>
        {
            await Task.Delay(500);
            s.SoftBreak();
        });

        var ex = Assert.Throws<GciException>(() => s.Execute("[true] whileTrue"));
        breaker.Wait();
        Assert.False(ex.IsFatal);
        Assert.True(s.IsLoggedIn);
        Assert.Equal(s.Execute("2"), s.Execute("1 + 1"));
    }
}
