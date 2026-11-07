using GemStone.Gci.Native;

namespace GemStone.Gci;

/// <summary>Storage format of an object (<c>GciFetchObjImpl</c>).</summary>
public enum GciObjectFormat
{
    /// <summary>Named and/or indexed OOP slots (Object, Array, OrderedCollection …).</summary>
    Pointer = 0,

    /// <summary>Bytes (String, ByteArray, Float …).</summary>
    Byte = 1,

    /// <summary>Non-sequenceable collection (IdentitySet, IdentityBag …).</summary>
    Nsc = 2,

    /// <summary>Immediate value (SmallInteger, Character, nil …).</summary>
    Special = 3,
}

/// <summary>
/// Structural object access: create objects, read and write named instance variables and
/// indexed slots, bytes and NSC members. All indices are 1-based, as in Smalltalk.
/// </summary>
/// <remarks>
/// Structural access bypasses Smalltalk methods (no <c>at:put:</c>, no invariants). OOPs read
/// from instance variables are not added to the export set (manual 1.4): they stay valid while
/// the containing object references them — refetch after commit/abort. Results of
/// <see cref="NewObject"/>, <see cref="NewByteArray"/> and <see cref="ResolveSymbol"/> are in the
/// export set; release them when done.
/// </remarks>
public sealed partial class GciSession
{
    private const int OopChunkSize = 8192;

    // ---- Creating objects -------------------------------------------------------------------

    /// <summary>New instance of <paramref name="cls"/> with <paramref name="indexedSize"/> indexed slots (GciNewOops).</summary>
    public unsafe Oop NewObject(Oop cls, long indexedSize = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(indexedSize);
        lock (Sync)
        {
            Activate();
            ulong clsValue = cls.Value, result = 0;
            GciNative.GciNewOops(1, &clsValue, &indexedSize, &result);
            ThrowIfError();
            var oop = new Oop(result);
            return oop.IsNil
                ? throw new ArgumentException($"{cls} has a special implementation and cannot be instantiated.", nameof(cls))
                : oop;
        }
    }

    /// <summary>New ByteArray with the given contents (GciNewByteObj).</summary>
    public unsafe Oop NewByteArray(ReadOnlySpan<byte> bytes)
    {
        lock (Sync)
        {
            Activate();
            Oop oop;
            fixed (byte* p = bytes)
                oop = new Oop(GciNative.GciNewByteObj(KnownClasses.ByteArray.Value, p, bytes.Length));
            ThrowIfError();
            return oop;
        }
    }

    /// <summary>
    /// Looks up a global (class, symbol-dictionary entry …) by name in the symbol list
    /// (GciResolveSymbolObj); <c>null</c> uses <c>System myUserProfile symbolList</c>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such name.</exception>
    public Oop ResolveSymbol(string name, Oop? symbolList = null) =>
        TryResolveSymbol(name, out var oop, symbolList)
            ? oop
            : throw new KeyNotFoundException($"'{name}' is not defined in the symbol list.");

    /// <summary>
    /// Like <see cref="ResolveSymbol"/>; false if the name is not defined. GciResolveSymbol then
    /// returns OOP_ILLEGAL without an error report (gci.hf — the manual does not say so).
    /// The result is in the export set; release it when done.
    /// </summary>
    public bool TryResolveSymbol(string name, out Oop value, Oop? symbolList = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (Sync)
        {
            // Not GciResolveSymbol: it leaves a String with the name in the PureExportSet on
            // every call (3.7.5), even for unknown names. Here the name object is our own.
            var str = NewString(name);
            try
            {
                Activate();
                value = new Oop(GciNative.GciResolveSymbolObj(str.Value, (symbolList ?? Oop.Nil).Value));
                ThrowIfError();
                return value != Oop.Illegal;
            }
            finally
            {
                Release(str);
            }
        }
    }

    // ---- Shape ------------------------------------------------------------------------------

    /// <summary>Storage format of an object (GciFetchObjImpl).</summary>
    public GciObjectFormat FetchFormat(Oop obj)
    {
        lock (Sync)
        {
            Activate();
            var impl = GciNative.GciFetchObjImpl(obj.Value);
            ThrowIfError();
            return impl is >= 0 and <= 3 ? (GciObjectFormat)impl : throw new GciException($"GciFetchObjImpl returned {impl}");
        }
    }

    /// <summary>Number of named instance variables (GciFetchNamedSize).</summary>
    public int FetchNamedSize(Oop obj)
    {
        lock (Sync)
        {
            Activate();
            var n = GciNative.GciFetchNamedSize(obj.Value);
            ThrowIfError();
            return n;
        }
    }

    /// <summary>Number of indexed (unnamed) slots, or NSC members (GciFetchVaryingSize_).</summary>
    public long FetchVaryingSize(Oop obj)
    {
        lock (Sync)
        {
            Activate();
            var n = GciNative.GciFetchVaryingSize_(obj.Value);
            ThrowIfError();
            return n;
        }
    }

    /// <summary>1-based index of a named instance variable of <paramref name="cls"/> (GciIvNameToIdx).</summary>
    /// <exception cref="ArgumentException">The class has no such instance variable.</exception>
    public int InstVarIndex(Oop cls, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (Sync)
        {
            Activate();
            var index = GciNative.GciIvNameToIdx(cls.Value, name);
            ThrowIfError();
            return index > 0 ? index : throw new ArgumentException($"No instance variable '{name}' in {cls}.", nameof(name));
        }
    }

    // ---- Named instance variables ---------------------------------------------------------

    /// <summary>All named instance variables, in declaration order (GciFetchNamedOops).</summary>
    public unsafe Oop[] FetchNamedOops(Oop obj)
    {
        lock (Sync)
        {
            var size = FetchNamedSize(obj);
            var result = new Oop[size];
            if (size == 0) return result;
            fixed (Oop* p = result)
            {
                var n = GciNative.GciFetchNamedOops(obj.Value, 1, (ulong*)p, size);
                ThrowIfError();
                return n == size ? result : result.AsSpan(0, n).ToArray();
            }
        }
    }

    /// <summary>Named instance variable at a 1-based index.</summary>
    public unsafe Oop FetchNamedOop(Oop obj, int index)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);
        lock (Sync)
        {
            Activate();
            ulong value = 0;
            var n = GciNative.GciFetchNamedOops(obj.Value, index, &value, 1);
            ThrowIfError();
            return n == 1 ? new Oop(value) : throw new ArgumentOutOfRangeException(nameof(index), index, $"{obj} has no named instance variable {index}.");
        }
    }

    /// <summary>Named instance variable by name (resolves the index via the object's class).</summary>
    public Oop FetchNamedOop(Oop obj, string name)
    {
        lock (Sync) return FetchNamedOop(obj, InstVarIndex(FetchClass(obj), name));
    }

    /// <summary>Stores into a named instance variable at a 1-based index (GciStoreNamedOops).</summary>
    public unsafe void StoreNamedOop(Oop obj, int index, Oop value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);
        lock (Sync)
        {
            Activate();
            var v = value.Value;
            GciNative.GciStoreNamedOops(obj.Value, index, &v, 1);
            ThrowIfError();
        }
    }

    /// <summary>Stores into a named instance variable by name.</summary>
    public void StoreNamedOop(Oop obj, string name, Oop value)
    {
        lock (Sync) StoreNamedOop(obj, InstVarIndex(FetchClass(obj), name), value);
    }

    /// <summary>Named instance variable converted with <see cref="ToValue"/>.</summary>
    public object? GetInstVar(Oop obj, string name) => ToValue(FetchNamedOop(obj, name));

    /// <summary>
    /// Stores a .NET value (converted with <see cref="FromValue"/>) into a named instance variable.
    /// The created object is released afterwards — the instance variable now references it.
    /// </summary>
    public void SetInstVar(Oop obj, string name, object? value)
    {
        lock (Sync)
        {
            var oop = FromValue(value);
            try { StoreNamedOop(obj, name, oop); }
            finally { if (value is not Oop) Release(oop); }
        }
    }

    // ---- Indexed slots ----------------------------------------------------------------------

    /// <summary>
    /// Indexed slots <paramref name="start"/> … <paramref name="start"/>+<paramref name="count"/>-1
    /// (GciFetchVaryingOops, fetched in chunks); <paramref name="count"/> = -1 reads to the end.
    /// For an NSC the order is unspecified.
    /// </summary>
    public unsafe Oop[] FetchVaryingOops(Oop obj, long start = 1, long count = -1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(start, 1);
        lock (Sync)
        {
            var available = Math.Max(0, FetchVaryingSize(obj) - start + 1);
            var total = count < 0 ? available : Math.Min(count, available);
            var result = new Oop[total];
            fixed (Oop* p = result)
            {
                for (long done = 0; done < total;)
                {
                    var chunk = (int)Math.Min(OopChunkSize, total - done);
                    var n = GciNative.GciFetchVaryingOops(obj.Value, start + done, (ulong*)(p + done), chunk);
                    ThrowIfError();
                    if (n <= 0) return result.AsSpan(0, (int)done).ToArray();
                    done += n;
                }
            }
            return result;
        }
    }

    /// <summary>Indexed slot at a 1-based index.</summary>
    public Oop FetchVaryingOop(Oop obj, long index)
    {
        var oops = FetchVaryingOops(obj, index, 1);
        return oops.Length == 1 ? oops[0] : throw new ArgumentOutOfRangeException(nameof(index), index, $"{obj} has no indexed slot {index}.");
    }

    /// <summary>Stores into indexed slots from <paramref name="start"/> on; grows the object if needed (GciStoreIdxOops).</summary>
    public unsafe void StoreVaryingOops(Oop obj, long start, params ReadOnlySpan<Oop> values)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(start, 1);
        lock (Sync)
        {
            Activate();
            fixed (Oop* p = values)
            {
                for (var done = 0; done < values.Length;)
                {
                    var chunk = Math.Min(OopChunkSize, values.Length - done);
                    GciNative.GciStoreIdxOops(obj.Value, start + done, (ulong*)(p + done), chunk);
                    ThrowIfError();
                    done += chunk;
                }
            }
        }
    }

    /// <summary>Appends to the indexed slots (GciAppendOops).</summary>
    public unsafe void AppendOops(Oop obj, params ReadOnlySpan<Oop> values)
    {
        lock (Sync)
        {
            Activate();
            fixed (Oop* p = values)
            {
                for (var done = 0; done < values.Length;)
                {
                    var chunk = Math.Min(OopChunkSize, values.Length - done);
                    GciNative.GciAppendOops(obj.Value, chunk, (ulong*)(p + done));
                    ThrowIfError();
                    done += chunk;
                }
            }
        }
    }

    /// <summary>All indexed slots converted with <see cref="ToValue"/> (one level, no recursion).</summary>
    public List<object?> ToList(Oop obj)
    {
        lock (Sync) return [.. FetchVaryingOops(obj).Select(ToValue)];
    }

    // ---- Bytes ------------------------------------------------------------------------------

    /// <summary>
    /// Overwrites bytes from a 1-based <paramref name="start"/> on, growing the object if needed
    /// (GciStoreBytes). Multi-byte objects (Unicode16 …) expect client byte order.
    /// </summary>
    public unsafe void StoreBytes(Oop obj, long start, ReadOnlySpan<byte> bytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(start, 1);
        lock (Sync)
        {
            Activate();
            fixed (byte* p = bytes)
                GciNative.GciStoreBytesInstanceOf(Oop.Nil.Value, obj.Value, start, p, bytes.Length);
            ThrowIfError();
        }
    }

    /// <summary>Appends bytes to a byte object (GciAppendBytes).</summary>
    public unsafe void AppendBytes(Oop obj, ReadOnlySpan<byte> bytes)
    {
        lock (Sync)
        {
            Activate();
            fixed (byte* p = bytes)
                GciNative.GciAppendBytes(obj.Value, bytes.Length, p);
            ThrowIfError();
        }
    }

    // ---- NSCs (IdentitySet, IdentityBag …) --------------------------------------------------

    /// <summary>Adds an object to an NSC (GciAddOopToNsc).</summary>
    public void AddToNsc(Oop nsc, Oop value)
    {
        lock (Sync)
        {
            Activate();
            GciNative.GciAddOopToNsc(nsc.Value, value.Value);
            ThrowIfError();
        }
    }

    /// <summary>Removes an object from an NSC; false if it was not a member (GciRemoveOopFromNsc).</summary>
    public bool RemoveFromNsc(Oop nsc, Oop value)
    {
        lock (Sync)
        {
            Activate();
            var removed = GciNative.GciRemoveOopFromNsc(nsc.Value, value.Value);
            ThrowIfError();
            return removed;
        }
    }

    /// <summary>Identity membership test (GciNscIncludesOop).</summary>
    public bool NscIncludes(Oop nsc, Oop value)
    {
        lock (Sync)
        {
            Activate();
            var included = GciNative.GciNscIncludesOop(nsc.Value, value.Value);
            ThrowIfError();
            return included;
        }
    }
}
