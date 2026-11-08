using System.Runtime.InteropServices;
using System.Text;
using GemStone.Gci.Native;

namespace GemStone.Gci;

/// <summary>
/// A new object for <see cref="GciSession.StoreAndExecute"/>: pointer objects (Array …) with
/// their indexed slots, byte objects (String, Utf8, ByteArray, Float) with their bytes. Its
/// <see cref="Oop"/> comes from <see cref="GciSession.GetFreeOops"/>.
/// </summary>
internal sealed class GciNewObject
{
    private GciNewObject(Oop oop, Oop @class, GciObjectFormat format, Oop[] oops, byte[] bytes, bool indexable, int swizzle)
    {
        Oop = oop;
        Class = @class;
        Format = format;
        Oops = oops;
        Bytes = bytes;
        Indexable = indexable;
        Swizzle = swizzle;
    }

    public Oop Oop { get; }
    public Oop Class { get; }
    public GciObjectFormat Format { get; }
    public Oop[] Oops { get; }
    public byte[] Bytes { get; }
    public bool Indexable { get; }

    /// <summary>GciByteSwizEType: 0 = none, 3 = 8-byte values (Float).</summary>
    public int Swizzle { get; }

    public static GciNewObject Pointers(Oop oop, Oop @class, Oop[] slots) =>
        new(oop, @class, GciObjectFormat.Pointer, slots, [], indexable: true, swizzle: 0);

    public static GciNewObject ByteObject(Oop oop, Oop @class, byte[] bytes) =>
        new(oop, @class, GciObjectFormat.Byte, [], bytes, indexable: true, swizzle: 0);

    /// <summary>A Float (8 bytes, not indexable, swizzled as one 8-byte value).</summary>
    public static GciNewObject Float(Oop oop, double value) =>
        new(oop, KnownClasses.Float, GciObjectFormat.Byte, [], BitConverter.GetBytes(value), indexable: false, swizzle: 3);

    internal int ValueBytes => Format == GciObjectFormat.Byte ? Bytes.Length : Oops.Length * 8;
    internal int ReportBytes => GciObjRepHdrSType.Size + ((ValueBytes + 7) & ~7);
}

public sealed partial class GciSession
{
    private const int GciStoreTravCreate = 0x2; // GCI_STORE_TRAV_CREATE (gci.ht)

    /// <summary>
    /// GciGetFreeOops hands out at most this many OOPs per call; the rest of the result array is
    /// silently left 0 (verified with 3.6.5 and 3.7.6; not in the manual or the header).
    /// </summary>
    internal const int MaxFreeOopsPerCall = 100_000;

    /// <summary>Allocates OOPs for objects that a store traversal creates (GciGetFreeOops, in chunks).</summary>
    internal unsafe Oop[] GetFreeOops(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var oops = new Oop[count];
        if (count == 0) return oops;
        lock (Sync)
        {
            fixed (Oop* p = oops)
            {
                for (var done = 0; done < count; done += MaxFreeOopsPerCall)
                {
                    var chunk = Math.Min(MaxFreeOopsPerCall, count - done);
                    Activate();
                    GciNative.GciGetFreeOops(chunk, (ulong*)(p + done));
                    ThrowIfError();
                }
            }
        }
        if (Array.IndexOf(oops, default(Oop)) is var missing and >= 0)
            throw new GciException($"GciGetFreeOops returned no OOP for index {missing} of {count}.");
        return oops;
    }

    /// <summary>
    /// Creates <paramref name="objects"/> (GciStoreTrav with GCI_STORE_TRAV_CREATE) and then
    /// executes <paramref name="source"/> with <paramref name="context"/> as <c>self</c>
    /// (GciExecuteStrFromContext): two round trips, however many objects. The created objects and
    /// the result are in the PureExportSet: the caller releases them.
    /// </summary>
    /// <remarks>
    /// Objects may reference each other in any order (the OOPs are allocated beforehand); a
    /// store traversal replaces <em>all</em> instance variables of an existing object, so it is
    /// only used here to create new objects. The code does the rest.
    /// Not GciStoreTravDo_ (one round trip): it reports the altered objects and therefore
    /// requires GciDirtyObjsInit, i.e. dirty tracking for the whole session (error 2086 otherwise).
    /// </remarks>
    internal unsafe Oop StoreAndExecute(IReadOnlyList<GciNewObject> objects, string source, Oop context)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(source);
        if (!Ascii.IsValid(source)) throw new ArgumentException("Source must be ASCII.", nameof(source));

        long body = 0;
        foreach (var o in objects) body += o.ReportBytes;
        if (body > int.MaxValue - GciTravBufHeader.Size)
            throw new ArgumentException("Too much data for one store traversal.", nameof(objects));
        var bodySize = Math.Max(GciTravBufHeader.MinBodySize, (int)body);
        var buffer = (byte*)NativeMemory.AlignedAlloc((nuint)(GciTravBufHeader.Size + bodySize), 8);
        try
        {
            var header = (GciTravBufHeader*)buffer;
            header->AllocatedBytes = (uint)bodySize;
            header->UsedBytes = (uint)body;
            var at = buffer + GciTravBufHeader.Size;
            foreach (var o in objects)
            {
                WriteReport(at, o);
                at += o.ReportBytes;
            }

            lock (Sync)
            {
                Activate();
                GciNative.GciStoreTrav(buffer, GciStoreTravCreate);
                ThrowIfError();
                Activate();
                return CheckResult(new Oop(GciNative.GciExecuteStrFromContext(source, context.Value, Oop.Nil.Value)));
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    private static unsafe void WriteReport(byte* at, GciNewObject o)
    {
        var size = o.Format == GciObjectFormat.Byte ? o.Bytes.Length : o.Oops.Length;
        var bits = (ulong)(uint)o.Format | (o.Indexable ? 0x04UL : 0) | ((ulong)o.Swizzle << 8);
        var hdr = (GciObjRepHdrSType*)at;
        *hdr = new GciObjRepHdrSType
        {
            ValueBuffSize = o.ValueBytes,
            NamedSize = 0,
            ObjectSecurityPolicyId = 0xFFFF, // UNDEFINED_ObjectSecurityPolicyId: the default policy
            ObjId = o.Oop.Value,
            OClass = o.Class.Value,
            FirstOffset = 1,
            IdxSizeBits = (o.Indexable ? (ulong)size << 24 : 0) | bits,
        };
        var values = at + GciObjRepHdrSType.Size;
        var span = new Span<byte>(values, (o.ValueBytes + 7) & ~7);
        span.Clear();
        if (o.Format == GciObjectFormat.Byte)
            o.Bytes.CopyTo(span);
        else
            for (var i = 0; i < o.Oops.Length; i++)
                ((ulong*)values)[i] = o.Oops[i].Value;
    }
}
