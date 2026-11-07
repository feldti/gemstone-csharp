using System.Numerics;
using System.Text;

namespace GemStone.Gci.Tests;

[Collection("Stone")]
public class GciValueIntegrationTests
{
    [StoneFact]
    public void PureEncodingsMatchTheServer()
    {
        using var s = StoneEnv.Login();
        Assert.Equal(s.Execute("42"), Oop.FromSmallInteger(42));
        Assert.Equal(s.Execute("-42"), Oop.FromSmallInteger(-42));
        Assert.Equal(s.Execute("SmallInteger maxVal"), Oop.FromSmallInteger(Oop.SmallIntegerMax));
        Assert.Equal(s.Execute("SmallInteger minVal"), Oop.FromSmallInteger(Oop.SmallIntegerMin));
        Assert.Equal(s.Execute("$a"), Oop.FromCharacter(new Rune('a')));
        Assert.Equal(s.Execute("Character codePoint: 16r1F600"), Oop.FromCharacter(new Rune(0x1F600)));
        Assert.True(s.Execute("DateAndTime now").IsSmallDateAndTime);
        Assert.True(s.Execute("1.5").IsSmallDouble);
        Assert.True(s.Execute("Date today").IsSmallDate);
        Assert.True(s.Execute("Time now").IsSmallTime);
        Assert.True(s.Execute("1/3").IsSmallFraction);
        Assert.True(s.Execute("1.5s1").IsSmallScaledDecimal);
        Assert.False(s.Execute("Date today").IsSmallTime);
    }

    public static TheoryData<string, object?> ScalarCases => new()
    {
        { "nil", null },
        { "true", true },
        { "false", false },
        { "3 + 4", 7L },
        { "-5", -5L },
        { "2 raisedTo: 62", 1L << 62 },                // LargeInteger within int64
        { "1.5", 1.5 },                                // SmallDouble
        { "1.0e300", 1e300 },                          // Float
        { "12.34s2", 12.34m },
        { "-0.001s3", -0.001m },
        { "(1/3) asScaledDecimal: 5", 0.33333m },
        { "$ä", new Rune('ä') },
        { "'abc'", "abc" },
        { "#foo:bar:", "foo:bar:" },
        { "'Grüße 日本'", "Grüße 日本" },
        { "'x😀y'", "x😀y" },
        { "'abc' asDoubleByteString", "abc" },
        { "'Grüße' encodeAsUTF8", "Grüße" },
        { "Date newDay: 15 monthNumber: 7 year: 2026", new DateOnly(2026, 7, 15) },
        { "Time fromSeconds: 3661", new TimeOnly(1, 1, 1) },
    };

    [StoneTheory]
    [MemberData(nameof(ScalarCases))]
    public void EvaluateMapsToDotNetValues(string source, object? expected)
    {
        using var s = StoneEnv.Login();
        Assert.Equal(expected, s.Evaluate(source));
    }

    [StoneFact]
    public void LargeIntegersBeyondInt64BecomeBigInteger()
    {
        using var s = StoneEnv.Login();
        Assert.Equal(BigInteger.Pow(2, 70), s.Evaluate("2 raisedTo: 70"));
        Assert.Equal(-BigInteger.Pow(2, 70), s.Evaluate("(2 raisedTo: 70) negated"));
        Assert.Throws<InvalidCastException>(() => s.ToInt64(s.Execute("2 raisedTo: 70")));
    }

    [StoneFact]
    public void LargeStringsAreFetchedCompletely()
    {
        using var s = StoneEnv.Login();
        // > 64 KiB fetch chunk, both 8-bit and UTF-8-encoded paths.
        Assert.Equal(new string('a', 200_000), s.Evaluate("String new: 200000 withAll: $a"));
        Assert.Equal(new string('ü', 100_000), s.Evaluate("String new: 100000 withAll: $ü"));
        Assert.Equal(
            string.Concat(Enumerable.Repeat("😀", 50_000)),
            s.Evaluate<string>("(Unicode32 new: 50000) atAllPut: (Character codePoint: 16r1F600); yourself"));
    }

    [StoneFact]
    public void ByteArrayBecomesBytesAndUnmappedObjectsStayOops()
    {
        using var s = StoneEnv.Login();
        Assert.Equal(new byte[] { 1, 2, 255 }, s.Evaluate("#[1 2 255]"));

        var array = Assert.IsType<Oop>(s.Evaluate("Array with: 1 with: 2"));
        try { Assert.Equal(KnownClasses.Array, s.FetchClass(array)); }
        finally { s.Release(array); }

        Assert.IsType<Oop>(s.Evaluate("1/3")); // SmallFraction: not mapped
    }

    public static TheoryData<object> RoundTripValues => new()
    {
        true, 0, 42, -42L, long.MaxValue, long.MinValue, BigInteger.Pow(10, 30), -BigInteger.Pow(10, 30),
        0.0, -2.5, 1e300, double.MaxValue, 12.345m, -0.5m, 123456789012345678901234.5678m,
        new Rune('x'), new Rune(0x1F600), "", "plain", "Grüße 日本 😀",
        new byte[] { 0, 1, 255 }, Array.Empty<byte>(),
        new DateOnly(2026, 7, 15), new DateOnly(1, 1, 1), new DateOnly(1900, 12, 31), new DateOnly(9999, 12, 31),
        new TimeOnly(0, 0), new TimeOnly(23, 59, 59, 999, 999), new TimeOnly(12, 30, 15, 123),
    };

    [StoneTheory]
    [MemberData(nameof(RoundTripValues))]
    public void FromValueToValueRoundTrips(object value)
    {
        using var s = StoneEnv.Login();
        var oop = s.FromValue(value);
        try
        {
            var back = s.ToValue(oop);
            switch (value)
            {
                case int i: Assert.Equal((long)i, back); break;
                default: Assert.Equal(value, back); break;
            }
        }
        finally
        {
            s.Release(oop);
        }
    }

    [StoneFact]
    public void DateAndTimeUsesUnixEpochAndKeepsOffsetAndMicroseconds()
    {
        using var s = StoneEnv.Login();

        // Created in .NET → interpreted correctly by the server.
        var dt = s.NewDateAndTime(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        try { Assert.Equal(1767268800m, s.Send(dt, "asPosixSeconds")); }
        finally { s.Release(dt); }

        // Created on the server → read correctly in .NET.
        Assert.Equal(
            new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(2)),
            s.Evaluate("DateAndTime year: 2026 month: 7 day: 1 hour: 12 minute: 0 second: 0 offset: (Duration seconds: 7200)"));

        // Round trip, microsecond precision (.NET ticks are 100 ns: sub-microsecond is truncated).
        var value = new DateTimeOffset(2026, 7, 1, 14, 30, 15, 123, TimeSpan.FromHours(2)).AddTicks(4567);
        var oop = s.NewDateAndTime(value);
        try
        {
            var back = s.ToDateTimeOffset(oop);
            Assert.Equal(value.AddTicks(-7), back);
            Assert.Equal(value.Offset, back.Offset);
        }
        finally { s.Release(oop); }
    }

    [StoneFact]
    public void SendConvertsArgumentsAndResult()
    {
        using var s = StoneEnv.Login();
        var str = s.NewString("Grüße");
        try
        {
            Assert.Equal(5L, s.Send(str, "size"));
            Assert.Equal("Grüße, Welt", s.Send(str, ",", ", Welt"));
            Assert.Equal(new Rune('ü'), s.Send(str, "at:", 3));
            Assert.Equal(7, s.Send<int>(Oop.FromSmallInteger(3), "+", 4));
            Assert.Equal(1.5m, s.Send<decimal>(s.Execute("1.0s1"), "+", 0.5m));
        }
        finally { s.Release(str); }
    }

    [StoneFact]
    public void EvaluateGenericConverts()
    {
        using var s = StoneEnv.Login();
        Assert.Equal(7, s.Evaluate<int>("3 + 4"));
        Assert.Equal("abc", s.Evaluate<string>("'abc'"));
        Assert.Null(s.Evaluate<string?>("nil"));
        Assert.Null(s.Evaluate<int?>("nil"));
        Assert.Throws<InvalidCastException>(() => s.Evaluate<int>("nil"));
        Assert.Throws<InvalidCastException>(() => s.Evaluate<int>("'abc'"));
    }

    [StoneFact]
    public void SymbolsAreCanonical()
    {
        using var s = StoneEnv.Login();
        Assert.Equal(s.Execute("#foo"), s.NewSymbol("foo"));
        Assert.Equal(s.Execute("'grüß' asSymbol"), s.NewSymbol("grüß"));
        Assert.Equal("grüß", s.ToStringValue(s.NewSymbol("grüß")));
    }

    [StoneFact]
    public void NewStringPicksSmallestClass()
    {
        using var s = StoneEnv.Login();
        Assert.Equal(KnownClasses.String, s.FetchClass(s.NewString("abc")));
        Assert.Equal(KnownClasses.Unicode16, s.FetchClass(s.NewString("日本")));
        Assert.Equal(KnownClasses.Unicode32, s.FetchClass(s.NewString("😀")));
    }

    [StoneFact]
    public void ConversionErrorsAreTyped()
    {
        using var s = StoneEnv.Login();
        Assert.Throws<InvalidCastException>(() => s.ToDouble(s.Execute("'abc'")));
        Assert.Throws<InvalidCastException>(() => s.ToInt64(s.Execute("'abc'")));
        Assert.Throws<GciException>(() => s.ToStringValue(Oop.FromSmallInteger(1)));
        Assert.Throws<ArgumentException>(() => s.FromValue(new object()));
        Assert.True(s.IsLoggedIn);
    }
}
