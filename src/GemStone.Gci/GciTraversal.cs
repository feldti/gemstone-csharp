using System.Text;
using GemStone.Gci.Native;

namespace GemStone.Gci;

/// <summary>
/// One object report of a traversal (<c>GciObjRepSType</c>), copied into managed memory.
/// </summary>
public sealed class GciObjectReport
{
    internal GciObjectReport(in GciObjRepHdrSType hdr, Oop[] oops, byte[] bytes)
    {
        Oop = new Oop(hdr.ObjId);
        Class = new Oop(hdr.OClass);
        Format = (GciObjectFormat)hdr.ObjImpl;
        NamedSize = hdr.NamedSize;
        IndexedSize = hdr.IdxSize;
        FirstOffset = hdr.FirstOffset;
        IsPartial = hdr.IsPartial;
        IsInvariant = hdr.IsInvariant;
        IsClamped = hdr.IsClamped;
        NoReadAuthorization = hdr.NoReadAuthorization;
        Oops = oops;
        Bytes = bytes;
    }

    public Oop Oop { get; }
    public Oop Class { get; }
    public GciObjectFormat Format { get; }
    public int NamedSize { get; }

    /// <summary>Number of indexed slots (or NSC members), even if <see cref="IsPartial"/>.</summary>
    public long IndexedSize { get; }

    /// <summary>Offset of the first value in the object (1 = first named instVar).</summary>
    public long FirstOffset { get; }

    /// <summary>The value buffer does not hold the whole object.</summary>
    public bool IsPartial { get; }

    public bool IsInvariant { get; }
    public bool IsClamped { get; }

    /// <summary>No read authorization: no values, <see cref="Class"/> is nil.</summary>
    public bool NoReadAuthorization { get; }

    /// <summary>Values of a pointer object or NSC: named instVars, then indexed slots / members.</summary>
    public IReadOnlyList<Oop> Oops { get; }

    /// <summary>Value buffer of a byte object, in client byte order (the GCI swizzles).</summary>
    public IReadOnlyList<byte> Bytes { get; }

    /// <summary>Named instance variable (1-based).</summary>
    public Oop Named(int index)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, NamedSize);
        return Oops[(int)(index - FirstOffset)];
    }

    /// <summary>Indexed slots / NSC members present in this report.</summary>
    public IEnumerable<Oop> Indexed => Oops.Skip((int)Math.Max(0, NamedSize - FirstOffset + 1));

    private GciObjectReport(GciObjectReport first, Oop[] oops, byte[] bytes, bool partial)
    {
        Oop = first.Oop;
        Class = first.Class;
        Format = first.Format;
        NamedSize = first.NamedSize;
        IndexedSize = first.IndexedSize;
        FirstOffset = first.FirstOffset;
        IsInvariant = first.IsInvariant;
        IsClamped = first.IsClamped;
        NoReadAuthorization = first.NoReadAuthorization;
        IsPartial = partial;
        Oops = oops;
        Bytes = bytes;
    }

    /// <summary>Appends a continuation report (same object, later FirstOffset).</summary>
    internal GciObjectReport MergeWith(GciObjectReport next)
    {
        var count = Format == GciObjectFormat.Byte ? Bytes.Count : Oops.Count;
        if (next.FirstOffset != FirstOffset + count)
            throw new GciException($"Unexpected continuation report for {Oop}: offset {next.FirstOffset}, expected {FirstOffset + count}.");
        Oop[] oops = [.. Oops, .. next.Oops];
        byte[] bytes = [.. Bytes, .. next.Bytes];
        var total = Format == GciObjectFormat.Byte ? IndexedSize : NamedSize + IndexedSize;
        var have = Format == GciObjectFormat.Byte ? bytes.Length : oops.Length;
        return new GciObjectReport(this, oops, bytes, FirstOffset != 1 || have < total);
    }

    public override string ToString() => $"{Oop} class {Class} {Format} named {NamedSize} indexed {IndexedSize}";
}

/// <summary>
/// Result of <see cref="GciSession.Traverse(Oop, int, GciTraversalOptions?)"/> and friends: all
/// object reports of a graph, fetched in one (or few) round trips and decodable without the server.
/// </summary>
/// <remarks>
/// Reported OOPs are not in the export set: a temporary (uncommitted) object that is reachable
/// only from this snapshot may be garbage-collected by the gem. The snapshot itself stays
/// readable; to keep working with the objects on the server, keep a root referenced (e.g. with
/// <see cref="GciTraversalOptions.KeepResult"/>) or commit them.
/// </remarks>
public sealed class GciTraversal
{
    private readonly Dictionary<Oop, GciObjectReport> _reports;

    internal GciTraversal(IReadOnlyList<Oop> roots, Oop? result, List<GciObjectReport> reports)
    {
        Roots = roots;
        Result = result;
        // An object larger than the buffer arrives as several partial reports with increasing
        // FirstOffset (one per GciMoreTraversal buffer): merge them into one report.
        _reports = new Dictionary<Oop, GciObjectReport>(reports.Count);
        var ordered = new List<GciObjectReport>(reports.Count);
        foreach (var r in reports)
        {
            if (_reports.TryGetValue(r.Oop, out var existing))
            {
                var merged = existing.MergeWith(r);
                _reports[r.Oop] = merged;
                ordered[ordered.IndexOf(existing)] = merged;
            }
            else
            {
                _reports.Add(r.Oop, r);
                ordered.Add(r);
            }
        }
        Reports = ordered;
    }

    /// <summary>Objects the traversal started from.</summary>
    public IReadOnlyList<Oop> Roots { get; }

    /// <summary>Execution result for <see cref="GciSession.ExecuteTraverse"/> / <see cref="GciSession.PerformTraverse(Oop, string, int, Oop[], GciTraversalOptions?)"/>.</summary>
    public Oop? Result { get; }

    /// <summary>All reports in traversal order.</summary>
    public IReadOnlyList<GciObjectReport> Reports { get; }

    public bool Contains(Oop oop) => _reports.ContainsKey(oop);

    public GciObjectReport this[Oop oop] =>
        _reports.TryGetValue(oop, out var r) ? r : throw new KeyNotFoundException($"{oop} is not part of this traversal.");

    public bool TryGetReport(Oop oop, out GciObjectReport report) => _reports.TryGetValue(oop, out report!);

    /// <summary>
    /// Decodes a value without a server round trip: specials (nil, Boolean, SmallInteger,
    /// SmallDouble, Character) and reported objects of the classes String, Symbol, Unicode7/16/32,
    /// DoubleByteString/Symbol, QuadByteSymbol, Utf8, ByteArray and Float. Anything else (and
    /// partial reports) is returned as <see cref="Oop"/>.
    /// </summary>
    public object? ToValue(Oop oop) => Decode(oop, _reports);

    /// <summary>The offline decoder behind <see cref="ToValue"/>, for reports collected from several traversals.</summary>
    internal static object? Decode(Oop oop, IReadOnlyDictionary<Oop, GciObjectReport> reports)
    {
        if (oop.IsNil) return null;
        if (oop.IsBoolean) return oop.ToBoolean();
        if (oop.IsSmallInteger) return oop.ToSmallInteger();
        if (oop.IsSmallDouble) return oop.ToSmallDouble();
        if (oop.IsCharacter) return oop.ToCharacter();
        if (!reports.TryGetValue(oop, out var r) || r.IsPartial || r.Format != GciObjectFormat.Byte)
            return oop;

        var bytes = r.Bytes as byte[] ?? [.. r.Bytes];
        var c = r.Class;
        if (c == KnownClasses.String || c == KnownClasses.Symbol || c == KnownClasses.Unicode7)
            return Encoding.Latin1.GetString(bytes);
        if (c == KnownClasses.Utf8)
            return Encoding.UTF8.GetString(bytes);
        if (c == KnownClasses.Unicode16 || c == KnownClasses.DoubleByteString || c == KnownClasses.DoubleByteSymbol)
            return (BitConverter.IsLittleEndian ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(bytes);
        if (c == KnownClasses.Unicode32 || c == KnownClasses.QuadByteSymbol)
            return new UTF32Encoding(bigEndian: !BitConverter.IsLittleEndian, byteOrderMark: false).GetString(bytes);
        if (c == KnownClasses.ByteArray)
            return bytes;
        if (c == KnownClasses.Float && bytes.Length == 8)
            return BitConverter.ToDouble(bytes, 0);
        return oop;
    }

    /// <summary>Named instance variable of a reported object, decoded with <see cref="ToValue"/>.</summary>
    public object? Named(Oop obj, int index) => ToValue(this[obj].Named(index));

    /// <summary>Indexed slots / members of a reported object, decoded with <see cref="ToValue"/>.</summary>
    public List<object?> ToList(Oop obj) => [.. this[obj].Indexed.Select(ToValue)];
}

/// <summary>Options for traversals.</summary>
public sealed record GciTraversalOptions
{
    /// <summary>Body size of the traversal buffer; larger means fewer GciMoreTraversal round trips.</summary>
    public int BufferSize { get; init; } = 256 * 1024;

    /// <summary>
    /// Keep the execution result of <see cref="GciSession.ExecuteTraverse"/> /
    /// <see cref="GciSession.PerformTraverse(Oop, string, int, Oop[], GciTraversalOptions?)"/> in the export set (release it yourself);
    /// by default it is released after the traversal, like every other reported object.
    /// </summary>
    public bool KeepResult { get; init; }
}
