using System.Collections;
using System.ComponentModel;
using System.Numerics;
using System.Text;

namespace GemStone.Gci.Mapping;

/// <summary>
/// Supplies the mapping metadata of C# types to a <see cref="GsObjectContext"/>: how to create
/// objects and collections and how to read and write mapped properties.
/// </summary>
/// <remarks>
/// Two implementations: the source generator of this package emits
/// <c>GemStone.Gci.Generated.GsGeneratedMappings</c> into every project with <see cref="GsClassAttribute"/>
/// types (trimming- and AOT-safe, pass <c>GsGeneratedMappings.Default</c>), and
/// <see cref="GsObjectContext(GciSession)"/> uses reflection.
/// </remarks>
public interface IGsMappingProvider
{
    /// <summary>
    /// Metadata for a <see cref="GsClassAttribute"/> type or a collection type, or <c>null</c> if
    /// the type is unknown to this provider. Scalars, <see cref="Oop"/> and <see cref="object"/>
    /// need no metadata.
    /// </summary>
    GsTypeMetadata? GetTypeMetadata(Type type);
}

internal enum GsShapeKind { Oop, Object, Scalar, Mapped, List, Array, Set, Dictionary }

/// <summary>
/// Mapping metadata of one type, created by <see cref="IGsMappingProvider"/> implementations
/// (generated code). Not meant to be used directly.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class GsTypeMetadata
{
    private GsTypeMetadata(Type type, GsShapeKind kind, params Type[] arguments)
    {
        Type = type;
        Kind = kind;
        Arguments = arguments;
    }

    internal Type Type { get; }
    internal GsShapeKind Kind { get; }

    /// <summary>Element type, or key and value type.</summary>
    internal Type[] Arguments { get; }

    /// <summary>Creates an empty collection (the argument is the expected size; the array length for arrays).</summary>
    internal Func<int, object>? New { get; private init; }

    /// <summary>Adds an element to a list or set.</summary>
    internal Action<object, object?>? Add { get; private init; }

    internal GsClassAttribute? GsClass { get; private init; }
    internal Func<object>? Create { get; private init; }
    internal IReadOnlyList<GsPropertyMetadata> Properties { get; private init; } = [];
    internal IReadOnlyList<Type> Subtypes { get; private init; } = [];
    internal Action<object, Oop>? SetOop { get; private init; }
    internal IReadOnlyList<string> Problems { get; private init; } = [];

    /// <summary>A list type (<c>List&lt;T&gt;</c>, <c>IList&lt;T&gt;</c>, <c>IReadOnlyList&lt;T&gt;</c> …), filled as a <c>List&lt;T&gt;</c>.</summary>
    public static GsTypeMetadata List<T>(Type declaredType) => new(declaredType, GsShapeKind.List, typeof(T))
    {
        New = n => new List<T>(n),
        Add = (c, v) => ((List<T>)c).Add((T)v!),
    };

    /// <summary>An array <c>T[]</c>.</summary>
    public static GsTypeMetadata Array<T>() => new(typeof(T[]), GsShapeKind.Array, typeof(T)) { New = n => new T[n] };

    /// <summary>A set type (<c>HashSet&lt;T&gt;</c>, <c>ISet&lt;T&gt;</c> …), filled as a <c>HashSet&lt;T&gt;</c>.</summary>
    public static GsTypeMetadata Set<T>(Type declaredType) => new(declaredType, GsShapeKind.Set, typeof(T))
    {
        New = _ => new HashSet<T>(),
        Add = (c, v) => ((HashSet<T>)c).Add((T)v!),
    };

    /// <summary>A dictionary type (<c>Dictionary&lt;K,V&gt;</c>, <c>IDictionary&lt;K,V&gt;</c> …), filled as a <c>Dictionary&lt;K,V&gt;</c>.</summary>
    public static GsTypeMetadata Dictionary<TKey, TValue>(Type declaredType) where TKey : notnull =>
        new(declaredType, GsShapeKind.Dictionary, typeof(TKey), typeof(TValue)) { New = _ => new Dictionary<TKey, TValue>() };

    /// <summary>A class with <see cref="GsClassAttribute"/>.</summary>
    /// <param name="className">The GemStone class name (<see cref="GsClassAttribute.Name"/>).</param>
    /// <param name="dictionary">The symbol dictionary (<see cref="GsClassAttribute.Dictionary"/>), or <c>null</c>.</param>
    /// <param name="create">Parameterless constructor; <c>null</c> for abstract classes.</param>
    /// <param name="properties">Mapped properties, including inherited ones.</param>
    /// <param name="subtypes">C# subclasses with their own <see cref="GsClassAttribute"/>, most derived first.</param>
    /// <param name="setOop">Setter of the <see cref="GsOopAttribute"/> property, if any.</param>
    /// <param name="problems">Mapping errors found while creating the metadata, reported on first use.</param>
    public static GsTypeMetadata Class<T>(string className, string? dictionary, Func<T>? create, GsPropertyMetadata[] properties,
        Type[] subtypes, Action<T, Oop>? setOop = null, string[]? problems = null) where T : class =>
        ForClass(typeof(T), new GsClassAttribute(className) { Dictionary = dictionary }, create is null ? null : () => create(), properties, subtypes,
            setOop is null ? null : (o, oop) => setOop((T)o, oop), problems ?? []);

    internal static GsTypeMetadata ForClass(Type type, GsClassAttribute gsClass, Func<object>? create,
        IReadOnlyList<GsPropertyMetadata> properties, IReadOnlyList<Type> subtypes, Action<object, Oop>? setOop,
        IReadOnlyList<string> problems) =>
        new(type, GsShapeKind.Mapped)
        {
            GsClass = gsClass, Create = create, Properties = properties, Subtypes = subtypes, SetOop = setOop, Problems = problems,
        };
}

/// <summary>A mapped property, see <see cref="GsTypeMetadata"/>. Not meant to be used directly.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class GsPropertyMetadata
{
    internal GsPropertyMetadata(string owner, string name, Type propertyType, string instVar,
        Func<object, object?> get, Action<object, object?> set, GsCollectionKind? collectionKind, bool readOnly,
        IGsValueConverter? converter)
    {
        Owner = owner;
        Name = name;
        PropertyType = propertyType;
        InstVar = instVar;
        Get = get;
        Set = set;
        CollectionKind = collectionKind;
        ReadOnly = readOnly;
        Converter = converter;
    }

    internal string Owner { get; }
    internal string Name { get; }
    internal Type PropertyType { get; }
    internal string InstVar { get; }
    internal Func<object, object?> Get { get; }
    internal Action<object, object?> Set { get; }
    internal GsCollectionKind? CollectionKind { get; }
    internal bool ReadOnly { get; }
    internal IGsValueConverter? Converter { get; }

    /// <summary>A property of <typeparamref name="TOwner"/> (the declaring class) mapped to <paramref name="instVar"/>.</summary>
    /// <param name="name">The property name (for error messages).</param>
    /// <param name="instVar">The GemStone instance variable.</param>
    /// <param name="get">Reads the property.</param>
    /// <param name="set">Writes the property.</param>
    /// <param name="collectionKind">From <see cref="GsCollectionAttribute"/>.</param>
    /// <param name="readOnly">From <see cref="GsReadOnlyAttribute"/>.</param>
    /// <param name="converter">From <see cref="GsConverterAttribute"/>.</param>
    public static GsPropertyMetadata Create<TOwner, TValue>(string name, string instVar, Func<TOwner, TValue> get,
        Action<TOwner, TValue> set, GsCollectionKind? collectionKind = null, bool readOnly = false,
        IGsValueConverter? converter = null) where TOwner : class =>
        new(typeof(TOwner).Name, name, typeof(TValue), instVar, o => get((TOwner)o), (o, v) => set((TOwner)o, (TValue)v!),
            collectionKind, readOnly, converter);
}

/// <summary>How a C# type is filled from a GemStone value (cached per type).</summary>
internal sealed class GsShape
{
    public required Type Type { get; init; }
    public required GsShapeKind Kind { get; init; }

    /// <summary>For scalars: the type without <see cref="Nullable{T}"/>.</summary>
    public Type? ScalarType { get; init; }

    public GsShape? Element { get; set; }
    public GsShape? Key { get; set; }
    public GsShape? Value { get; set; }
    public GsTypeMap? Map { get; set; }

    /// <summary>Creates an empty collection (List&lt;T&gt;, T[] of the given length, HashSet&lt;T&gt;, Dictionary&lt;K,V&gt;).</summary>
    public Func<int, object>? New { get; init; }

    /// <summary>Adds an element to a List&lt;T&gt; or HashSet&lt;T&gt;.</summary>
    public Action<object, object?>? AddElement { get; init; }

    public bool IsCollection => Kind is GsShapeKind.List or GsShapeKind.Array or GsShapeKind.Set or GsShapeKind.Dictionary;

    public override string ToString() => Type.Name;
}

/// <summary>A C# class with <see cref="GsClassAttribute"/> and its resolved GemStone class.</summary>
internal sealed class GsTypeMap
{
    public required Type Type { get; init; }
    public required GsClassAttribute Class { get; init; }
    public Func<object>? Create { get; init; }
    public List<GsPropertyMap> Properties { get; } = [];
    public Action<object, Oop>? SetOop { get; init; }

    /// <summary>C# subclasses with their own <see cref="GsClassAttribute"/>, most derived first.</summary>
    public List<GsTypeMap> Subtypes { get; } = [];

    public Oop ClassOop { get; set; } = Oop.Illegal;

    public override string ToString() => $"{Type.Name} ↔ {Class.Name}";
}

internal sealed class GsPropertyMap
{
    public required GsPropertyMetadata Metadata { get; init; }
    public required GsShape Shape { get; init; }

    public string InstVar => Metadata.InstVar;
    public Type PropertyType => Metadata.PropertyType;
    public IGsValueConverter? Converter => Metadata.Converter;
    public GsCollectionKind? CollectionKind => Metadata.CollectionKind;
    public bool ReadOnly => Metadata.ReadOnly;
    public object? Get(object entity) => Metadata.Get(entity);
    public void Set(object entity, object? value) => Metadata.Set(entity, value);

    /// <summary>1-based named instVar index in the GemStone class.</summary>
    public int Index { get; set; }

    public string Path => $"{Metadata.Owner}.{Metadata.Name}";
}

/// <summary>Builds shapes and type maps from the metadata of an <see cref="IGsMappingProvider"/>.</summary>
internal sealed class GsShapeFactory(IGsMappingProvider provider)
{
    private static readonly HashSet<Type> ScalarTypes =
    [
        typeof(bool), typeof(sbyte), typeof(byte), typeof(short), typeof(ushort), typeof(int), typeof(uint),
        typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal), typeof(BigInteger),
        typeof(char), typeof(Rune), typeof(string), typeof(byte[]), typeof(DateTimeOffset), typeof(DateTime),
        typeof(DateOnly), typeof(TimeOnly),
    ];

    internal static bool IsScalar(Type type) => ScalarTypes.Contains(type) || type.IsEnum;

    private readonly Dictionary<Type, GsShape> _shapes = [];

    /// <summary>Type maps created by <see cref="ShapeOf"/> that still need their server side resolved.</summary>
    public List<GsTypeMap> NewMaps { get; } = [];

    public GsShape ShapeOf(Type type, List<string> errors, string where)
    {
        if (_shapes.TryGetValue(type, out var known)) return known;

        var underlying = Nullable.GetUnderlyingType(type);
        if (type == typeof(Oop) || underlying == typeof(Oop))
            return Add(new GsShape { Type = type, Kind = GsShapeKind.Oop });
        if (type == typeof(object))
            return Add(new GsShape { Type = type, Kind = GsShapeKind.Object });
        var scalar = underlying ?? type;
        if (IsScalar(scalar))
            return Add(new GsShape { Type = type, Kind = GsShapeKind.Scalar, ScalarType = scalar });

        var meta = provider.GetTypeMetadata(type);
        switch (meta?.Kind)
        {
            case GsShapeKind.List or GsShapeKind.Array or GsShapeKind.Set:
            {
                var shape = Add(new GsShape { Type = type, Kind = meta.Kind, New = meta.New, AddElement = meta.Add });
                shape.Element = ShapeOf(meta.Arguments[0], errors, where);
                return shape;
            }
            case GsShapeKind.Dictionary:
            {
                var shape = Add(new GsShape { Type = type, Kind = GsShapeKind.Dictionary, New = meta.New });
                shape.Key = ShapeOf(meta.Arguments[0], errors, where);
                shape.Value = ShapeOf(meta.Arguments[1], errors, where);
                return shape;
            }
            case GsShapeKind.Mapped:
            {
                var shape = Add(new GsShape { Type = type, Kind = GsShapeKind.Mapped });
                BuildMap(shape, meta, errors); // the shape is registered first: properties may refer back to it
                return shape;
            }
        }

        errors.Add(provider is ReflectionMappingProvider
            ? $"{where}: type {type} is not supported (no [GsClass] and not a scalar, Oop or collection type)."
            : $"{where}: type {type} is not supported or unknown to {provider.GetType().Name} (the source generator covers [GsClass] " +
              "types, the types of their mapped properties and the T of Load<T> calls).");
        return Add(new GsShape { Type = type, Kind = GsShapeKind.Object });
    }

    private GsShape Add(GsShape shape)
    {
        _shapes[shape.Type] = shape;
        return shape;
    }

    private void BuildMap(GsShape shape, GsTypeMetadata meta, List<string> errors)
    {
        var map = new GsTypeMap { Type = meta.Type, Class = meta.GsClass!, Create = meta.Create, SetOop = meta.SetOop };
        shape.Map = map;
        NewMaps.Add(map);
        errors.AddRange(meta.Problems);

        foreach (var p in meta.Properties)
        {
            var where = $"{p.Owner}.{p.Name}";
            var propShape = p.Converter is null
                ? ShapeOf(p.PropertyType, errors, where)
                : new GsShape { Type = p.PropertyType, Kind = GsShapeKind.Object };
            var kind = p.CollectionKind;
            if (kind is not null && !propShape.IsCollection)
                errors.Add($"{where}: [GsCollection] on a property that is not a collection type.");
            if (kind == GsCollectionKind.Dictionary && propShape.IsCollection && propShape.Kind != GsShapeKind.Dictionary)
                errors.Add($"{where}: [GsCollection(Dictionary)] needs a dictionary property type.");
            if (kind is not null and not GsCollectionKind.Dictionary && propShape.Kind == GsShapeKind.Dictionary)
                errors.Add($"{where}: a dictionary property type needs [GsCollection(Dictionary)] or none.");
            map.Properties.Add(new GsPropertyMap { Metadata = p, Shape = propShape });
        }

        foreach (var sub in meta.Subtypes)
            if (ShapeOf(sub, errors, sub.Name).Map is { } subMap)
                map.Subtypes.Add(subMap);
    }
}
