using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using GemStone.Gci.Native;

namespace GemStone.Gci.Tests;

/// <summary>Pure tag-bit logic ported from the GCI_* macros; no stone needed.</summary>
public class OopTests
{
    // GciSession.Perform passes an Oop[] to the native OopType[] parameter by pointer cast.
    [Fact]
    public void OopHasSameSizeAsOopType() => Assert.Equal(sizeof(ulong), Unsafe.SizeOf<Oop>());

    [Fact]
    public void PosixDateAndTimeHasNativeLayout() // int64 + int, padded
    {
        Assert.Equal(16, Marshal.SizeOf<GciPosixDateAndTime>());
        Assert.Equal(8, (int)Marshal.OffsetOf<GciPosixDateAndTime>(nameof(GciPosixDateAndTime.OffsetSeconds)));
    }

    [Theory]
    [InlineData(0, 0x2UL)]
    [InlineData(1, 0xAUL)]
    [InlineData(7, 0x3AUL)]
    [InlineData(-1, 0xFFFFFFFFFFFFFFFAUL)]
    public void SmallIntegerEncoding(long value, ulong expectedOop)
    {
        var oop = Oop.FromSmallInteger(value);
        Assert.Equal(expectedOop, oop.Value);
        Assert.True(oop.IsSmallInteger);
        Assert.True(oop.IsSpecial);
        Assert.Equal(value, oop.ToSmallInteger());
    }

    [Theory]
    [InlineData(Oop.SmallIntegerMin)]
    [InlineData(Oop.SmallIntegerMax)]
    public void SmallIntegerRangeLimitsRoundTrip(long value) =>
        Assert.Equal(value, Oop.FromSmallInteger(value).ToSmallInteger());

    [Theory]
    [InlineData(Oop.SmallIntegerMax + 1)]
    [InlineData(Oop.SmallIntegerMin - 1)]
    [InlineData(long.MaxValue)]
    public void OutsideSmallIntegerRangeThrows(long value)
    {
        Assert.False(Oop.FitsSmallInteger(value));
        Assert.Throws<ArgumentOutOfRangeException>(() => Oop.FromSmallInteger(value));
    }

    [Fact]
    public void Booleans()
    {
        Assert.Equal(Oop.True, Oop.FromBoolean(true));
        Assert.Equal(Oop.False, Oop.FromBoolean(false));
        Assert.True(Oop.True.IsBoolean);
        Assert.True(Oop.False.IsBoolean);
        Assert.False(Oop.Nil.IsBoolean);
        Assert.True(Oop.True.ToBoolean());
        Assert.False(Oop.False.ToBoolean());
        Assert.Throws<InvalidCastException>(() => Oop.Nil.ToBoolean());
    }

    [Theory]
    [InlineData(0x00, 0x1CUL)]       // OOP_ASCII_NUL
    [InlineData(0x61, 0x611CUL)]     // $a
    [InlineData(0xE4, 0xE41CUL)]     // $ä
    [InlineData(0x1F600, 0x1F6001CUL)] // 😀
    public void CharacterEncoding(int codePoint, ulong expectedOop)
    {
        var oop = Oop.FromCharacter(new Rune(codePoint));
        Assert.Equal(expectedOop, oop.Value);
        Assert.True(oop.IsCharacter);
        Assert.Equal(codePoint, oop.ToCharacter().Value);
    }

    [Fact]
    public void KindPredicatesAreExclusive()
    {
        Assert.True(Oop.Nil.IsNil);
        Assert.True(Oop.Nil.IsSpecial);
        Assert.False(Oop.Nil.IsSmallInteger);
        Assert.False(Oop.True.IsNil);
        Assert.False(Oop.Illegal.IsSpecial); // POM tag bit
        Assert.False(new Oop(66817).IsSpecial); // a heap object (class Array)
        Assert.Throws<InvalidCastException>(() => Oop.Nil.ToSmallInteger());
        Assert.Throws<InvalidCastException>(() => Oop.Nil.ToCharacter());
    }
}
