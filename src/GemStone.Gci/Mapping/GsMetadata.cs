using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Reflection;
using System.Text;

namespace GemStone.Gci.Mapping;

internal enum GsShapeKind { Oop, Object, Scalar, Mapped, List, Array, Set, Dictionary }

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

    /// <summary>Concrete collection type to instantiate (List&lt;T&gt;, HashSet&lt;T&gt;, Dictionary&lt;K,V&gt;).</summary>
    public Type? Concrete { get; init; }

    /// <summary>HashSet&lt;T&gt;.Add.</summary>
    public MethodInfo? AddMethod { get; init; }

    public bool IsCollection => Kind is GsShapeKind.List or GsShapeKind.Array or GsShapeKind.Set or GsShapeKind.Dictionary;

    public override string ToString() => Type.Name;
}

/// <summary>A C# class with <see cref="GsClassAttribute"/> and its resolved GemStone class.</summary>
internal sealed class GsTypeMap
{
    public required Type Type { get; init; }
    public required GsClassAttribute Class { get; init; }
    public ConstructorInfo? Constructor { get; init; }
    public List<GsPropertyMap> Properties { get; } = [];
    public PropertyInfo? OopProperty { get; set; }

    /// <summary>C# subclasses with their own <see cref="GsClassAttribute"/>, most derived first.</summary>
    public List<GsTypeMap> Subtypes { get; } = [];

    public Oop ClassOop { get; set; } = Oop.Illegal;

    public override string ToString() => $"{Type.Name} ↔ {Class.Name}";
}

internal sealed class GsPropertyMap
{
    public required PropertyInfo Property { get; init; }
    public required string InstVar { get; init; }
    public required GsShape Shape { get; init; }
    public IGsValueConverter? Converter { get; init; }
    public GsCollectionKind? CollectionKind { get; init; }
    public bool ReadOnly { get; init; }

    /// <summary>1-based named instVar index in the GemStone class.</summary>
    public int Index { get; set; }

    public string Path => $"{Property.DeclaringType?.Name}.{Property.Name}";
}

/// <summary>Builds shapes and type maps by reflection.</summary>
[RequiresUnreferencedCode("The object mapper inspects mapped types by reflection.")]
[RequiresDynamicCode("The object mapper creates generic collection types at runtime.")]
internal sealed class GsShapeFactory
{
    private static readonly HashSet<Type> ScalarTypes =
    [
        typeof(bool), typeof(sbyte), typeof(byte), typeof(short), typeof(ushort), typeof(int), typeof(uint),
        typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal), typeof(BigInteger),
        typeof(char), typeof(Rune), typeof(string), typeof(byte[]), typeof(DateTimeOffset), typeof(DateTime),
        typeof(DateOnly), typeof(TimeOnly),
    ];

    private readonly Dictionary<Type, GsShape> _shapes = [];
    private readonly Dictionary<Assembly, Type[]> _mappedTypesByAssembly = [];

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
        if (ScalarTypes.Contains(scalar) || scalar.IsEnum)
            return Add(new GsShape { Type = type, Kind = GsShapeKind.Scalar, ScalarType = scalar });

        if (type.IsArray && type.GetArrayRank() == 1)
        {
            var shape = Add(new GsShape { Type = type, Kind = GsShapeKind.Array });
            shape.Element = ShapeOf(type.GetElementType()!, errors, where);
            return shape;
        }

        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments();
            if (def == typeof(List<>) || def == typeof(IList<>) || def == typeof(ICollection<>) || def == typeof(IEnumerable<>)
                || def == typeof(IReadOnlyList<>) || def == typeof(IReadOnlyCollection<>))
            {
                var shape = Add(new GsShape { Type = type, Kind = GsShapeKind.List, Concrete = typeof(List<>).MakeGenericType(args) });
                shape.Element = ShapeOf(args[0], errors, where);
                return shape;
            }
            if (def == typeof(HashSet<>) || def == typeof(ISet<>)
#if NET
                || def == typeof(IReadOnlySet<>)
#endif
                )
            {
                var concrete = typeof(HashSet<>).MakeGenericType(args);
                var shape = Add(new GsShape
                {
                    Type = type, Kind = GsShapeKind.Set, Concrete = concrete,
                    AddMethod = concrete.GetMethod(nameof(HashSet<object>.Add), [args[0]]),
                });
                shape.Element = ShapeOf(args[0], errors, where);
                return shape;
            }
            if (def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
            {
                var shape = Add(new GsShape { Type = type, Kind = GsShapeKind.Dictionary, Concrete = typeof(Dictionary<,>).MakeGenericType(args) });
                shape.Key = ShapeOf(args[0], errors, where);
                shape.Value = ShapeOf(args[1], errors, where);
                return shape;
            }
        }

        if (type.IsClass && type.GetCustomAttribute<GsClassAttribute>() is { } cls)
        {
            var shape = Add(new GsShape { Type = type, Kind = GsShapeKind.Mapped });
            BuildMap(shape, type, cls, errors); // the shape is registered first: properties may refer back to it
            return shape;
        }

        errors.Add($"{where}: type {type} is not supported (no [GsClass] and not a scalar, Oop or collection type).");
        return Add(new GsShape { Type = type, Kind = GsShapeKind.Object });
    }

    private GsShape Add(GsShape shape)
    {
        _shapes[shape.Type] = shape;
        return shape;
    }

    private void BuildMap(GsShape shape, Type type, GsClassAttribute cls, List<string> errors)
    {
        var ctor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        var map = new GsTypeMap { Type = type, Class = cls, Constructor = ctor };
        shape.Map = map;
        NewMaps.Add(map);
        if (ctor is null && !type.IsAbstract)
            errors.Add($"{type.Name}: needs a parameterless constructor.");

        foreach (var p in PropertiesOf(type))
        {
            if (p.GetCustomAttribute<GsIgnoreAttribute>() is not null) continue;
            var where = $"{type.Name}.{p.Name}";
            if (p.GetCustomAttribute<GsOopAttribute>() is not null)
            {
                if (p.PropertyType != typeof(Oop)) errors.Add($"{where}: [GsOop] requires type Oop.");
                else if (p.SetMethod is null) errors.Add($"{where}: [GsOop] property needs a setter.");
                else map.OopProperty = p;
                continue;
            }
            if (p.GetCustomAttribute<GsInstVarAttribute>() is not { } iv) continue;
            if (p.SetMethod is null)
            {
                errors.Add($"{where}: mapped property needs a setter (set or init).");
                continue;
            }

            IGsValueConverter? converter = null;
            if (p.GetCustomAttribute<GsConverterAttribute>() is { } conv)
            {
                if (!typeof(IGsValueConverter).IsAssignableFrom(conv.ConverterType) || conv.ConverterType.GetConstructor(Type.EmptyTypes) is null)
                    errors.Add($"{where}: converter {conv.ConverterType.Name} must implement IGsValueConverter and have a public parameterless constructor.");
                else
                    converter = (IGsValueConverter)Activator.CreateInstance(conv.ConverterType)!;
            }

            var propShape = converter is null ? ShapeOf(p.PropertyType, errors, where) : new GsShape { Type = p.PropertyType, Kind = GsShapeKind.Object };
            var kind = p.GetCustomAttribute<GsCollectionAttribute>()?.Kind;
            if (kind is not null && !propShape.IsCollection)
                errors.Add($"{where}: [GsCollection] on a property that is not a collection type.");
            if (kind == GsCollectionKind.Dictionary && propShape.IsCollection && propShape.Kind != GsShapeKind.Dictionary)
                errors.Add($"{where}: [GsCollection(Dictionary)] needs a dictionary property type.");
            if (kind is not null and not GsCollectionKind.Dictionary && propShape.Kind == GsShapeKind.Dictionary)
                errors.Add($"{where}: a dictionary property type needs [GsCollection(Dictionary)] or none.");

            map.Properties.Add(new GsPropertyMap
            {
                Property = p, InstVar = iv.Name, Shape = propShape, Converter = converter, CollectionKind = kind,
                ReadOnly = p.GetCustomAttribute<GsReadOnlyAttribute>() is not null,
            });
        }

        // C# subclasses mapped to GemStone subclasses (polymorphic loading), most derived first.
        foreach (var sub in MappedTypesIn(type.Assembly).Where(t => t != type && type.IsAssignableFrom(t))
                     .OrderByDescending(Depth))
            map.Subtypes.Add(ShapeOf(sub, errors, sub.Name).Map!);

        static int Depth(Type t)
        {
            var d = 0;
            for (var b = t.BaseType; b is not null; b = b.BaseType) d++;
            return d;
        }
    }

    /// <summary>
    /// Properties including inherited ones, each taken from its declaring type: seen through a
    /// derived type, an inherited <c>private set</c> has no SetMethod, and private base members
    /// are missing entirely.
    /// </summary>
    private static IEnumerable<PropertyInfo> PropertiesOf(Type type)
    {
        var seen = new HashSet<string>();
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
            foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (seen.Add(p.Name)) yield return p; // most derived declaration wins
    }

    private Type[] MappedTypesIn(Assembly assembly)
    {
        if (!_mappedTypesByAssembly.TryGetValue(assembly, out var types))
        {
            Type?[] all;
            try { all = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { all = e.Types; }
            types = [.. all.Where(t => t is { IsClass: true } && t.GetCustomAttribute<GsClassAttribute>() is not null).Select(t => t!)];
            _mappedTypesByAssembly[assembly] = types;
        }
        return types;
    }
}
