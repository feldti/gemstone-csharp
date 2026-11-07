using System.Runtime.InteropServices;
using GemStone.Gci.Native;

namespace GemStone.Gci.Tests;

/// <summary>
/// Guards the managed layout of GciErrSType against the native C++ class
/// (8-byte OopType, natural alignment, 1024-char message and reason buffers).
/// </summary>
public class GciErrSTypeLayoutTests
{
    [Theory]
    [InlineData(nameof(GciErrSType.Category), 0)]
    [InlineData(nameof(GciErrSType.Context), 8)]
    [InlineData(nameof(GciErrSType.ExceptionObj), 16)]
    [InlineData(nameof(GciErrSType.Args), 24)]
    [InlineData(nameof(GciErrSType.Number), 104)]
    [InlineData(nameof(GciErrSType.ArgCount), 108)]
    [InlineData(nameof(GciErrSType.Fatal), 112)]
    [InlineData(nameof(GciErrSType.MessageBytes), 113)]
    [InlineData(nameof(GciErrSType.ReasonBytes), 1138)]
    public void FieldOffsetsMatchNativeLayout(string field, int expectedOffset) =>
        Assert.Equal(expectedOffset, (int)Marshal.OffsetOf<GciErrSType>(field));

    [Fact]
    public void SizeIsPaddedToEightBytes() =>
        Assert.Equal(2168, Marshal.SizeOf<GciErrSType>());

    [Fact]
    public unsafe void MessageIsDecodedUpToNullTerminator()
    {
        var err = new GciErrSType { ArgCount = 2 };
        var text = "Zugriff verweigert ü"u8;
        for (var i = 0; i < text.Length; i++) err.MessageBytes[i] = text[i];
        err.Args[0] = 42; err.Args[1] = 7;

        Assert.Equal("Zugriff verweigert ü", err.Message);
        Assert.Equal(string.Empty, err.Reason);
        Assert.Equal(new ulong[] { 42, 7 }, err.GetArgs());
    }
}
