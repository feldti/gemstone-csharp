using System.Runtime.InteropServices;
using GemStone.Gci.Native;

namespace GemStone.Gci.Tests;

/// <summary>Native layouts of the traversal structs (gci.ht / gcicmn.ht, 3.7.5).</summary>
public class GciTraversalLayoutTests
{
    [Theory]
    [InlineData(nameof(GciObjRepHdrSType.ValueBuffSize), 0)]
    [InlineData(nameof(GciObjRepHdrSType.NamedSize), 4)]
    [InlineData(nameof(GciObjRepHdrSType.ObjectSecurityPolicyId), 6)]
    [InlineData(nameof(GciObjRepHdrSType.ObjId), 8)]
    [InlineData(nameof(GciObjRepHdrSType.OClass), 16)]
    [InlineData(nameof(GciObjRepHdrSType.FirstOffset), 24)]
    [InlineData(nameof(GciObjRepHdrSType.IdxSizeBits), 32)]
    public void ObjectReportHeader(string field, int offset) =>
        Assert.Equal(offset, (int)Marshal.OffsetOf<GciObjRepHdrSType>(field));

    [Theory]
    [InlineData(nameof(GciClampedTravArgsSType.ClampSpec), 0)]
    [InlineData(nameof(GciClampedTravArgsSType.ResultOop), 8)]
    [InlineData(nameof(GciClampedTravArgsSType.TravBuff), 16)]
    [InlineData(nameof(GciClampedTravArgsSType.Level), 24)]
    [InlineData(nameof(GciClampedTravArgsSType.RetrievalFlags), 28)]
    [InlineData(nameof(GciClampedTravArgsSType.IsRpc), 32)]
    public void ClampedTraversalArgs(string field, int offset) =>
        Assert.Equal(offset, (int)Marshal.OffsetOf<GciClampedTravArgsSType>(field));

    [Fact]
    public void Sizes()
    {
        Assert.Equal(GciObjRepHdrSType.Size, Marshal.SizeOf<GciObjRepHdrSType>());
        Assert.Equal(40, Marshal.SizeOf<GciClampedTravArgsSType>());
        Assert.Equal(GciTravBufHeader.Size, Marshal.SizeOf<GciTravBufHeader>());
    }

    [Fact]
    public void ReportHeaderBitsAndAlignment()
    {
        var hdr = new GciObjRepHdrSType { ValueBuffSize = 13, IdxSizeBits = (5UL << 24) | 0x04 | 0x10 | 0x01 };
        Assert.Equal(5, hdr.IdxSize);
        Assert.Equal((int)GciObjectFormat.Byte, hdr.ObjImpl);
        Assert.True(hdr.IsIndexable);
        Assert.True(hdr.IsPartial);
        Assert.False(hdr.IsInvariant);
        Assert.Equal(40 + 16, hdr.UsedBytes); // value buffer padded to 8
    }
}
