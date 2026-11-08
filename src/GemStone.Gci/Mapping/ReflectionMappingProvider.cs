using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace GemStone.Gci.Mapping;

/// <summary>Mapping metadata by reflection (used by <see cref="GsObjectContext(GciSession)"/>).</summary>
[RequiresUnreferencedCode("The object mapper inspects mapped types by reflection.")]
[RequiresDynamicCode("The object mapper creates generic collection types at runtime.")]
internal sealed class ReflectionMappingProvider : IGsMappingProvider
{
    private readonly Dictionary<Assembly, Type[]> _mappedTypesByAssembly = [];

    public GsTypeMetadata? GetTypeMetadata(Type type)
    {
        if (type.IsArray && type.GetArrayRank() == 1)
            return Generic(nameof(GsTypeMetadata.Array), [type.GetElementType()!]);

        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments();
            if (def == typeof(List<>) || def == typeof(IList<>) || def == typeof(ICollection<>) || def == typeof(IEnumerable<>)
                || def == typeof(IReadOnlyList<>) || def == typeof(IReadOnlyCollection<>))
                return Generic(nameof(GsTypeMetadata.List), args, type);
            if (def == typeof(HashSet<>) || def == typeof(ISet<>)
#if NET
                || def == typeof(IReadOnlySet<>)
#endif
                )
                return Generic(nameof(GsTypeMetadata.Set), args, type);
            if (def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
                return Generic(nameof(GsTypeMetadata.Dictionary), args, type);
        }

        return type.IsClass && type.GetCustomAttribute<GsClassAttribute>() is { } cls ? ForClass(type, cls) : null;
    }

    private static GsTypeMetadata Generic(string factory, Type[] args, params object[] parameters) =>
        (GsTypeMetadata)typeof(GsTypeMetadata).GetMethod(factory)!.MakeGenericMethod(args).Invoke(null, parameters)!;

    private GsTypeMetadata ForClass(Type type, GsClassAttribute cls)
    {
        var problems = new List<string>();
        var ctor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (ctor is null && !type.IsAbstract)
            problems.Add($"{type.Name}: needs a parameterless constructor.");

        Action<object, Oop>? setOop = null;
        var properties = new List<GsPropertyMetadata>();
        foreach (var p in PropertiesOf(type))
        {
            if (p.GetCustomAttribute<GsIgnoreAttribute>() is not null) continue;
            var where = $"{type.Name}.{p.Name}";
            if (p.GetCustomAttribute<GsOopAttribute>() is not null)
            {
                if (p.PropertyType != typeof(Oop)) problems.Add($"{where}: [GsOop] requires type Oop.");
                else if (p.SetMethod is null) problems.Add($"{where}: [GsOop] property needs a setter.");
                else setOop = (o, oop) => p.SetValue(o, oop);
                continue;
            }
            if (p.GetCustomAttribute<GsInstVarAttribute>() is not { } iv) continue;
            if (p.SetMethod is null)
            {
                problems.Add($"{where}: mapped property needs a setter (set or init).");
                continue;
            }

            IGsValueConverter? converter = null;
            if (p.GetCustomAttribute<GsConverterAttribute>() is { } conv)
            {
                if (!typeof(IGsValueConverter).IsAssignableFrom(conv.ConverterType) || conv.ConverterType.GetConstructor(Type.EmptyTypes) is null)
                    problems.Add($"{where}: converter {conv.ConverterType.Name} must implement IGsValueConverter and have a public parameterless constructor.");
                else
                    converter = (IGsValueConverter)Activator.CreateInstance(conv.ConverterType)!;
            }

            properties.Add(new GsPropertyMetadata(p.DeclaringType!.Name, p.Name, p.PropertyType, iv.Name,
                p.GetValue, p.SetValue, p.GetCustomAttribute<GsCollectionAttribute>()?.Kind,
                p.GetCustomAttribute<GsReadOnlyAttribute>() is not null, converter));
        }

        // C# subclasses mapped to GemStone subclasses (polymorphic loading), most derived first.
        Type[] subtypes = [.. MappedTypesIn(type.Assembly).Where(t => t != type && type.IsAssignableFrom(t)).OrderByDescending(Depth)];

        return GsTypeMetadata.ForClass(type, cls, ctor is null || type.IsAbstract ? null : () => ctor.Invoke(null),
            properties, subtypes, setOop, problems);

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
