using System.Runtime.InteropServices;
using System.Text;

namespace GemStone.Gci.Native;

/// <summary>
/// Managed mirror of the C++ class <c>GciErrSType</c> (gci.ht), see
/// gemstone-c/06-gembuilder-for-c-files-and-data-structures.md, "Error Report Structure".
/// </summary>
/// <remarks>
/// Field order and sizes must match the native layout exactly; the struct is passed by
/// pointer to <c>GciErr</c>. <c>OopType</c> is 64 bit in GemStone/S 64 Bit (the manual's
/// "32-bit" wording is outdated).
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct GciErrSType
{
    public const int MaxErrArgs = 10;      // GCI_MAX_ERR_ARGS
    public const int ErrStrSize = 1024;    // GCI_ERR_STR_SIZE
    public const int ReasonSize = 1024;    // GCI_ERR_reasonSize

    public ulong Category;                 // deprecated, always OOP_GEMSTONE_ERROR_CAT
    public ulong Context;                  // GsProcess or OOP_NIL
    public ulong ExceptionObj;             // Exception instance or nil
    public fixed ulong Args[MaxErrArgs];
    public int Number;
    public int ArgCount;
    public byte Fatal;
    public fixed byte MessageBytes[ErrStrSize + 1];
    public fixed byte ReasonBytes[ReasonSize + 1];

    public readonly bool IsFatal => Fatal != 0;

    public readonly string Message
    {
        get { fixed (byte* p = MessageBytes) return DecodeUtf8(p, ErrStrSize + 1); }
    }

    public readonly string Reason
    {
        get { fixed (byte* p = ReasonBytes) return DecodeUtf8(p, ReasonSize + 1); }
    }

    public readonly ulong[] GetArgs()
    {
        var count = Math.Clamp(ArgCount, 0, MaxErrArgs);
        var result = new ulong[count];
        fixed (ulong* p = Args)
            for (var i = 0; i < count; i++)
                result[i] = p[i];
        return result;
    }

    private static string DecodeUtf8(byte* p, int capacity)
    {
        var span = new ReadOnlySpan<byte>(p, capacity);
        var len = span.IndexOf((byte)0);
        return Encoding.UTF8.GetString(len < 0 ? span : span[..len]);
    }
}
