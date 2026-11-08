using System.Text;

namespace GemStone.Gci;

/// <summary>
/// A GemStone object-oriented pointer (<c>OopType</c>, 64 bit in GemStone/S 64 Bit).
/// Special objects (SmallInteger, SmallDouble, Character, Boolean, nil …) are immediate values
/// encoded in the OOP itself, so equal values compare equal as OOPs and need no round trip.
/// </summary>
/// <remarks>
/// Constants and the tag-bit encoding are ported from <c>$GEMSTONE/include/gcioop.ht</c> and the
/// <c>GCI_*</c> macros in <c>gcicmn.ht</c> (3.7.5); the manual names them but does not list values.
/// </remarks>
public readonly record struct Oop(ulong Value)
{
    // ---- Tag bits (gcioop.ht) ------------------------------------------------------------
    private const ulong RamTagMask = 0x7;       // OOP_RAM_TAG_MASK
    private const ulong TagPomOop = 0x1;        // OOP_TAG_POM_OOP: object on disk / in the object table
    private const ulong TagSmallInt = 0x2;      // OOP_TAG_SMALLINT
    private const ulong TagSpecial = 0x4;       // OOP_TAG_SPECIAL
    private const ulong TagSmallDouble = 0x6;   // OOP_TAG_SMALLDOUBLE
    private const int NumTagBits = 3;           // OOP_NUM_TAG_BITS
    private const ulong AsciiNul = 0x1C;        // OOP_ASCII_NUL: Character with value 0
    private const int SpecialValueShift = 8;    // GCI_OOP_SPECIAL_VALUE_SHIFT
    private const uint MaxCodePoint = 0x10FFFF; // CHAR32_MAX_UTF8
    private const ulong SpecialClassMask = 0xFE;           // OOP_SPECIAL_CLASS_MASK
    private const ulong SmallFractionClassField = 40;      // OOP_SPECIAL_SmallFractionCLASS_FIELD
    private const ulong SmallScaledDecimalClassField = 48; // OOP_SPECIAL_SmallScaledDecimalCLASS_FIELD
    private const ulong SmallDateAndTimeClassField = 56;   // OOP_SPECIAL_SmallDateAndTimeCLASS_FIELD
    private const ulong SmallTimeClassField = 64;          // OOP_SPECIAL_SmallTimeCLASS_FIELD
    private const ulong SmallDateClassField = 72;          // OOP_SPECIAL_SmallDateCLASS_FIELD

    /// <summary>OOP_ILLEGAL — never refers to any object.</summary>
    public static readonly Oop Illegal = new(0x01);

    /// <summary>OOP_NIL.</summary>
    public static readonly Oop Nil = new(0x14);

    /// <summary>OOP_FALSE.</summary>
    public static readonly Oop False = new(0x0C);

    /// <summary>OOP_TRUE.</summary>
    public static readonly Oop True = new(0x10C);

    /// <summary>SmallInteger range: 61-bit two's complement.</summary>
    public const long SmallIntegerMin = -(1L << 60);
    public const long SmallIntegerMax = (1L << 60) - 1;

    public bool IsNil => this == Nil;

    /// <summary>GCI_OOP_IS_SPECIAL — immediate value, not a heap object.</summary>
    public bool IsSpecial => (Value & RamTagMask) != 0 && (Value & TagPomOop) == 0;

    /// <summary>GCI_OOP_IS_SMALL_INT.</summary>
    public bool IsSmallInteger => (Value & RamTagMask) == TagSmallInt;

    /// <summary>GCI_OOP_IS_SMALL_DOUBLE.</summary>
    public bool IsSmallDouble => (Value & RamTagMask) == TagSmallDouble;

    /// <summary>GCI_OOP_IS_BOOL.</summary>
    public bool IsBoolean => (Value & 0xFF) == False.Value;

    /// <summary>GCI_OOP_IS_CHAR.</summary>
    public bool IsCharacter => (Value & 0xFF) == AsciiNul;

    /// <summary>GCI_OOP_IS_SmallDateAndTime.</summary>
    public bool IsSmallDateAndTime => HasSpecialClass(SmallDateAndTimeClassField);

    /// <summary>GCI_OOP_IS_SmallDate.</summary>
    public bool IsSmallDate => HasSpecialClass(SmallDateClassField);

    /// <summary>GCI_OOP_IS_SmallTime.</summary>
    public bool IsSmallTime => HasSpecialClass(SmallTimeClassField);

    /// <summary>GCI_OOP_IS_SMALL_FRACTION.</summary>
    public bool IsSmallFraction => HasSpecialClass(SmallFractionClassField);

    /// <summary>GCI_OOP_IS_SmallScaledDecimal.</summary>
    public bool IsSmallScaledDecimal => HasSpecialClass(SmallScaledDecimalClassField);

    private bool HasSpecialClass(ulong classField) => (Value & SpecialClassMask) == (classField | TagSpecial);

    // ---- Pure conversions (macros, no GCI call) ---------------------------------------

    /// <summary>GCI_I64_IS_SMALL_INT.</summary>
    public static bool FitsSmallInteger(long value) => value is >= SmallIntegerMin and <= SmallIntegerMax;

    /// <summary>SmallInteger OOP for <paramref name="value"/> (value &lt;&lt; 3 | tag).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside the SmallInteger range — use
    /// <see cref="GciSession.NewInteger"/>, which creates a LargeInteger.</exception>
    public static Oop FromSmallInteger(long value)
    {
        if (!FitsSmallInteger(value))
            throw new ArgumentOutOfRangeException(nameof(value), value, "Outside the SmallInteger range.");
        return new Oop(((ulong)value << NumTagBits) | TagSmallInt);
    }

    /// <summary>Value of a SmallInteger OOP (arithmetic shift keeps the sign).</summary>
    public long ToSmallInteger() =>
        IsSmallInteger ? (long)Value >> NumTagBits : throw NotA("SmallInteger");

    /// <summary>
    /// GCI_OOP_TO_SMALL_DOUBLE (gcicmn.ht): a SmallDouble stores sign, an 8-bit exponent
    /// (re-biased by 0x380) and 52 fraction bits in the OOP itself.
    /// </summary>
    public double ToSmallDouble()
    {
        if (!IsSmallDouble) throw NotA("SmallDouble");
        const ulong exponentBase = 0x380; // SMALL_DOUBLE_EXPONENT_BASE
        ulong result = 0;
        if (Value >> (NumTagBits + 1) != 0)
        {
            var expBits = (Value >> 56) + exponentBase;
            result = ((Value << 8) >> 12) | (expBits << 52);
        }
        result |= (Value & 0x8) << 60; // sign bit
        return BitConverter.Int64BitsToDouble((long)result);
    }

    /// <summary>
    /// Inverse of <see cref="ToSmallDouble"/> (what GciFltToOop does locally): ±0 and doubles with
    /// an exponent of 0x380–0x47F (about 6e-39 to 6e38) are SmallDoubles; everything else
    /// (larger, smaller, NaN, infinities) needs a Float object.
    /// </summary>
    public static bool TryFromSmallDouble(double value, out Oop oop)
    {
        const ulong exponentBase = 0x380; // SMALL_DOUBLE_EXPONENT_BASE
        var bits = (ulong)BitConverter.DoubleToInt64Bits(value);
        var sign = bits >> 63;
        var exponent = (bits >> 52) & 0x7FF;
        var fraction = bits & 0xF_FFFF_FFFF_FFFF;
        if (exponent == 0 && fraction == 0)
        {
            oop = new Oop((sign << 3) | TagSmallDouble);
            return true;
        }
        // Exponent 0x380 only with a non-zero fraction: all-zero bits above the sign are ±0.
        if (exponent < exponentBase || exponent > exponentBase + 0xFF || (exponent == exponentBase && fraction == 0))
        {
            oop = default;
            return false;
        }
        oop = new Oop(((exponent - exponentBase) << 56) | (fraction << 4) | (sign << 3) | TagSmallDouble);
        return true;
    }

    /// <summary>GCI_BOOL_TO_OOP.</summary>
    public static Oop FromBoolean(bool value) => value ? True : False;

    /// <summary>GCI_OOP_TO_BOOL.</summary>
    public bool ToBoolean() =>
        this == True || (this == False ? false : throw NotA("Boolean"));

    /// <summary>GCI_CHR_TO_OOP_ — Character for a Unicode code point.</summary>
    public static Oop FromCharacter(Rune codePoint) =>
        new(((ulong)(uint)codePoint.Value << SpecialValueShift) | AsciiNul);

    /// <summary>GCI_OOP_TO_CHR for the full Unicode range.</summary>
    public Rune ToCharacter()
    {
        if (!IsCharacter) throw NotA("Character");
        var cp = (uint)(Value >> SpecialValueShift);
        return cp <= MaxCodePoint && Rune.IsValid((int)cp)
            ? new Rune((int)cp)
            : throw new InvalidCastException($"{this} is a Character outside the valid Unicode scalar range (0x{cp:X}).");
    }

    private InvalidCastException NotA(string kind) => new($"{this} is not a {kind}.");

    public override string ToString() => $"Oop(0x{Value:X})";
}
