using System.Runtime.InteropServices;

namespace GemStone.Gci.Native;

/// <summary>
/// Header of a <c>GciTravBufType</c> (gcicmn.ht): <c>uint allocatedBytes; uint usedBytes;</c>
/// followed by the body (object reports). Allocated by us: 8 bytes header + body.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GciTravBufHeader
{
    public const int Size = 8;
    public const int MinBodySize = 2048; // GCI_MIN_TRAV_BUFF_SIZE

    public uint AllocatedBytes; // allocated size of the body
    public uint UsedBytes;      // used bytes of the body
}

/// <summary>
/// Mirror of <c>GciObjRepHdrSType</c> (gci.ht), 40 bytes; the value buffer (bytes or OOPs)
/// follows directly, padded to a multiple of 8 (GCI_ALIGN).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GciObjRepHdrSType
{
    public const int Size = 40;

    // Low bits of _idxSizeBits (gci.ht enum).
    private const ulong ImplMask = 0x03, IndexableMask = 0x04, InvariantMask = 0x08, PartialMask = 0x10,
        OverlayMask = 0x20, NoReadAuthMask = 0x40, ClampedMask = 0x80;

    public int ValueBuffSize;          // size in bytes of the value buffer
    public short NamedSize;            // number of named instVars
    public ushort ObjectSecurityPolicyId;
    public ulong ObjId;
    public ulong OClass;
    public long FirstOffset;           // 1 = value buffer starts with the first named instVar
    public ulong IdxSizeBits;          // 40 bits size << 24 | 8 bits dyn. ivs << 16 | 16 flag bits

    public readonly long IdxSize => (long)(IdxSizeBits >> 24);
    public readonly int ObjImpl => (int)(IdxSizeBits & ImplMask);
    public readonly bool IsIndexable => (IdxSizeBits & IndexableMask) != 0;
    public readonly bool IsInvariant => (IdxSizeBits & InvariantMask) != 0;
    public readonly bool IsPartial => (IdxSizeBits & PartialMask) != 0;
    public readonly bool IsOverlayed => (IdxSizeBits & OverlayMask) != 0;
    public readonly bool NoReadAuthorization => (IdxSizeBits & NoReadAuthMask) != 0;
    public readonly bool IsClamped => (IdxSizeBits & ClampedMask) != 0;

    /// <summary>usedBytes(): header + value buffer aligned to 8.</summary>
    public readonly int UsedBytes => Size + ((ValueBuffSize + 7) & ~7);
}

/// <summary>
/// Mirror of <c>GciClampedTravArgsSType</c> (gcicmn.ht). The manual lists the fields in a
/// different order and omits <c>resultOop</c>; this follows the 3.7.5 header. Use
/// <see cref="Create"/>: the C++ constructor sets <c>isRpc = 1</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct GciClampedTravArgsSType
{
    public ulong ClampSpec;      // ClampSpecification or OOP_NIL
    public ulong ResultOop;      // result of GciPerformTrav / GciExecuteStrTrav
    public byte* TravBuff;       // GciTravBufType*
    public int Level;
    public int RetrievalFlags;   // GCI_RETRIEVE_* (gcicmn.ht)
    public int IsRpc;            // private to the GCI

    public static GciClampedTravArgsSType Create(byte* buffer, int level, int retrievalFlags) => new()
    {
        ClampSpec = 0x14, // OOP_NIL
        ResultOop = 0x14,
        TravBuff = buffer,
        Level = level,
        RetrievalFlags = retrievalFlags,
        IsRpc = 1,
    };
}
