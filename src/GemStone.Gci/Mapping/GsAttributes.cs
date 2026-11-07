namespace GemStone.Gci.Mapping;

/// <summary>Maps a C# class to a GemStone class.</summary>
/// <remarks>
/// The class needs a parameterless constructor (it may be non-public). Only properties marked
/// with <see cref="GsInstVarAttribute"/> are mapped.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class GsClassAttribute(string name) : Attribute
{
    /// <summary>Name of the GemStone class.</summary>
    public string Name { get; } = name;

    /// <summary>
    /// Symbol dictionary holding the class (e.g. <c>"UserGlobals"</c>). If <c>null</c>, the
    /// name is resolved through the session's symbol list.
    /// </summary>
    public string? Dictionary { get; init; }
}

/// <summary>Maps a property to a named instance variable of the GemStone class.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class GsInstVarAttribute(string name) : Attribute
{
    /// <summary>Name of the instance variable.</summary>
    public string Name { get; } = name;
}

/// <summary>Excludes a property from mapping (reserved for future naming conventions).</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class GsIgnoreAttribute : Attribute;

/// <summary>Loaded, but never written back (read-only loaders ignore it).</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class GsReadOnlyAttribute : Attribute;

/// <summary>Receives the OOP of the loaded object; the property type must be <see cref="Oop"/>.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class GsOopAttribute : Attribute;

/// <summary>How a collection-valued instance variable is stored on the GemStone side.</summary>
public enum GsCollectionKind
{
    /// <summary>Array (or a subclass).</summary>
    Array,
    /// <summary>OrderedCollection or SortedCollection (or a subclass).</summary>
    Ordered,
    /// <summary>Set.</summary>
    Set,
    /// <summary>IdentitySet.</summary>
    IdentitySet,
    /// <summary>Bag.</summary>
    Bag,
    /// <summary>IdentityBag.</summary>
    IdentityBag,
    /// <summary>Any AbstractDictionary (Dictionary, KeyValueDictionary, SymbolDictionary …).</summary>
    Dictionary,
}

/// <summary>
/// Declares the GemStone collection kind of an instance variable. The loader checks the
/// object's actual class against it.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class GsCollectionAttribute(GsCollectionKind kind) : Attribute
{
    public GsCollectionKind Kind { get; } = kind;
}

/// <summary>Converts the value of an instance variable with a custom <see cref="IGsValueConverter"/>.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class GsConverterAttribute(Type converterType) : Attribute
{
    /// <summary>A type implementing <see cref="IGsValueConverter"/> with a parameterless constructor.</summary>
    public Type ConverterType { get; } = converterType;
}

/// <summary>Custom conversion of a property value.</summary>
public interface IGsValueConverter
{
    /// <summary>
    /// Converts a GemStone value to the property type. <paramref name="value"/> is already
    /// decoded: <c>null</c>, <see cref="bool"/>, <see cref="long"/>, <see cref="System.Numerics.BigInteger"/>,
    /// <see cref="double"/>, <see cref="decimal"/>, <see cref="System.Text.Rune"/>, <see cref="string"/>
    /// (also for Symbols), <c>byte[]</c>, date/time values, or the <see cref="Oop"/> for anything else.
    /// </summary>
    object? FromGemStone(object? value, Type targetType);
}
