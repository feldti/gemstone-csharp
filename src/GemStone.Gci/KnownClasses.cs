namespace GemStone.Gci;

/// <summary>
/// OOPs of kernel classes from <c>$GEMSTONE/include/gcioop.ht</c> (3.7.5). These are fixed
/// for a GemStone version; classes not listed there (e.g. DateAndTime) are resolved by name.
/// </summary>
public static class KnownClasses
{
    public static readonly Oop Integer = new(70145);              // OOP_CLASS_INTEGER
    public static readonly Oop SmallInteger = new(74241);         // OOP_CLASS_SMALL_INTEGER
    public static readonly Oop LargeInteger = new(136193);        // OOP_CLASS_LargeInteger
    public static readonly Oop Float = new(135937);               // OOP_CLASS_Float
    public static readonly Oop SmallFloat = new(136705);          // OOP_CLASS_SmallFloat
    public static readonly Oop SmallDouble = new(121345);         // OOP_CLASS_SMALL_DOUBLE
    public static readonly Oop ScaledDecimal = new(146433);       // OOP_CLASS_ScaledDecimal
    public static readonly Oop SmallScaledDecimal = new(159233);  // OOP_CLASS_SmallScaledDecimal
    public static readonly Oop Boolean = new(68097);              // OOP_CLASS_BOOLEAN
    public static readonly Oop Character = new(68353);            // OOP_CLASS_CHARACTER
    public static readonly Oop UndefinedObject = new(76289);      // OOP_CLASS_UNDEFINED_OBJECT
    public static readonly Oop CharacterCollection = new(80385);  // OOP_CLASS_CHARACTER_COLLECTION
    public static readonly Oop String = new(74753);               // OOP_CLASS_STRING
    public static readonly Oop Symbol = new(110849);              // OOP_CLASS_SYMBOL
    public static readonly Oop DoubleByteString = new(143873);    // OOP_CLASS_DoubleByteString
    public static readonly Oop DoubleByteSymbol = new(144129);    // OOP_CLASS_DoubleByteSymbol
    public static readonly Oop QuadByteSymbol = new(144641);      // OOP_CLASS_QuadByteSymbol
    public static readonly Oop Utf8 = new(154113);                // OOP_CLASS_Utf8
    public static readonly Oop Unicode7 = new(154369);            // OOP_CLASS_Unicode7
    public static readonly Oop Unicode16 = new(154625);           // OOP_CLASS_Unicode16
    public static readonly Oop Unicode32 = new(154881);           // OOP_CLASS_Unicode32
    public static readonly Oop ByteArray = new(103425);           // OOP_CLASS_BYTE_ARRAY
    public static readonly Oop Array = new(66817);                // OOP_CLASS_ARRAY
    public static readonly Oop SmallDateAndTime = new(159489);    // OOP_CLASS_SmallDateAndTime
}
