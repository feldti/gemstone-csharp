using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;
using GemStone.Gci.Native;

namespace GemStone.Gci;

/// <summary>Conversion between GemStone objects and .NET values.</summary>
/// <remarks>
/// Ownership: every <c>New…</c>/<see cref="FromValue"/> result that is not a special object
/// (SmallInteger, Character, Boolean, nil …) sits in the PureExportSet until passed to
/// <see cref="Release"/>. <see cref="Evaluate"/> and <see cref="Send"/> release everything they
/// create themselves, except a result they return unconverted as <see cref="Oop"/>.
/// </remarks>
public sealed partial class GciSession
{
    private const int FetchChunkSize = 64 * 1024;
    internal static readonly int SmalltalkDateBase = new DateOnly(1901, 1, 1).DayNumber; // Date fromDays: 0
    private Oop? _dateAndTimeClass, _dateClass, _timeClass;

    // ---- Integers ---------------------------------------------------------------------------

    /// <summary>SmallInteger, or a new LargeInteger outside the SmallInteger range (GciI64ToOop).</summary>
    public Oop NewInteger(long value)
    {
        if (Oop.FitsSmallInteger(value)) return Oop.FromSmallInteger(value); // no round trip
        lock (Sync)
        {
            Activate();
            var oop = new Oop(GciNative.GciI64ToOop(value));
            ThrowIfError();
            return oop;
        }
    }

    /// <summary>Integer value (SmallInteger or LargeInteger within int64) via GciOopToI64_.</summary>
    /// <exception cref="InvalidCastException">Not an Integer, or outside the int64 range.</exception>
    public unsafe long ToInt64(Oop obj)
    {
        if (obj.IsSmallInteger) return obj.ToSmallInteger();
        lock (Sync)
        {
            Activate();
            int error = 0;
            var value = GciNative.GciOopToI64_(obj.Value, &error);
            ThrowIfError();
            return error == 0 ? value : throw new InvalidCastException($"{obj} is not an Integer within the Int64 range.");
        }
    }

    /// <summary>Any GemStone Integer, including LargeIntegers beyond int64.</summary>
    public BigInteger ToBigInteger(Oop obj)
    {
        if (obj.IsSmallInteger) return obj.ToSmallInteger();
        lock (Sync)
        {
            if (!IsKindOf(obj, KnownClasses.Integer))
                throw new InvalidCastException($"{obj} is not an Integer.");
            try { return ToInt64(obj); }
            catch (InvalidCastException)
            {
                // Integer printString is plain decimal digits (no locale-dependent separators).
                return BigInteger.Parse(PrintString(obj), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            }
        }
    }

    // ---- Floats -----------------------------------------------------------------------------

    /// <summary>SmallDouble, or a new Float (GciFltToOop).</summary>
    public Oop NewFloat(double value)
    {
        if (Oop.TryFromSmallDouble(value, out var small)) return small; // no round trip
        lock (Sync)
        {
            Activate();
            var oop = new Oop(GciNative.GciFltToOop(value));
            if (oop.IsNil) ThrowIfError();
            return oop;
        }
    }

    /// <summary>Value of a SmallDouble, SmallFloat or Float via GciOopToFlt_.</summary>
    /// <exception cref="InvalidCastException">Not a binary float.</exception>
    public unsafe double ToDouble(Oop obj)
    {
        lock (Sync)
        {
            Activate();
            if (!GciNative.HasOopToFltWithStatus)
            {
                // GemStone 3.6 has no GciOopToFlt_; GciOopToFlt reports a non-float via GciErr.
                var value = GciNative.GciOopToFlt(obj.Value);
                try
                {
                    ThrowIfError();
                }
                catch (GciException e) when (!e.IsFatal)
                {
                    throw new InvalidCastException($"{obj} is not a Float: {e.Message}", e);
                }
                return value;
            }
            double result;
            var ok = GciNative.GciOopToFlt_(obj.Value, &result);
            ThrowIfError();
            return ok ? result : throw new InvalidCastException($"{obj} is not a Float.");
        }
    }

    // ---- Scaled decimals ---------------------------------------------------------------------

    /// <summary>New ScaledDecimal, e.g. 12.34 → <c>12.34s2</c>. Compiled from a literal, so independent of the gem's locale.</summary>
    public Oop NewScaledDecimal(decimal value)
    {
        var scale = value.Scale;
        var literal = value.ToString(CultureInfo.InvariantCulture) + "s" + scale.ToString(CultureInfo.InvariantCulture);
        return Execute(literal);
    }

    /// <summary>
    /// Value of a ScaledDecimal. Computed as <c>(self * 10^scale) rounded</c> and <c>scale</c>
    /// on the server, because its printString depends on the gem's locale (e.g. <c>0,333</c>).
    /// </summary>
    /// <exception cref="OverflowException">Does not fit a .NET decimal.</exception>
    public decimal ToDecimal(Oop obj)
    {
        lock (Sync)
        {
            var scaleOop = Perform(obj, "scale");
            var scale = ToInt64(scaleOop);
            if (scale is < 0 or > 28)
                throw new OverflowException($"ScaledDecimal scale {scale} is outside the .NET decimal range (0–28).");

            var factor = FromValue(BigInteger.Pow(10, (int)scale)); // may be a LargeInteger
            Oop scaled = Oop.Nil, rounded = Oop.Nil;
            try
            {
                scaled = Perform(obj, "*", factor);
                rounded = Perform(scaled, "rounded");
                return (decimal)ToBigInteger(rounded) / (decimal)BigInteger.Pow(10, (int)scale);
            }
            finally
            {
                Release(factor, scaled, rounded);
            }
        }
    }

    // ---- Strings and symbols -------------------------------------------------------------

    /// <summary>
    /// New string: an 8-bit String for pure ASCII (GciNewString), otherwise the smallest of
    /// Unicode7/16/32 that holds all characters (GciNewUtf8String_).
    /// </summary>
    public unsafe Oop NewString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (Sync)
        {
            Activate();
            Oop oop;
            if (Ascii.IsValid(value))
            {
                oop = new Oop(GciNative.GciNewString(value));
            }
            else
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                fixed (byte* p = bytes)
                    oop = new Oop(GciNative.GciNewUtf8String_(p, (nuint)bytes.Length, utf8OrUnicode: true));
            }
            ThrowIfError();
            return oop;
        }
    }

    /// <summary>Symbol (GciNewSymbol for ASCII, otherwise <c>asSymbol</c> on a Unicode string).</summary>
    public Oop NewSymbol(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (Sync)
        {
            Activate();
            if (Ascii.IsValid(value))
            {
                var oop = new Oop(GciNative.GciNewSymbol(value));
                ThrowIfError();
                return oop;
            }
            var str = NewString(value);
            try { return Perform(str, "asSymbol"); }
            finally { Release(str); }
        }
    }

    /// <summary>
    /// Contents of any CharacterCollection (String, Symbol, Unicode*, DoubleByteString, Utf8 …)
    /// as a .NET string — not its printString. Uses GciFetchUtf8Bytes_, fetching large strings in chunks.
    /// </summary>
    /// <exception cref="GciException">Not a CharacterCollection.</exception>
    public unsafe string ToStringValue(Oop obj)
    {
        lock (Sync)
        {
            Activate();
            var buffer = ArrayPool<byte>.Shared.Rent(FetchChunkSize);
            ulong utf8 = 0;
            try
            {
                long n;
                fixed (byte* p = buffer)
                    n = GciNative.GciFetchUtf8Bytes_(obj.Value, 1, p, FetchChunkSize, &utf8, 0);
                ThrowIfError();
                if (n < FetchChunkSize)
                    return Encoding.UTF8.GetString(buffer, 0, (int)n);

                // More data: the rest is fetched from the UTF-8 encoded object, whose bytes
                // continue at offset n (GciFetchUtf8Bytes_ docs).
                var source = utf8 != 0 ? new Oop(utf8) : obj;
                var all = new byte[FetchSize(source)];
                buffer.AsSpan(0, (int)n).CopyTo(all);
                fixed (byte* p = all)
                {
                    var rest = GciNative.GciFetchBytes_(source.Value, n + 1, p + n, all.Length - n);
                    ThrowIfError();
                    return Encoding.UTF8.GetString(all, 0, (int)(n + rest));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                if (utf8 != 0 && utf8 != obj.Value)
                    Release(new Oop(utf8));
            }
        }
    }

    // ---- DateAndTime ---------------------------------------------------------------------

    /// <summary>New DateAndTime with the same instant and UTC offset (GciNewDateAndTimePosix), microsecond precision.</summary>
    public Oop NewDateAndTime(DateTimeOffset value)
    {
        var micros = (value.UtcTicks - GciPosixDateAndTime.PosixEpoch.Ticks) / TimeSpan.TicksPerMicrosecond;
        lock (Sync)
        {
            Activate();
            var oop = new Oop(GciNative.GciNewDateAndTimePosix(micros, (int)value.Offset.TotalSeconds));
            ThrowIfError();
            return oop == Oop.Illegal ? throw new GciException("GciNewDateAndTimePosix failed") : oop;
        }
    }

    /// <summary>
    /// Value of a DateAndTime (GciFetchDateAndTimePosix). Offsets that are not whole minutes
    /// (historic local mean times) cannot be represented by DateTimeOffset and are returned as UTC.
    /// </summary>
    public unsafe DateTimeOffset ToDateTimeOffset(Oop obj)
    {
        GciPosixDateAndTime result;
        lock (Sync)
        {
            Activate();
            var ok = GciNative.GciFetchDateAndTimePosix(obj.Value, &result);
            ThrowIfError();
            if (!ok) throw new InvalidCastException($"{obj} is not a DateAndTime.");
        }

        var utc = new DateTimeOffset(GciPosixDateAndTime.PosixEpoch.Ticks + result.Microseconds * TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
        return result.OffsetSeconds % 60 == 0 ? utc.ToOffset(TimeSpan.FromSeconds(result.OffsetSeconds)) : utc;
    }

    // ---- Date and Time ---------------------------------------------------------------------

    /// <summary>New Date (<c>Date fromDays:</c>, days since 1901-01-01; proleptic Gregorian like .NET).</summary>
    public Oop NewDate(DateOnly value)
    {
        lock (Sync) return Perform(DateClass, "fromDays:", Oop.FromSmallInteger(value.DayNumber - SmalltalkDateBase));
    }

    /// <summary>Value of a Date (<c>asDays</c>).</summary>
    public DateOnly ToDateOnly(Oop obj)
    {
        lock (Sync)
        {
            var days = ToInt64(Perform(obj, "asDays")); // SmallInteger: nothing to release
            return DateOnly.FromDayNumber((int)(days + SmalltalkDateBase));
        }
    }

    /// <summary>New Time (<c>Time fromMicroseconds:</c>); sub-microsecond ticks are truncated.</summary>
    public Oop NewTime(TimeOnly value)
    {
        lock (Sync) return Perform(TimeClass, "fromMicroseconds:", Oop.FromSmallInteger(value.Ticks / TimeSpan.TicksPerMicrosecond));
    }

    /// <summary>Value of a Time (<c>asMicroseconds</c>).</summary>
    public TimeOnly ToTimeOnly(Oop obj)
    {
        lock (Sync) return new TimeOnly(ToInt64(Perform(obj, "asMicroseconds")) * TimeSpan.TicksPerMicrosecond);
    }

    // ---- Classes -----------------------------------------------------------------------------

    /// <summary>Class of an object (GciFetchClass).</summary>
    public Oop FetchClass(Oop obj)
    {
        lock (Sync)
        {
            Activate();
            return CheckResult(new Oop(GciNative.GciFetchClass(obj.Value)));
        }
    }

    /// <summary>True if the object's class is <paramref name="cls"/> or a subclass (GciIsKindOfClass).</summary>
    public bool IsKindOf(Oop obj, Oop cls)
    {
        lock (Sync)
        {
            Activate();
            var result = GciNative.GciIsKindOfClass(obj.Value, cls.Value);
            ThrowIfError();
            return result;
        }
    }

    // ---- Generic mapping ------------------------------------------------------------------

    /// <summary>
    /// Maps a GemStone object to the matching .NET value:
    /// nil → <c>null</c>, Boolean → <see cref="bool"/>, Integer → <see cref="long"/> (or
    /// <see cref="BigInteger"/> beyond int64), Float/SmallDouble → <see cref="double"/>,
    /// ScaledDecimal → <see cref="decimal"/>, Character → <see cref="Rune"/>,
    /// CharacterCollection → <see cref="string"/>, ByteArray → <c>byte[]</c>,
    /// DateAndTime → <see cref="DateTimeOffset"/>, Date → <see cref="DateOnly"/>,
    /// Time → <see cref="TimeOnly"/>. Anything else (Array, Fraction, own classes …) is returned
    /// as <see cref="Oop"/>; use <see cref="ToList"/> or <see cref="GetInstVar"/> to look inside.
    /// </summary>
    public object? ToValue(Oop obj)
    {
        if (obj.IsNil) return null;
        if (obj.IsBoolean) return obj.ToBoolean();
        if (obj.IsSmallInteger) return obj.ToSmallInteger();
        if (obj.IsCharacter) return obj.ToCharacter();
        if (obj.IsSmallDouble) return obj.ToSmallDouble(); // no round trip
        if (obj.IsSmallDateAndTime) return ToDateTimeOffset(obj);
        if (obj.IsSmallDate) return ToDateOnly(obj);
        if (obj.IsSmallTime) return ToTimeOnly(obj);

        lock (Sync)
        {
            var cls = FetchClass(obj);
            if (cls == KnownClasses.SmallScaledDecimal || cls == KnownClasses.ScaledDecimal) return ToDecimal(obj);
            if (obj.IsSpecial) return obj; // SmallFraction, SmallDate, SmallTime …

            if (cls == KnownClasses.String || cls == KnownClasses.Symbol || cls == KnownClasses.Unicode7
                || cls == KnownClasses.Unicode16 || cls == KnownClasses.Unicode32 || cls == KnownClasses.Utf8
                || cls == KnownClasses.DoubleByteString || cls == KnownClasses.DoubleByteSymbol
                || cls == KnownClasses.QuadByteSymbol)
                return ToStringValue(obj);
            if (cls == KnownClasses.LargeInteger) return Narrow(ToBigInteger(obj));
            if (cls == KnownClasses.Float || cls == KnownClasses.SmallFloat) return ToDouble(obj);
            if (cls == KnownClasses.ByteArray) return FetchBytes(obj);

            // Subclasses of the kernel classes.
            if (IsKindOf(obj, KnownClasses.CharacterCollection)) return ToStringValue(obj);
            if (IsKindOf(obj, KnownClasses.Integer)) return Narrow(ToBigInteger(obj));
            if (IsKindOf(obj, DateAndTimeClass)) return ToDateTimeOffset(obj);
            if (IsKindOf(obj, DateClass)) return ToDateOnly(obj);
            if (IsKindOf(obj, TimeClass)) return ToTimeOnly(obj);
            return obj;
        }

        // Cast to object: otherwise ?: unifies both branches to BigInteger.
        static object Narrow(BigInteger value) =>
            value >= long.MinValue && value <= long.MaxValue ? (object)(long)value : value;
    }

    // Server side of ToValues: one tagged Array per object, readable offline from a traversal.
    // Same arithmetic as the single-value conversions (ToDecimal, ToDateOnly, ToTimeOnly …),
    // nothing is parsed from locale-dependent printString output (Integer printString is plain digits).
    private const string ToValuesBlock =
        "[:objs | objs collect: [:x |" +
        " (x isKindOf: Integer) ifTrue: [{1. x printString}] ifFalse: [" +
        " (x isKindOf: ScaledDecimal) ifTrue: [{2. (x * (10 raisedTo: x scale)) rounded printString. x scale}] ifFalse: [" +
        " (x isKindOf: DateAndTime) ifTrue: [{3. (x asPosixSeconds * 1000000) rounded printString. x offset asSeconds}] ifFalse: [" +
        " (x isKindOf: Date) ifTrue: [{4. x asDays}] ifFalse: [" +
        " (x isKindOf: Time) ifTrue: [{5. x asMicroseconds}] ifFalse: [" +
        " (x isKindOf: CharacterCollection) ifTrue: [{6. x asUnicodeString}] ifFalse: [" +
        " (x isKindOf: BinaryFloat) ifTrue: [{7. x asFloat}] ifFalse: [" +
        " (x isKindOf: Utf8) ifTrue: [{6. x decodeToUnicode}] ifFalse: [" + // Utf8 is a ByteArray, not a CharacterCollection
        " (x isKindOf: ByteArray) ifTrue: [{8. ByteArray withAll: x}] ifFalse: [0]]]]]]]]]]]";

    /// <summary>
    /// <see cref="ToValue"/> for many objects in one round trip (plus a few fixed ones), instead of
    /// one or more per object. Immediate values are decoded locally; objects that have no .NET
    /// counterpart are returned as their <see cref="Oop"/>, as with <see cref="ToValue"/>.
    /// </summary>
    /// <exception cref="OverflowException">A ScaledDecimal does not fit <see cref="decimal"/>.</exception>
    public object?[] ToValues(IReadOnlyList<Oop> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        var result = new object?[objects.Count];
        var pending = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            var o = objects[i];
            if (o.IsNil) result[i] = null;
            else if (o.IsBoolean) result[i] = o.ToBoolean();
            else if (o.IsSmallInteger) result[i] = o.ToSmallInteger();
            else if (o.IsSmallDouble) result[i] = o.ToSmallDouble();
            else if (o.IsCharacter) result[i] = o.ToCharacter();
            else pending.Add(i);
        }
        if (pending.Count == 0) return result;

        lock (Sync)
        {
            var array = NewObject(KnownClasses.Array, pending.Count);
            var block = Oop.Nil;
            try
            {
                StoreVaryingOops(array, 1, [.. pending.Select(i => objects[i])]);
                block = Execute(ToValuesBlock);
                var t = PerformTraverse(block, "value:", 3, [array], null);
                var tagged = t[t.Result!.Value].Indexed.ToArray();
                for (var k = 0; k < pending.Count; k++)
                {
                    var i = pending[k];
                    result[i] = tagged[k].IsSpecial ? objects[i] : Untag(t, t[tagged[k]].Indexed.ToArray(), objects[i]);
                }
                return result;
            }
            finally
            {
                Release(array, block);
            }
        }

        static object? Untag(GciTraversal t, Oop[] items, Oop original)
        {
            string Text(int i) => t.ToValue(items[i]) as string ?? throw new GciException($"Unexpected conversion result for {original}.");
            long Small(int i) => items[i].ToSmallInteger();
            switch (Small(0))
            {
                case 1:
                {
                    var big = BigInteger.Parse(Text(1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                    return big >= long.MinValue && big <= long.MaxValue ? (object)(long)big : big;
                }
                case 2:
                {
                    var scale = Small(2);
                    if (scale is < 0 or > 28)
                        throw new OverflowException($"ScaledDecimal scale {scale} is outside the .NET decimal range (0–28).");
                    var unscaled = BigInteger.Parse(Text(1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                    return (decimal)unscaled / (decimal)BigInteger.Pow(10, (int)scale);
                }
                case 3:
                {
                    var micros = long.Parse(Text(1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                    var offsetSeconds = Small(2);
                    var utc = new DateTimeOffset(GciPosixDateAndTime.PosixEpoch.Ticks + micros * TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
                    return offsetSeconds % 60 == 0 ? utc.ToOffset(TimeSpan.FromSeconds(offsetSeconds)) : utc;
                }
                case 4:
                    return DateOnly.FromDayNumber((int)(Small(1) + SmalltalkDateBase));
                case 5:
                    return new TimeOnly(Small(1) * TimeSpan.TicksPerMicrosecond);
                case 6:
                    return Text(1);
                case 7:
                    return t.ToValue(items[1]) is double d ? d : throw new GciException($"Unexpected conversion result for {original}.");
                case 8:
                    return t.ToValue(items[1]) as byte[] ?? throw new GciException($"Unexpected conversion result for {original}.");
                default:
                    return original;
            }
        }
    }

    /// <summary>
    /// Creates the GemStone counterpart of a .NET value: <c>null</c>, <see cref="bool"/>, integer
    /// types, <see cref="BigInteger"/>, <see cref="float"/>/<see cref="double"/>, <see cref="decimal"/>
    /// (ScaledDecimal), <see cref="char"/>/<see cref="Rune"/> (Character), <see cref="string"/>,
    /// <see cref="DateTimeOffset"/>/<see cref="DateTime"/> (DateAndTime), <see cref="DateOnly"/> (Date),
    /// <see cref="TimeOnly"/> (Time), <c>byte[]</c> (ByteArray) and <see cref="Oop"/> (as is).
    /// Release non-special results with <see cref="Release"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Unsupported type.</exception>
    public Oop FromValue(object? value) => value switch
    {
        null => Oop.Nil,
        Oop oop => oop,
        bool b => Oop.FromBoolean(b),
        Rune r => Oop.FromCharacter(r),
        char c => char.IsSurrogate(c)
            ? throw new ArgumentException("A lone surrogate char is not a Unicode code point; pass a Rune.", nameof(value))
            : Oop.FromCharacter(new Rune(c)),
        sbyte or byte or short or ushort or int or uint or long => NewInteger(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        ulong u => u <= long.MaxValue ? NewInteger((long)u) : NewBigInteger(u),
        BigInteger big => big >= long.MinValue && big <= long.MaxValue ? NewInteger((long)big) : NewBigInteger(big),
        float f => NewFloat(f),
        double d => NewFloat(d),
        decimal m => NewScaledDecimal(m),
        string s => NewString(s),
        DateTimeOffset dto => NewDateAndTime(dto),
        DateTime dt => NewDateAndTime(new DateTimeOffset(dt)),
        DateOnly date => NewDate(date),
        TimeOnly time => NewTime(time),
        byte[] bytes => NewByteArray(bytes),
        _ => throw new ArgumentException($"No GemStone mapping for {value.GetType()}.", nameof(value)),
    };

    private Oop NewBigInteger(BigInteger value) =>
        Execute(value.ToString(CultureInfo.InvariantCulture)); // integer literal, any size

    // ---- Convenience ------------------------------------------------------------------------

    /// <summary>Executes Smalltalk and converts the result with <see cref="ToValue"/>; converted results are released.</summary>
    public object? Evaluate(string source)
    {
        lock (Sync)
        {
            var result = Execute(source);
            return ConvertAndRelease(result);
        }
    }

    /// <summary>
    /// <see cref="Evaluate(string)"/> with a typed result; numeric results are converted with
    /// <see cref="Convert.ChangeType(object, Type, IFormatProvider)"/> (e.g. long → int).
    /// </summary>
    public T Evaluate<T>(string source) => CastTo<T>(Evaluate(source));

    /// <summary>
    /// Sends a message with .NET arguments (converted with <see cref="FromValue"/>) and converts
    /// the result with <see cref="ToValue"/>. Everything created here is released, unless the
    /// result is returned as <see cref="Oop"/>.
    /// </summary>
    public object? Send(Oop receiver, string selector, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        lock (Sync)
        {
            var argOops = new List<Oop>(args.Length);
            try
            {
                foreach (var arg in args)
                    argOops.Add(FromValue(arg));
                return ConvertAndRelease(Perform(receiver, selector, [.. argOops]));
            }
            finally
            {
                // Only release what FromValue created; caller-supplied Oops stay untouched.
                Release([.. argOops.Where((_, i) => args[i] is not Oop)]);
            }
        }
    }

    /// <inheritdoc cref="Send(Oop, string, object?[])"/>
    public T Send<T>(Oop receiver, string selector, params object?[] args) => CastTo<T>(Send(receiver, selector, args));

    private object? ConvertAndRelease(Oop result)
    {
        object? value;
        try
        {
            value = ToValue(result);
        }
        catch
        {
            Release(result);
            throw;
        }
        if (value is not Oop) Release(result);
        return value;
    }

    private static T CastTo<T>(object? value)
    {
        if (value is T t) return t;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (value is null)
            return default(T) is null ? default! : throw new InvalidCastException($"nil cannot be converted to {typeof(T)}.");
        // Only numeric widening/narrowing (e.g. long → int, long → double); strings are never parsed.
        // Out-of-range values throw OverflowException.
        if (IsNumeric(value.GetType()) && IsNumeric(target))
            return (T)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        throw new InvalidCastException($"{value.GetType()} cannot be converted to {typeof(T)}.");
    }

    private static bool IsNumeric(Type t) => Type.GetTypeCode(t) is >= TypeCode.SByte and <= TypeCode.Decimal;

    // Classes without a fixed OOP in gcioop.ht, resolved once per session (kept in the export set).
    private Oop DateAndTimeClass => _dateAndTimeClass ??= ResolveSymbol("DateAndTime");

    private Oop DateClass => _dateClass ??= ResolveSymbol("Date");

    private Oop TimeClass => _timeClass ??= ResolveSymbol("Time");
}
